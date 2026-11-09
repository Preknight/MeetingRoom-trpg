namespace MeetingRoom.Core.Maps;

/// <summary>
/// 角色视角的文字段（M9a）：**只写这个角色自己该看到的那一份**。
/// <para>
/// 三条闸门（硬约束）：
/// ① 只提已探索格（`explored ∪ visible`）里的东西；
/// ② `visible=false` 的 token 与区域一律不提；
/// ③ 不写像素坐标、不报"地图上有 N 个 token"这种总量。
/// 迷雾未启用时视为全可见——行为与引入迷雾前一致，只是多出这一段。
/// </para>
/// 与说书人的全量段（<see cref="MapContextBuilder.Build"/>）并存但内容不同：
/// 说书人是"全知"的裁判，角色只有"站在这里能感知到的"。
/// </summary>
public static class MapCharacterView
{
    /// <summary>段的字符上限：比说书人段（200）宽一点——它含位置与周围描述，但仍要短。</summary>
    public const int MaxChars = 320;

    public static string Build(MapDocument document, string actorId, double radiusCells = 12)
    {
        ArgumentNullException.ThrowIfNull(document);

        var grid = document.Grid;

        if (grid is not { Dpi: > 0 })
        {
            return string.Empty;
        }

        var self = (document.Tokens ?? [])
            .FirstOrDefault(token => string.Equals(token.Id, actorId, StringComparison.OrdinalIgnoreCase));

        if (self is null)
        {
            return string.Empty;
        }

        var fog = document.Fog is { Enabled: true } enabled ? enabled : null;
        var explored = fog is null
            ? null
            : new HashSet<GridCell>(fog.ExploredCells.Concat(fog.VisibleCells));

        bool Known(GridCell cell) => explored is null || explored.Contains(cell);

        var rooms = document.Rooms ?? [];
        var feetPerCell = grid.Scale > 0 ? grid.Scale : 5;
        var selfCell = CellOf(document, self.X, self.Y);
        var parts = new List<string>
        {
            $"【地图】{document.Name}（每格 {Round(feetPerCell)} 尺）",
            $"你{WhereAmI(self, RoomOf(rooms, self))}。",
        };

        // —— 附近的人：半径内、可见、且确实在已探索格里 ——
        var nearby = new List<string>();
        var blockers = (document.Walls ?? []).Where(BlocksSight).ToArray();

        foreach (var token in document.Tokens ?? [])
        {
            if (ReferenceEquals(token, self) || !token.Visible || !Known(CellOf(document, token.X, token.Y)))
            {
                continue;
            }

            var cells = Math.Round(Distance(self, token) / grid.Dpi);

            if (cells > radiusCells)
            {
                continue;
            }

            var blocked = BlockerBetween(self, token, blockers);
            var place = RoomOf(rooms, token)?.Name ?? "未登记区域";
            var sight = blocked is null ? "直线可见" : $"隔着{blocked}";

            nearby.Add($"{token.Name}（{place}，{cells:0} 格外，{sight}）");
        }

        parts.Add(nearby.Count > 0
            ? $"附近：{string.Join("；", nearby)}"
            : $"附近没人（{Round(radiusCells)} 格内）");

        // —— 脚下与相邻一格内的区域：只提可见的 ——
        var zones = new List<string>();

        foreach (var zone in (document.Zones ?? []).Where(zone => zone.Visible))
        {
            var cells = ZoneCellsOf(document, zone);

            if (cells.Count == 0 || !cells.Any(Known))
            {
                continue;
            }

            var underfoot = cells.Contains(selfCell);
            var beside = cells.Any(cell => Math.Abs(cell.X - selfCell.X) <= 1 && Math.Abs(cell.Y - selfCell.Y) <= 1);

            if (!underfoot && !beside)
            {
                continue;
            }

            var note = string.IsNullOrWhiteSpace(zone.Note) ? string.Empty : $"，{zone.Note.Trim()}";
            zones.Add($"{zone.Name}（{MapZoneKinds.Describe(zone.Kind)}{note}）{(underfoot ? "就在你脚下" : "在你旁边")}");
        }

        if (zones.Count > 0)
        {
            parts.Add($"身边：{string.Join("；", zones)}");
        }

        // —— 迷雾：自己这格的状态 + 半径内还有多少没探索（只给数量与方位） ——
        if (fog is not null)
        {
            var visibleCells = new HashSet<GridCell>(fog.VisibleCells);
            var state = visibleCells.Contains(selfCell) ? "此刻在视野里" : "在视野外（眼前偏暗）";
            var span = (int)Math.Ceiling(radiusCells);
            var unexplored = 0;
            var west = 0;
            var east = 0;
            var north = 0;
            var south = 0;

            for (var dy = -span; dy <= span; dy += 1)
            {
                for (var dx = -span; dx <= span; dx += 1)
                {
                    if (Math.Sqrt((dx * dx) + (dy * dy)) > radiusCells)
                    {
                        continue;
                    }

                    if (explored!.Contains(new GridCell(selfCell.X + dx, selfCell.Y + dy)))
                    {
                        continue;
                    }

                    unexplored += 1;
                    west += dx < 0 ? 1 : 0;
                    east += dx > 0 ? 1 : 0;
                    north += dy < 0 ? 1 : 0;
                    south += dy > 0 ? 1 : 0;
                }
            }

            if (unexplored == 0)
            {
                parts.Add($"你{state}。");
            }
            else
            {
                var horizontal = west >= east ? "西" : "东";
                var vertical = north >= south ? "北" : "南";

                parts.Add($"你{state}；{Round(radiusCells)} 格内还有 {unexplored} 格没探索过（多在{vertical}、{horizontal}侧）。");
            }
        }

        var text = string.Join("\n", parts);
        return text.Length <= MaxChars ? text : text[..MaxChars] + "…";
    }

    private static bool BlocksSight(MapWall wall) =>
        wall.Kind == MapWallKinds.Wall || (wall.Kind == MapWallKinds.Door && !wall.Open);

    private static MapToken? FindById(IReadOnlyList<MapToken> tokens, string id) =>
        tokens.FirstOrDefault(token => string.Equals(token.Id, id, StringComparison.OrdinalIgnoreCase));

    private static MapRoom? RoomOf(IReadOnlyList<MapRoom> rooms, MapToken token)
    {
        foreach (var room in rooms)
        {
            if (room.Polygon is { Count: >= 3 } polygon && MapContextBuilder.InsidePolygon(polygon, token.X, token.Y))
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>自己在哪：房间名 + 相对房间中心的方位（不写像素）。</summary>
    private static string WhereAmI(MapToken self, MapRoom? room)
    {
        if (room?.Polygon is not { Count: >= 3 } polygon)
        {
            return "在一处未登记的区域";
        }

        var minX = polygon.Min(point => point.X);
        var maxX = polygon.Max(point => point.X);
        var minY = polygon.Min(point => point.Y);
        var maxY = polygon.Max(point => point.Y);

        var horizontal = self.X < minX + ((maxX - minX) / 3) ? "西"
            : self.X > maxX - ((maxX - minX) / 3) ? "东" : string.Empty;
        var vertical = self.Y < minY + ((maxY - minY) / 3) ? "北"
            : self.Y > maxY - ((maxY - minY) / 3) ? "南" : string.Empty;

        var corner = vertical + horizontal switch
        {
            "" => "中间",
            var value => value + "角",
        };

        return $"在{room.Name}的{corner}";
    }

    /// <summary>区域覆盖到的格（圆按格心到圆心 ≤ 半径，多边形按点在多边形内）。</summary>
    /// <summary>区域覆盖的格集合（<c>check_terrain</c> 与角色节共用同一套几何）。</summary>
    public static HashSet<GridCell> ZoneCellsOf(MapDocument document, MapZone zone)
    {
        var cells = new HashSet<GridCell>();
        var grid = document.Grid!;
        var width = document.Image?.Width ?? MapStore.BlankWidth;
        var height = document.Image?.Height ?? MapStore.BlankHeight;
        var columns = (int)Math.Ceiling(Math.Max(0, width - grid.OffsetX) / grid.Dpi);
        var rows = (int)Math.Ceiling(Math.Max(0, height - grid.OffsetY) / grid.Dpi);

        if (zone.Shape?.Type == "polygon" && zone.Shape.Points is { Count: >= 3 } polygon)
        {
            for (var y = 0; y < rows; y += 1)
            {
                for (var x = 0; x < columns; x += 1)
                {
                    if (MapContextBuilder.InsidePolygon(polygon, grid.OffsetX + ((x + 0.5) * grid.Dpi), grid.OffsetY + ((y + 0.5) * grid.Dpi)))
                    {
                        cells.Add(new GridCell(x, y));
                    }
                }
            }

            return cells;
        }

        if (zone.Shape?.Type == "circle" && zone.Shape.Center is { } center)
        {
            var radius = (zone.Shape.RadiusCells <= 0 ? 2 : zone.Shape.RadiusCells) * grid.Dpi;
            var centerCell = CellOf(document, center.X, center.Y);
            var span = (int)Math.Ceiling(radius / grid.Dpi) + 1;

            for (var dy = -span; dy <= span; dy += 1)
            {
                for (var dx = -span; dx <= span; dx += 1)
                {
                    var cellX = centerCell.X + dx;
                    var cellY = centerCell.Y + dy;

                    if (cellX < 0 || cellY < 0 || cellX >= columns || cellY >= rows)
                    {
                        continue;
                    }

                    var pointX = grid.OffsetX + ((cellX + 0.5) * grid.Dpi);
                    var pointY = grid.OffsetY + ((cellY + 0.5) * grid.Dpi);

                    if (Math.Sqrt(Math.Pow(pointX - center.X, 2) + Math.Pow(pointY - center.Y, 2)) <= radius)
                    {
                        cells.Add(new GridCell(cellX, cellY));
                    }
                }
            }
        }

        return cells;
    }

    /// <summary>像素坐标 → 格索引。</summary>
    private static GridCell CellOf(MapDocument document, double x, double y)
    {
        var grid = document.Grid;

        if (grid is not { Dpi: > 0 })
        {
            return new GridCell(0, 0);
        }

        return new GridCell(
            (int)Math.Floor((x - grid.OffsetX) / grid.Dpi),
            (int)Math.Floor((y - grid.OffsetY) / grid.Dpi));
    }

    private static string? BlockerBetween(MapToken from, MapToken to, IReadOnlyList<MapWall> blockers)
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

    private static double Distance(MapToken left, MapToken right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static string Round(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.05 ? Math.Round(value).ToString("0") : value.ToString("0.#");
}
