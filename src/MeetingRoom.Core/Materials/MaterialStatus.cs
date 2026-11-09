namespace MeetingRoom.Core.Materials;

/// <summary>资料六态（设计文档 §4.2）：<c>draft → host_review → user_review → adopted | rejected | parked</c>。</summary>
public enum MaterialStatus
{
    Draft,
    HostReview,
    UserReview,
    Adopted,
    Rejected,
    Parked,
}

internal static class MaterialStatusExtensions
{
    /// <summary>事件与前端约定的取值：snake_case，与 <c>wwwroot/js/dom.js</c> 的资料状态标签一一对应。</summary>
    internal static string ToWire(this MaterialStatus status) => status switch
    {
        MaterialStatus.HostReview => "host_review",
        MaterialStatus.UserReview => "user_review",
        _ => status.ToString().ToLowerInvariant(),
    };

    internal static MaterialStatus FromWire(string value) => value switch
    {
        "draft" => MaterialStatus.Draft,
        "host_review" => MaterialStatus.HostReview,
        "user_review" => MaterialStatus.UserReview,
        "adopted" => MaterialStatus.Adopted,
        "rejected" => MaterialStatus.Rejected,
        "parked" => MaterialStatus.Parked,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "未知的资料状态。"),
    };
}
