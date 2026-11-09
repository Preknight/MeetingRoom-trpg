using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeetingRoom.Core.Events;

namespace MeetingRoom.Core.Images;

/// <summary>登记一张生成结果所需的全部信息（参数收在一个 record 里，免得方法长成一行十个参数）。</summary>
public sealed record GeneratedAssetSpec(
    byte[] Bytes,
    string MediaType,
    string Prompt,
    string Model,
    string Provider,
    IReadOnlyList<string> ReferenceIds,
    double? Cost);

/// <summary>
/// 图片资产库（设计方案 §三）：<c>refs/</c>（参考图）+ <c>out/</c>（生成结果）+ <c>manifest.json</c>。
/// 三条不变式：
/// <list type="number">
///   <item>任何 <see cref="ImageAsset.Path"/> 必须落在 <c>images/</c> 内 —— 由 <see cref="ImagePathGuard"/> 强制；</item>
///   <item>manifest 先写 <c>.tmp</c> 再替换，崩溃不留半截；</item>
///   <item>文件先落盘、再改 manifest：文件写入失败不回滚已写好的文件，内存与磁盘保持一致。</item>
/// </list>
/// 另有一条<b>实测教训</b>：读文件一律 <see cref="FileShare.ReadWrite"/> ——
/// 写者以 <c>FileShare.Read</c> 打开时，读者用默认方式打不开（见《实施进度与待办》风险 9）。
/// </summary>
public sealed class ImageAssetStore
{
    public const string ManifestFileName = "manifest.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEventBus? _bus;
    private ImageManifest _manifest;

    public ImageAssetStore(string imagesDirectory, IEventBus? bus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesDirectory);

        ImagesDirectory = Path.GetFullPath(imagesDirectory);
        RefsDirectory = Path.Combine(ImagesDirectory, "refs");
        OutDirectory = Path.Combine(ImagesDirectory, "out");
        ManifestPath = Path.Combine(ImagesDirectory, ManifestFileName);
        _bus = bus;

        Directory.CreateDirectory(RefsDirectory);
        Directory.CreateDirectory(OutDirectory);

        var loaded = LoadManifest();
        _manifest = loaded ?? new ImageManifest();

        if (loaded is null)
        {
            // manifest 第一次就落盘：它的存在即"这个工作空间有图片区"的标志（同 MaterialStore 的做法）。
            SaveManifestSync(_manifest);
        }
    }

    /// <summary>图片区根目录（<c>&lt;工作空间&gt;/images</c>）。</summary>
    public string ImagesDirectory { get; }

    public string RefsDirectory { get; }

    public string OutDirectory { get; }

    public string ManifestPath { get; }

    /// <summary>资产索引（不含字节）。</summary>
    public IReadOnlyList<ImageAsset> List() => _manifest.Images.ToArray();

    /// <summary>给前端 / SSE 的投影列表（`image.list` 与 <c>GET /api/images</c> 共用同一份）。</summary>
    public IReadOnlyList<object> WireList() => _manifest.Images.Select(ImageAssetWire.Of).ToArray();

    public ImageAsset? Find(string id) =>
        _manifest.Images.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 导入一张参考图（两条通道共用）。按 <c>sha256</c> 去重：命中时什么都不写，返回既有资产并标 <c>Reused</c>。
    /// </summary>
    public async Task<(ImageAsset Asset, bool Reused)> ImportAsync(
        ImageSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 读盘与校验放在锁外：一张不合法的图不该占着队列。
        var (bytes, mediaType) = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sha = HashOf(bytes);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_manifest.Images.FirstOrDefault(
                    a => a.Kind == ImageAssetKinds.Reference && string.Equals(a.Sha256, sha, StringComparison.Ordinal))
                is { } existing)
            {
                return (existing, true);
            }

            var fileName = sha[..16] + ImageBytes.ExtensionFor(mediaType);
            var full = Path.Combine(RefsDirectory, fileName);

            await File.WriteAllBytesAsync(full, bytes, cancellationToken).ConfigureAwait(false);

            var asset = new ImageAsset
            {
                Id = NextId(),
                Kind = ImageAssetKinds.Reference,
                Path = "refs/" + fileName,
                MediaType = mediaType,
                Bytes = bytes.LongLength,
                Sha256 = sha,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await AppendAsync(asset, cancellationToken).ConfigureAwait(false);
            return (asset, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>登记一张生成结果（字节已在调用方手里）。</summary>
    public async Task<ImageAsset> AddGeneratedAsync(
        GeneratedAssetSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = NextId();
            var fileName = id + ImageBytes.ExtensionFor(spec.MediaType);
            var full = Path.Combine(OutDirectory, fileName);

            await File.WriteAllBytesAsync(full, spec.Bytes, cancellationToken).ConfigureAwait(false);

            var asset = new ImageAsset
            {
                Id = id,
                Kind = ImageAssetKinds.Generated,
                Path = "out/" + fileName,
                MediaType = spec.MediaType,
                Bytes = spec.Bytes.LongLength,
                Sha256 = HashOf(spec.Bytes),
                Prompt = spec.Prompt,
                Model = spec.Model,
                ReferenceIds = spec.ReferenceIds,
                Provider = spec.Provider,
                Cost = spec.Cost,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await AppendAsync(asset, cancellationToken).ConfigureAwait(false);
            return asset;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删掉一条资产：先删文件（不在也算成功 —— 用户可能自己删过），再摘掉 manifest 条目。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var asset = Find(id);
            if (asset is null)
            {
                return false;
            }

            var full = ImagePathGuard.ResolveWithin(ImagesDirectory, asset.Path);
            if (File.Exists(full))
            {
                File.Delete(full);
            }

            var next = _manifest with
            {
                Images = _manifest.Images
                    .Where(a => !string.Equals(a.Id, id, StringComparison.Ordinal))
                    .ToArray(),
            };

            await SaveManifestAsync(next, cancellationToken).ConfigureAwait(false);
            _manifest = next;
            PublishList();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读字节。<b>必须 <see cref="FileShare.ReadWrite"/></b>：生成器可能刚写完，别处可能正在读。</summary>
    public byte[] ReadBytes(string id)
    {
        var asset = Find(id) ?? throw new KeyNotFoundException($"图片 {id} 不存在。");
        var full = ImagePathGuard.ResolveWithin(ImagesDirectory, asset.Path);

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>把参考图 id 读成字节（生成时内联成 data URL）。找不到的 id 跳过，不打断整次生成。</summary>
    public IReadOnlyList<ReferenceImage> ReadReferenceImages(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var list = new List<ReferenceImage>(ids.Count);
        foreach (var id in ids)
        {
            if (Find(id) is { } asset)
            {
                list.Add(new ReferenceImage(asset.MediaType, ReadBytes(id)));
            }
        }

        return list;
    }

    private string NextId() => $"img-{_manifest.NextImageNumber:D4}";

    private async Task AppendAsync(ImageAsset asset, CancellationToken cancellationToken)
    {
        var next = _manifest with
        {
            Images = [.. _manifest.Images, asset],
            NextImageNumber = _manifest.NextImageNumber + 1,
        };

        await SaveManifestAsync(next, cancellationToken).ConfigureAwait(false);
        _manifest = next;
        PublishList();
    }

    private static async Task SaveManifestAsync(
        ImageManifest manifest,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var temporary = manifestPath + ".tmp";

        await File.WriteAllTextAsync(temporary, json, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, manifestPath, overwrite: true);
    }

    private Task SaveManifestAsync(ImageManifest manifest, CancellationToken cancellationToken) =>
        SaveManifestAsync(manifest, ManifestPath, cancellationToken);

    /// <summary>构造期专用：此刻还没有并发读者，同步写一次即可（同 MaterialStore 的做法）。</summary>
    private void SaveManifestSync(ImageManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var temporary = ManifestPath + ".tmp";

        File.WriteAllText(temporary, json, Utf8NoBom);
        File.Move(temporary, ManifestPath, overwrite: true);
    }

    private ImageManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        var json = File.ReadAllText(ManifestPath);

        try
        {
            return JsonSerializer.Deserialize<ImageManifest>(json, JsonOptions)
                ?? throw new InvalidDataException($"图片清单是空的：{ManifestPath}");
        }
        catch (JsonException ex)
        {
            // 非法 JSON 时 JsonSerializer 抛的是 JsonException —— 包一层换成带路径的可读错误，
            // 免得排错时只看到"$ 处 0xE8 不是合法的属性名开头"却不知道是哪个文件。
            throw new InvalidDataException($"图片清单解析失败：{ManifestPath}（{ex.Message}）", ex);
        }
    }

    private void PublishList() =>
        _bus?.Publish("image.list", new { images = WireList() });

    private static string HashOf(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
