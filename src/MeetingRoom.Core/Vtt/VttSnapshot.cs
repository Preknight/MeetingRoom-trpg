using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MeetingRoom.Core.Vtt;

/// <summary>
/// 距离算法（《枭熊地图接入实现方案》§4）。<b>由场景的网格设置决定，客户端不得覆盖</b>——
/// 否则模型可以自己挑一套更有利的算法。
/// </summary>
public enum VttMeasurement
{
    /// <summary>对角与直走同价：<c>max(dx, dy)</c>。</summary>
    Chessboard,

    /// <summary>5e 的交替对角：每两个对角格算三格，即 <c>max + floor(min / 2)</c>。</summary>
    Alternating,

    /// <summary><c>sqrt(dx² + dy²)</c>。</summary>
    Euclidean,

    /// <summary><c>|dx| + |dy|</c>。</summary>
    Manhattan,
}

/// <summary>网格设置：像素 / 格（<see cref="Dpi"/>）与每格多少尺（<see cref="Multiplier"/>）。</summary>
public sealed record VttGrid(
    int Dpi,
    string Type,
    VttMeasurement Measurement,
    double Multiplier,
    string Unit)
{
    /// <summary>没有 dpi 或没有比例就无法换算——这时整份快照按"不可用"处理。</summary>
    [JsonIgnore]
    public bool IsUsable => Dpi > 0 && Multiplier > 0;

    public static VttGrid Empty { get; } = new(0, string.Empty, VttMeasurement.Chessboard, 0, "ft");
}

/// <summary>地图上的一个点（像素坐标，原点在场景左上角）。</summary>
public sealed record VttPoint(double X, double Y);

/// <summary>
/// 一个 token。<see cref="Visible"/> 来自 OBR 的显示开关——“DM 把它关掉”就是“还没揭示”，
/// 这是唯一的信息闸门（扩展读不到迷雾）。
/// </summary>
public sealed record VttToken(string Id, string Name, double X, double Y, bool Visible);

/// <summary>一条量尺：读数是桥按场景规则算好的，客户端只用来引用，不重新解释。</summary>
public sealed record VttRuler(string Id, string Measurement, VttPoint Start, VttPoint End);

/// <summary>
/// 一次地图快照（<c>GET http://127.0.0.1:9877/state</c> 的解析结果）。
/// <para>
/// <see cref="Available"/> 为 <c>false</c> 时表示<b>本轮没有地图段落</b>：桥没启动、超时、JSON 异常都归到这里。
/// 降级绝不抛异常——地图挂了不能中断回合。
/// </para>
/// </summary>
public sealed record VttSnapshot
{
    public bool Available { get; init; }

    /// <summary>不可用时的原因（写进日志与 <c>GET /api/roleplay/vtt</c>，不写进模型上下文）。</summary>
    public string? UnavailableReason { get; init; }

    public string Schema { get; init; } = string.Empty;

    /// <summary>桥侧的采集时间（Unix 毫秒）。</summary>
    public long ReceivedAt { get; init; }

    public bool SceneReady { get; init; }

    public VttGrid Grid { get; init; } = VttGrid.Empty;

    public IReadOnlyList<VttToken> Tokens { get; init; } = [];

    public IReadOnlyList<string> Selected { get; init; } = [];

    public IReadOnlyList<VttRuler> Rulers { get; init; } = [];

    [JsonIgnore]
    public DateTimeOffset? ReceivedAtTime =>
        ReceivedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ReceivedAt) : null;

    /// <summary>降级快照：带着原因，但没有可注入的内容。</summary>
    public static VttSnapshot Unavailable(string reason) => new() { Available = false, UnavailableReason = reason };
}

/// <summary>
/// 快照解析（《枭熊地图接入实现方案》§4 的数据契约）。
/// <para>
/// 解析是<b>宽容</b>的：缺字段用安全的默认值补齐（桥的版本会更新的），但
/// <b>缺 dpi / 缺比例 / 场景未就绪</b>时整份视为不可用——那种数据算出来的距离一定是错的。
/// </para>
/// </summary>
public static partial class VttSnapshotParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>解析桥的快照 JSON。格式不对时抛 <see cref="JsonException"/>（由客户端统一降级）。</summary>
    public static VttSnapshot Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var payload = JsonSerializer.Deserialize<StatePayload>(json, Options)
            ?? throw new JsonException("快照是空的");

        // 桥刚启动、扩展还没上报时，它给的就是 {"schema":…, "receivedAt":null, "scene":null}。
        // 这不是"格式不对"，而是"还没数据"——要能说人话，否则排查会从 JSON 报错开始走弯路。
        if (payload.Scene is null)
        {
            return VttSnapshot.Unavailable(
                "桥还没收到任何上报：OBR 扩展没有连上，或场景还没就绪。"
                + "先看桥的 /state 是否返回 scene，再看 OBR 里 Map Bridge 面板的连接状态。");
        }

        var scene = payload.Scene;

        if (!scene.SceneReady)
        {
            return VttSnapshot.Unavailable("枭熊场景还没就绪（sceneReady = false）");
        }

        var grid = ReadGrid(scene.Grid);

        if (!grid.IsUsable)
        {
            return VttSnapshot.Unavailable("快照里没有可用的网格（dpi 或 scale.multiplier 缺失）");
        }

        var snapshot = new VttSnapshot
        {
            Available = true,
            Schema = payload.Schema ?? string.Empty,
            ReceivedAt = payload.ReceivedAt ?? 0,
            SceneReady = scene.SceneReady,
            Grid = grid,
            Tokens = (scene.Tokens ?? [])
                .Where(token => !string.IsNullOrWhiteSpace(token.Id))
                .Select(token => new VttToken(
                    token.Id!,
                    string.IsNullOrWhiteSpace(token.Name) ? token.Id! : token.Name!,
                    token.X,
                    token.Y,
                    // 桥没上报 visible 时按"可见"处理：它是显示开关，缺省就是显示着。
                    token.Visible ?? true))
                .ToArray(),
            Selected = (scene.Selected ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToArray(),
            Rulers = (scene.Rulers ?? [])
                .Where(ruler => ruler.Start is not null && ruler.End is not null)
                .Select(ruler => new VttRuler(
                    ruler.Id ?? string.Empty,
                    ruler.Measurement ?? string.Empty,
                    new VttPoint(ruler.Start!.X, ruler.Start.Y),
                    new VttPoint(ruler.End!.X, ruler.End.Y)))
                .ToArray(),
        };

        return snapshot;
    }

    /// <summary>把桥 / OBR 的度量名归一：大小写不敏感，也认 OBR 的枚举写法（CHEBYSHEV 等）。</summary>
    public static VttMeasurement ParseMeasurement(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return VttMeasurement.Chessboard;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "chessboard" or "chebyshev" or "square" or "grid" => VttMeasurement.Chessboard,
            "alternating" or "alternating-diagonals" or "5e" => VttMeasurement.Alternating,
            "euclidean" or "circle" or "radial" => VttMeasurement.Euclidean,
            "manhattan" or "taxicab" => VttMeasurement.Manhattan,
            // 认不出就用最保守的（对角与直走同价），并在度量名里原样保留原始值：
            // 宁可少算几尺，也不要让模型按一套没人声明过的规则推距离。
            _ => VttMeasurement.Chessboard,
        };
    }

    private static VttGrid ReadGrid(GridPayload? payload)
    {
        if (payload is null)
        {
            return VttGrid.Empty;
        }

        var multiplier = payload.Scale?.Parsed?.Multiplier ?? 0;
        var unit = payload.Scale?.Parsed?.Unit ?? string.Empty;

        // 只有 raw（如 "5 ft"）而没有 parsed 时自己拆一次：桥的旧版本只给 raw。
        if (multiplier <= 0 && !string.IsNullOrWhiteSpace(payload.Scale?.Raw))
        {
            var match = RawScalePattern().Match(payload.Scale!.Raw!);

            if (match.Success)
            {
                multiplier = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                unit = match.Groups[2].Value;
            }
        }

        return new VttGrid(
            payload.Dpi,
            payload.Type ?? string.Empty,
            ParseMeasurement(payload.Measurement),
            multiplier,
            string.IsNullOrWhiteSpace(unit) ? "ft" : unit);
    }

    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]*)\s*$")]
    private static partial Regex RawScalePattern();

    // ——— 契约的 DTO（只在这一层出现，领域层用上面的 record） ———

    private sealed record StatePayload(
        string? Schema,
        // 桥未收到上报时给的是 null（不是 0）——这里必须可空，否则反序列化会先抛 JSON 错，
        // 连“还没数据”这种正常状态都会被当成格式损坏。
        long? ReceivedAt,
        ScenePayload? Scene);

    private sealed record ScenePayload(
        bool SceneReady,
        GridPayload? Grid,
        TokenPayload[]? Tokens,
        string?[]? Selected,
        RulerPayload[]? Rulers);

    private sealed record GridPayload(
        int Dpi,
        string? Type,
        string? Measurement,
        ScalePayload? Scale);

    private sealed record ScalePayload(string? Raw, ParsedScalePayload? Parsed);

    private sealed record ParsedScalePayload(double Multiplier, string? Unit);

    private sealed record TokenPayload(
        string? Id,
        string? Name,
        double X,
        double Y,
        bool? Visible);

    private sealed record RulerPayload(
        string? Id,
        string? Measurement,
        PointPayload? Start,
        PointPayload? End);

    private sealed record PointPayload(double X, double Y);
}
