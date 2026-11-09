namespace MeetingRoom.Core.Maps;

/// <summary>
/// 地图文档（M1）：底图 + 网格 + 墙 / 房间 / token。**所有坐标都是图片像素坐标**
/// （与 <c>board.js</c> 一致，不引入第二套坐标系）。
/// </summary>
public sealed record MapDocument(
    string Id,
    string Name,
    /// <summary><c>image</c>（有底图）/ <c>sketch</c>（手绘：没有底图）。由 <see cref="Image"/> 是否存在推导。</summary>
    string Mode,
    MapImageInfo? Image,
    MapCalibration Grid,
    IReadOnlyList<MapWall> Walls,
    IReadOnlyList<MapRoom> Rooms,
    IReadOnlyList<MapToken> Tokens,
    long UpdatedAt,
    /// <summary>迷雾（M6a）：三态按<b>格索引</b>存（标定一改像素会漂，格索引不会）。旧文档没有它 → 空。</summary>
    MapFog? Fog = null,
    /// <summary>特殊区域（M7）：法术范围 / 困难地形 / 传送门…旧文档没有它 → 空数组。</summary>
    IReadOnlyList<MapZone>? Zones = null);

/// <summary>
/// 迷雾（M6a）。三态：**都不在两表里 = 未探索（全黑）**；在 `Explored` = 已探索（半透明）；
/// 在 `Visible` = 当前视野（全亮）。约定 `Visible ⊆ Explored`，服务端写入时强制。
/// <para>
/// 默认两张表都为空 = 整张图全黑——等价于"一开始涂满所有格子"，但不必存上千条记录。
/// </para>
/// </summary>
public sealed record MapFog(
    bool Enabled = false,
    IReadOnlyList<GridCell>? Explored = null,
    IReadOnlyList<GridCell>? Visible = null)
{
    /// <summary>空迷雾（未启用、两表为空 = 整张图全黑）。</summary>
    public static MapFog Disabled { get; } = new(false, [], []);

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<GridCell> ExploredCells => Explored ?? [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<GridCell> VisibleCells => Visible ?? [];
}

/// <summary>
/// 特殊区域（M7）：法术范围、困难地形、传送门、危险地……<c>Visible=false</c> 表示只给 DM 看。
/// </summary>
public sealed record MapZone(
    string Id,
    string Name,
    string Kind,
    MapZoneShape Shape,
    string Color = MapZoneKinds.DefaultColor,
    string Note = "",
    bool Visible = true);

/// <summary>区域形状：圆形（圆心像素 + 半径格数）或多边形（像素点集，闭合、首尾不重复）。</summary>
public sealed record MapZoneShape(
    string Type,
    IReadOnlyList<Point2>? Points = null,
    Point2? Center = null,
    double RadiusCells = 2);

public static class MapZoneKinds
{
    public const string Spell = "spell";
    public const string Terrain = "terrain";
    public const string Portal = "portal";
    public const string Hazard = "hazard";
    public const string Custom = "custom";

    public const string DefaultColor = "#c9a227";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Spell, Terrain, Portal, Hazard, Custom,
    };

    public static readonly IReadOnlySet<string> Shapes = new HashSet<string>(StringComparer.Ordinal)
    {
        "circle", "polygon",
    };

    /// <summary>类型的明文（进上下文与界面用；`custom` 靠 name 表达）。</summary>
    public static string Describe(string kind) => kind switch
    {
        Spell => "法术",
        Terrain => "地形",
        Portal => "传送门",
        Hazard => "危险",
        _ => "其他",
    };
}

/// <summary>
/// 圆形区域（M6b）：说书人提议揭示时可以给"圆心像素 + 半径格数"，由会话层换算成格索引。
/// </summary>
public sealed record FogShape(string Type, IReadOnlyList<double>? Center, double RadiusCells);

/// <summary>格索引（整数，不是像素）：`gx = floor((x - offsetX) / dpi)`，JSON 写成 `[gx, gy]`。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(GridCellConverter))]
public readonly record struct GridCell(int X, int Y);

/// <summary><see cref="GridCell"/> 的 JSON 形状：`[gx, gy]` 两个整数。</summary>
public sealed class GridCellConverter : System.Text.Json.Serialization.JsonConverter<GridCell>
{
    public override GridCell Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
        {
            throw new System.Text.Json.JsonException("格索引必须是 [gx, gy] 数组。");
        }

        reader.Read();
        var x = reader.GetInt32();
        reader.Read();
        var y = reader.GetInt32();
        reader.Read();

        return new GridCell(x, y);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, GridCell value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

/// <summary>底图信息：文件名与**字节头读出的**宽高（不接受前端传值）。</summary>
public sealed record MapImageInfo(string File, int Width, int Height);

/// <summary>
/// 一段墙：<c>wall</c> 挡通行与视线；<c>door</c> 由 <see cref="Open"/> 决定是否挡；
/// <c>window</c> 不挡通行、可选挡视线。
/// </summary>
public sealed record MapWall(
    string Id,
    IReadOnlyList<Point2> Points,
    string Kind = MapWallKinds.Wall,
    bool Open = false);

/// <summary>一个房间：闭合多边形（首尾不重复）、名称、光照、备注。</summary>
public sealed record MapRoom(
    string Id,
    string Name,
    IReadOnlyList<Point2> Polygon,
    string Light = MapLightLevels.Dim,
    string Note = "");

/// <summary>
/// 一个 token。<see cref="Id"/> **必须等于角色 id**（与 <c>SheetStore</c>、<c>roleplay.state.cast</c> 同名），
/// 这样战斗、检定、地图三处引用的是同一个人。
/// </summary>
public sealed record MapToken(
    string Id,
    string Name,
    double X,
    double Y,
    bool Visible = true,
    string Kind = MapTokenKinds.Character);

/// <summary>图片像素坐标（<c>[x, y]</c> 数组在 JSON 里由 <see cref="Point2"/> 的转换器读写）。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Point2Converter))]
public readonly record struct Point2(double X, double Y);

public static class MapWallKinds
{
    public const string Wall = "wall";
    public const string Door = "door";
    public const string Window = "window";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Wall, Door, Window,
    };
}

public static class MapLightLevels
{
    public const string Bright = "bright";
    public const string Dim = "dim";
    public const string Dark = "dark";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Bright, Dim, Dark,
    };
}

public static class MapTokenKinds
{
    public const string Character = "character";
    public const string Npc = "npc";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Character, Npc,
    };
}

/// <summary>
/// <see cref="Point2"/> 的 JSON 形状：<c>[x, y]</c> 数组（与设计文档的 <c>points</c> / <c>polygon</c> 一致）。
/// 用数组而不是 <c>{x,y}</c>：一张图几百个点，键名会把文件撑大一倍。
/// </summary>
public sealed class Point2Converter : System.Text.Json.Serialization.JsonConverter<Point2>
{
    public override Point2 Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
        {
            throw new System.Text.Json.JsonException("点必须是 [x, y] 数组。");
        }

        reader.Read();
        var x = reader.GetDouble();
        reader.Read();
        var y = reader.GetDouble();
        reader.Read();

        if (reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
        {
            throw new System.Text.Json.JsonException("点必须是 [x, y] 两个数。");
        }

        return new Point2(x, y);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Point2 value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Round(value.X, 2));
        writer.WriteNumberValue(Math.Round(value.Y, 2));
        writer.WriteEndArray();
    }
}
