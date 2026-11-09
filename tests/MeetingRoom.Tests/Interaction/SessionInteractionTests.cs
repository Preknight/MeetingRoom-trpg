using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;

namespace MeetingRoom.Tests.Interaction;

/// <summary>
/// 待决事项接进编排内核之后的行为（设计文档 §14.4）：等答复期间呈 <see cref="MeetingStatus.AwaitingInput"/>、
/// 不发起新 turn，答复 / 结束都收口干净，事件照 §8.3 发。
/// </summary>
public sealed class SessionInteractionTests
{
    [Fact]
    public async Task AwaitingInputBlocksNewTurnsUntilTheAnswerArrives()
    {
        var bus = new EventBus();
        await using var session = new ProbeSession(bus);
        session.Start();

        var asking = session.AskAsync([Question]);
        var pendingId = PendingId(bus);

        Assert.Equal(MeetingStatus.AwaitingInput, session.Status);

        // 门就是"发起新 turn 之前的那道检查"（会议 / 叙事每次派 turn 都过它）。
        var blocked = session.WaitBlockedAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted, "等答复期间不该放行新 turn");

        Assert.True(session.AnswerPendingDecision(pendingId, [new InteractionAnswer("choice", ["b"])]));
        var outcome = await asking.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MeetingStatus.Running, session.Status);
        Assert.False(outcome!.TimedOut);
        Assert.Equal(new[] { "b" }, outcome.Answers[0].Selected);

        await blocked.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            new[] { "interaction.request", "interaction.resolved" },
            bus.Recent(20).Select(e => e.Type).Where(t => t.StartsWith("interaction", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SkippingClosesWithTheDefaultOption()
    {
        var bus = new EventBus();
        await using var session = new ProbeSession(bus);
        session.Start();

        var asking = session.AskAsync([Question with { DefaultOptionId = "b" }]);
        var pendingId = PendingId(bus);

        Assert.True(session.SkipPendingDecision(pendingId, "用户跳过"));

        var outcome = await asking.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(outcome!.Skipped);
        Assert.Equal("用户跳过", outcome.SkipReason);
        Assert.Equal(new[] { "b" }, outcome.Answers[0].Selected);
        Assert.Equal(MeetingStatus.Running, session.Status);
    }

    [Fact]
    public async Task EndingTheSessionClosesWhateverIsStillWaiting()
    {
        var bus = new EventBus();
        await using var session = new ProbeSession(bus);
        session.Start();

        var asking = session.AskAsync([Question]);

        await session.StopAsync();

        var outcome = await asking.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(outcome);
        Assert.True(outcome!.Skipped);
        Assert.False(session.HasPendingDecision(outcome.Id));
    }

    [Fact]
    public async Task DisabledInteractionModeAsksNothingAndKeepsRunning()
    {
        var bus = new EventBus();
        await using var session = new ProbeSession(bus, new InteractionOptions { Enabled = false });
        session.Start();

        var outcome = await session.AskAsync([Question]);

        Assert.Null(outcome);
        Assert.Equal(MeetingStatus.Running, session.Status);
        Assert.DoesNotContain(
            bus.Recent(20),
            e => e.Type.StartsWith("interaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TooManyOptionsIsAnImplementationError()
    {
        var bus = new EventBus();
        await using var session = new ProbeSession(bus, new InteractionOptions { MaxOptions = 2 });
        session.Start();

        var tooMany = Question with
        {
            Options =
            [
                new InteractionOption("a", "A"),
                new InteractionOption("b", "B"),
                new InteractionOption("c", "C"),
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => session.AskAsync([tooMany]));
        Assert.Equal(MeetingStatus.Running, session.Status);
    }

    /// <summary>快照里带上了还挂着的事项（前端重连后据此重建浮层与第 3 列）。</summary>
    [Fact]
    public async Task SnapshotCarriesThePendingDecisions()
    {
        var bus = new EventBus();
        await using var session = new ProbeSession(bus);
        session.Start();

        var asking = session.AskAsync([Question]);

        // 快照里那份"还挂着的事项"就是 SnapshotCore 用的同一个来源。
        Assert.Single(session.PendingInSnapshot());

        session.AnswerPendingDecision(PendingId(bus), [new InteractionAnswer("choice", ["a"])]);
        await asking.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(session.PendingInSnapshot());
    }

    private static InteractionQuestion Question => new(
        "choice",
        "选哪个？",
        [
            new InteractionOption("a", "方案 A"),
            new InteractionOption("b", "方案 B"),
        ]);

    private static string PendingId(EventBus bus) =>
        bus.Recent(20)
            .First(e => e.Type == "interaction.request")
            .Payload!["id"]!
            .GetValue<string>();

    /// <summary>最小会话外壳：只把内核的待决机制暴露给测试。</summary>
    private sealed class ProbeSession : SessionCore
    {
        private readonly EventBus _bus;
        private readonly InteractionOptions _interaction;

        public ProbeSession(EventBus bus, InteractionOptions? interaction = null)
        {
            _bus = bus;
            _interaction = interaction ?? new InteractionOptions();
        }

        protected override IEventBus Bus => _bus;

        protected override string StateEventType => "probe.state";

        protected override InteractionOptions Interaction => _interaction;

        protected override object SnapshotCore() => new
        {
            status = Status.ToWire(),
            pending = PendingDecisionsSnapshot(),
        };

        public void Start() => MarkRunning("这场探测");

        public Task StopAsync() => EndAsync();

        public Task<InteractionOutcome?> AskAsync(
            IReadOnlyList<InteractionQuestion> questions,
            PendingDecisionKind kind = PendingDecisionKind.AskUser,
            CancellationToken cancellationToken = default) =>
            RequestDecisionAsync(kind, questions, turnId: "turn-1", cancellationToken: cancellationToken);

        public Task WaitBlockedAsync(CancellationToken cancellationToken) =>
            WaitWhileBlockedAsync(cancellationToken);

        /// <summary>快照里那份待决事项（<see cref="SnapshotCore"/> 用的就是它）。</summary>
        public object[] PendingInSnapshot() => (object[])PendingDecisionsSnapshot();
    }
}
