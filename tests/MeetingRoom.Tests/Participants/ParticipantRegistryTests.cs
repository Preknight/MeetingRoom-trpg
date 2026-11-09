using MeetingRoom.Agent;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Participants;

/// <summary>
/// 与会者运行时与并发闸门（实现步骤 5.1 的验收）：
/// <c>maxConcurrentTurns = 2</c> 时第 3 个与会者进入 queued，前一个结束后自动开始；
/// 状态 / 思考 / 工具事件都从 EventBus 出去（供唯一那条 SSE 推给前端）。
/// </summary>
public sealed class ParticipantRegistryTests
{
    [Fact]
    public async Task ThirdParticipantQueuesThenStartsAutomatically()
    {
        var bus = new EventBus();

        // 一个 turn ≈ 5 块 × 100ms ≈ 500ms。
        var client = FakeChatClient.Text(chunks: 5, delay: TimeSpan.FromMilliseconds(100));
        var registry = new ParticipantRegistry(
            new ParticipantRegistryOptions { MaxConcurrentTurns = 2, Bus = bus },
            client);

        registry.Add(Card("a"));
        registry.Add(Card("b"));
        registry.Add(Card("c"));

        var a = registry.RunTurnAsync("a", Request(registry, "a"));
        var b = registry.RunTurnAsync("b", Request(registry, "b"));
        await Task.Delay(60);

        var c = registry.RunTurnAsync("c", Request(registry, "c"));
        await Task.Delay(150);

        // 并发上限已满：第 3 个在排队，前两个在跑。
        Assert.Equal(ParticipantStatus.Queued, registry.Find("c")!.Status);
        Assert.Equal(ParticipantStatus.Thinking, registry.Find("a")!.Status);
        Assert.Equal(ParticipantStatus.Thinking, registry.Find("b")!.Status);

        await Task.WhenAll(a, b, c);

        // 槽位一释放就自动开始，并正常跑完。
        var third = registry.Find("c")!;
        Assert.Equal(ParticipantStatus.Idle, third.Status);
        Assert.Equal(1, third.CompletedTurns);
        Assert.All(registry.Participants, p => Assert.Equal(ParticipantStatus.Idle, p.Status));
    }

    [Fact]
    public async Task StatusAndDeltaEventsReachTheBus()
    {
        var bus = new EventBus();
        var registry = new ParticipantRegistry(
            new ParticipantRegistryOptions { MaxConcurrentTurns = 1, Bus = bus },
            FakeChatClient.Text(chunks: 2, delay: TimeSpan.Zero));

        registry.Add(Card("product-manager"));

        await registry.RunTurnAsync("product-manager", Request(registry, "product-manager"));

        var events = bus.Recent(100);
        var statuses = events
            .Where(e => e.Type == "participant.status")
            .Select(e => e.Payload!["status"]!.GetValue<string>())
            .ToList();

        // 事件取值是前端约定的 snake_case（wwwroot/js/dom.js 的状态灯直接吃这个值）。
        Assert.Contains("thinking", statuses);
        Assert.Contains("idle", statuses);
        Assert.Contains(events, e => e.Type == "participant.delta");
    }

    [Fact]
    public async Task ThoughtEventsCarryTheTurnId()
    {
        var bus = new EventBus();
        var registry = new ParticipantRegistry(
            new ParticipantRegistryOptions { MaxConcurrentTurns = 1, Bus = bus },
            FakeChatClient.ThoughtThenText("先想想定价的边界。", "先做两档。"));

        registry.Add(Card("engineer"));

        await registry.RunTurnAsync("engineer", Request(registry, "engineer"));

        var thought = Assert.Single(bus.Recent(50), e => e.Type == "participant.thought");
        Assert.Equal("先想想定价的边界。", thought.Payload!["text"]!.GetValue<string>());
        Assert.Equal("turn-1", thought.Payload!["turnId"]!.GetValue<string>());
        Assert.Equal("engineer", thought.Payload!["id"]!.GetValue<string>());
    }

    [Fact]
    public void DuplicateParticipantIdIsRejected()
    {
        var registry = new ParticipantRegistry(
            new ParticipantRegistryOptions(),
            FakeChatClient.Text(chunks: 1, delay: TimeSpan.Zero));

        registry.Add(Card("a"));

        Assert.Throws<InvalidOperationException>(() => registry.Add(Card("a")));
    }

    [Fact]
    public void CarriesHostToolsDecidesWhoGetsTheHostToolSet()
    {
        AIFunction hostTool = AIFunctionFactory.Create(() => "host", "host_only");
        AIFunction memberTool = AIFunctionFactory.Create(() => "member", "member_only");

        var registry = new ParticipantRegistry(
            new ParticipantRegistryOptions
            {
                Tools = [hostTool],
                MemberTools = [memberTool],
                // 叙事模式的传法：按 id 认导演，而不是看角色卡上的 role: host。
                CarriesHostTools = card => card.Id == "director",
            },
            FakeChatClient.Text(chunks: 1, delay: TimeSpan.Zero));

        registry.Add(Card("director"));   // 注意：这张卡上的 Role 仍是 Member
        registry.Add(Card("ella"));

        Assert.Equal(new[] { "host_only" }, registry.Find("director")!.Session.Tools.Select(tool => tool.Name));
        Assert.Equal(new[] { "member_only" }, registry.Find("ella")!.Session.Tools.Select(tool => tool.Name));
    }

    private static RoleCard Card(string id) => new()
    {
        Id = id,
        Name = id,
        Body = $"你是 {id}。",
    };

    private static TurnRequest Request(ParticipantRegistry registry, string id) => new(
        id,
        "请给出本轮结论。",
        0,
        registry.CreateComposer(
            id,
            [],
            new MeetingStrategy(),
            new ComposeRequest { SharedSection = "议题：定价" }));
}
