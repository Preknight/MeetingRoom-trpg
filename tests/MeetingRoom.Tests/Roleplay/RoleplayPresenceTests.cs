using MeetingRoom.Core.Events;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 在场与长程记忆的接线（设计文档 §11.4-3 / §11.4-7）：
/// 在场名单的权威是<b>世界状态</b>（进场 / 离场由说书人提议、导演确认），
/// 每条正文带上"这一幕的见证者"，角色的私有节带上他自己记得的事。
/// </summary>
public sealed class RoleplayPresenceTests
{
    [Fact]
    public async Task LinesCarryTheActorsWhoWerePresent()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        var line = Assert.Single(session.Timeline.Turns[^1].Lines);

        Assert.Contains("narrator", line.Witnesses);
        Assert.Contains("ella", line.Witnesses);
        Assert.Contains("ran", line.Witnesses);
    }

    [Fact]
    public async Task AnActorWhoLeftStopsSpeaking()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        // 让艾拉离场：她不该再接话——在场名单的权威在世界状态，不在构造时那份卡司。
        Assert.True(session.World.Apply(new WorldStateMutation { Leave = ["ella"] }).Applied);

        await session.SayAsync("我推门出去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var authors = session.Timeline.Turns[^1].Lines.Select(line => line.Author).ToArray();

        Assert.NotEmpty(authors);
        Assert.DoesNotContain("ella", authors);
        Assert.Contains("ran", authors);
    }

    [Fact]
    public async Task AnActorWhoJoinedGetsToSpeak()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), cast: ["ran"]);

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        // 一开始只有冉在场；让艾拉进场后，她就该接得上话。
        Assert.True(session.World.Apply(new WorldStateMutation { Enter = ["ella"] }).Applied);

        await session.SayAsync("我朝门口喊了一声。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var authors = session.Timeline.Turns[^1].Lines.Select(line => line.Author).ToArray();

        Assert.Contains("ella", authors);
    }

    [Fact]
    public async Task ThePrivateSectionCarriesWhatThatActorRemembers()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        session.World.Apply(new WorldStateMutation
        {
            Knowledge = [new KnowledgeDelta("ella", "看守人不是意外坠塔")],
        });

        var mine = session.PrivateSectionFor("ella");
        var shared = session.BuildSharedSection();

        // 他自己的那份：已知信息 + 他记得的事。
        Assert.Contains("看守人不是意外坠塔", mine, StringComparison.Ordinal);
        Assert.Contains("[你记得的事]", mine, StringComparison.Ordinal);
        // 近窗口的原文也在（假客户端写的开场正文）。
        Assert.Contains("块1", mine, StringComparison.Ordinal);

        // 共享节是所有会话字节一致的那一段：秘密不能进。
        Assert.DoesNotContain("看守人不是意外坠塔", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("[你记得的事]", shared, StringComparison.Ordinal);
    }

    private static RoleplaySession NewSession(IChatClient client, IReadOnlyList<string>? cast = null)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" },
            new RoleCard { Id = "ran", Name = "冉", Body = "路过的水手。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "雨夜。灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                NarratorId = "narrator",
                Cast = cast ?? [],
            },
            client,
            new EventBus(),
            cards);
    }

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
