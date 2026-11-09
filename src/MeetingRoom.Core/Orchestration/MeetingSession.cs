using System.Text;
using MeetingRoom.Agent;
using MeetingRoom.Agent.Tools;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Persistence;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 会议编排器（设计文档 §3 的 Orchestrator）：把与会者运行时、资料库、打断策略、主持人 Agent
/// 与事件总线串在一起，实现原则 3 的完整闭环——
/// <b>快产出 → 采纳 → 慢者感知（软注入）→ 兜底补轮 → 最终产出基于最新版</b>。
/// </summary>
public sealed class MeetingSession : SessionCore, IHostActions
{
    private readonly MeetingOptions _options;
    private readonly IEventBus _bus;
    private readonly MaterialStore _materials;
    private readonly ISpeechLog _speech;
    private readonly IOrchestrationStrategy _strategy;
    private readonly ParticipantRegistry _registry;
    private readonly InterruptPolicy _interrupt;
    private readonly MeetingToolDispatcher _toolDispatcher;
    private readonly MaterialSelector _selector;
    private readonly ITurnScheduler _scheduler;
    private readonly TranscriptWriter? _transcript;
    private readonly List<string> _recentSpeeches = [];
    private readonly CacheDiagnostics _diagnostics = new();
    private readonly Dictionary<string, string> _lastPrefixFingerprint = new(StringComparer.Ordinal);

    /// <summary>自上次 turn 以来收到过软注入的与会者——用来给"前缀为什么失效"归因（§7.4）。</summary>
    private readonly HashSet<string> _steeredSinceLastTurn = new(StringComparer.Ordinal);
    private readonly TimeSpan _pumpInterval;

    private readonly List<string> _userNotes = [];
    private readonly Queue<HostIntent> _hostIntents = new();

    private string _topic;
    private Task? _interruptPump;
    private int _round;
    private string? _hostId;

    protected override IEventBus Bus => _bus;

    protected override string StateEventType => "meeting.state";

    protected override InteractionOptions Interaction => _options.Interaction;

    /// <summary>打断泵随会话创建即常驻，Dispose 时要一并等到。</summary>
    protected override IReadOnlyList<Task?> ExtraBackgroundTasks => [_interruptPump];

    public MeetingSession(
        MeetingOptions options,
        IChatClient chatClient,
        IEventBus bus,
        MaterialStore materials,
        ISpeechLog speech,
        IOrchestrationStrategy? strategy = null,
        TimeSpan? pumpInterval = null,
        TranscriptWriter? transcript = null)
    {
        _options = options;
        _topic = options.Topic;
        _bus = bus;
        _materials = materials;
        _speech = speech;
        _transcript = transcript;
        _strategy = strategy ?? new MeetingStrategy();
        _interrupt = new InterruptPolicy(options.SteerMergeWindowMs);
        _toolDispatcher = new MeetingToolDispatcher(this, IsHostParticipant, materials, _strategy, speech);
        _scheduler = _strategy.Dispatch == TurnDispatch.Sequential
            ? new SequentialTurnScheduler()
            : new ParallelTurnScheduler();

        _selector = new MaterialSelector(
            options.AlwaysMaterialMaxCount,
            options.TriggeredTokenBudget,
            options.TriggeredCooldownRounds);
        _pumpInterval = pumpInterval ?? TimeSpan.FromMilliseconds(100);

        _registry = new ParticipantRegistry(
            new ParticipantRegistryOptions
            {
                MaxConcurrentTurns = options.MaxConcurrentTurns,
                MaxStepsPerTurn = options.MaxStepsPerTurn,
                ThoughtSummaryMaxChars = options.ThoughtSummaryMaxChars,
                MaxSpeechChars = options.MaxSpeechChars,
                SystemPrompt = options.SystemPrompt,
                Bus = bus,
                ToolDispatcher = _toolDispatcher,
                Tools = _toolDispatcher.Tools,
                // 叙事模式下 NPC 不带工具（§11.5-2）；主持人 / 说书人始终带。
                MemberTools = _strategy.MembersCarryTools ? _toolDispatcher.Tools : [],
                // 版本兜底（§6.3）靠它：产出时与当前版本比较。
                CurrentMaterialVersion = () => _materials.Version,
            },
            chatClient);

        // 打断泵随会话创建即常驻：补料/采纳要在"有会话正在跑"时立刻生效，
        // 而那时会议未必已经 StartAsync（例如用户先点名一个人发言）。
        _interruptPump = Task.Run(() => PumpInterruptsAsync(LifetimeToken), CancellationToken.None);
    }

    public int Round
    {
        get
        {
            lock (Gate)
            {
                return _round;
            }
        }
    }

    public int MaterialVersion => _materials.Version;

    public IReadOnlyList<Participant> Participants => _registry.Participants;

    public MaterialStore Materials => _materials;

    /// <summary>本次会议的工具集（全员一致，不按角色裁剪）。</summary>
    public IReadOnlyList<AITool> Tools => _toolDispatcher.Tools;

    /// <summary>前缀缓存诊断（§7.4）：前缀指纹 / 命中未命中 token / 上次 miss 原因。</summary>
    public IReadOnlyList<ParticipantCacheReport> CacheReport() => _diagnostics.Report();

    /// <summary>登记一个与会者。主持人用 <c>role: host</c> 的角色卡。</summary>
    public Participant AddParticipant(RoleCard card)
    {
        var participant = _registry.Add(card);

        if (card.Role == ParticipantRole.Host)
        {
            lock (Gate)
            {
                _hostId = card.Id;
            }
        }

        return participant;
    }

    /// <summary>
    /// 开始会议。<b>立即返回</b>：会议是长跑的，开局（主持人决策 + 第一轮）在后台推进，
    /// 否则用户会眼睁睁看着一个 HTTP 请求跑到整场会议结束，期间没法暂停、补料或纠偏。
    /// </summary>
    public Task StartAsync(string? topic = null, CancellationToken cancellationToken = default)
    {
        MarkRunning("这场会议");

        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(topic))
            {
                _topic = topic.Trim();
            }
        }

        PublishState();
        _bus.Publish("meeting.started", new
        {
            topic = _topic,
            participants = _registry.Participants
                .Select(p => new { id = p.Id, name = p.Name, role = p.Role == ParticipantRole.Host ? "host" : "member" })
                .ToArray(),
        });

        StartBackground(RunOpeningAsync);
        return Task.CompletedTask;
    }

    /// <summary>后台推进：主持人开局 → 执行它的调度意图（点名/推进轮次/结束）。</summary>
    private async Task RunOpeningAsync()
    {
        try
        {
            await RunHostAsync(
                "会议刚开始。请决定第一轮由谁发言、任务是什么；也可以让全体并行讨论一轮。",
                LifetimeToken).ConfigureAwait(false);

            FallbackToDefaultRoundIfHostSilent();

            await ProcessHostIntentsAsync(LifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 会议被结束/进程退出：正常路径。
        }
        catch (Exception ex)
        {
            _bus.Publish("meeting.error", new { code = "run_loop", message = ex.Message });
        }
    }

    /// <summary>
    /// 问答同样进发言日志与 `transcript.jsonl`（`kind=interaction`）：重启后能回答"当时用户选了什么"（§14.6）。
    /// </summary>
    protected override Task OnInteractionResolvedAsync(PendingDecision decision, InteractionOutcome outcome) =>
        RecordSpeechAsync(
            new SpeechRecord(
                decision.Id,
                "user",
                "interaction",
                AskUserSpec.DescribeAnswers(outcome, decision.Questions),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

    /// <summary>
    /// 记一条发言：进内存日志（`recall_speech` 的检索源），同时落 `transcript.jsonl`。
    /// 落盘交给 <see cref="TranscriptWriter"/>（一路串行、`FileShare.Read`），它是 append-only 的审计面；
    /// 读回来只在启动阶段做一次（风险 9：不要在写入期间读它）。
    /// </summary>
    private async Task RecordSpeechAsync(SpeechRecord record, CancellationToken cancellationToken)
    {
        _speech.Append(record);

        if (_transcript is not null)
        {
            await _transcript
                .AppendAsync(
                    new TranscriptRecord(0, record.Kind, record.Author, record.Text, record.At, record.Id),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>会议结束时的收尾：把合并窗口里剩下的注入一次性应用，别让它们悬着。</summary>
    protected override async Task OnEndedAsync(CancellationToken cancellationToken)
    {
        if (_interrupt.DrainImmediately() is { } remaining)
        {
            await ApplySteerAsync(remaining, cancellationToken).ConfigureAwait(false);
        }

        _bus.Publish("meeting.ended", new { round = Round, materialVersion = _materials.Version });
    }

    /// <summary>用户发言。带 <c>@</c> 点名的会直接触发目标会话发言（§6.4「纠偏 Direct」）。</summary>
    public async Task SayAsync(
        string text,
        IReadOnlyList<string>? mentions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var record = new SpeechRecord($"user-{Guid.NewGuid():N}", "user", "speech", text, DateTimeOffset.UtcNow);
        await RecordSpeechAsync(record, cancellationToken).ConfigureAwait(false);
        _bus.Publish("message.committed", new
        {
            id = record.Id,
            author = "user",
            kind = "speech",
            text,
            mentions = mentions ?? [],
        });

        if (mentions is null || mentions.Count == 0)
        {
            return;
        }

        var targets = mentions
            .Distinct(StringComparer.Ordinal)
            .Select(_registry.Find)
            .Where(participant => participant is { Role: ParticipantRole.Member })
            .Select(participant => participant!)
            .ToArray();

        await Task.WhenAll(targets
            .Select(target => RunParticipantAsync(target, $"[用户指名] {text}", cancellationToken)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 补料/纠偏：内容进共享节（常驻），运行中的会话按打断策略处理（§6.4）。
    /// 走的是软注入——合并窗口到期后由泵统一应用。
    /// </summary>
    public Task BroadcastAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (Gate)
        {
            _userNotes.Add(text);
        }

        _bus.Publish("meeting.broadcast", new { text });
        _interrupt.Request(new SteerNotice("用户补料", $"[用户补充] {text}", DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    /// <summary>用户直接点名某个与会者发言（绕过主持人，§6.4）。</summary>
    public async Task NudgeAsync(string participantId, string task, CancellationToken cancellationToken = default)
    {
        var participant = _registry.Find(participantId)
            ?? throw new KeyNotFoundException($"没有这个与会者：{participantId}");

        await RunParticipantAsync(participant, task, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用户终审（采纳/打回/搁置）：采纳会触发对运行中会话的软注入。</summary>
    public async Task<MaterialTransition> ReviewByUserAsync(
        string materialId,
        ReviewDecision decision,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var transition = await _materials
            .ReviewAsync(materialId, decision, ReviewActorKind.User, note, cancellationToken)
            .ConfigureAwait(false);

        if (transition.Applied && transition.Card?.Status == MaterialStatus.Adopted)
        {
            OnMaterialAdopted(transition.Card);
        }

        return transition;
    }

    // ——— IHostActions ———

    public async Task<HostActionOutcome> ReviewMaterialAsync(
        string materialId,
        bool approve,
        string? note,
        CancellationToken cancellationToken)
    {
        var transition = await _materials
            .ReviewAsync(
                materialId,
                approve ? ReviewDecision.Approve : ReviewDecision.Reject,
                ReviewActorKind.Host,
                note,
                cancellationToken)
            .ConfigureAwait(false);

        return transition.Applied
            ? new HostActionOutcome(true, approve ? $"{materialId} 已通过，转用户终审。" : $"{materialId} 已驳回。")
            : new HostActionOutcome(false, transition.Rejection ?? "审核未生效。");
    }

    public async Task<HostActionOutcome> AskUserAsync(
        IReadOnlyList<InteractionQuestion> questions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var problems = AskUserSpec.Validate(questions, Interaction);

        if (problems.Count > 0)
        {
            // 回灌给模型而不是抛异常：它下一个 step 就能改好重发。
            return new HostActionOutcome(false, $"这次提问没有发出去：{string.Join("；", problems)}");
        }

        var normalized = AskUserSpec.Normalize(questions);
        var outcome = await RequestDecisionAsync(
            PendingDecisionKind.AskUser,
            normalized,
            turnId: ResolveHostTurnId(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (outcome is null)
        {
            // 交互模式关掉：不提问，退回全自动推进。
            return new HostActionOutcome(true, "当前不允许提问（interaction.enabled = false），按你自己的判断继续推进。");
        }

        MarkAskedThisRound();
        return new HostActionOutcome(true, AskUserSpec.DescribeAnswers(outcome, normalized));
    }

    private string? ResolveHostTurnId()
    {
        lock (Gate)
        {
            return _hostId is null ? null : _registry.Find(_hostId)?.Session.CurrentTurnId;
        }
    }

    /// <summary>主持人权限判定：工具内校验用（工具集对全员一致，权限只能在这里分）。</summary>
    private bool IsHostParticipant(string participantId)
    {
        lock (Gate)
        {
            return _hostId is not null && string.Equals(_hostId, participantId, StringComparison.Ordinal);
        }
    }

    public HostActionOutcome NudgeMember(string participantId, string task)
    {
        var participant = _registry.Find(participantId);
        if (participant is null)
        {
            return new HostActionOutcome(false, $"没有这个与会者：{participantId}");
        }

        if (participant.Role == ParticipantRole.Host)
        {
            return new HostActionOutcome(false, "主持人不需要给自己点名。");
        }

        Enqueue(new HostIntent("nudge", participantId, task));
        return new HostActionOutcome(true, $"已安排 {participant.Name} 发言（在本次决策结束后执行）。");
    }

    public HostActionOutcome AdvanceRound(string task)
    {
        Enqueue(new HostIntent("round", null, task));
        return new HostActionOutcome(true, "已安排进入下一轮（在本次决策结束后执行）。");
    }

    public HostActionOutcome DeclareGoalMet(string reason)
    {
        Enqueue(new HostIntent("goal", null, reason));
        return new HostActionOutcome(true, "已记录目标达成，将在本次决策结束后结束会议。");
    }

    // ——— 内部：轮次与 turn ———

    private async Task RunRoundAsync(string task, CancellationToken cancellationToken)
    {
        int round;
        lock (Gate)
        {
            if (Status == MeetingStatus.Ended)
            {
                return;
            }

            if (_round >= _options.MaxRounds)
            {
                round = -1;
            }
            else
            {
                _round++;
                round = _round;
            }
        }

        if (round < 0)
        {
            // 轮次上限：终止条件，避免"推进轮次 → 再评估 → 再推进"无限循环。
            await EndAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        BeginInteractionRound();
        _bus.Publish("meeting.round", new { round, task });

        var members = _registry.Participants
            .Where(p => p.Role == ParticipantRole.Member)
            .ToArray();

        if (_strategy.Dispatch == TurnDispatch.Sequential)
        {
            // 时序对话（§11.4-1）：一次一个，后者能看到前者刚说的话——
            // 全员并行会让 NPC 各说各的、重复回答同一句话。
            await RunSequentialRoundAsync(members, task, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(members
                .Select(member => RunParticipantAsync(member, task, cancellationToken)))
                .ConfigureAwait(false);
        }

        await RunHostAsync($"第 {round} 轮讨论结束。请评估是否已经收敛，决定继续推进还是结束。", cancellationToken)
            .ConfigureAwait(false);

        // 一轮讨论完、主持人调度之前收口交互（§14.5.1）：该问就问，用户先拍板再由主持人安排下一轮。
        await EnforceRoundInteractionAsync(cancellationToken).ConfigureAwait(false);

        FallbackToDefaultRoundIfHostSilent();

        await ProcessHostIntentsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>时序调度：按调度器的决定逐个发言（后者的上下文里已经能看到前者）。</summary>
    private async Task RunSequentialRoundAsync(
        IReadOnlyList<Participant> members,
        string task,
        CancellationToken cancellationToken)
    {
        var spoken = new List<string>();

        while (true)
        {
            var plan = _scheduler.Plan(new SchedulingContext
            {
                Candidates = members.Select(member => member.Id).ToArray(),
                Spoken = spoken,
            });

            if (plan.Count == 0)
            {
                return;
            }

            var speaker = members.FirstOrDefault(member =>
                string.Equals(member.Id, plan[0].ParticipantId, StringComparison.Ordinal));

            if (speaker is null)
            {
                return;
            }

            spoken.Add(speaker.Id);
            await RunParticipantAsync(speaker, task, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>把刚产出的一条发言记进"最近发言"（进共享节，供后续发言者参考）。</summary>
    private void RememberSpeech(string author, string text)
    {
        var condensed = text.Length > 200 ? text[..200] + "…" : text;

        lock (Gate)
        {
            _recentSpeeches.Add($"{author}：{condensed}");

            while (_recentSpeeches.Count > 8)
            {
                _recentSpeeches.RemoveAt(0);
            }
        }
    }

    private async Task RunParticipantAsync(
        Participant participant,
        string task,
        CancellationToken cancellationToken,
        bool allowCatchUp = true)
    {
        await WaitWhileBlockedAsync(cancellationToken).ConfigureAwait(false);

        if (Status == MeetingStatus.Ended)
        {
            return;
        }

        var composer = CreateComposer(participant.Id, participant.Card.Body, task);
        var request = new TurnRequest(participant.Id, task, _materials.Version, composer);

        var outcome = await _registry.RunTurnAsync(participant.Id, request, cancellationToken).ConfigureAwait(false);

        RecordCacheDiagnostics(participant.Id, composer.PrefixFingerprint, composer.RoleSectionFingerprint, outcome);

        if (!outcome.Produced)
        {
            return;
        }

        var turnId = participant.Session.CurrentTurnId ?? $"turn-{Round}";
        await RecordSpeechAsync(
            new SpeechRecord(turnId, participant.Id, "speech", outcome.Text, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        RememberSpeech(participant.Name, outcome.Text);

        _bus.Publish("message.committed", new
        {
            id = $"{participant.Id}-{turnId}",
            author = participant.Id,
            kind = "speech",
            text = outcome.Text,
            mentions = Array.Empty<string>(),
        });

        // 版本兜底（§6.3）：产出基于旧版资料 → 追加一轮，而不是丢弃产出。
        if (outcome.Stale && allowCatchUp)
        {
            await CatchUpAsync(participant, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>迟到产出的补轮：明确告诉这个会话"你思考期间资料变了"。</summary>
    private async Task CatchUpAsync(Participant participant, CancellationToken cancellationToken)
    {
        var notice =
            $"你思考期间资料已更新到版本 {_materials.Version}，请基于最新资料修订你的结论（只补差异，不必重述）。";

        _bus.Publish("interrupt.applied", new
        {
            participantId = participant.Id,
            kind = "catchup",
            reason = notice,
        });

        await RunParticipantAsync(participant, notice, cancellationToken, allowCatchUp: false).ConfigureAwait(false);
    }

    private async Task RunHostAsync(string situation, CancellationToken cancellationToken)
    {
        Participant? host;
        lock (Gate)
        {
            host = _hostId is null ? null : _registry.Find(_hostId);
        }

        if (host is null || Status == MeetingStatus.Ended)
        {
            return;
        }

        await WaitWhileBlockedAsync(cancellationToken).ConfigureAwait(false);

        var task = situation
            + " 必须**调用一个工具**来表达你的决定，不要只在正文里描述要调用什么。"
            + "调度偏好：能用 nudge_member 点名一两个人继续追问，就不要用 advance_round 全员重跑一轮——"
            + "会议靠多轮轮流交锋，而不是一轮里人人写长文。可选工具："
            + "nudge_member（点名某人发言）/ advance_round（全员再讨论一轮）"
            + " / approve_material / reject_material（审核资料）/ declare_goal_met（宣告目标达成）。"
            + "正文只用一两句话说明理由，然后立刻调用工具。";

        var request = new TurnRequest(
            host.Id,
            task,
            _materials.Version,
            CreateComposer(host.Id, host.Card.Body, task));

        var outcome = await _registry.RunTurnAsync(host.Id, request, cancellationToken).ConfigureAwait(false);

        // 主持人的"发言"是调度说明，属于第三列下半的主持人记录——不进讨论区（§6.5）。
        // 无论有没有产出都发：它同时是"主持人这一轮的流式结束"信号，前端据此收掉草稿。
        _bus.Publish("host.decision", new
        {
            action = "调度说明",
            participantId = host.Id,
            target = (string?)null,
            reason = outcome.Produced && !string.IsNullOrWhiteSpace(outcome.Text)
                ? outcome.Text.Trim()
                : "（本轮没有输出说明）",
        });
    }

    /// <summary>主持人是否给出了调度意图。</summary>
    private bool HasPendingHostIntents()
    {
        lock (Gate)
        {
            return _hostIntents.Count > 0;
        }
    }

    /// <summary>
    /// 默认推进力（设计文档 §6.4）：用户不输入时会议要能继续走，主持人这一轮没给出调度决定就兜底推进一轮；
    /// 循环由 <see cref="MeetingOptions.MaxRounds"/> 兜住。
    /// </summary>
    /// <summary>
    /// 记录一次缓存诊断：前缀指纹变了才可能是 miss，而"变了的原因"看本轮有没有消费注入
    /// （steer 之后的 token 必然失效）。见设计文档 §7.4。
    /// </summary>
    private void RecordCacheDiagnostics(
        string participantId,
        string prefixFingerprint,
        string roleFingerprint,
        TurnOutcome outcome)
    {
        string? missReason;

        lock (Gate)
        {
            var had = _lastPrefixFingerprint.TryGetValue(participantId, out var previous);
            _lastPrefixFingerprint[participantId] = prefixFingerprint;

            var steered = _steeredSinceLastTurn.Contains(participantId);

            missReason = !had
                ? "首次请求"
                : string.Equals(previous, prefixFingerprint, StringComparison.Ordinal)
                    ? null
                    : steered || outcome.SteerCount > 0
                        ? "steer"
                        : "资料版本更新";

            // 只有真正归因到 steer 时才消费掉标记：否则"首次请求"那一轮会把标记吃掉，
            // 后面真正的失效就再也归因不上了。
            if (missReason == "steer")
            {
                _steeredSinceLastTurn.Remove(participantId);
            }
        }

        _diagnostics.Record(
            participantId,
            prefixFingerprint,
            roleFingerprint,
            outcome.CacheHitTokens,
            outcome.CacheMissTokens,
            missReason);

        _bus.Publish("cache.diagnostics", new
        {
            id = participantId,
            prefix = prefixFingerprint[..12],
            cacheHitTokens = outcome.CacheHitTokens,
            cacheMissTokens = outcome.CacheMissTokens,
            lastMissReason = missReason,
        });
    }

    private void FallbackToDefaultRoundIfHostSilent()
    {
        if (Status != MeetingStatus.Running
            || HasPendingHostIntents()
            || !_registry.Participants.Any(p => p.Role == ParticipantRole.Member))
        {
            return;
        }

        Enqueue(new HostIntent(
            "round",
            null,
            "请就当前议题给出你的结论：先一句话结论，再给理由；"
            + "然后用 submit_material 把它提交成一份待审批资料（标题 + 一句话摘要 + 正文）。"));
        _bus.Publish("host.decision", new
        {
            action = "默认推进",
            participantId = (string?)null,
            target = (string?)null,
            reason = "主持人本轮没有给出调度决定，按默认推进力进入下一轮。",
        });
    }

    private async Task ProcessHostIntentsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            HostIntent? intent;

            lock (Gate)
            {
                intent = _hostIntents.Count > 0 ? _hostIntents.Dequeue() : null;
            }

            if (intent is null || Status == MeetingStatus.Ended)
            {
                return;
            }

            _bus.Publish("host.decision", new
            {
                action = intent.Kind switch
                {
                    "nudge" => "点名发言",
                    "round" => "推进轮次",
                    "goal" => "宣告达成",
                    _ => intent.Kind,
                },
                participantId = _hostId,
                target = intent.Target,
                reason = intent.Text,
            });

            switch (intent.Kind)
            {
                case "nudge" when intent.Target is not null && _registry.Find(intent.Target) is { } member:
                    await RunParticipantAsync(member, intent.Text, cancellationToken).ConfigureAwait(false);
                    break;

                case "round":
                    await RunRoundAsync(intent.Text, cancellationToken).ConfigureAwait(false);
                    break;

                case "goal":
                    await EndAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    // ——— 内部：软注入与装配 ———

    private async Task PumpInterruptsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pumpInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_interrupt.Drain(DateTimeOffset.UtcNow) is { } notice)
            {
                await ApplySteerAsync(notice, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>软注入优先：能注入就注入，注入不了就留给下一轮的共享节（记为 skipped）。</summary>
    private Task ApplySteerAsync(SteerNotice notice, CancellationToken cancellationToken)
    {
        foreach (var participant in _registry.Participants)
        {
            if (!participant.Session.IsRunning)
            {
                _bus.Publish("interrupt.skipped", new { participantId = participant.Id, reason = notice.Reason });
                continue;
            }

            if (participant.Session.TrySteer(notice.Text))
            {
                lock (Gate)
                {
                    // 记下来：这个会话下一次报诊断时，前缀失效要归因到 steer。
                    _steeredSinceLastTurn.Add(participant.Id);
                }

                _bus.Publish("interrupt.applied", new
                {
                    participantId = participant.Id,
                    kind = "steer",
                    reason = notice.Reason,
                });
            }
            else
            {
                _bus.Publish("interrupt.skipped", new { participantId = participant.Id, reason = notice.Reason });
            }
        }

        return Task.CompletedTask;
    }

    private void OnMaterialAdopted(MaterialCard card)
    {
        // 资料被采纳 → 对所有运行中的会话软注入（§6.2）；合并窗口负责把连击折成一次。
        _interrupt.Request(new SteerNotice(
            "资料版本更新",
            $"[新增已采纳资料] {card.Id}《{card.Title}》：{card.Summary}。请把它纳入你的判断。",
            DateTimeOffset.UtcNow));
    }

    private ContextComposer CreateComposer(string participantId, string roleSection, string task)
    {
        string shared;
        lock (Gate)
        {
            shared = BuildSharedSection();
        }

        // 常驻 / 触发式资料的选择（§7.2）：每 turn 选一次，冷却记录随之推进。
        var selection = _selector.Select(task, Math.Max(1, Round), _materials.List());

        // 这里曾发布 `context.selection`（跳过多少资料），但前后端都没有消费者——删掉这条死发布；
        // 真要做"资料预算诊断"时再按需要重新设计事件形状。

        return new ContextComposer(
            participantId,
            BuildSystemPrompt(),
            _toolDispatcher.Tools,
            _strategy,
            new ComposeRequest
            {
                SharedSection = shared,
                RoleSection = roleSection,
                Materials = selection.Entries
                    .Select(card => new MaterialIndexEntry(card, IndexLine(card)))
                    .ToList(),
            });
    }

    /// <summary>
    /// 共享 system prompt（所有会话字节一致）。这里把"发言要短、要多轮轮流"写进去：
    /// 会议的价值在于观点在轮次之间交锋，一条三千字的独白没人会读。
    /// </summary>
    private string BuildSystemPrompt()
    {
        if (_options.MaxSpeechChars <= 0)
        {
            return _options.SystemPrompt;
        }

        return _options.SystemPrompt
            + Environment.NewLine
            + Environment.NewLine
            + $"每次发言控制在 {_options.MaxSpeechChars} 字以内：只讲结论与关键理由，细节留给下一轮或资料。"
            + "宁可少说一点、多轮交换意见，也不要把一整篇塞进一次发言。";
    }

    private string BuildSharedSection()
    {
        var builder = new StringBuilder();
        builder.Append("议题：").Append(_topic);
        builder.Append('\n').Append("当前轮次：第 ").Append(Math.Max(1, _round)).Append(" 轮");
        builder.Append('\n').Append("资料版本：").Append(_materials.Version);

        if (_recentSpeeches.Count > 0)
        {
            // 最近发言进共享节：会议模式让与会者看到别人的立场，叙事模式让后者接得上前者（§11.4-1）。
            builder.Append('\n').Append('\n').Append("[最近发言]");

            foreach (var line in _recentSpeeches)
            {
                builder.Append('\n').Append("- ").Append(line);
            }
        }

        if (_userNotes.Count > 0)
        {
            builder.Append('\n').Append('\n').Append("[用户补充]");
            foreach (var note in _userNotes)
            {
                builder.Append('\n').Append("- ").Append(note);
            }
        }

        return builder.ToString();
    }

    private static string IndexLine(MaterialCard card) =>
        $"{card.Id}｜{card.Title}｜{card.Status.ToWire()}｜{card.Author}｜{card.Summary}｜{card.Path}";

    private void Enqueue(HostIntent intent)
    {
        lock (Gate)
        {
            _hostIntents.Enqueue(intent);
        }
    }

    /// <summary>全量快照（重连校准用，§8.4）。</summary>
    public object Snapshot() => SnapshotCore();

    protected override object SnapshotCore()
    {
        lock (Gate)
        {
            return new
            {
                status = Status.ToWire(),
                materialVersion = _materials.Version,
                round = _round,
                topic = _topic,
                participants = _registry.Participants
                    .Select(p => new
                    {
                        id = p.Id,
                        name = p.Name,
                        role = p.Role == ParticipantRole.Host ? "host" : "member",
                        status = p.Status.ToWire(),
                    })
                    .ToArray(),
                materials = _materials.List()
                    .Select(card => new
                    {
                        id = card.Id,
                        title = card.Title,
                        summary = card.Summary,
                        summarySource = card.SummarySource,
                        path = card.Path,
                        author = card.Author,
                        status = card.Status.ToWire(),
                        basedOnVersion = card.BasedOnVersion,
                        reviewNote = card.ReviewNote,
                        reviewer = card.Reviewer,
                    })
                    .ToArray(),
                // 还挂着没答复的待决事项（主持人提问）：前端重连后据此重建浮层。
                pendingDecisions = PendingDecisionsSnapshot(),
            };
        }
    }

    private sealed record HostIntent(string Kind, string? Target, string Text);
}
