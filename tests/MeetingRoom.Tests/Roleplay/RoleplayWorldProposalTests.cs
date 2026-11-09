using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 世界状态的提议回路（设计文档 §11.4 第 6b 项）：说书人<b>只能提议</b>，导演拍板才落盘；
/// 超时按"采用"收口（用户拍板）；驳回与结算失败都不改世界状态，而正文照旧（§11.3-4）。
/// </summary>
public sealed class RoleplayWorldProposalTests
{
    [Fact]
    public async Task OnlyTheNarratorCarriesTheControlTools()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), new EventBus());

        var narrator = session.Participants.Single(participant => participant.Id == "narrator");
        var actor = session.Participants.Single(participant => participant.Id == "ella");

        Assert.NotEmpty(narrator.Session.Tools);
        // NPC 一个工具都不带：工具调用会产生元话语、破坏沉浸（§11.5-2）。
        Assert.Empty(actor.Session.Tools);
    }

    [Fact]
    public async Task ApprovingTheProposalAppliesTheWorldState()
    {
        var bus = new EventBus();
        await using var session = NewSession(ProposingNarrator(), bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "等导演拍板");

        Assert.True(session.AnswerPendingDecision(
            PendingId(bus),
            [new InteractionAnswer(RoleplaySession.WorldStateQuestionId, [RoleplaySession.ApplyOptionId])]));

        // 落盘与广播是同一条链上的两步：等两者都到齐再断言，别抢在事件之前。
        await WaitForWorldAsync(session, bus);

        Assert.Equal("灯塔顶层", session.World.Current.Place);
        Assert.True(session.World.Current.Characters["ella"].Present);

        var world = bus.Recent(200).Last(ev => ev.Type == "roleplay.world");
        Assert.True(world.Payload!["applied"]!.GetValue<bool>());
        Assert.False(world.Payload!["timedOut"]!.GetValue<bool>());
    }

    /// <summary>提议的两个选项都要带一行说明：用户在浮层里只能凭这两行字决定。</summary>
    [Fact]
    public async Task EveryProposalOptionCarriesADescription()
    {
        var bus = new EventBus();
        await using var session = NewSession(ProposingNarrator(), bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "等导演拍板");

        var asked = bus.Recent(200).Last(ev => ev.Type == "interaction.request").Payload!;

        Assert.Equal(2, asked["questions"]![0]!["options"]!.AsArray().Count);
        Assert.All(
            asked["questions"]![0]!["options"]!.AsArray(),
            option => Assert.False(
                string.IsNullOrWhiteSpace(option!["description"]?.GetValue<string>()),
                $"选项「{option!["label"]?.GetValue<string>()}」缺少 description"));
    }

    [Fact]
    public async Task RejectingTheProposalKeepsTheWorldState()
    {
        var bus = new EventBus();
        await using var session = NewSession(ProposingNarrator(), bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "等导演拍板");

        Assert.True(session.AnswerPendingDecision(
            PendingId(bus),
            [new InteractionAnswer(RoleplaySession.WorldStateQuestionId, [RoleplaySession.RejectOptionId])]));

        // 等的是"这次提议的结论"本身（interaction.resolved 先发，roleplay.world 紧随其后）。
        await WaitForAsync(
            () => bus.Recent(200).Any(ev => ev.Type == "roleplay.world"),
            "这次提议已经收口");

        // 说书人不能自己落盘剧情决策：导演说"不改"就是不改。
        Assert.Equal(0, session.World.Version);
        Assert.Equal("暴雨夜，灯塔看守人倒在塔下。", session.World.Current.Place);

        var world = bus.Recent(200).Last(ev => ev.Type == "roleplay.world");
        Assert.False(world.Payload!["applied"]!.GetValue<bool>());
    }

    /// <summary>超时按"采用"收口（用户拍板）：导演不在场时宁可让剧情连续，也不要停在半截。</summary>
    [Fact]
    public async Task TimeoutAppliesTheProposal()
    {
        var bus = new EventBus();
        await using var session = NewSession(
            ProposingNarrator(),
            bus,
            new InteractionOptions { TimeoutSeconds = 1 });

        await session.StartAsync();
        await WaitForAsync(
            () => session.World.Version >= 1 && bus.Recent(200).Any(ev => ev.Type == "roleplay.world"),
            "超时后按采用收口并广播",
            TimeSpan.FromSeconds(15));

        var world = bus.Recent(200).Last(ev => ev.Type == "roleplay.world");
        Assert.True(world.Payload!["applied"]!.GetValue<bool>());
        Assert.True(world.Payload!["timedOut"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnInvalidProposalIsReportedAndLeavesTheWorldStateAlone()
    {
        var bus = new EventBus();
        // 提议让一个没登记过的角色进场：结算整次不生效（原子性），但正文照旧。
        await using var session = NewSession(ProposingNarrator(place: "灯塔顶层", enter: "ghost"), bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "等导演拍板");

        session.AnswerPendingDecision(
            PendingId(bus),
            [new InteractionAnswer(RoleplaySession.WorldStateQuestionId, [RoleplaySession.ApplyOptionId])]);

        // 等的是"这次提议的结论"本身：interaction.resolved 先发，roleplay.world 紧随其后。
        await WaitForAsync(
            () => bus.Recent(200).Any(ev => ev.Type == "roleplay.world"),
            "这次提议的结论已经出来");

        Assert.Equal(0, session.World.Version);

        // 结算失败要广播出去：导演也该看得见"这次提议没生效、为什么"（只在工具回执里等于只有模型知道）。
        var world = bus.Recent(200).Last(ev => ev.Type == "roleplay.world");
        Assert.False(world.Payload!["applied"]!.GetValue<bool>());

        var errors = world.Payload!["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray();
        Assert.Contains(errors, message => message.Contains("未知角色", StringComparison.Ordinal));
    }

    private static FakeChatClient ProposingNarrator(string place = "灯塔顶层", string enter = "ella") =>
        new((call, cancellationToken) => call == 1
            ? FakeChatClient.ToolCallStream(
                Narrator.ProposeWorldState,
                new Dictionary<string, object?>
                {
                    ["place"] = place,
                    ["enter"] = new[] { enter },
                    ["reason"] = "他们上了楼",
                },
                cancellationToken)
            : FakeChatClient.TextStream(1, TimeSpan.Zero, null, cancellationToken));

    /// <summary>运行期 data/roles/ 里那张 <c>host.md</c> 是会议主持人：叙事里它不该出场。</summary>
    [Fact]
    public async Task AHostCardThatIsNotTheNarratorStaysOutOfTheScene()
    {
        await using var session = NewSession(
            FakeChatClient.Text(1, TimeSpan.Zero),
            new EventBus(),
            cast: ["ella"],
            extraCards:
            [
                new RoleCard { Id = "host", Name = "主持人", Role = ParticipantRole.Host, Body = "你主持一场会议。" },
            ]);

        var characters = session.World.Current.Characters;

        // 会议主持人卡没在卡司里 → 它不在场（按 role 判定时它会凭空出现在场景里并参与接话）。
        Assert.False(characters["host"].Present);
        // 说书人始终在场（它控场），卡司里的角色在场。
        Assert.True(characters["narrator"].Present);
        Assert.True(characters["ella"].Present);
    }

    private static RoleplaySession NewSession(
        IChatClient client,
        IEventBus bus,
        InteractionOptions? interaction = null,
        IReadOnlyList<string>? cast = null,
        IReadOnlyList<RoleCard>? extraCards = null)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" },
            .. extraCards ?? [],
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                NarratorId = "narrator",
                Cast = cast ?? [],
                Interaction = interaction ?? new InteractionOptions(),
            },
            client,
            bus,
            cards);
    }

    /// <summary>世界状态落盘与应用事件都到齐。</summary>
    private static Task WaitForWorldAsync(RoleplaySession session, EventBus bus) =>
        WaitForAsync(
            () => session.World.Version >= 1 && bus.Recent(200).Any(ev => ev.Type == "roleplay.world"),
            "世界状态落盘并广播");

    private static string PendingId(EventBus bus) =>
        bus.Recent(200).First(ev => ev.Type == "interaction.request").Payload!["id"]!.GetValue<string>();

    private static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"等待「{what}」超时。");
            }

            await Task.Delay(10);
        }
    }
}
