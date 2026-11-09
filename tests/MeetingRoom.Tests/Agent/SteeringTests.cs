using MeetingRoom.Agent;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Tests.Fakes;

namespace MeetingRoom.Tests.Agent;

/// <summary>
/// 两级打断里的"软注入"（设计文档 §6.2）：TrySteer 只在 turn 运行中准入，
/// 在下一个 step 边界消费；没等到边界的注入用 HasUnappliedSteer 报告，交宿主补轮（§6.3）。
/// </summary>
public sealed class SteeringTests
{
    [Fact]
    public async Task TrySteerIsAcceptedOnlyWhileTurnIsRunning()
    {
        var acceptedWhileRunning = false;
        AgentSession? session = null;

        var client = FakeChatClient.Text(
            chunks: 3,
            delay: TimeSpan.FromMilliseconds(1),
            onChunk: i =>
            {
                if (i == 1)
                {
                    acceptedWhileRunning = session!.TrySteer("mat-001 已采纳，请基于最新资料修订");
                }
            });

        session = CreateSession(client);
        var outcome = await session.RunTurnAsync(Request());

        Assert.True(outcome.Produced);

        // 运行中 → 准入（true）。
        Assert.True(acceptedWhileRunning);

        // turn 结束后 → 不接纳（false），调用方应改走"补充轮"。
        Assert.False(session.TrySteer("turn 已经结束了"));

        // 上面那条注入发生在最后一个 step 边界之后，直到 turn 结束都没被消费 → 报告为未应用。
        Assert.True(session.HasUnappliedSteer);
        Assert.Equal("mat-001 已采纳，请基于最新资料修订", Assert.Single(session.TakeUnappliedSteer()));
        Assert.False(session.HasUnappliedSteer);
    }

    [Fact]
    public async Task SteerArrivingDuringAStepIsConsumedAtTheNextBoundary()
    {
        var applied = new List<string>();
        AgentSession? session = null;

        var client = new FakeChatClient((call, ct) => call == 1
            // 第一个 step：先吐一段正文，再要一个工具调用 → 循环会进入下一个 step。
            ? FakeChatClient.TextThenToolCalls("我先查一下资料。", 1, () => session!.TrySteer("新增资料 mat-002"), ct)
            : FakeChatClient.TextStream(3, TimeSpan.Zero, null, ct));

        session = CreateSession(client, new TurnCallbacks { SteerApplied = applied.Add });
        var outcome = await session.RunTurnAsync(Request());

        Assert.True(outcome.Produced);
        Assert.Equal(2, client.StreamCalls);

        // 注入在第二个 step 边界被消费（Reasonix 的同款位置），因此没有"未应用"残留。
        Assert.Equal("新增资料 mat-002", Assert.Single(applied));
        Assert.False(session.HasUnappliedSteer);
    }

    private static AgentSession CreateSession(FakeChatClient client, TurnCallbacks? callbacks = null) =>
        new(
            new AgentSessionOptions
            {
                ParticipantId = "product-manager",
                Callbacks = callbacks ?? new TurnCallbacks(),
            },
            client);

    private static TurnRequest Request() =>
        new(
            "product-manager",
            "请给出本轮结论。",
            0,
            new ContextComposer(
                "product-manager",
                "你是与会者。",
                [],
                new MeetingStrategy(),
                new ComposeRequest { SharedSection = "议题：定价", RoleSection = "产品经理视角" }));
}
