using MeetingRoom.Core.Interaction;

namespace MeetingRoom.Tests.Interaction;

/// <summary>
/// 待决条目机制（阶段 9 / 批 4 的共同地基）：
/// 答复、跳过、超时、会话结束四条收口路径，以及"收口时一定给得出可用答案"这条不变式。
/// </summary>
public sealed class PendingDecisionRegistryTests
{
    [Fact]
    public async Task AnswerReturnsExactlyWhatTheUserPicked()
    {
        using var registry = new PendingDecisionRegistry();
        var decision = registry.Open(PendingDecisionKind.AskUser, [Question()]);

        Assert.True(registry.HasPending(decision.Id));
        Assert.True(registry.Answer(decision.Id, [new InteractionAnswer("choice", ["b"])]));

        var outcome = await decision.Completion;

        Assert.Equal(new[] { "b" }, outcome.Answers[0].Selected);
        Assert.False(outcome.TimedOut);
        Assert.False(outcome.Skipped);
        Assert.False(registry.HasPending(decision.Id));
    }

    [Fact]
    public async Task TimeoutFallsBackToTheDeclaredDefaultOption()
    {
        using var registry = new PendingDecisionRegistry();
        var decision = registry.Open(
            PendingDecisionKind.WorldState,
            [Question(defaultOption: "b")],
            timeoutSeconds: 1);

        var outcome = await decision.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // 超时不是"没人管"：它按默认选项收口，调用方照样拿到一份可直接用的答案。
        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Skipped);
        Assert.Equal(new[] { "b" }, outcome.Answers[0].Selected);
        Assert.False(registry.HasPending(decision.Id));
    }

    [Fact]
    public void ZeroTimeoutWaitsForever()
    {
        using var registry = new PendingDecisionRegistry();
        var decision = registry.Open(PendingDecisionKind.AskUser, [Question()], timeoutSeconds: 0);

        Assert.False(decision.Completion.IsCompleted);
        Assert.True(registry.HasPending(decision.Id));
    }

    [Fact]
    public async Task SkipClosesWithTheDefaultOptionAndKeepsTheReason()
    {
        using var registry = new PendingDecisionRegistry();
        var decision = registry.Open(PendingDecisionKind.AskUser, [Question(defaultOption: "b")]);

        Assert.True(registry.Skip(decision.Id, "用户跳过"));

        var outcome = await decision.Completion;

        Assert.True(outcome.Skipped);
        Assert.False(outcome.TimedOut);
        Assert.Equal("用户跳过", outcome.SkipReason);
        Assert.Equal(new[] { "b" }, outcome.Answers[0].Selected);
    }

    [Fact]
    public async Task CancelAllClosesEverythingStillWaiting()
    {
        using var registry = new PendingDecisionRegistry();
        var first = registry.Open(PendingDecisionKind.AskUser, [Question()]);
        var second = registry.Open(PendingDecisionKind.WorldState, [Question()], timeoutSeconds: 0);

        registry.CancelAll("会话已结束");

        // 没有这一刀，等在两件事上的 turn 会永远挂着。
        Assert.True((await first.Completion).Skipped);
        Assert.Equal("会话已结束", (await second.Completion).SkipReason);
        Assert.Empty(registry.OpenDecisions());
    }

    [Fact]
    public async Task AnsweringTwiceIsRefused()
    {
        using var registry = new PendingDecisionRegistry();
        var decision = registry.Open(PendingDecisionKind.AskUser, [Question()]);

        Assert.True(registry.Answer(decision.Id, [new InteractionAnswer("choice", ["a"])]));
        Assert.False(registry.Answer(decision.Id, [new InteractionAnswer("choice", ["b"])]));
        Assert.False(registry.Skip(decision.Id, "晚了"));

        // 第一次答复是有效的，后来者不会把它改写掉。
        Assert.Equal(new[] { "a" }, (await decision.Completion).Answers[0].Selected);
    }

    [Fact]
    public void OpenDecisionsExposesWhatIsStillWaiting()
    {
        using var registry = new PendingDecisionRegistry();
        var decision = registry.Open(PendingDecisionKind.AskUser, [Question()], turnId: "turn-7");

        var open = Assert.Single(registry.OpenDecisions());

        Assert.Equal(decision.Id, open.Id);
        Assert.Equal("turn-7", open.TurnId);
        Assert.Equal(PendingDecisionKind.AskUser, open.Kind);
    }

    [Fact]
    public void DefaultSelectionPrefersDeclaredThenFirstThenNothing()
    {
        var declared = PendingDecision.ResolveDefault([Question(defaultOption: "b")]);
        var first = PendingDecision.ResolveDefault([Question()]);
        var none = PendingDecision.ResolveDefault([Question(options: [])]);

        Assert.Equal(new[] { "b" }, declared[0].Selected);
        Assert.Equal(new[] { "a" }, first[0].Selected);
        Assert.Empty(none[0].Selected);
    }

    [Fact]
    public void DefaultSelectionFallsBackWhenTheDeclaredOptionDoesNotExist()
    {
        // 声明了一个不存在的选项属于调用方的笔误——宁可退回第一个选项，也不要给出一份空答案。
        var resolved = PendingDecision.ResolveDefault([Question(defaultOption: "nope")]);

        Assert.Equal(new[] { "a" }, resolved[0].Selected);
    }

    private static InteractionQuestion Question(string? defaultOption = null, IReadOnlyList<InteractionOption>? options = null) =>
        new(
            "choice",
            "选哪个？",
            options ??
            [
                new InteractionOption("a", "方案 A"),
                new InteractionOption("b", "方案 B"),
            ],
            defaultOption);
}
