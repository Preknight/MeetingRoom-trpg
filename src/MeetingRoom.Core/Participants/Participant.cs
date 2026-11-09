using MeetingRoom.Agent;
using MeetingRoom.Core.Roles;

namespace MeetingRoom.Core.Participants;

/// <summary>
/// 与会者（设计文档 §4.1）：角色卡 + 独立会话 + 状态。
/// 主持人 Agent 只是 <c>Role = Host</c> 的与会者——共用同一个 <see cref="AgentSession"/> 实现，
/// 差别只在权限与产出类型（§6.5）。
/// </summary>
public sealed class Participant
{
    internal Participant(RoleCard card, AgentSession session)
    {
        Card = card;
        Session = session;
    }

    public string Id => Card.Id;

    public string Name => Card.Name;

    public ParticipantRole Role => Card.Role;

    public RoleCard Card { get; }

    public AgentSession Session { get; }

    public ParticipantStatus Status { get; internal set; } = ParticipantStatus.Idle;

    /// <summary>已完成的 turn 数（诊断用）。</summary>
    public int CompletedTurns { get; internal set; }
}
