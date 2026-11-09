using System.Text;
using MeetingRoom.Agent.Tools;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Agent;

/// <summary>
/// step 循环（设计文档 §6.1）——本方案的核心，<b>手写</b>：
/// 绝不用会自动跑完工具循环的装饰器（如 FunctionInvokingChatClient），那会把 step 边界藏起来，
/// 三个取消点与 steer 注入点会全部失效（§6.6）。
/// <code>
/// for (step …) {
///   drainSteerQueue()                       // ② step 边界消费注入（Reasonix 同款位置）
///   if (materialVersion &gt; basedOn) stale   // ③ 版本校验
///   compose(prefix + privateHistory)        // ③ 装配请求
///   stream = chatClient.StreamAsync(…, ct)  // 取消点 A：流式读取中；取消点 C：等待连接/首字节
///   if (hasToolCalls) executeTools()        // 取消点 B：每个工具调用前
///   else return produceSpeech()             // ④ 产出正文
/// }
/// </code>
/// </summary>
internal sealed class StepLoop
{
    private readonly IChatClient _chatClient;
    private readonly IToolDispatcher _tools;
    private readonly TurnCallbacks _callbacks;
    private readonly int _maxSteps;
    private readonly Func<int>? _currentMaterialVersion;
    private readonly ChatOptions? _chatOptions;
    private readonly int _maxSpeechChars;

    public StepLoop(IChatClient chatClient, IToolDispatcher tools, AgentSessionOptions options)
    {
        _chatClient = chatClient;
        _tools = tools;
        _callbacks = options.Callbacks;
        _maxSteps = options.MaxStepsPerTurn;
        _currentMaterialVersion = options.CurrentMaterialVersion;
        _chatOptions = options.Tools.Count > 0 ? new ChatOptions { Tools = [.. options.Tools] } : null;
        _maxSpeechChars = options.MaxSpeechChars;
    }

    /// <summary>跑完一个 turn。取消是正常路径，不向宿主抛异常。</summary>
    /// <param name="history">会话私有历史；只在正常产出时追加，取消时保持原样（上下文重放，§6.2）。</param>
    public async Task<TurnOutcome> RunAsync(
        TurnRequest request,
        ISteerSource steer,
        List<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        // 本轮工作副本：注入与工具往返都先落在这里，确认产出后才回写历史。
        var conversation = new List<ChatMessage>(history);
        var text = new StringBuilder();
        var thoughts = new StringBuilder();
        var toolNames = new List<string>();
        var stale = false;
        var step = 0;
        var steerCount = 0;
        long cacheHit = 0;
        long cacheMiss = 0;

        try
        {
            for (step = 1; step <= _maxSteps; step++)
            {
                // ② step 边界：消费这段时间里到达的中途注入（Reasonix 的同款位置）。
                var injectedItems = steer.Drain();
                steerCount += injectedItems.Count;

                foreach (var injected in injectedItems)
                {
                    conversation.Add(new ChatMessage(ChatRole.User, injected));
                    _callbacks.SteerApplied?.Invoke(injected);
                }

                // ③ 版本校验：思考期间资料变了就标记陈旧，产出后由编排层补一轮（§6.3）。
                if (_currentMaterialVersion is { } current && current() > request.BasedOnMaterialVersion)
                {
                    stale = true;
                }

                _callbacks.StepStarted?.Invoke(step);

                var composed = request.Composer.Compose(request.ParticipantId, conversation, request.Task);
                var toolCalls = new List<FunctionCallContent>();

                // 每个 step 的正文各自独立：产出只取"决定结束的那一步"，
                // 否则多 step 的 turn 会把每一步的过场话都拼进同一条发言（讨论区会又长又杂）。
                text.Clear();

                // 取消点 A（流式读取中）与取消点 C（等待连接/首字节）：ct 一路传到最底层 HTTP。
                await foreach (var update in _chatClient
                    .GetStreamingResponseAsync(composed.Messages, _chatOptions, cancellationToken)
                    .WithCancellation(cancellationToken))
                {
                    Collect(update, text, thoughts, toolCalls, ref cacheHit, ref cacheMiss);
                }

                if (toolCalls.Count == 0)
                {
                    // 产出前的最后一道版本校验：流式期间资料可能已经变了，
                    // 只靠 step 开头那次检查会漏掉"单 step 的 turn"（§6.3 的迟到产出）。
                    if (_currentMaterialVersion is { } latest && latest() > request.BasedOnMaterialVersion)
                    {
                        stale = true;
                    }

                    var produced = Trim(text.ToString());

                    history.Add(new ChatMessage(ChatRole.Assistant, produced));
                    return new TurnOutcome(
                        produced, step, stale,
                        HitStepLimit: false, Cancelled: false,
                        Thought: thoughts.ToString(), ToolCalls: toolNames,
                        CacheHitTokens: cacheHit, CacheMissTokens: cacheMiss, SteerCount: steerCount);
                }

                foreach (var call in toolCalls)
                {
                    toolNames.Add(call.Name);
                }

                // 这一步还要继续（去调工具），它的文本只是过场说明：
                // 记入后台记录（第 5 列），不进讨论区。
                var narration = text.ToString().Trim();
                if (narration.Length > 0)
                {
                    _callbacks.ThoughtDelta?.Invoke(narration + Environment.NewLine);
                }

                _callbacks.ToolCallsRequested?.Invoke(toolCalls);

                foreach (var call in toolCalls)
                {
                    // 取消点 B：每个工具调用之前——避免"已决定写文件但资料已过期"的无效写入。
                    cancellationToken.ThrowIfCancellationRequested();

                    var result = await _tools
                        .InvokeAsync(request.ParticipantId, call, cancellationToken)
                        .ConfigureAwait(false);
                    conversation.Add(new ChatMessage(ChatRole.Assistant, [call]));
                    conversation.Add(new ChatMessage(ChatRole.Tool, [result]));
                    _callbacks.ToolInvoked?.Invoke(call.Name, result.Result?.ToString() ?? string.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TurnOutcome(
                text.ToString(), step, stale,
                HitStepLimit: false, Cancelled: true,
                Thought: thoughts.ToString(), ToolCalls: toolNames,
                CacheHitTokens: cacheHit, CacheMissTokens: cacheMiss, SteerCount: steerCount);
        }

        // 到 maxSteps 仍未产出正文：弱模型空转保护（§13.1-6）。历史不追加，避免把空转写进上下文。
        return new TurnOutcome(
            text.ToString(), _maxSteps, stale,
            HitStepLimit: true, Cancelled: false,
            Thought: thoughts.ToString(), ToolCalls: toolNames,
            CacheHitTokens: cacheHit, CacheMissTokens: cacheMiss, SteerCount: steerCount);
    }

    /// <summary>发言长度兜底：超限就截断并加省略号（提示词已经要求过，这里只防意外）。</summary>
    private string Trim(string text)
    {
        if (_maxSpeechChars <= 0 || text.Length <= _maxSpeechChars)
        {
            return text;
        }

        return text[..(_maxSpeechChars - 1)].TrimEnd() + "…";
    }

    private void Collect(
        ChatResponseUpdate update,
        StringBuilder text,
        StringBuilder thoughts,
        List<FunctionCallContent> toolCalls,
        ref long cacheHit,
        ref long cacheMiss)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } chunk:
                    text.Append(chunk.Text);
                    _callbacks.Delta?.Invoke(chunk.Text);
                    break;

                // 模型直接输出的思考过程：只落盘与展示，默认不进上下文（设计文档 §4.4）。
                case TextReasoningContent { Text.Length: > 0 } reasoning:
                    thoughts.Append(reasoning.Text);
                    _callbacks.ThoughtDelta?.Invoke(reasoning.Text);
                    break;

                case FunctionCallContent call:
                    toolCalls.Add(call);
                    break;

                // provider 的用量信息（含前缀缓存命中/未命中）随流式响应一起回来。
                case UsageContent usage:
                    AccumulateUsage(usage.Details, ref cacheHit, ref cacheMiss);
                    break;
            }
        }
    }

    /// <summary>把 provider 的用量折成"缓存命中 / 未命中"两个数（设计文档 §7.4）。</summary>
    private static void AccumulateUsage(UsageDetails? details, ref long cacheHit, ref long cacheMiss)
    {
        if (details is null)
        {
            return;
        }

        var hit = details.CachedInputTokenCount ?? 0;

        // 有些兼容端点把缓存计数放在附加计数里（DeepSeek 的 prompt_cache_hit_tokens 等）。
        if (details.AdditionalCounts is { } counts)
        {
            foreach (var (key, value) in counts)
            {
                if (key.Contains("hit", StringComparison.OrdinalIgnoreCase))
                {
                    hit = Math.Max(hit, value);
                }
            }
        }

        var input = details.InputTokenCount ?? 0;
        cacheHit += hit;
        cacheMiss += Math.Max(0, input - hit);
    }
}
