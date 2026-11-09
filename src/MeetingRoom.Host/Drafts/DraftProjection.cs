using MeetingRoom.Core.Drafts;

namespace MeetingRoom.Host.Drafts;

/// <summary>
/// 待整理条目与已归档清单的对外形状（设计方案 §15.6 的端点与事件共用同一份）。
/// 前端没有类型检查（风险 6），字段只在这里定义一次。
/// </summary>
public static class DraftProjection
{
    public static object ToWire(DraftEntry entry) => new
    {
        id = entry.Id,
        kind = entry.KindWire,
        title = entry.Title,
        createdAt = entry.CreatedAt,
    };

    public static object ToWire(ArchivedItem item) => new
    {
        id = item.Id,
        kind = item.KindWire,
        title = item.Title,
        path = item.Path,
        bytes = item.Bytes,
    };
}
