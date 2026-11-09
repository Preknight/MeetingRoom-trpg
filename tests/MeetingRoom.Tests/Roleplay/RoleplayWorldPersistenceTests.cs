using System.Text.Json;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 世界状态的落盘与重放（设计文档 §11.4-2 / §11.4-6）：成功结算追加一条 <c>kind=world</c> 行，
/// 重开一场时按序重放——"此刻的时间 / 地点 / 在场"与剧情一致；重放失败要说话，不能静默。
/// </summary>
public sealed class RoleplayWorldPersistenceTests
{
    [Fact]
    public async Task WorldStateSurvivesARestart()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");
        var bus = new EventBus();

        long revision;

        await using (var first = NewSession(ProposingNarrator(), path, bus))
        {
            await first.StartAsync();
            await WaitForAsync(() => first.Status == MeetingStatus.AwaitingInput, "等导演拍板");

            first.AnswerPendingDecision(PendingId(bus), Answer(RoleplaySession.ApplyOptionId));
            await WaitForWorldAsync(first, bus);
            // 世界状态落盘之后，说书人还要把开场正文写完：那是另一条落盘路径。
            await WaitForAsync(() => HasWrittenOpening(first), "开场正文落盘");

            revision = first.Revision;
        }

        // 重开一场（同一份 story.jsonl）：世界状态按 kind=world 行重建。
        await using var second = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), path, new EventBus());

        Assert.Equal(1, second.World.Version);
        Assert.Equal("灯塔顶层", second.World.Current.Place);

        var ella = second.World.Current.Characters["ella"];
        Assert.True(ella.Present);
        Assert.Contains("黄铜钥匙", ella.Items);
        Assert.Contains("看守人不是意外坠塔", ella.Known);

        // 剧情时间线也一起回来了。
        Assert.Equal(revision, second.Revision);
    }

    [Fact]
    public async Task AnAppliedChangeLandsInTheJournalNextToTheTurns()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");
        var bus = new EventBus();

        await using var session = NewSession(ProposingNarrator(), path, bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "等导演拍板");

        session.AnswerPendingDecision(PendingId(bus), Answer(RoleplaySession.ApplyOptionId));
        await WaitForWorldAsync(session, bus);
        await WaitForAsync(() => HasWrittenOpening(session), "开场正文落盘");

        var lines = File.ReadAllLines(path);
        var world = lines.Where(line => line.Contains("\"kind\":\"world\"", StringComparison.Ordinal)).ToArray();

        // 两种行同文件、同序：回合同样在，世界状态也在。
        Assert.NotEmpty(world);
        Assert.Contains("灯塔顶层", string.Join('\n', world), StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("\"kind\":\"turn\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARejectedProposalIsNotWrittenToTheJournal()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");
        var bus = new EventBus();

        await using var session = NewSession(ProposingNarrator(), path, bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "等导演拍板");

        session.AnswerPendingDecision(PendingId(bus), Answer(RoleplaySession.RejectOptionId));
        await WaitForAsync(
            () => bus.Recent(200).Any(ev => ev.Type == "roleplay.world"),
            "这次提议已经收口");

        // 没生效就没什么可重放的：日志里不该留下一条"假变更"。
        Assert.Equal(0, session.World.Version);
        Assert.DoesNotContain(
            File.ReadAllLines(path),
            line => line.Contains("\"kind\":\"world\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReplayThatCannotBeAppliedIsReported()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");
        var bus = new EventBus();

        // 先按真实链路落一条"艾拉进场、拿到钥匙"的变更。
        await using (var first = NewSession(ProposingNarrator(), path, bus))
        {
            await first.StartAsync();
            await WaitForAsync(() => first.Status == MeetingStatus.AwaitingInput, "等导演拍板");

            first.AnswerPendingDecision(PendingId(bus), Answer(RoleplaySession.ApplyOptionId));
            await WaitForWorldAsync(first, bus);
        }

        // 再开同一场戏，但这次场里没有艾拉（相当于角色卡被删掉）：那条变更重放不了。
        var replayBus = new EventBus();
        await using var second = NewSession(
            FakeChatClient.Text(1, TimeSpan.Zero),
            path,
            replayBus,
            withElla: false);

        // 恢复不需要完美，但不能静默：状态没变，并且有一条错误事件说明是哪一条重放不了。
        Assert.Equal(0, second.World.Version);
        Assert.Contains(
            replayBus.Recent(20),
            ev => ev.Type == "roleplay.error" && ev.Payload!["code"]!.GetValue<string>() == "world_replay");
    }

    private static FakeChatClient ProposingNarrator() =>
        new((call, cancellationToken) => call == 1
            ? FakeChatClient.ToolCallStream(
                Narrator.ProposeWorldState,
                new Dictionary<string, object?>
                {
                    ["place"] = "灯塔顶层",
                    ["enter"] = new[] { "ella" },
                    ["items"] = JsonDocument
                        .Parse("""[{"character":"ella","item":"黄铜钥匙","remove":false}]""").RootElement,
                    ["knowledge"] = JsonDocument
                        .Parse("""[{"character":"ella","fact":"看守人不是意外坠塔"}]""").RootElement,
                    ["reason"] = "他们上了楼",
                },
                cancellationToken)
            : FakeChatClient.TextStream(1, TimeSpan.Zero, null, cancellationToken));

    private static InteractionAnswer[] Answer(string option) =>
        [new InteractionAnswer(RoleplaySession.WorldStateQuestionId, [option])];

    private static string PendingId(EventBus bus) =>
        bus.Recent(200).First(ev => ev.Type == "interaction.request").Payload!["id"]!.GetValue<string>();

    private static RoleplaySession NewSession(
        IChatClient client,
        string storyPath,
        IEventBus bus,
        bool withElla = true)
    {
        var cards = new List<RoleCard>
        {
            new() { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
        };

        if (withElla)
        {
            cards.Add(new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" });
        }

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                NarratorId = "narrator",
                StoryPath = storyPath,
            },
            client,
            bus,
            cards);
    }

    /// <summary>开场这一轮真的写完了（带着正文落进时间线），而不只是 revision 变了。</summary>
    private static bool HasWrittenOpening(RoleplaySession session)
    {
        var turns = session.Timeline.Turns;
        return turns.Count > 0 && turns[^1].Lines.Count > 0;
    }

    /// <summary>世界状态改了内存里的版本还不够——落盘与广播都在其后，等三样都到齐再断言。</summary>
    private static Task WaitForWorldAsync(RoleplaySession session, IEventBus bus) =>
        WaitForAsync(
            () => session.World.Version >= 1 && bus.Recent(200).Any(ev => ev.Type == "roleplay.world"),
            "世界状态落盘并广播");

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

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
