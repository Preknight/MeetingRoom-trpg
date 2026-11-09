using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Maps;

/// <summary>
/// 网格标定（F2/F4）：<c>dpi</c> 是一格多少像素，<c>offset</c> 是网格起点相对图片左上角的偏移，
/// <c>scale</c> 是每格代表多少尺（5e 默认 5）。<c>gridType</c> 目前只有 <c>square</c>。
/// </summary>
public sealed record MapCalibration(
    double Dpi,
    double OffsetX,
    double OffsetY,
    string GridType,
    double Scale)
{
    /// <summary>5e 的常见默认：一格 5 尺、100 dpi、无偏移（F4 的标定 UI 再微调）。</summary>
    public static MapCalibration Default { get; } = new(100, 0, 0, "square", 5);

    /// <summary>是否已标定：<c>dpi &gt; 0</c> 才算（0 表示"还没标过"）。</summary>
    public bool IsCalibrated => Dpi > 0;
}

/// <summary>
/// 一张战术板底图（F2）。<see cref="FileName"/> 是 <c>maps/</c> 下的文件名（<c>&lt;id&gt;.webp|png</c>），
/// 宽高来自**字节头**（<see cref="Images.ImageSize"/>），不是前端传的。
/// </summary>
public sealed record MapAsset(
    string Id,
    string Name,
    // sketch（手绘）地图没有底图文件：FileName / ContentType 为 null，Width/Height 是画布尺寸。
    string? FileName,
    string? ContentType,
    int Width,
    int Height,
    MapCalibration? Calibration,
    // M1：墙 / 房间 / token。**旧文件没有这三个字段 = 空数组**（F2/F4 的文件不用迁移）。
    IReadOnlyList<MapWall>? Walls = null,
    IReadOnlyList<MapRoom>? Rooms = null,
    IReadOnlyList<MapToken>? Tokens = null,
    long UpdatedAt = 0,
    MapFog? Fog = null,
    IReadOnlyList<MapZone>? Zones = null)
{
    public bool Calibrated => Calibration?.IsCalibrated == true;
}

/// <summary>
/// 战术板底图仓库（F2）：工作空间 <c>maps/</c> 下的 <c>&lt;id&gt;.webp|png</c> + <c>&lt;id&gt;.json</c>。
/// <para>
/// <b>刻意独立于 <c>ImageAssetStore</c></b>：那边是"生成的图"，这里是"用户导入的地图"，
/// 混在一个清单里会让图片库列表出现一堆地图。但路径防护复用同一套思路（id 白名单 + 越界检查）。
/// </para>
/// </summary>
public sealed class MapStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;

    public MapStore(string mapsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapsDirectory);

        _directory = Path.GetFullPath(mapsDirectory);
        Directory.CreateDirectory(_directory);
    }

    public string MapsDirectory => _directory;

    /// <summary>全部地图（按名字排序；坏 json 跳过而不是让整张列表挂掉）。</summary>
    public IReadOnlyList<MapAsset> List()
    {
        var assets = new List<MapAsset>();

        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (JsonSerializer.Deserialize<MapAsset>(File.ReadAllText(path), ReadOptions) is { } asset)
                {
                    assets.Add(asset);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 坏元数据不影响别的图：跳过。
            }
        }

        return assets.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public MapAsset? Find(string id) => IsValidId(id) ? List().FirstOrDefault(asset => asset.Id == id) : null;

    /// <summary>底图文件的绝对路径；id 不合法或文件不在册时返回 <c>null</c>（路径防护的第一道）。</summary>
    public string? ContentPath(string id)
    {
        if (Find(id) is not { FileName: { } fileName } asset)
        {
            // 没有底图（sketch）或不在册：都没有可读的内容文件。
            return null;
        }

        return ResolveWithin(fileName);
    }

    /// <summary>
    /// 导入一张图：类型与宽高都从字节头读，id 取内容哈希（同一张图重复上传是幂等的）。
    /// </summary>
    public async Task<(MapAsset? Asset, string? Error, bool Reused)> ImportAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        if (bytes.Length == 0)
        {
            return (null, "上传的文件是空的。", false);
        }

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 64));
        var contentType = Images.ImageBytes.Sniff(head);

        if (contentType is not ("image/webp" or "image/png"))
        {
            return (null, "只收 webp / png 的地图底图。", false);
        }

        if (!Images.ImageSize.TryRead(head, out var width, out var height))
        {
            return (null, "读不出图片尺寸（不是有效的 webp / png 头）。", false);
        }

        var id = "map-" + Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var extension = contentType == "image/png" ? ".png" : ".webp";
        var contentFileName = id + extension;
        var metaPath = Path.Combine(_directory, id + ".json");

        if (File.Exists(metaPath) && Find(id) is { } existing)
        {
            return (existing, null, true);
        }

        var contentPath = ResolveWithin(contentFileName);
        await File.WriteAllBytesAsync(contentPath, bytes, cancellationToken).ConfigureAwait(false);

        var asset = new MapAsset(
            id,
            string.IsNullOrWhiteSpace(fileName) ? id : Path.GetFileNameWithoutExtension(fileName),
            contentFileName,
            contentType,
            width,
            height,
            Calibration: null);

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(asset, WriteOptions), cancellationToken)
            .ConfigureAwait(false);

        return (asset, null, false);
    }

    /// <summary>
    /// 读完整文档（M1）。旧文件缺 <c>walls/rooms/tokens</c> 时按空数组返回——
    /// F2/F4 生成的文件必须能正常打开，不能因为少了新字段就崩。
    /// </summary>
    public MapDocument? GetDocument(string id)
    {
        if (Find(id) is not { } asset)
        {
            return null;
        }

        return new MapDocument(
            asset.Id,
            asset.Name,
            asset.FileName is null ? "sketch" : "image",
            asset.FileName is null ? null : new MapImageInfo(asset.FileName, asset.Width, asset.Height),
            asset.Calibration ?? MapCalibration.Default,
            asset.Walls ?? [],
            asset.Rooms ?? [],
            asset.Tokens ?? [],
            asset.UpdatedAt,
            asset.Fog ?? MapFog.Disabled,
            asset.Zones ?? []);
    }

    /// <summary>
    /// 整体替换墙 / 房间 / token（M1）。**校验在 <see cref="MapDocumentValidator"/> 里先做完**，
    /// 这里只负责写入——失败不做部分写入的前提是"先在内存里校验通过，再一次性落盘"。
    /// 底图与标定不受影响（它们走各自的端点）。
    /// </summary>
    public MapDocument? SaveDocument(
        string id,
        IReadOnlyList<MapWall> walls,
        IReadOnlyList<MapRoom> rooms,
        IReadOnlyList<MapToken> tokens,
        MapFog? fog = null,
        IReadOnlyList<MapZone>? zones = null)
    {
        ArgumentNullException.ThrowIfNull(walls);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(tokens);

        if (Find(id) is not { } asset)
        {
            return null;
        }

        var updated = asset with
        {
            Walls = walls,
            Rooms = rooms,
            Tokens = tokens,
            Fog = fog ?? asset.Fog,
            Zones = zones ?? asset.Zones,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // 先写临时文件再替换：写到一半断电不会留下半个 json。
        var target = Path.Combine(_directory, id + ".json");
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated, WriteOptions));
        File.Move(temporary, target, overwrite: true);

        return GetDocument(id);
    }

    /// <summary>保存标定（F2 先用 curl / F4 用标定 UI）。</summary>
    public MapAsset? SetCalibration(string id, MapCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);

        if (Find(id) is not { } asset)
        {
            return null;
        }

        var updated = asset with { Calibration = calibration };
        File.WriteAllText(Path.Combine(_directory, id + ".json"), JsonSerializer.Serialize(updated, WriteOptions));
        return updated;
    }

    /// <summary>sketch 地图的默认画布尺寸（像素）：没有底图时网格与绘制都需要一个坐标系。</summary>
    public const int BlankWidth = 2000;

    public const int BlankHeight = 1500;

    /// <summary>
    /// 新建一张**空白（手绘）**地图（M5）：没有底图文件，`image` 为 null、`mode` 推导为 `sketch`。
    /// 画布用固定尺寸（<see cref="BlankWidth"/> × <see cref="BlankHeight"/>），标定后即可画墙与房间。
    /// </summary>
    public MapAsset CreateBlank(string name)
    {
        var id = "map-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

        var asset = new MapAsset(
            id,
            string.IsNullOrWhiteSpace(name) ? "未命名地图" : name.Trim(),
            FileName: null,
            ContentType: null,
            BlankWidth,
            BlankHeight,
            // 默认标定（100px/格、5 尺/格、无偏移）：列表与文档都拿得到同一份，
            // 否则网格画不出来（列表里 calibration 为 null → board 不画网格）。
            Calibration: MapCalibration.Default);

        File.WriteAllText(Path.Combine(_directory, id + ".json"), JsonSerializer.Serialize(asset, WriteOptions));
        return asset;
    }

    /// <summary>删除一张图与它的元数据；不存在返回 <c>false</c>。</summary>
    public bool Delete(string id)
    {
        if (Find(id) is not { } asset)
        {
            return false;
        }

        if (asset.FileName is { } fileName)
        {
            TryDelete(ResolveWithin(fileName));
        }

        TryDelete(Path.Combine(_directory, id + ".json"));
        return true;
    }

    /// <summary>id 白名单：只允许 <c>map-</c> + 十六进制，杜绝 <c>..</c> 与绝对路径。</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.StartsWith("map-", StringComparison.Ordinal)
        && id.Length <= 32
        && id[4..].All(char.IsAsciiHexDigitLower);

    private string ResolveWithin(string fileName)
    {
        var full = Path.GetFullPath(Path.Combine(_directory, fileName));

        if (!full.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"地图文件越出了 maps 目录：{fileName}");
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 文件被占用：删除失败不致命（下次列表会因为有 json 而显示，但内容缺失 → 端点给 404）。
        }
    }
}
