using MeetingRoom.Core.Events;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 世界状态的两个视图（设计文档 §11.4-3 / §11.4-6）：
/// <b>公共视图</b>进共享节（时间 / 地点 / 在场），<b>私有视图</b>只进角色自己的角色节。
/// "谁知道什么"是角色的秘密，绝不能进共享节——那既是保密要求，也是前缀缓存的要求。
/// </summary>
public sealed class RoleplayWorldStateViewTests
{
    [Fact]
    public void PublicViewCarriesTimePlaceAndPresenceButNoSecrets()
    {
        var world = NewWorld();

        var view = world.SnapshotPublic();

        Assert.Contains("时间：深夜 23:40", view, StringComparison.Ordinal);
        Assert.Contains("地点：侦探事务所", view, StringComparison.Ordinal);
        Assert.Contains("在场：", view, StringComparison.Ordinal);
        Assert.Contains("法医", view, StringComparison.Ordinal);

        // 秘密（谁知道什么）与私人细节一概不进公共视图。
        Assert.DoesNotContain("已知", view, StringComparison.Ordinal);
        Assert.DoesNotContain("看守人不是意外坠塔", view, StringComparison.Ordinal);
        Assert.DoesNotContain("好感", view, StringComparison.Ordinal);
        Assert.DoesNotContain("手套", view, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateViewCarriesOnlyYourOwnDetails()
    {
        var world = NewWorld();

        var mine = world.SnapshotFor("coroner");
        var theirs = world.SnapshotFor("detective-assistant");

        Assert.Contains("看守人不是意外坠塔", mine, StringComparison.Ordinal);
        Assert.Contains("手套", mine, StringComparison.Ordinal);
        Assert.Contains("好感 5", mine, StringComparison.Ordinal);

        // 别人的秘密不给：助手那边看不到法医知道什么。
        Assert.DoesNotContain("看守人不是意外坠塔", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain("手套", theirs, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateViewIsEmptyWhenThereIsNothingToSay()
    {
        var world = NewWorld();

        // 一个没有属性 / 持有物 / 已知的角色：给空串，不占 token。
        Assert.Empty(world.SnapshotFor("client"));
        Assert.Empty(world.SnapshotFor("nobody"));
        Assert.Empty(world.SnapshotFor(string.Empty));
    }

    [Fact]
    public void FullViewStillCarriesEverything()
    {
        // 全量视图留给说书人（它控场要看"谁知道什么"）与第 1 列展示。
        var view = NewWorld().Snapshot();

        Assert.Contains("时间：深夜 23:40", view, StringComparison.Ordinal);
        Assert.Contains("已知：看守人不是意外坠塔", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedSectionHasTheSceneButNoSecretsWhileThePrivateSectionHasThem()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));
        ApplyScene(session);

        var shared = session.BuildSharedSection();
        var mine = session.PrivateSectionFor("ella");

        // 共享节：所有会话字节一致，所以只能有"谁都能看见的现场"。
        Assert.Contains("[此刻的现场]", shared, StringComparison.Ordinal);
        Assert.Contains("时间：深夜 23:40", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("看守人不是意外坠塔", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("好感", shared, StringComparison.Ordinal);

        // 角色节：他自己的那份（属性 / 持有 / 已知）。
        Assert.Contains("看守人不是意外坠塔", mine, StringComparison.Ordinal);
        Assert.Contains("好感 5", mine, StringComparison.Ordinal);

        // 说书人不走这条：它的全量视图已经在 Narrator.BuildSystemPrompt 里了。
        Assert.Empty(session.PrivateSectionFor("narrator"));
    }

    private static WorldStateStore NewWorld()
    {
        var store = new WorldStateStore(
            [
                new CharacterState("coroner", "法医"),
                new CharacterState("detective-assistant", "侦探助手") { Present = false },
                new CharacterState("client", "委托人"),
            ],
            time: "深夜 23:40",
            place: "侦探事务所");

        var result = store.Apply(new WorldStateMutation
        {
            Attributes = [new AttributeDelta("coroner", "好感", 5)],
            Items = [new ItemDelta("coroner", "手套")],
            Knowledge = [new KnowledgeDelta("coroner", "看守人不是意外坠塔")],
        });

        Assert.True(result.Applied, string.Join("；", result.Errors));
        Assert.Equal(1, result.State.Version);

        return store;
    }

    private static void ApplyScene(RoleplaySession session)
    {
        var result = session.World.Apply(new WorldStateMutation
        {
            Time = "深夜 23:40",
            Attributes = [new AttributeDelta("ella", "好感", 5)],
            Knowledge = [new KnowledgeDelta("ella", "看守人不是意外坠塔")],
        });

        Assert.True(result.Applied, string.Join("；", result.Errors));
    }

    private static RoleplaySession NewSession(IChatClient client)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                NarratorId = "narrator",
            },
            client,
            new EventBus(),
            cards);
    }
}
