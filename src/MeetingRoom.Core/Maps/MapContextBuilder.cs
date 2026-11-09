using System.Text;

namespace MeetingRoom.Core.Maps;

/// <summary>
/// 把地图文档压缩成**说书人能算的一段话**（M3，设计文档 §5）。
/// <para>
/// 三条硬规则：<b>只给 <c>visible != false</c> 的 token</b>；距离同时给"格 + 尺"；
/// 两点之间隔着墙或关着的门时**必须说出来**，不能只给一个会误导的直线距离。
/// 总长控制在 <see cref="MaxChars"/> 以内，超出按"离焦点角色最近"裁剪。
/// </para>
/// </summary>
public static class MapContextBuilder
{
    /// <summary>总长度上限（字符）：一张图通常 2–6 个房间、3–8 个 token，够用且不撑爆上下文。</summary>
    public const int MaxChars = 200;

    // 下标 = atan2(-dy, dx) / 45°：0° 是正东，逆时针（y 向下，所以 dy 取负）。
    private static readonly string[] CompassNames =
        ["正东", "东北", "正北", "西北", "正西", "西南", "正南", "东南"];

    /// <summary>
    /// 生成地图段。<paramref name="focusActorId"/> 是当前行动者（用于相对方位与裁剪优先级）；
    /// 没有地图或没有任何可见 token 时返回空串——<b>调用方据此不输出这一段</b>。
    /// </summary>
    public static string Build(MapDocument document, string? focusActorId = null, string? preferredActorId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var dpi = document.Grid?.Dpi ?? 0;
        var feetPerCell = document.Grid?.Scale ?? 5;
        var visible = document.Tokens.Where(token => token.Visible).ToArray();

        if (dpi <= 0 || visible.Length == 0)
        {
            return string.Empty;
        }

        // 参照物优先级：① 战斗中当前行动者 ② 玩家角色 ③ 按 id 排序的第一个可见 token。
        // 第 ③ 条**必须排序**：直接取 visible[0] 会随 tokens 数组顺序变，同一张图两次生成的段就不一样了。
        var focus = FindById(visible, focusActorId)
            ?? FindById(visible, preferredActorId)
            ?? visible.OrderBy(token => token.Id, StringComparer.Ordinal).First();
        var rooms = document.Rooms ?? [];
        var blockers = (document.Walls ?? []).Where(BlocksSight).ToArray();

        var header = $"【地图】{document.Name}（格宽 {Round(dpi)}px = {Round(feetPerCell)} 尺）";
        var roomLines = DescribeRooms(rooms, dpi);
        var roomsPart = roomLines.Count > 0 ? "房间：" + string.Join("；", roomLines) : string.Empty;

        // 角色行的预算 = 上限 − 标题 − 房间行 − 两个换行。先给足，超了再按"离焦点最近"裁。
        var budget = MaxChars - header.Length - roomsPart.Length - 2;
        var actorLine = DescribeActors(visible, rooms, blockers, focus, dpi, feetPerCell, Math.Max(budget, 40));

        var text = new StringBuilder(header);

        if (roomsPart.Length > 0)
        {
            text.Append('\n').Append(roomsPart);
        }

        if (actorLine.Length > 0)
        {
            text.Append('\n').Append(actorLine);
        }

        if (DescribeZones(document) is { Length: > 0 } zoneLine)
        {
            text.Append('\n').Append(zoneLine);
        }

        if (DescribeFog(document) is { Length: > 0 } fogLine)
        {
            text.Append('\n').Append(fogLine);
        }

        return Fit(text.ToString());
    }

    private static MapToken? FindById(IReadOnlyList<MapToken> tokens, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : tokens.FirstOrDefault(token => string.Equals(token.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 区域概括（M7）：**不逐点**，只给名字 / 类型 / 大致尺寸 / 备注。
    /// 隐藏区域也写给说书人（它该知道 DM 藏了什么），角色侧要到 M9 才考虑。
    /// </summary>
    private static string DescribeZones(MapDocument document)
    {
        var zones = document.Zones ?? [];

        if (zones.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        foreach (var zone in zones.Take(6))
        {
            var shape = zone.Shape?.Type == "polygon"
                ? $"多边形 {zone.Shape.Points?.Count ?? 0} 点"
                : $"圆 {Round(zone.Shape?.RadiusCells ?? 0)} 格";
            var note = string.IsNullOrWhiteSpace(zone.Note) ? string.Empty : $"，{zone.Note.Trim()}";
            var hidden = zone.Visible ? string.Empty : "（隐藏）";

            parts.Add($"{zone.Name}（{MapZoneKinds.Describe(zone.Kind)}，{shape}{note}）{hidden}");
        }

        if (zones.Count > 6)
        {
            parts.Add($"另有 {zones.Count - 6} 个区域");
        }

        return "区域：" + string.Join("；", parts) + "。";
    }

    /// <summary>
    /// 迷雾概括（M6b）：说书人要知道"哪些地方还没揭开"才提得出合理的揭示建议。
    /// **不能逐格列举**（一张图上千格会直接吃掉上下文预算），只给计数 + 未探索区域偏在哪一侧。
    /// </summary>
    private static string DescribeFog(MapDocument document)
    {
        if (document.Fog is not { Enabled: true } fog || document.Grid is not { Dpi: > 0 } grid)
        {
            return string.Empty;
        }

        var width = document.Image?.Width ?? MapStore.BlankWidth;
        var height = document.Image?.Height ?? MapStore.BlankHeight;
        var columns = (int)Math.Ceiling(Math.Max(0, width - grid.OffsetX) / grid.Dpi);
        var rows = (int)Math.Ceiling(Math.Max(0, height - grid.OffsetY) / grid.Dpi);

        if (columns <= 0 || rows <= 0)
        {
            return string.Empty;
        }

        var explored = new HashSet<GridCell>(fog.ExploredCells);
        var visibleCount = fog.VisibleCells.Count;
        var total = columns * rows;
        var unexplored = total - explored.Count;

        if (unexplored <= 0)
        {
            return $"迷雾：全图已探索（{explored.Count} 格），当前视野 {visibleCount} 格。";
        }

        // 未探索格按四个方位累计，取最多的两侧做概括（不逐格列举）。
        var buckets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["北"] = 0, ["南"] = 0, ["西"] = 0, ["东"] = 0,
        };

        for (var y = 0; y < rows; y += 1)
        {
            for (var x = 0; x < columns; x += 1)
            {
                if (explored.Contains(new GridCell(x, y)))
                {
                    continue;
                }

                buckets[y < rows / 2 ? "北" : "南"] += 1;
                buckets[x < columns / 2 ? "西" : "东"] += 1;
            }
        }

        var where = string.Join(
            "与",
            buckets
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(2)
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key));

        return $"迷雾：已探索 {explored.Count} 格（共 {total} 格），当前视野 {visibleCount} 格；"
            + $"还有 {unexplored} 格未探索，集中在{where}侧。";
    }

    /// <summary>只有真正挡视线的东西才算"隔"：墙，以及关着的门（窗不算）。</summary>
    private static bool BlocksSight(MapWall wall) =>
        wall.Kind == MapWallKinds.Wall
        || (wall.Kind == MapWallKinds.Door && !wall.Open);

    private static List<string> DescribeRooms(IReadOnlyList<MapRoom> rooms, double dpi)
    {
        var lines = new List<string>();

        foreach (var room in rooms)
        {
            if (room.Polygon is null || room.Polygon.Count < 3)
            {
                continue;
            }

            var width = room.Polygon.Max(point => point.X) - room.Polygon.Min(point => point.X);
            var height = room.Polygon.Max(point => point.Y) - room.Polygon.Min(point => point.Y);
            var cells = $"{Math.Round(width / dpi):0}×{Math.Round(height / dpi):0} 格";
            var light = LightText(room.Light);
            var note = string.IsNullOrWhiteSpace(room.Note) ? string.Empty : $"，{room.Note.Trim()}";

            lines.Add($"{room.Name}（约 {cells}，{light}{note}）");
        }

        return lines;
    }

    private static string DescribeActors(
        IReadOnlyList<MapToken> tokens,
        IReadOnlyList<MapRoom> rooms,
        IReadOnlyList<MapWall> blockers,
        MapToken? focus,
        double dpi,
        double feetPerCell,
        int budget)
    {
        // 焦点第一；其余按距离，**距离相同按 id**（否则同一张图两次渲染顺序会抖）。
        var order = tokens
            .OrderBy(token => ReferenceEquals(token, focus) ? -1 : 0)
            .ThenBy(token => focus is null ? 0 : Distance(token, focus))
            .ThenBy(token => token.Id, StringComparer.Ordinal)
            .ToArray();

        var parts = new List<string>();

        foreach (var token in order)
        {
            var room = RoomOf(rooms, token);
            var placement = DescribePlacement(token, room, rooms, blockers, focus, dpi, feetPerCell);
            parts.Add($"{token.Name}{placement}");
        }

        var line = "角色：" + string.Join("；", parts);

        if (line.Length <= budget)
        {
            return line;
        }

        // 太长：按上面的顺序留前几个，其余收成一句"另有 N 个目标在别处"。
        var kept = new List<string>();
        var pending = parts.Count;

        foreach (var part in parts)
        {
            var candidate = "角色：" + string.Join("；", kept.Append(part)) + $"；另有 {pending - kept.Count - 1} 个目标在别处。";

            if (candidate.Length > budget)
            {
                break;
            }

            kept.Add(part);
        }

        var hidden = parts.Count - kept.Count;
        var tail = hidden > 0 ? $"；另有 {hidden} 个目标在别处。" : "。";

        return "角色：" + string.Join("；", kept) + tail;
    }

    private static string DescribePlacement(
        MapToken token,
        MapRoom? room,
        IReadOnlyList<MapRoom> rooms,
        IReadOnlyList<MapWall> blockers,
        MapToken? focus,
        double dpi,
        double feetPerCell)
    {
        // 有焦点时用"相对焦点"的说法；没有焦点（例如还没有行动者）只说所在房间。
        if (focus is null || ReferenceEquals(token, focus))
        {
            return room is null ? " 在未登记区域" : $" 在{room.Name}";
        }

        // 格数取整、尺数按格算：模型对"3 格"比"3.42 格"敏感，两者也要互相对得上。
        var cells = Math.Round(Distance(token, focus) / dpi);
        var feet = Math.Round(cells * feetPerCell);
        var blocked = BlockerBetween(focus, token, blockers, rooms);
        var distanceText = $"{cells:0} 格 / {feet:0} 尺";

        if (blocked is not null)
        {
            // 隔墙：直线距离仍然给，但必须标明它被什么隔开——否则模型会当"可以直冲过去"。
            return $" 在{Where(token, room, focus, rooms)}（直线 {distanceText}，但隔着{blocked}）";
        }

        return $" 在{Where(token, room, focus, rooms)}，{distanceText}";
    }

    private static string Where(MapToken token, MapRoom? room, MapToken focus, IReadOnlyList<MapRoom> rooms)
    {
        var roomName = room?.Name ?? "未登记区域";
        var direction = Compass(focus, token);

        return string.IsNullOrEmpty(direction) ? roomName : $"{roomName}{direction}";
    }

    /// <summary>八方位：屏幕坐标 y 向下，所以"北"是 y 更小。</summary>
    private static string Compass(MapToken from, MapToken to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;

        if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
        {
            return string.Empty;
        }

        var angle = Math.Atan2(-dy, dx) * 180 / Math.PI; // 0 = 正东，90 = 正北
        var index = (int)Math.Round(((angle + 360) % 360) / 45) % 8;
        return CompassNames[index];
    }

    private static double Distance(MapToken left, MapToken right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    /// <summary>点是否在多边形内（射线法）。</summary>
    public static bool InsidePolygon(IReadOnlyList<Point2> polygon, double x, double y)
    {
        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var xi = polygon[i].X;
            var yi = polygon[i].Y;
            var xj = polygon[j].X;
            var yj = polygon[j].Y;

            if (yi > y != yj > y && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static MapRoom? RoomOf(IReadOnlyList<MapRoom> rooms, MapToken token)
    {
        foreach (var room in rooms)
        {
            if (room.Polygon is { Count: >= 3 } polygon && InsidePolygon(polygon, token.X, token.Y))
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>
    /// 两点连线被什么挡住：返回"一堵墙" / "一扇关着的门"，没挡住返回 <c>null</c>。
    /// 先判房间（同一房间内不可能隔墙——墙是房间内部的隔断，这里按几何算，不做房间假设）。
    /// </summary>
    private static string? BlockerBetween(
        MapToken from,
        MapToken to,
        IReadOnlyList<MapWall> blockers,
        IReadOnlyList<MapRoom> rooms)
    {
        foreach (var wall in blockers)
        {
            var points = wall.Points ?? [];

            for (var index = 1; index < points.Count; index++)
            {
                if (Intersects(from.X, from.Y, to.X, to.Y, points[index - 1], points[index]))
                {
                    return wall.Kind == MapWallKinds.Door ? "一扇关着的门" : "一堵墙";
                }
            }
        }

        return null;
    }

    /// <summary>线段相交（含端点擦边不算：用严格不等式，避免"贴着墙走"被判成穿过）。</summary>
    private static bool Intersects(double ax, double ay, double bx, double by, Point2 p, Point2 q)
    {
        var d1 = Cross(p, q, new Point2(ax, ay));
        var d2 = Cross(p, q, new Point2(bx, by));
        var d3 = Cross(new Point2(ax, ay), new Point2(bx, by), p);
        var d4 = Cross(new Point2(ax, ay), new Point2(bx, by), q);

        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static double Cross(Point2 a, Point2 b, Point2 c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static string LightText(string light) => light switch
    {
        MapLightLevels.Bright => "明亮",
        MapLightLevels.Dark => "黑暗",
        _ => "昏暗",
    };

    private static string Round(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.05
            ? Math.Round(value).ToString("0")
            : value.ToString("0.#");

    /// <summary>总长度兜底：超出上限就硬裁（房间那行优先保留，它比单个人物更重要）。</summary>
    private static string Fit(string text)
    {
        if (text.Length <= MaxChars)
        {
            return text;
        }

        var cut = text[..MaxChars];
        var lastBreak = cut.LastIndexOf('；');

        return lastBreak > 0 ? cut[..(lastBreak + 1)] + "…" : cut + "…";
    }
}
