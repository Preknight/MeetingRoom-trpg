using MeetingRoom.Agent.Thoughts;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Agent;

/// <summary>会话级选项（跨 turn 稳定）。随会议推进变化的内容一律走 <see cref="TurnRequest"/>。</summary>
public sealed record AgentSessionOptions
{
    public required string ParticipantId { get; init; }

    /// <summary>单轮最大 step 数，防弱模型空转（设计文档 §6.1、§13.1-6）。</summary>
    public int MaxStepsPerTurn { get; init; } = 12;

    /// <summary>
    /// 当前资料版本的读取器（阶段 5 接真实 MaterialStore）；为 null 时本 turn 不做陈旧判定。
    /// </summary>
    public Func<int>? CurrentMaterialVersion { get; init; }

    /// <summary>思考总结的上限（汉字），对应 <c>summaries.thoughtSummaryMaxChars</c>（设计文档 §4.4）。</summary>
    public int ThoughtSummaryMaxChars { get; init; } = ThoughtSummarizer.DefaultMaxChars;

    /// <summary>轮次记录器：思考全文只走这里落盘；不传则只生成摘要、不落盘。</summary>
    public ITurnRecorder? Recorder { get; init; }

    /// <summary>
    /// 单次发言的字数上限（0 = 不限制）。提示词负责"优先"，这里负责兜底：
    /// 模型偶尔仍会写超长，截断保证讨论区不会被单条巨文占满。
    /// </summary>
    public int MaxSpeechChars { get; init; }

    /// <summary>
    /// 工具集。<b>同一模式下所有会话必须完全一致</b>（不按角色裁剪），
    /// 否则 tools hash 会变、前缀缓存全部 miss（设计文档 §7.1）。
    /// </summary>
    public IReadOnlyList<AITool> Tools { get; init; } = [];

    public TurnCallbacks Callbacks { get; init; } = new();
}

/// <summary>
/// turn 运行期的事件回调。阶段 3 起会接到 EventBus 上，现在先留成回调便于测试。
/// </summary>
public sealed record TurnCallbacks
{
    /// <summary>正文分片（流式）。</summary>
    public Action<string>? Delta { get; init; }

    /// <summary>思考分片（模型直接输出的思考过程；只落盘与展示，默认不进上下文）。</summary>
    public Action<string>? ThoughtDelta { get; init; }

    /// <summary>一个 step 开始（含 step 序号）。</summary>
    public Action<int>? StepStarted { get; init; }

    /// <summary>在 step 边界消费到了一条中途注入。</summary>
    public Action<string>? SteerApplied { get; init; }

    /// <summary>模型请求了一批工具调用（即将逐个执行）。</summary>
    public Action<IReadOnlyList<FunctionCallContent>>? ToolCallsRequested { get; init; }

    /// <summary>单个工具执行完成。</summary>
    public Action<string, string>? ToolInvoked { get; init; }
}
