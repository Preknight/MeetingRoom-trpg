using System.Runtime.CompilerServices;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 工具集按模式下发（设计文档 §11.5-2）：会议模式全员一致；
/// 叙事模式 NPC 一个都不带——工具调用会产生元话语、破坏沉浸；只有导演带控场工具。
/// </summary>
public sealed class NarrativeToolPolicyTests
{
    [Fact]
    public void MeetingModeGivesEveryParticipantTheSameTools()
    {
        var registry = Build(new MeetingStrategy());

        var host = registry.Add(Card("host", ParticipantRole.Host));
        var member = registry.Add(Card("ella", ParticipantRole.Member));

        Assert.NotEmpty(host.Session.Tools);
        Assert.Equal(host.Session.Tools.Count, member.Session.Tools.Count);
    }

    [Fact]
    public void RoleplayModeLeavesNpcsWithoutTools()
    {
        var registry = Build(new RoleplayStrategy([new RoleplayActor("ella", "艾拉")]));

        var director = registry.Add(Card("director", ParticipantRole.Host));
        var npc = registry.Add(Card("ella", ParticipantRole.Member));

        Assert.NotEmpty(director.Session.Tools);
        Assert.Empty(npc.Session.Tools);
    }

    [Fact]
    public void StrategiesDeclareThePolicyExplicitly()
    {
        var roleplay = new RoleplayStrategy([new RoleplayActor("ella", "艾拉")]);
        IOrchestrationStrategy meeting = new MeetingStrategy();

        Assert.True(meeting.MembersCarryTools);
        Assert.False(roleplay.MembersCarryTools);
        Assert.Equal("director", roleplay.NarratorId);
        Assert.NotEmpty(Narrator.ToolNames);
    }

    /// <summary>装配一个注册表：编排层正是按策略的 <c>MembersCarryTools</c> 决定成员工具集的。</summary>
    private static ParticipantRegistry Build(IOrchestrationStrategy strategy)
    {
        var tools = new List<AITool> { AIFunctionFactory.Create(() => "ok", "demo") };

        return new ParticipantRegistry(
            new ParticipantRegistryOptions
            {
                Tools = tools,
                MemberTools = strategy.MembersCarryTools ? tools : [],
            },
            new FakeChatClient((_, _) => Empty()));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static RoleCard Card(string id, ParticipantRole role) => new()
    {
        Id = id,
        Name = id,
        Role = role,
        Body = "测试角色。",
    };
}
