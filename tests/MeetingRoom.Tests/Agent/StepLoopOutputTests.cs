using MeetingRoom.Agent;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Tests.Fakes;

namespace MeetingRoom.Tests.Agent;

/// <summary>
/// 产出的边界：讨论区的一条消息应当只等于"本轮最后一次发言"，
/// 不能把多 step turn 里伴随工具调用的过场说明也拼进去——否则讨论区会出现又长又杂的混合文本。
/// </summary>
public sealed class StepLoopOutputTests
{
    [Fact]
    public async Task OnlyTheFinalStepTextBecomesTheSpeech()
    {
        var narrations = new List<string>();

        var client = new FakeChatClient((call, ct) => call == 1
            ? FakeChatClient.TextThenToolCalls("我准备先查一下资料再回答。", 1, null, ct)
            : FakeChatClient.TextStream(1, TimeSpan.Zero, null, ct));

        var session = new AgentSession(
            new AgentSessionOptions
            {
                ParticipantId = "product-manager",
                Callbacks = new TurnCallbacks { ThoughtDelta = narrations.Add },
            },
            client);

        var outcome = await session.RunTurnAsync(Request());

        Assert.True(outcome.Produced);

        // 产出只含最后一步的正文；第一步的过场说明走后台记录，不进讨论区。
        Assert.Equal("块1", outcome.Text);
        Assert.Contains(
            narrations,
            item => item.Contains("我准备先查一下资料再回答。", StringComparison.Ordinal));
    }

    private static TurnRequest Request() =>
        new(
            "product-manager",
            "请给出结论。",
            0,
            new ContextComposer(
                "product-manager",
                "你是与会者。",
                [],
                new MeetingStrategy(),
                new ComposeRequest { SharedSection = "议题：定价", RoleSection = "产品经理视角" }));

    [Fact]
    public async Task SpeechesAreTrimmedToTheConfiguredLimit()
    {
        var session = new AgentSession(
            new AgentSessionOptions { ParticipantId = "product-manager", MaxSpeechChars = 10 },
            FakeChatClient.Text(chunks: 20, delay: TimeSpan.Zero));

        var outcome = await session.RunTurnAsync(Request());

        Assert.True(outcome.Text.Length <= 10, $"实际长度 {outcome.Text.Length}");
        Assert.EndsWith("…", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroLimitMeansNoTrimming()
    {
        var session = new AgentSession(
            new AgentSessionOptions { ParticipantId = "product-manager" },
            FakeChatClient.Text(chunks: 20, delay: TimeSpan.Zero));

        var outcome = await session.RunTurnAsync(Request());

        Assert.True(outcome.Text.Length > 10);
        Assert.DoesNotContain("…", outcome.Text, StringComparison.Ordinal);
    }
}
