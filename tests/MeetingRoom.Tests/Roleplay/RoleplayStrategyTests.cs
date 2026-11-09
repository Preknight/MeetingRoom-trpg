using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 可见性与知识披露（设计文档 §11.4-3）+ 打断策略反转（§11.5-1）：
/// 带标签的资料只有"知道该标签"的角色能看到；叙事模式不因状态更新打断角色说话。
/// </summary>
public sealed class RoleplayStrategyTests
{
    [Fact]
    public void TaggedMaterialIsOnlyVisibleToThoseWhoKnowTheTag()
    {
        var strategy = new RoleplayStrategy(
        [
            new RoleplayActor("ella", "艾拉") { KnownTags = new HashSet<string>(["秘密:钥匙"], StringComparer.Ordinal) },
            new RoleplayActor("ran", "冉") { KnownTags = new HashSet<string>(StringComparer.Ordinal) },
        ]);

        var secret = Material("mat-001", "钥匙的来历", "秘密:钥匙");
        var open = Material("mat-002", "小镇地图");

        Assert.True(strategy.VisibleTo("ella", secret));
        Assert.False(strategy.VisibleTo("ran", secret));
        Assert.True(strategy.VisibleTo("ran", open));
    }

    [Fact]
    public void ActorWithSeveralTagsNeedsAllOfThem()
    {
        var strategy = new RoleplayStrategy(
        [
            new RoleplayActor("ella", "艾拉") { KnownTags = new HashSet<string>(["秘密:钥匙"], StringComparer.Ordinal) },
        ]);

        var both = Material("mat-003", "钥匙与身世", "秘密:钥匙", "秘密:身世");

        Assert.False(strategy.VisibleTo("ella", both));
    }

    [Fact]
    public void RevealGrantsKnowledge()
    {
        var strategy = new RoleplayStrategy([new RoleplayActor("ran", "冉")]);
        var secret = Material("mat-001", "钥匙的来历", "秘密:钥匙");

        Assert.False(strategy.VisibleTo("ran", secret));

        strategy.Reveal("ran", "秘密:钥匙");

        Assert.True(strategy.VisibleTo("ran", secret));
    }

    [Fact]
    public void NarrativesNeverInterruptOnMaterialChange()
    {
        var roleplay = new RoleplayStrategy([new RoleplayActor("ella", "艾拉")]);
        var meeting = new MeetingStrategy();

        Assert.False(roleplay.InterruptsOnMaterialChange);
        Assert.True(meeting.InterruptsOnMaterialChange);
        Assert.Equal(TurnDispatch.Sequential, roleplay.Dispatch);
        Assert.Equal(CommitBoundary.PlayerTurnCompleted, roleplay.CommitBoundary);
    }

    [Fact]
    public void OnlyPresentActorsAreCandidates()
    {
        var strategy = new RoleplayStrategy(
        [
            new RoleplayActor("ella", "艾拉"),
            new RoleplayActor("ran", "冉") { Present = false },
        ]);

        Assert.Equal(["ella"], strategy.PresentActors.Select(actor => actor.Id));
    }

    private static MaterialCard Material(string id, string title, params string[] tags) => new()
    {
        Id = id,
        Title = title,
        Summary = $"{title}的摘要",
        Path = $"materials/{id}.md",
        Author = "narrator",
        Status = MaterialStatus.Adopted,
        Tags = tags,
    };
}
