namespace MeetingRoom.Core.Participants;

/// <summary>与会者状态（设计文档 §4.3），供第 4 列状态灯使用。</summary>
public enum ParticipantStatus
{
    Idle,
    Queued,
    Thinking,
    Reading,
    Writing,
    WaitingApproval,
    Interrupted,
    Failed,
}

internal static class ParticipantStatusExtensions
{
    /// <summary>事件与前端约定的取值：snake_case，与 <c>wwwroot/js/dom.js</c> 的状态标签一一对应。</summary>
    internal static string ToWire(this ParticipantStatus status) => status switch
    {
        ParticipantStatus.WaitingApproval => "waiting_approval",
        _ => status.ToString().ToLowerInvariant(),
    };
}
