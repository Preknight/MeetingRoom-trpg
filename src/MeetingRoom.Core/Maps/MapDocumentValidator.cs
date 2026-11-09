namespace MeetingRoom.Core.Maps;

/// <summary>
/// 地图文档校验（M1，设计文档 §4.1）。**先全部校验通过再落盘**——失败不做部分写入，
/// 否则用户会得到"一半新一半旧"的地图，而且很难看出来是哪一半。
/// </summary>
public static class MapDocumentValidator
{
    /// <summary>
    /// 校验墙 / 房间 / token。
    /// <paramref name="knownActorIds"/> 是地图上允许出现的角色 id（会话的 <c>cast</c>）；
    /// token 的 id 拼错会导致"地图上有个永远不跟角色关联的图标"，所以这里**点名拒绝**。
    /// </summary>
    public static (bool Ok, string? Error) Validate(
        IReadOnlyList<MapWall> walls,
        IReadOnlyList<MapRoom> rooms,
        IReadOnlyList<MapToken> tokens,
        IReadOnlyCollection<string> knownActorIds,
        MapFog? fog = null,
        IReadOnlyList<MapZone>? zones = null,
        int gridWidth = 0,
        int gridHeight = 0,
        MapCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(walls);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(knownActorIds);

        if (DuplicateOf(walls.Select(wall => wall.Id)) is { } duplicateWall)
        {
            return (false, $"墙的 id 重复：{duplicateWall}");
        }

        if (DuplicateOf(rooms.Select(room => room.Id)) is { } duplicateRoom)
        {
            return (false, $"房间的 id 重复：{duplicateRoom}");
        }

        if (DuplicateOf(tokens.Select(token => token.Id)) is { } duplicateToken)
        {
            return (false, $"token 的 id 重复：{duplicateToken}");
        }

        foreach (var wall in walls)
        {
            if (string.IsNullOrWhiteSpace(wall.Id))
            {
                return (false, "有一段墙没有 id。");
            }

            if (!MapWallKinds.All.Contains(wall.Kind))
            {
                return (false, $"墙 {wall.Id} 的 kind 不合法：{wall.Kind}（只能是 wall / door / window）。");
            }

            if (wall.Points is null || wall.Points.Count < 2)
            {
                return (false, $"墙 {wall.Id} 至少要 2 个点（现在 {wall.Points?.Count ?? 0} 个）。");
            }

            if (InvalidPoint(wall.Points) is { } badWallPoint)
            {
                return (false, $"墙 {wall.Id} 的第 {badWallPoint + 1} 个点不是有效坐标。");
            }
        }

        foreach (var room in rooms)
        {
            if (string.IsNullOrWhiteSpace(room.Id))
            {
                return (false, "有一个房间没有 id。");
            }

            if (!MapLightLevels.All.Contains(room.Light))
            {
                return (false, $"房间 {room.Id} 的 light 不合法：{room.Light}（只能是 bright / dim / dark）。");
            }

            if (room.Polygon is null || room.Polygon.Count < 3)
            {
                return (false, $"房间 {room.Id} 的多边形至少要 3 个点（现在 {room.Polygon?.Count ?? 0} 个）。");
            }

            if (InvalidPoint(room.Polygon) is { } badRoomPoint)
            {
                return (false, $"房间 {room.Id} 的第 {badRoomPoint + 1} 个点不是有效坐标。");
            }
        }

        // 角色 id 大小写不敏感（与 SheetStore / RoleplayStrategy 一致），但比较时用 OrdinalIgnoreCase，
        // 不改变用户填的原始大小写。
        var known = new HashSet<string>(knownActorIds, StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Id))
            {
                return (false, "有一个 token 没有 id（它必须等于角色 id）。");
            }

            if (!MapTokenKinds.All.Contains(token.Kind))
            {
                return (false, $"token {token.Id} 的 kind 不合法：{token.Kind}（只能是 character / npc）。");
            }

            if (!double.IsFinite(token.X) || !double.IsFinite(token.Y))
            {
                return (false, $"token {token.Id} 的坐标不是有效数字。");
            }

            if (!known.Contains(token.Id))
            {
                unknown.Add(token.Id);
            }
        }

        if (unknown.Count > 0)
        {
            var knownText = known.Count == 0
                ? "（当前没有已知角色：先起一场叙事，或检查角色目录）"
                : $"已知角色：{string.Join("、", knownOrdered(knownActorIds))}";

            return (false, $"这些 token 的 id 不是已知角色：{string.Join("、", unknown)}。{knownText}");
        }

        if (fog is { Enabled: true } && ValidateFog(fog, gridWidth, gridHeight, calibration) is { } fogError)
        {
            return (false, fogError);
        }

        if (zones is { Count: > 0 } && ValidateZones(zones) is { } zoneError)
        {
            return (false, zoneError);
        }

        return (true, null);
    }

    /// <summary>
    /// 迷雾可绘制的**边缘冗余格数**：迷雾允许比底图大，用来遮挡底图边缘（图外空白、白边、
    /// 贴边内容）。所以合法范围是 <c>[-margin, cols-1+margin]</c> 而不是 <c>[0, cols)</c>——
    /// 以前按"格索引非负"卡，用户想遮边距就会被 400 挡住（`迷雾格 [3,-3] 超出画布`）。
    /// 超出冗余范围才是真脏数据。
    /// </summary>
    public const int FogMarginCells = 3;

    /// <summary>
    /// 区域校验（M7）：形状二选一（多边形 ≥3 点 / 圆半径 1–12）、类型与颜色合法、id 不重复。
    /// </summary>
    private static string? ValidateZones(IReadOnlyList<MapZone> zones)
    {
        if (DuplicateOf(zones.Select(zone => zone.Id)) is { } duplicateZone)
        {
            return $"区域的 id 重复：{duplicateZone}";
        }

        foreach (var zone in zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id))
            {
                return "有一个区域没有 id。";
            }

            if (!MapZoneKinds.All.Contains(zone.Kind))
            {
                return $"区域 {zone.Id} 的 kind 不合法：{zone.Kind}（只能是 spell / terrain / portal / hazard / custom）。";
            }

            if (!MapZoneKinds.Shapes.Contains(zone.Shape?.Type ?? string.Empty))
            {
                return $"区域 {zone.Id} 的 shape.type 不合法：{zone.Shape?.Type}（只能是 circle / polygon）。";
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(zone.Color ?? string.Empty, "^#[0-9a-fA-F]{6}$"))
            {
                return $"区域 {zone.Id} 的颜色不合法：{zone.Color}（要 #rrggbb）。";
            }

            if (zone.Shape!.Type == "polygon")
            {
                if (zone.Shape.Points is not { Count: >= 3 })
                {
                    return $"区域 {zone.Id} 的多边形至少要 3 个点（现在 {zone.Shape.Points?.Count ?? 0} 个）。";
                }
            }
            else
            {
                if (zone.Shape.Center is null)
                {
                    return $"区域 {zone.Id} 的圆形缺少 center。";
                }

                if (zone.Shape.RadiusCells is < 1 or > 12)
                {
                    return $"区域 {zone.Id} 的半径要 1–12 格（现在 {zone.Shape.RadiusCells}）。";
                }
            }
        }

        return null;
    }

    private static string? ValidateFog(MapFog fog, int gridWidth, int gridHeight, MapCalibration? calibration)
    {
        var explored = new HashSet<GridCell>(fog.ExploredCells);

        foreach (var cell in fog.VisibleCells)
        {
            if (!explored.Contains(cell))
            {
                return $"迷雾不一致：视野格 [{cell.X},{cell.Y}] 不在已探索里（visible 必须是 explored 的子集）。";
            }
        }

        if (calibration is not { Dpi: > 0 } grid || gridWidth <= 0 || gridHeight <= 0)
        {
            return null;
        }

        // 网格列数：从画布尺寸与偏移换算（与编辑器画出来的格子同一口径）。
        var columns = (int)Math.Ceiling(Math.Max(0, gridWidth - grid.OffsetX) / grid.Dpi);
        var rows = (int)Math.Ceiling(Math.Max(0, gridHeight - grid.OffsetY) / grid.Dpi);
        const int margin = FogMarginCells;

        foreach (var cell in fog.ExploredCells.Concat(fog.VisibleCells))
        {
            if (cell.X < -margin || cell.Y < -margin || cell.X > columns - 1 + margin || cell.Y > rows - 1 + margin)
            {
                return $"迷雾格 [{cell.X},{cell.Y}] 超出迷雾可绘制范围（含 {margin} 格边缘冗余；网格 {columns}×{rows}）。";
            }
        }

        return null;
    }

    private static string? DuplicateOf(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && !seen.Add(id))
            {
                return id;
            }
        }

        return null;
    }

    private static int? InvalidPoint(IReadOnlyList<Point2> points)
    {
        for (var index = 0; index < points.Count; index++)
        {
            if (!double.IsFinite(points[index].X) || !double.IsFinite(points[index].Y))
            {
                return index;
            }
        }

        return null;
    }

    private static IEnumerable<string> knownOrdered(IEnumerable<string> ids) =>
        ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
}
