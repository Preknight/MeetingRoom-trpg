using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;

namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 编排内核：会议与叙事<b>共用的机制</b>——状态机（Idle / Running / Paused / AwaitingInput / Ended）、
/// 后台任务生命周期（Start 立即返回、Dispose 等到收尾）、等待门、待决事项（谁在等用户拍板）、
/// 状态事件与快照骨架。
/// <para>
/// <b>领域语义不进内核</b>：权威状态（资料库 / 剧情时间线）、控制面工具、上下文装配、快照载荷与事件前缀
/// 都留在各自的会话外壳里——这是设计文档 §11.3「内核共用、策略各异」在编排层的落法。
/// </para>
/// </summary>
public abstract class SessionCore : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly PendingDecisionRegistry _decisions = new();
    private TaskCompletionSource _resumeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runLoop;
    private MeetingStatus _status = MeetingStatus.Idle;
    private int _awaiting;
    private int _round;
    private int _consecutiveSkips;
    private bool _askedThisRound;

    /// <summary>事件出口。</summary>
    protected abstract IEventBus Bus { get; }

    /// <summary>状态事件类型（<c>meeting.state</c> / <c>roleplay.state</c>）。</summary>
    protected abstract string StateEventType { get; }

    /// <summary>状态快照载荷（两种模式的字段不同，各自实现）。</summary>
    protected abstract object SnapshotCore();

    /// <summary>除长跑循环之外还要在 Dispose 时等到的后台任务（会议有打断泵，叙事没有）。</summary>
    protected virtual IReadOnlyList<Task?> ExtraBackgroundTasks => [];

    /// <summary>交互模式配置（阶段 9）；两种模式各自接自己那份。</summary>
    protected virtual InteractionOptions Interaction => new();

    protected Lock Gate => _gate;

    protected CancellationToken LifetimeToken => _lifetime.Token;

    /// <summary>
    /// 对外的状态：等用户拍板期间呈 <see cref="MeetingStatus.AwaitingInput"/>——
    /// 底层的"跑不跑"仍是 Running，只是这一刻在等人。暂停 / 结束优先于它。
    /// </summary>
    public MeetingStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status == MeetingStatus.Running && _awaiting > 0
                    ? MeetingStatus.AwaitingInput
                    : _status;
            }
        }
    }

    /// <summary>开始：子类在 StartAsync 里调用（Idle → Running；重复开始即报错，文案带上自己那场戏的说法）。</summary>
    protected void MarkRunning(string what)
    {
        lock (_gate)
        {
            if (_status != MeetingStatus.Idle)
            {
                throw new InvalidOperationException($"{what}已经开始了。");
            }

            _status = MeetingStatus.Running;
        }
    }

    /// <summary>
    /// 把状态钉在 Ended，并唤醒等待门（等待中的循环会自己收尾）。
    /// 还挂着没答复的待决事项一次性收口——否则等它的人会永远等下去。
    /// </summary>
    protected void MarkEnded()
    {
        lock (_gate)
        {
            if (_status == MeetingStatus.Ended)
            {
                return;
            }

            _status = MeetingStatus.Ended;
            ReleaseGateLocked();
        }

        // 锁外收口：完成回调（continuation）可能回来取 Gate，锁内做会自锁。
        _decisions.CancelAll("会话已结束");
    }

    /// <summary>暂停：不再发起新 turn。运行中的 turn 不被打断（默认跑完当前 step）。</summary>
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // 结束之后没有"暂停"可言；Idle 也允许按住——用户可以先停住，避免一 Start 就跑。
            if (_status is MeetingStatus.Ended or MeetingStatus.Paused)
            {
                return Task.CompletedTask;
            }

            _status = MeetingStatus.Paused;
            ReleaseGateLocked();
        }

        PublishState();
        return Task.CompletedTask;
    }

    /// <summary>继续：从暂停点恢复，已完成的部分不重放。</summary>
    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_status != MeetingStatus.Paused)
            {
                return Task.CompletedTask;
            }

            _status = MeetingStatus.Running;
            ReleaseGateLocked();
        }

        PublishState();
        return Task.CompletedTask;
    }

    /// <summary>结束：钉住状态 → 给子类一次收尾机会（会议要倒空注入合并窗口）→ 推一份新快照。</summary>
    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        MarkEnded();
        await OnEndedAsync(cancellationToken).ConfigureAwait(false);
        PublishState();
    }

    /// <summary>结束后、推快照前的收尾钩子。</summary>
    protected virtual Task OnEndedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 等待门：<b>暂停</b>期间、以及<b>有事项在等用户拍板</b>期间，不发起新 turn。
    /// 暂停 / 答复 / 结束都会放行，醒来后重新判条件（条件仍成立就继续等新的那道门）。
    /// </summary>
    protected async Task WaitWhileBlockedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task gate;

            lock (_gate)
            {
                if (!IsBlockedLocked() || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                gate = _resumeGate.Task;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>抛出一道待用户拍板的事项，等答复 / 跳过 / 超时，返回收口结果。</summary>
    /// <remarks>
    /// 交互模式关掉（<c>interaction.enabled = false</c>）时返回 <c>null</c>——调用方按"这次没得问"处理，
    /// 退回全自动推进。选项数量超过 <c>interaction.maxOptions</c> 属实现错误，抛异常而不是静默截断。
    /// </remarks>
    protected async Task<InteractionOutcome?> RequestDecisionAsync(
        PendingDecisionKind kind,
        IReadOnlyList<InteractionQuestion> questions,
        string? turnId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questions);

        if (!Interaction.Enabled)
        {
            return null;
        }

        if (questions.Any(question => question.Options.Count > Interaction.MaxOptions))
        {
            throw new ArgumentException(
                $"选项数量超过 interaction.maxOptions（{Interaction.MaxOptions}）。",
                nameof(questions));
        }

        var decision = _decisions.Open(kind, questions, turnId, timeoutSeconds: Interaction.TimeoutSeconds);

        // 先广播再进等待：监听方（前端 / 脚本）看到 awaitingInput 时，这件事一定已经在流里了。
        Bus.Publish("interaction.request", Project(decision));
        EnterAwaitingLocked();

        InteractionOutcome outcome;

        try
        {
            outcome = await decision.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 会话被结束 / 进程退出：把这件事收口，别留一个永不完成的句柄。
            _decisions.Skip(decision.Id, "会话已结束");
            throw;
        }
        finally
        {
            ExitAwaitingLocked();
        }

        Bus.Publish("interaction.resolved", new
        {
            id = outcome.Id,
            answers = Project(outcome.Answers),
            timedOut = outcome.TimedOut,
            skipped = outcome.Skipped,
            skipReason = outcome.SkipReason,
        });

        await OnInteractionResolvedAsync(decision, outcome).ConfigureAwait(false);

        return outcome;
    }

    /// <summary>
    /// 收口之后的落盘钩子（设计文档 §14.6）：各模式把"谁问了什么、用户选了什么"记进自己的审计日志。
    /// 默认不做事——记在哪、记成什么形状，是各模式的领域。
    /// </summary>
    protected virtual Task OnInteractionResolvedAsync(PendingDecision decision, InteractionOutcome outcome) =>
        Task.CompletedTask;

    /// <summary>
    /// **只登记、不等待**（B5 的 DM 提醒）：不进 awaiting、不阻塞剧情、不 await 完成。
    /// <para>
    /// 与 <see cref="RequestDecisionAsync"/> 的差别是那三条：它<b>不会</b>让 <c>WaitWhileBlockedAsync</c> 挂住，
    /// 所以导演投提醒时剧情照跑；答复 / 超时后由调用方在 <c>Completion</c> 上收口。
    /// </para>
    /// </summary>
    protected PendingDecision RegisterSideDecision(
        PendingDecisionKind kind,
        IReadOnlyList<InteractionQuestion> questions,
        IReadOnlyList<InteractionAnswer>? defaultSelection = null,
        int timeoutSeconds = 0,
        Action<PendingDecision, InteractionOutcome>? onResolved = null)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var decision = _decisions.Open(
            kind,
            questions,
            turnId: null,
            defaultSelection: defaultSelection,
            timeoutSeconds: timeoutSeconds,
            onResolved);

        // 照样广播：前端据它把卡片放上第五列（重连后靠 state.pending 重建）。
        Bus.Publish("interaction.request", Project(decision));
        return decision;
    }

    /// <summary>这件事是不是还在等（端点据此判断该投递给哪个会话）。</summary>
    public bool HasPendingDecision(string id) => _decisions.HasPending(id);

    /// <summary>答复（端点用）：<c>false</c> 表示这件事已经不在等（重复答复 / 已超时）。</summary>
    public bool AnswerPendingDecision(string id, IReadOnlyList<InteractionAnswer> answers) =>
        _decisions.Answer(id, answers);

    /// <summary>跳过（端点用）：按默认选项收口并留下原因。</summary>
    public bool SkipPendingDecision(string id, string? reason) => _decisions.Skip(id, reason);

    /// <summary>还挂着的事项（快照用：前端重连后据此重建浮层与第 3 列）。</summary>
    protected object PendingDecisionsSnapshot() =>
        _decisions.OpenDecisions().Select(decision => (object)Project(decision)).ToArray();

    protected void PublishState() => Bus.Publish(StateEventType, SnapshotCore());

    /// <summary>
    /// 把长跑循环交给后台：<c>StartAsync</c> 必须立即返回，否则用户会眼睁睁看着一个 HTTP 请求
    /// 跑到整场结束，期间没法暂停、补料或纠偏。
    /// </summary>
    protected void StartBackground(Func<Task> loop) => _runLoop = Task.Run(loop, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _decisions.CancelAll("会话已释放");

        // 等后台任务真正退出：否则同进程里连续创建 / 销毁多场会话时，上一场的循环可能还活着，
        // 短暂地继续往总线推事件。它们都把取消当正常路径（各自吞掉 OperationCanceledException）。
        foreach (var background in new[] { _runLoop }.Concat(ExtraBackgroundTasks))
        {
            if (background is null)
            {
                continue;
            }

            try
            {
                await background.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消是正常路径。
            }
        }

        _decisions.Dispose();
        _lifetime.Dispose();
    }

    private bool IsBlockedLocked() =>
        _status == MeetingStatus.Paused
        || (_status == MeetingStatus.Running && _awaiting > 0);

    /// <summary>
    /// 放行当前这道门，然后换一道新的：等待者醒来会重新判条件，条件仍成立就在新门上继续等。
    /// 只 <c>TrySetResult</c> 不换门会在"条件仍成立"时变成忙循环。
    /// </summary>
    private void ReleaseGateLocked()
    {
        _resumeGate.TrySetResult();
        _resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ——— 交互模式的轮次账（设计文档 §14.5.1） ———

    /// <summary>轮次号（从 1 开始）：事件与留痕里用它对齐"第几轮问的 / 跳的"。</summary>
    protected int RoundNumber
    {
        get
        {
            lock (_gate)
            {
                return _round;
            }
        }
    }

    /// <summary>开始新一轮：这一轮还没问过，轮次号 +1。</summary>
    protected void BeginInteractionRound()
    {
        lock (_gate)
        {
            _askedThisRound = false;
            _round++;
        }
    }

    /// <summary>记下"这一轮的账"：确实问过了，并把连续跳过清零。</summary>
    protected void MarkAskedThisRound()
    {
        lock (_gate)
        {
            _askedThisRound = true;
            _consecutiveSkips = 0;
        }
    }

    /// <summary>
    /// 一轮结束时按配置收口交互（设计文档 §14.5.1）。三条路：
    /// <list type="bullet">
    ///   <item><b>每轮必须问</b>（<c>everyRound</c> 且不允许自己跳过）：这一轮没问过 → 用默认问题集
    ///     <b>强制发问</b>——"必有交互"由程序保证，而不是靠提示词；</item>
    ///   <item><b>允许自己跳过</b>：发一条 <c>interaction.skipped</c> 留痕（"连着几轮没问我"要看得见），
    ///     连续跳到 <c>maxConsecutiveSkips</c> 时强制问一次；</item>
    ///   <item>交互模式关掉：什么都不做。</item>
    /// </list>
    /// </summary>
    protected async Task EnforceRoundInteractionAsync(CancellationToken cancellationToken)
    {
        var options = Interaction;

        if (!options.Enabled || Status == MeetingStatus.Ended)
        {
            return;
        }

        lock (_gate)
        {
            if (_askedThisRound)
            {
                return;
            }
        }

        if (options.EveryRound && !options.AllowSelfSkip)
        {
            await AskWithDefaultsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!options.AllowSelfSkip)
        {
            return;
        }

        int consecutive;
        int round;

        lock (_gate)
        {
            _consecutiveSkips++;
            consecutive = _consecutiveSkips;
            round = _round;
        }

        Bus.Publish("interaction.skipped", new
        {
            round,
            reason = "这一轮没什么可问的（allowSelfSkip = true）",
        });

        if (options.MaxConsecutiveSkips > 0 && consecutive >= options.MaxConsecutiveSkips)
        {
            lock (_gate)
            {
                _consecutiveSkips = 0;
            }

            Bus.Publish("interaction.skipped", new
            {
                round,
                reason = $"连着 {consecutive} 轮没提问，按 maxConsecutiveSkips 强制问一次",
            });

            await AskWithDefaultsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>用默认问题集发问：编排层兜底时用它，答复按默认选项收口。</summary>
    private async Task AskWithDefaultsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RequestDecisionAsync(
                PendingDecisionKind.AskUser,
                AskUserSpec.DefaultRoundQuestions(),
                turnId: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 本场结束 / 进程退出：正常路径。
        }
    }

    private void EnterAwaitingLocked()
    {
        lock (_gate)
        {
            _awaiting++;
        }

        PublishState();
    }

    private void ExitAwaitingLocked()
    {
        lock (_gate)
        {
            if (_awaiting > 0)
            {
                _awaiting--;
            }

            ReleaseGateLocked();
        }

        PublishState();
    }

    private static object Project(PendingDecision decision) => new
    {
        id = decision.Id,
        kind = Wire(decision.Kind),
        turnId = decision.TurnId,
        questions = decision.Questions.Select(question => new
        {
            id = question.Id,
            prompt = question.Prompt,
            options = question.Options
                .Select(option => new { id = option.Id, label = option.Label, description = option.Description })
                .ToArray(),
            defaultOptionId = question.DefaultOptionId,
        }).ToArray(),
        defaultSelection = Project(decision.DefaultSelection),
        timeoutSeconds = decision.TimeoutSeconds,
        createdAt = decision.CreatedAt,
    };

    private static IReadOnlyList<object> Project(IReadOnlyList<InteractionAnswer> answers) =>
        answers
            .Select(answer => (object)new { questionId = answer.QuestionId, selected = answer.Selected })
            .ToArray();

    private static string Wire(PendingDecisionKind kind) => kind switch
    {
        PendingDecisionKind.AskUser => "ask_user",
        PendingDecisionKind.WorldState => "world_state",
        PendingDecisionKind.DmNote => "dm_note",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
