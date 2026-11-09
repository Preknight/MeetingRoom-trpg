namespace MeetingRoom.Core.Vtt;

/// <summary>
/// 地图状态的<b>对外形状</b>（<c>GET /api/roleplay/vtt</c> 与 <c>roleplay.state</c> 快照共用一份）：
/// 前端第 1 列据它显示"地图已连接 / 最后更新 / 标尺"。
/// <para>形状必须两边一致——前端是纯事件归约，同一个端点返回两种形状会把它搞坏。</para>
/// </summary>
public static class VttStatusView
{
    /// <summary>把一份快照投影成端点 / 快照字段。不可用时带 <c>reason</c>，其余字段仍是安全空值。</summary>
    public static object Project(VttSnapshot snapshot, bool enabled, bool includeHidden = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new
        {
            enabled,
            // 导演要看得出"这一列没有藏东西"：它是配置项，不是快照里的字段。
            includeHidden,
            available = snapshot.Available,
            reason = snapshot.UnavailableReason,
            receivedAt = snapshot.ReceivedAt,
            lastUpdated = snapshot.ReceivedAtTime,
            sceneReady = snapshot.SceneReady,
            grid = new
            {
                dpi = snapshot.Grid.Dpi,
                type = snapshot.Grid.Type,
                measurement = snapshot.Grid.Measurement.ToString().ToLowerInvariant(),
                multiplier = snapshot.Grid.Multiplier,
                unit = snapshot.Grid.Unit,
            },
            tokens = snapshot.Tokens.Select(token => new
            {
                id = token.Id,
                name = token.Name,
                x = token.X,
                y = token.Y,
                visible = token.Visible,
            }).ToArray(),
            selected = snapshot.Selected,
            rulers = snapshot.Rulers.Select(ruler => new
            {
                id = ruler.Id,
                measurement = ruler.Measurement,
            }).ToArray(),
        };
    }

    /// <summary>没有地图 / 没有会话时的空形状（字段与真实形状对齐）。</summary>
    public static object Idle(string reason, bool enabled = false, bool includeHidden = false) =>
        Project(VttSnapshot.Unavailable(reason), enabled, includeHidden);
}
