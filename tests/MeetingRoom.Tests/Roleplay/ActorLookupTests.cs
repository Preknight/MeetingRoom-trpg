using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 角色 id 的查找口径（§26 端到端踩到的坑）：说书人常把 <c>Ella</c> 写进工具参数，
/// 而角色卡 id 是 <c>ella</c>——查找必须大小写不敏感，否则"角色明明在场却查不到"。
/// </summary>
public sealed class ActorLookupTests
{
    [Theory]
    [InlineData("ella")]
    [InlineData("Ella")]
    [InlineData("ELLA")]
    public void ActorLookupIgnoresCase(string id)
    {
        var strategy = new RoleplayStrategy([new RoleplayActor("ella", "Ella")]);

        Assert.NotNull(strategy.Actor(id));
        Assert.Equal("ella", strategy.Actor(id)!.Id);
    }

    [Fact]
    public void UnknownActorStillReturnsNull()
    {
        var strategy = new RoleplayStrategy([new RoleplayActor("ella", "Ella")]);

        Assert.Null(strategy.Actor("goblin"));
    }
}
