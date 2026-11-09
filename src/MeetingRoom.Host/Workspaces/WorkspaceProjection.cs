using MeetingRoom.Core.Workspaces;

namespace MeetingRoom.Host.Workspaces;

/// <summary>
/// 工作空间与会话的对外形状（设计方案 §15.7 的端点与事件共用同一份）。
/// 前端没有类型检查（风险 6），所以字段只在这里定义一次，端点与 SSE 事件都引它。
/// </summary>
public static class WorkspaceProjection
{
    public static object ToWire(WorkspaceDescriptor descriptor) => new
    {
        path = descriptor.Path,
        kind = descriptor.KindWire,
        name = descriptor.Name,
        createdAt = descriptor.CreatedAt,
        sessionCount = SessionCount(descriptor),
    };

    public static object SessionToWire(SessionDescriptor session) => new
    {
        id = session.Id,
        kind = session.KindWire,
        title = session.Title,
        createdAt = session.CreatedAt,
        topic = session.Topic,
        worldview = session.Worldview,
    };

    /// <summary>会话数：叙事型数 <c>stories/</c>、会议型数 <c>meetings/</c>；Legacy 两边都数。</summary>
    public static int SessionCount(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var layout = descriptor.Layout;

        return descriptor.Kind switch
        {
            WorkspaceKind.Roleplay => Count(layout.StoriesDir),
            WorkspaceKind.Legacy => Count(layout.MeetingsDir) + Count(layout.StoriesDir),
            _ => Count(layout.MeetingsDir),
        };
    }

    private static int Count(string directory) =>
        Directory.Exists(directory) ? Directory.GetDirectories(directory).Length : 0;
}
