using MeetingRoom.Core.Maps;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Maps;

/// <summary>
/// 战术板底图（F2，设计稿 §5.1）。存储在工作空间 <c>maps/</c>，**与生成的图片分开**。
/// <para>
/// 返回的 <c>width</c> / <c>height</c> 都来自**字节头**（<see cref="Core.Images.ImageSize"/>），
/// 不接受前端传值——前端传错尺寸会让网格与 token 全部错位，而这类错误极难发现。
/// </para>
/// </summary>
public static class MapEndpoints
{
    public static void MapMapEndpoints(this WebApplication app)
    {
        var maps = app.MapGroup("/api/maps");

        // 上传：webp / png，一次一张（地图不像参考图需要批量）。
        maps.MapPost("/upload", async Task<IResult> (HttpRequest request, SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间；地图存在工作空间的 maps/ 下。" });
            }

            var store = new MapStore(workspace.Layout.MapsDir);

            try
            {
                Stream stream;
                string fileName;

                if (request.HasFormContentType)
                {
                    // 与图片上传同一条理由：手动读表单，绕开 [FromForm] 的 antiforgery 要求。
                    // 注意：multipart 超过一定体积会落到 %TEMP%，沙箱环境里可能不可写——
                    // 所以下面还留了一条"原始字节"通道给脚本与受限环境用。
                    var form = await request.ReadFormAsync(ct).ConfigureAwait(false);

                    if (form.Files.Count != 1)
                    {
                        return Results.BadRequest(new { error = "一次上传一张底图。" });
                    }

                    stream = form.Files[0].OpenReadStream();
                    fileName = form.Files[0].FileName;
                }
                else
                {
                    // 原始字节：curl --data-binary @map.webp "…/api/maps/upload?name=map.webp"
                    stream = request.Body;
                    fileName = request.Query["name"].ToString();
                }

                await using var owned = stream as IAsyncDisposable;
                var (asset, error, reused) = await store.ImportAsync(stream, fileName, ct)
                    .ConfigureAwait(false);

                if (asset is null)
                {
                    return Results.BadRequest(new { error });
                }

                return Results.Ok(new
                {
                    id = asset.Id,
                    width = asset.Width,
                    height = asset.Height,
                    name = asset.Name,
                    contentType = asset.ContentType,
                    reused,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 新建空白（手绘）地图（M5）：没有底图文件，`image` 为 null、`mode = sketch`。
        maps.MapPost("/blank", (MapBlankBody body, SessionHub hub) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间；地图存在工作空间的 maps/ 下。" });
            }

            var asset = new MapStore(workspace.Layout.MapsDir).CreateBlank(body.Name ?? string.Empty);

            return Results.Ok(new
            {
                id = asset.Id,
                name = asset.Name,
                width = asset.Width,
                height = asset.Height,
                mode = "sketch",
            });
        });

        maps.MapGet("/", (SessionHub hub) =>
            hub.Workspace is not { } workspace
                ? Results.Conflict(new { error = "还没有打开工作空间。" })
                : Results.Ok(new MapStore(workspace.Layout.MapsDir).List().Select(asset => new
                {
                    id = asset.Id,
                    name = asset.Name,
                    width = asset.Width,
                    height = asset.Height,
                    calibrated = asset.Calibrated,
                    // M5：image / sketch 由有没有底图文件推导，列表与文档口径一致。
                    mode = asset.FileName is null ? "sketch" : "image",
                    // 标定参数一起给：只给布尔的话前端画不出网格（F2 踩过）。
                    calibration = asset.Calibration,
                    // 「已归档」列的地图列表要显示最后修改时间（本批新增；不是新端点，只多一个字段）。
                    updatedAt = asset.UpdatedAt,
                }).ToArray()));

        // 原图：Content-Type 按扩展名给（webp / png 都在 ContentType 里存着）。
        maps.MapGet("/{id}/content", (string id, SessionHub hub) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间。" });
            }

            var store = new MapStore(workspace.Layout.MapsDir);
            var asset = store.Find(id);
            var path = store.ContentPath(id);

            if (asset is null || path is null || !File.Exists(path))
            {
                return Results.NotFound(new { error = $"没有这张地图：{id}" });
            }

            return Results.File(path, asset.ContentType);
        });

        // 标定（F2 先用 curl 写，F4 做 UI）。
        maps.MapPut("/{id}/calibration", (string id, MapCalibrationBody body, SessionHub hub) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间。" });
            }

            if (body.Dpi <= 0)
            {
                return Results.BadRequest(new { error = "dpi 必须大于 0（0 表示还没标定）。" });
            }

            var calibration = new MapCalibration(
                body.Dpi,
                body.OffsetX,
                body.OffsetY,
                string.IsNullOrWhiteSpace(body.GridType) ? "square" : body.GridType,
                body.Scale <= 0 ? 5 : body.Scale);

            var updated = new MapStore(workspace.Layout.MapsDir).SetCalibration(id, calibration);

            return updated is null
                ? Results.NotFound(new { error = $"没有这张地图：{id}" })
                : Results.Ok(new
                {
                    id = updated.Id,
                    calibrated = updated.Calibrated,
                    calibration,
                });
        });

        // 合成视图（M8a）：给"模型看图"用的 PNG。**不含 token、不含灰色迷雾**——
        // 这两类每轮都在变，画进去图就每轮不同、缓存永远失效。
        maps.MapGet("/{id}/view", (string id, string? mode, int? maxPx, SessionHub hub) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间。" });
            }

            var store = new MapStore(workspace.Layout.MapsDir);
            var document = store.GetDocument(id);

            if (document is null)
            {
                return Results.NotFound(new { error = $"没有这张地图：{id}" });
            }

            var playerView = string.Equals(mode, "player", StringComparison.OrdinalIgnoreCase);
            var pixels = Math.Clamp(maxPx ?? MapViewRenderer.DefaultMaxPx, MapViewRenderer.MinMaxPx, MapViewRenderer.MaxMaxPx);

            // 缓存键：文档没有独立 revision 字段，用 `UpdatedAt` 做等价键（每次保存都会更新它）。
            var key = $"{id}|{document.UpdatedAt}|{(playerView ? "player" : "dm")}|{pixels}";

            if (MapViewCache.Current.TryGet(key) is { } cached)
            {
                return Results.File(cached, "image/png");
            }

            MapImageSource? source = null;

            if (document.Image is { } image && store.ContentPath(id) is { } path && File.Exists(path))
            {
                source = new MapImageSource(File.ReadAllBytes(path));
            }

            var png = MapViewRenderer.Render(document, source, playerView, pixels);

            if (png is null)
            {
                // 未标定 / player 视角下没有任何已探索格：不出图（设计 §5.2）。
                return Results.NoContent();
            }

            MapViewCache.Current.Put(key, png);
            return Results.File(png, "image/png");
        });

        // 完整文档（M1）：底图 + 网格 + 墙 / 房间 / token。旧文件缺三个字段时返回空数组。
        maps.MapGet("/{id}/document", (string id, SessionHub hub) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间。" });
            }

            var document = new MapStore(workspace.Layout.MapsDir).GetDocument(id);

            return document is null
                ? Results.NotFound(new { error = $"没有这张地图：{id}" })
                : Results.Ok(document);
        });

        // 整体替换（M1）：**先校验、后写入**——失败不做部分写入。
        maps.MapPut("/{id}/document", (string id, MapDocumentBody body, SessionHub hub) =>
        {
            if (hub.Workspace is not { } workspace)
            {
                return Results.Conflict(new { error = "还没有打开工作空间。" });
            }

            var store = new MapStore(workspace.Layout.MapsDir);
            var walls = body.Walls ?? [];
            var rooms = body.Rooms ?? [];
            var tokens = body.Tokens ?? [];

            // 已知角色 = 当前叙事会话的在场角色。没有会话时为空 → 任何 token 都会被拒，
            // 这是有意的：地图上的 token 必须能对应到一个真人/真角色。
            var known = hub.Roleplay?.ActorIds ?? [];
            var current = store.GetDocument(id);

            var (ok, error) = MapDocumentValidator.Validate(
                walls,
                rooms,
                tokens,
                known,
                body.Fog,
                body.Zones,
                current?.Image?.Width ?? MapStore.BlankWidth,
                current?.Image?.Height ?? MapStore.BlankHeight,
                current?.Grid);

            if (!ok)
            {
                return Results.BadRequest(new { error });
            }

            var saved = store.SaveDocument(id, walls, rooms, tokens, body.Fog, body.Zones);

            // M4：编辑器保存后立刻推一次 roleplay 快照，叙事侧的战术板 2 秒内跟上
            // （不靠轮询，也不让前端自己再拉一份 document）。
            if (saved is not null)
            {
                hub.Roleplay?.NotifyMapChanged();
            }

            return saved is null
                ? Results.NotFound(new { error = $"没有这张地图：{id}" })
                : Results.Ok(saved);
        });

        maps.MapDelete("/{id}", (string id, SessionHub hub) =>
            hub.Workspace is not { } workspace
                ? Results.Conflict(new { error = "还没有打开工作空间。" })
                : new MapStore(workspace.Layout.MapsDir).Delete(id)
                    ? Results.Ok(new { deleted = id })
                    : Results.NotFound(new { error = $"没有这张地图：{id}" }));
    }
}

/// <summary>标定写入的请求体（camelCase：<c>dpi / offsetX / offsetY / gridType / scale</c>）。</summary>
public sealed record MapCalibrationBody(
    double Dpi,
    double OffsetX,
    double OffsetY,
    string? GridType,
    double Scale);

/// <summary>
/// 文档写入的请求体（M1）：只收墙 / 房间 / token——底图与标定走各自的端点，
/// 防止"保存 token 时把标定也顺手改回默认值"。
/// </summary>
public sealed record MapDocumentBody(
    IReadOnlyList<MapWall>? Walls,
    IReadOnlyList<MapRoom>? Rooms,
    IReadOnlyList<MapToken>? Tokens,
    MeetingRoom.Core.Maps.MapFog? Fog = null,
    IReadOnlyList<MeetingRoom.Core.Maps.MapZone>? Zones = null);

/// <summary>新建空白地图的请求体（M5）：只要一个名字。</summary>
public sealed record MapBlankBody(string? Name);

/// <summary>
/// 合成视图的内存 LRU（M8a，容量 8）：键 <c>(mapId, updatedAt, mode, maxPx)</c>。
/// 文档一保存 <c>UpdatedAt</c> 就变，旧图自然淘汰——不必单独维护失效逻辑。
/// </summary>
public sealed class MapViewCache
{
    private const int Capacity = 8;

    private readonly Lock _gate = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public static MapViewCache Current { get; } = new();

    public byte[]? TryGet(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var bytes))
            {
                return null;
            }

            _order.Remove(key);
            _order.Add(key);
            return bytes;
        }
    }

    public void Put(string key, byte[] bytes)
    {
        lock (_gate)
        {
            _entries[key] = bytes;
            _order.Remove(key);
            _order.Add(key);

            while (_order.Count > Capacity)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>诊断用：当前缓存了几张。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }
}
