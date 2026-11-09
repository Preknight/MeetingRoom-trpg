namespace MeetingRoom.Core.Participants;

/// <summary>与会者在会议里的角色：主持人 Agent = <see cref="Host"/>（同一张卡格式，仅权限不同）。</summary>
public enum ParticipantRole
{
    Host,
    Member,
}
