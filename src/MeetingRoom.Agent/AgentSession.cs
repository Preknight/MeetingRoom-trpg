using MeetingRoom.Agent.Thoughts;
using MeetingRoom.Agent.Tools;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Agent;

/// <summary>
/// 一个与会者的会话运行时：持有私有历史与 steer 队列，管理 turn 的生命周期。
/// 打断语义照搬 Reasonix 已实证的模型（设计文档 §6.2、§6.3）：
/// <list type="bullet">
///   <item>软注入优先：turn 运行中 <see cref="TrySteer"/> 入队，在下一个 step 边界生效；</item>
///   <item>硬打断兜底：由调用方的 <see cref="CancellationToken"/> 完成；</item>
///   <item>版本校验兜住迟到产出：turn 结束时仍有未消费的注入，用 <see cref="HasUnappliedSteer"/> 报告，
///         宿主据此<b>让位</b>再合成续轮，而不是丢弃产出。</item>
/// </list>
/// </summary>
public sealed class AgentSession : ISteerSource
{
    private readonly AgentSessionOptions _options;
    private readonly StepLoop _loop;
    private readonly List<ChatMessage> _history = [];
    private readonly Queue<string> _pendingSteer = new();
    private readonly List<string> _unappliedSteer = [];
    private readonly Lock _gate = new();
    private bool _running;
    private int _turnCounter;

    public AgentSession(AgentSessionOptions options, IChatClient chatClient, IToolDispatcher? toolDispatcher = null)
    {
        _options = options;
        _loop = new StepLoop(chatClient, toolDispatcher ?? new NullToolDispatcher(), options);
    }

    public string ParticipantId => _options.ParticipantId;

    /// <summary>
    /// 本会话实际拿到的工具集。决定它的是编排层的策略——叙事模式下 NPC 一个都不带（§11.5-2）。
    /// </summary>
    public IReadOnlyList<AITool> Tools => _options.Tools;

    /// <summary>是否有 turn 正在运行——这是 <see cref="TrySteer"/> 唯一的准入条件。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>当前（或最近一次）turn 的标识；事件载荷用它把同一轮的增量归到一起。</summary>
    public string? CurrentTurnId { get; private set; }

    /// <summary>turn 结束后仍有未被消费的中途注入（对应 Reasonix 的 <c>HasUnappliedSteer</c>）。</summary>
    public bool HasUnappliedSteer
    {
        get
        {
            lock (_gate)
            {
                return _unappliedSteer.Count > 0;
            }
        }
    }

    /// <summary>本会话的私有历史（诊断与测试用）。</summary>
    public IReadOnlyList<ChatMessage> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>
    /// 尝试中途注入。返回 <c>false</c> 表示<b>未接受</b>（会话不在运行中），
    /// 调用方应改走"补充轮"兜底，而不是丢弃这段内容（设计文档 §6.2）。
    /// </summary>
    public bool TrySteer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_running)
            {
                return false;
            }

            _pendingSteer.Enqueue(text);
            return true;
        }
    }

    /// <summary>取走并清空"未应用"的注入（宿主合成续轮时使用）。</summary>
    public IReadOnlyList<string> TakeUnappliedSteer()
    {
        lock (_gate)
        {
            var copy = _unappliedSteer.ToArray();
            _unappliedSteer.Clear();
            return copy;
        }
    }

    /// <summary>跑一个 turn。取消不会被抛成异常，而是以 <see cref="TurnOutcome.Cancelled"/> 返回。</summary>
    public async Task<TurnOutcome> RunTurnAsync(TurnRequest request, CancellationToken cancellationToken = default)
    {
        string turnId;
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException($"会话 {ParticipantId} 已有一个 turn 在运行。");
            }

            _running = true;
            _unappliedSteer.Clear();
            turnId = $"turn-{++_turnCounter}";
            CurrentTurnId = turnId;
        }

        try
        {
            var outcome = await _loop.RunAsync(request, this, _history, cancellationToken).ConfigureAwait(false);

            // 思考全文落盘、并生成有界摘要进私有历史（设计文档 §4.4）。
            // 被取消的 turn 不落盘：按 §6.2，硬打断后当前 step 的部分进度作废、上下文重放。
            if (!outcome.Cancelled)
            {
                await RecordOutcomeAsync(turnId, request.Task, outcome, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            lock (_gate)
            {
                // 直到 turn 结束都没等到 step 边界的注入：记为"未应用"，交宿主补轮（§6.3）。
                while (_pendingSteer.Count > 0)
                {
                    _unappliedSteer.Add(_pendingSteer.Dequeue());
                }

                _running = false;
            }
        }
    }

    /// <summary>
    /// 落盘本轮记录，并把<b>程序生成的</b>思考摘要追加进私有历史。
    /// 摘要绝不进共享前缀——否则所有会话的缓存前缀都会被同一份思考污染（§4.4）。
    /// </summary>
    private async Task RecordOutcomeAsync(
        string turnId,
        string task,
        TurnOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_options.Recorder is { } recorder)
        {
            if (outcome.Thought.Length > 0)
            {
                await recorder
                    .RecordAsync(new TurnRecord("thought", ParticipantId, turnId, outcome.Thought), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (outcome.Text.Length > 0)
            {
                await recorder
                    .RecordAsync(new TurnRecord("speech", ParticipantId, turnId, outcome.Text), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // 没有思考就没什么可摘要的：只有模型真的给出了思考过程，才需要把它压缩后留在私有历史里。
        if (outcome.Thought.Length == 0)
        {
            return;
        }

        var summary = ThoughtSummarizer.Summarize(
            outcome.Thought,
            new ThoughtSummaryInputs
            {
                Task = task,
                ToolCalls = outcome.Tools,
                Steps = outcome.Steps,
            },
            _options.ThoughtSummaryMaxChars);

        if (summary.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _history.Add(new ChatMessage(ChatRole.User, "[本轮思考摘要]" + Environment.NewLine + summary));
        }
    }

    IReadOnlyList<string> ISteerSource.Drain()
    {
        lock (_gate)
        {
            if (_pendingSteer.Count == 0)
            {
                return [];
            }

            var drained = _pendingSteer.ToArray();
            _pendingSteer.Clear();
            return drained;
        }
    }
}
