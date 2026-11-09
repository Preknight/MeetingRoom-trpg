using MeetingRoom.Agent;
using MeetingRoom.Agent.Tools;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Agent;

/// <summary>
/// 三个取消点（设计文档 §6.1 / 实现步骤 4.2）：
/// A 流式读取中、B 每个工具调用前、C 等待连接/首字节。取消必须立刻生效、且不把异常抛给宿主。
/// </summary>
public sealed class StepCancellationTests
{
    [Fact]
    public async Task CancelDuringStreamingStopsImmediatelyAndCanRestart()
    {
        using var first = new CancellationTokenSource();
        var client = FakeChatClient.Text(
            chunks: 50,
            delay: TimeSpan.FromMilliseconds(2),
            onChunk: i =>
            {
                if (i == 10)
                {
                    first.Cancel();
                }
            });

        var session = CreateSession(client);

        // ① 调用被中断：抛出的取消被吸收，没有泄漏到宿主。
        var cancelled = await session.RunTurnAsync(Request(), first.Token);

        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.Produced);
        Assert.Equal(1, client.StreamCalls);
        Assert.False(session.IsRunning);

        // ② 已产出的文本被丢弃：历史保持干净，所以可以按新基准重新发起。
        Assert.Empty(session.History);

        using var second = new CancellationTokenSource();
        var restarted = await session.RunTurnAsync(Request(), second.Token);

        Assert.True(restarted.Produced);
        Assert.Equal(2, client.StreamCalls);
        Assert.Single(session.History);
    }

    [Fact]
    public async Task CancelBeforeSecondToolCallSkipsRemainingTools()
    {
        using var cts = new CancellationTokenSource();
        var dispatcher = new RecordingToolDispatcher(onInvoke: _ => cts.Cancel());
        var session = CreateSession(FakeChatClient.ToolCalls(2), dispatcher);

        var outcome = await session.RunTurnAsync(Request(), cts.Token);

        Assert.True(outcome.Cancelled);

        // 取消点 B：第二个工具调用之前就被拦下，不会出现"资料已过期还去写文件"。
        Assert.Single(dispatcher.Invocations);
    }

    [Fact]
    public async Task CancelWhileWaitingForFirstByteProducesNothing()
    {
        using var cts = new CancellationTokenSource();
        var deltas = new List<string>();
        var client = FakeChatClient.Text(chunks: 5, delay: TimeSpan.FromMilliseconds(400));
        var session = CreateSession(client, callbacks: new TurnCallbacks { Delta = deltas.Add });

        var running = session.RunTurnAsync(Request(), cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        var outcome = await running;

        Assert.True(outcome.Cancelled);
        Assert.Empty(deltas);
        Assert.Equal(1, client.StreamCalls);
        Assert.True(session.IsRunning == false);
    }

    private static AgentSession CreateSession(
        IChatClient client,
        IToolDispatcher? tools = null,
        TurnCallbacks? callbacks = null) =>
        new(
            new AgentSessionOptions
            {
                ParticipantId = "product-manager",
                MaxStepsPerTurn = 8,
                Callbacks = callbacks ?? new TurnCallbacks(),
            },
            client,
            tools);

    private static TurnRequest Request() =>
        new(
            "product-manager",
            "请给出本轮结论。",
            BasedOnMaterialVersion: 0,
            new ContextComposer(
                "product-manager",
                "你是与会者。",
                [],
                new MeetingStrategy(),
                new ComposeRequest { SharedSection = "议题：定价", RoleSection = "产品经理视角" }));
}
