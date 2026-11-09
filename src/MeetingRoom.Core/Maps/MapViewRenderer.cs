using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MeetingRoom.Core.Maps;

/// <summary>
/// 把地图文档合成成一张 PNG（M8a，设计 §5.2）。
/// <para>
/// <b>刻意不画两样东西</b>：
/// ① **token**、② **灰色（已探索）迷雾** —— 这两类每轮都在变；画进去图就每轮都不同，
/// 缓存永远失效，而"可见性"语义其实由纯黑未探索格 + 文字段表达。
/// 画了它们，等于把"每轮变的东西"塞进一个按文档 revision 缓存的图里，是自相矛盾的。
/// </para>
/// <para>
/// 图层顺序：底图（或纯色底）→ 网格 → 纯黑未探索 → 房间/墙 → 区域填充。
/// </para>
/// </summary>
public static class MapViewRenderer
{
    /// <summary>默认长边像素（设计 §5.2：768 附近性价比最好）。</summary>
    public const int DefaultMaxPx = 768;

    /// <summary>允许的尺寸范围。</summary>
    public const int MinMaxPx = 256;

    public const int MaxMaxPx = 2048;

    private static readonly Color Background = Color.ParseHex("#0d1117");
    private static readonly Color GridColor = Color.ParseHex("#2a3342");
    private static readonly Color WallColor = Color.ParseHex("#c8d2e0");
    private static readonly Color RoomColor = Color.ParseHex("#6e91d2");

    /// <summary>
    /// 合成一张图。<paramref name="mode"/> 为 <c>player</c> 时只画已探索区域的外接矩形，
    /// 且没有已探索格时返回 <c>null</c>（端点据此回 204）。
    /// </summary>
    public static byte[]? Render(
        MapDocument document,
        MapImageSource? source,
        bool playerView,
        int maxPx = DefaultMaxPx)
    {
        ArgumentNullException.ThrowIfNull(document);

        var grid = document.Grid;

        // 没有格就画不了网格与迷雾——按设计直接不出图。
        if (grid is not { Dpi: > 0 })
        {
            return null;
        }

        var width = document.Image?.Width ?? MapStore.BlankWidth;
        var height = document.Image?.Height ?? MapStore.BlankHeight;
        var fog = document.Fog is { Enabled: true } enabled ? enabled : null;
        var known = fog is null
            ? null
            : new HashSet<GridCell>(fog.ExploredCells.Concat(fog.VisibleCells));

        // player：只画已探索区域的外接矩形（四周留 1 格）。
        var crop = new RectangleF(0, 0, width, height);

        if (playerView)
        {
            if (known is not { Count: > 0 })
            {
                return null;
            }

            var minX = known.Min(cell => cell.X);
            var maxX = known.Max(cell => cell.X);
            var minY = known.Min(cell => cell.Y);
            var maxY = known.Max(cell => cell.Y);

            // 迷雾允许比底图大（遮边距），所以裁框**不能**钳在底图内——否则玩家视角根本看不到
            // 那些边缘雾。下限放到「网格原点 - 冗余格」，上限放到「底图边 + 冗余格」。
            var marginPx = MapDocumentValidator.FogMarginCells * grid.Dpi;
            var left = Math.Max(grid.OffsetX - marginPx, grid.OffsetX + ((minX - 1) * grid.Dpi));
            var top = Math.Max(grid.OffsetY - marginPx, grid.OffsetY + ((minY - 1) * grid.Dpi));
            var right = Math.Min(width + marginPx, grid.OffsetX + ((maxX + 2) * grid.Dpi));
            var bottom = Math.Min(height + marginPx, grid.OffsetY + ((maxY + 2) * grid.Dpi));

            if (right - left < 1 || bottom - top < 1)
            {
                return null;
            }

            crop = new RectangleF((float)left, (float)top, (float)(right - left), (float)(bottom - top));
        }

        var target = Math.Clamp(maxPx, MinMaxPx, MaxMaxPx);
        var scale = Math.Min(1.0, target / Math.Max(crop.Width, crop.Height));
        var outputWidth = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(crop.Height * scale));

        using var image = new Image<Rgba32>(outputWidth, outputHeight);

        image.Mutate(context =>
        {
            // player 视图的裁框可能伸到底图外（边缘冗余雾）；那里没底图可画，
            // 必须与「未探索」同色（纯黑），否则玩家会看到一圈比雾更浅的底。
            context.Fill(playerView ? Color.Black : Background);

            // 底图：裁切 + 缩放到输出尺寸。sketch 模式没有底图，纯色底已经铺好。
            if (source is not null)
            {
                using var bitmap = Image.Load(source.Bytes);

                // 裁框可能伸到底图外（玩家视角的边缘冗余雾）：只取与底图**相交**的那块，
                // 再画到它在输出里的位置。直接在越界坐标上 Crop 会让 ImageSharp 抛错。
                var intersectLeft = Math.Max(0, (int)Math.Floor(crop.X));
                var intersectTop = Math.Max(0, (int)Math.Floor(crop.Y));
                var intersectRight = Math.Min(bitmap.Width, (int)Math.Ceiling(crop.Right));
                var intersectBottom = Math.Min(bitmap.Height, (int)Math.Ceiling(crop.Bottom));

                if (intersectRight > intersectLeft && intersectBottom > intersectTop)
                {
                    var cropRect = new Rectangle(
                        intersectLeft,
                        intersectTop,
                        intersectRight - intersectLeft,
                        intersectBottom - intersectTop);
                    var drawnWidth = Math.Max(1, (int)Math.Round(cropRect.Width * scale));
                    var drawnHeight = Math.Max(1, (int)Math.Round(cropRect.Height * scale));

                    bitmap.Mutate(bitmapContext => bitmapContext.Crop(cropRect));
                    bitmap.Mutate(bitmapContext => bitmapContext.Resize(drawnWidth, drawnHeight));

                    context.DrawImage(
                        bitmap,
                        new Point(
                            (int)Math.Round((intersectLeft - crop.X) * scale),
                            (int)Math.Round((intersectTop - crop.Y) * scale)),
                        1f);
                }
            }

            // 把「图片像素」映射到「输出像素」：一次统一缩放，不做逐角色裁剪（否则缓存全废）。
            PointF Map(double x, double y) => new(
                (float)((x - crop.X) * scale),
                (float)((y - crop.Y) * scale));

            // 网格：细线，跟着缩放走。
            var gridPen = Pens.Solid(GridColor, 1f);
            var startX = grid.OffsetX;

            for (var x = startX; x <= crop.Right; x += grid.Dpi)
            {
                if (x < crop.Left)
                {
                    continue;
                }

                var top = Map(x, crop.Top);
                var bottom = Map(x, crop.Bottom);
                context.DrawLine(gridPen, top, bottom);
            }

            for (var y = grid.OffsetY; y <= crop.Bottom; y += grid.Dpi)
            {
                if (y < crop.Top)
                {
                    continue;
                }

                var left = Map(crop.Left, y);
                var right = Map(crop.Right, y);
                context.DrawLine(gridPen, left, right);
            }

            // 纯黑未探索迷雾（DM 视角不画迷雾）。
            if (playerView && known is not null)
            {
                var columns = (int)Math.Ceiling(Math.Max(0, width - grid.OffsetX) / grid.Dpi);
                var rows = (int)Math.Ceiling(Math.Max(0, height - grid.OffsetY) / grid.Dpi);

                for (var gy = 0; gy < rows; gy += 1)
                {
                    for (var gx = 0; gx < columns; gx += 1)
                    {
                        if (known.Contains(new GridCell(gx, gy)))
                        {
                            continue;
                        }

                        var cellLeft = grid.OffsetX + (gx * grid.Dpi);
                        var cellTop = grid.OffsetY + (gy * grid.Dpi);

                        var topLeft = Map(cellLeft, cellTop);
                        var bottomRight = Map(cellLeft + grid.Dpi, cellTop + grid.Dpi);

                        context.Fill(
                            Color.Black,
                            new RectangleF(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));
                    }
                }
            }

            // 房间与墙：细线（结构，不是秘密）。
            var roomPen = Pens.Solid(RoomColor, 1f);
            var wallPen = Pens.Solid(WallColor, 1.5f);

            foreach (var room in document.Rooms ?? [])
            {
                if (room.Polygon is not { Count: >= 3 } polygon)
                {
                    continue;
                }

                var points = polygon.Select(point => Map(point.X, point.Y)).ToArray();
                context.DrawLine(roomPen, [.. points, points[0]]);
            }

            foreach (var wall in document.Walls ?? [])
            {
                var points = wall.Points ?? [];

                for (var index = 1; index < points.Count; index += 1)
                {
                    context.DrawLine(wallPen, Map(points[index - 1].X, points[index - 1].Y), Map(points[index].X, points[index].Y));
                }
            }

            // 区域：半透明填充（颜色 = 区域自己的颜色）。
            foreach (var zone in (document.Zones ?? []).Where(zone => zone.Visible))
            {
                var color = ParseColor(zone.Color);
                var pixel = color.ToPixel<Rgba32>();
                var fill = Color.FromRgba(pixel.R, pixel.G, pixel.B, (byte)46);

                if (zone.Shape?.Type == "polygon" && zone.Shape.Points is { Count: >= 3 } zonePolygon)
                {
                    var points = zonePolygon.Select(point => Map(point.X, point.Y)).ToArray();
                    context.Fill(fill, new Polygon(points));
                }
                else if (zone.Shape?.Type == "circle" && zone.Shape.Center is { } center)
                {
                    var radius = (float)((zone.Shape.RadiusCells <= 0 ? 2 : zone.Shape.RadiusCells) * grid.Dpi * scale);
                    var middle = Map(center.X, center.Y);
                    context.Fill(fill, new EllipsePolygon(middle, radius));
                }
            }
        });

        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        return output.ToArray();
    }

    private static Color ParseColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith('#') && hex.Length == 7)
        {
            try
            {
                return Color.ParseHex(hex);
            }
            catch (ArgumentException)
            {
                // 非法颜色退回默认（校验层已经拦过一次，这里只是兜底）。
            }
        }

        return Color.ParseHex(MapZoneKinds.DefaultColor);
    }
}

/// <summary>底图字节（M8a）：由 Host 侧从 <c>maps/</c> 读出来传给渲染器。</summary>
public sealed record MapImageSource(byte[] Bytes);
