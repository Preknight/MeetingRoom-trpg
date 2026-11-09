using MeetingRoom.Core.Images;

namespace MeetingRoom.Host.Images;

/// <summary>
/// 图片模块的端点（设计方案 §四）。分组挂在 <c>/api/images</c> 下，六个端点：
/// reference（路径导入）/ upload（字节上传）/ generate（发起生成）/ list / content / delete。
/// <para>
/// 业务逻辑一律在 <see cref="ImageStudio"/> 与 <see cref="ImageAssetStore"/> 里 ——
/// 端点只做参数校验与投影，这样领域层可以脱离 HTTP 单测。
/// </para>
/// <para>
/// <b>没有 save-as 端点</b>：前端用 <c>&lt;a download href="/api/images/{id}/content"&gt;</c> 就能"另存为"，
/// 不必让服务端去弹一个"另存为"对话框（少一条 PowerShell 依赖与 2 分钟超时路径）。
/// </para>
/// </summary>
public static class ImageEndpoints
{
    public static void MapImageEndpoints(this WebApplication app)
    {
        var images = app.MapGroup("/api/images");

        // ① 路径导入：原生对话框给的是**绝对路径**（网页侧永远拿不到路径，见 SystemDialogEndpoints 的注释）。
        images.MapPost("/reference", async Task<IResult> (
            ReferenceRequest request,
            ImageStudio studio,
            CancellationToken ct) =>
        {
            if (!studio.TryGetStore(out var store, out var error))
            {
                return Results.Conflict(new { error });
            }

            var paths = request.Paths ?? [];
            if (paths.Count == 0)
            {
                return Results.BadRequest(new { error = "paths 不能为空。" });
            }

            var imported = new List<object>();
            var rejected = new List<object>();

            foreach (var path in paths)
            {
                try
                {
                    var (asset, reused) = await store
                        .ImportAsync(ImageSource.FromPath(path, studio.MaxReferenceBytes), ct)
                        .ConfigureAwait(false);

                    imported.Add(new { id = asset.Id, path = asset.Path, reused });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
                {
                    // 单条失败不打断其余（用户一次可能选了多张，其中一张不是图片）。
                    rejected.Add(new { path, reason = ex.Message });
                }
            }

            return Results.Ok(new { imported, rejected });
        });

        // ② 字节上传：拖拽 / <input type=file> / 粘贴 —— 这三条路拿不到路径，只能把字节发上来。
        images.MapPost("/upload", async Task<IResult> (
            HttpRequest request,
            ImageStudio studio,
            CancellationToken ct) =>
        {
            if (!studio.TryGetStore(out var store, out var error))
            {
                return Results.Conflict(new { error });
            }

            // **手动读表单**：用 IFormFile 参数绑定会走 [FromForm] 的绑定路径、新版 ASP.NET Core
            // 对它会要求 antiforgery；手动读取就绕开了（设计方案 §四）。
            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);

            var imported = new List<object>();
            var rejected = new List<object>();

            foreach (var file in form.Files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    var (asset, reused) = await store
                        .ImportAsync(ImageSource.FromStream(file.FileName, stream, studio.MaxReferenceBytes), ct)
                        .ConfigureAwait(false);

                    imported.Add(new { id = asset.Id, path = asset.Path, reused });
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
                {
                    rejected.Add(new { path = file.FileName, reason = ex.Message });
                }
            }

            return Results.Ok(new { imported, rejected });
        });

        // ③ 发起生成：立即回 jobId，结果走 SSE（image.job → image.generated / image.failed）。
        images.MapPost("/generate", (GenerateImageRequest request, ImageStudio studio) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.BadRequest(new { error = "prompt 不能为空。" });
            }

            var domain = new ImageGenerationRequest
            {
                Prompt = request.Prompt,
                ReferenceIds = request.ReferenceIds ?? [],
                AspectRatio = request.AspectRatio,
                Resolution = request.Resolution,
                Quality = request.Quality,
                Count = request.Count ?? 1,
                Model = request.Model,
                Seed = request.Seed,
            };

            var (jobId, error) = studio.Start(domain);
            return error is null ? Results.Ok(new { jobId }) : Results.Conflict(new { error });
        });

        // ④ 资产列表（不含字节）——前端首次连接与刷新用；SSE 上还有 image.list 快照。
        images.MapGet("/", (ImageStudio studio) =>
            studio.TryGetStore(out var store, out var error)
                ? Results.Ok(new { images = store.WireList() })
                : Results.Conflict(new { error }));

        // ⑤ 图片二进制。Content-Type 用嗅探结果；ETag 用内容哈希，前端拿 sha256 前 8 位当缓存键。
        images.MapGet("/{id}/content", (string id, HttpContext ctx, ImageStudio studio) =>
        {
            if (!studio.TryGetStore(out var store, out var error))
            {
                return Results.Conflict(new { error });
            }

            if (store.Find(id) is not { } asset)
            {
                return Results.NotFound(new { error = $"没有这张图片：{id}" });
            }

            byte[] bytes;
            try
            {
                bytes = store.ReadBytes(id);
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException)
            {
                return Results.NotFound(new { error = ex.Message });
            }

            // 本项目所有动态端点都不许缓存（与 wwwroot 的既有策略一致）。
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";

            return Results.File(
                bytes,
                contentType: asset.MediaType,
                enableRangeProcessing: false,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{asset.Sha256[..16]}\""));
        });

        // ⑥ 删除（用户 2026-10 拍板要做）：删文件 + manifest 条目。
        images.MapPost("/{id}/delete", async Task<IResult> (
            string id,
            ImageStudio studio,
            CancellationToken ct) =>
        {
            if (!studio.TryGetStore(out var store, out var error))
            {
                return Results.Conflict(new { error });
            }

            return await store.DeleteAsync(id, ct).ConfigureAwait(false)
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new { error = $"没有这张图片：{id}" });
        });
    }
}

/// <summary>路径导入的请求体：来自系统对话框（或用户手输）的绝对路径。</summary>
public sealed record ReferenceRequest(IReadOnlyList<string>? Paths = null);

/// <summary>发起生成的请求体（字段名与前端 camelCase 对齐）。</summary>
public sealed record GenerateImageRequest(
    string Prompt,
    IReadOnlyList<string>? ReferenceIds = null,
    string? AspectRatio = null,
    string? Resolution = null,
    string? Quality = null,
    int? Count = null,
    string? Model = null,
    int? Seed = null);
