using System.Text.Json;
using MeetingRoom.Core.Images;

namespace MeetingRoom.Tests.Images;

/// <summary>
/// 图片资产库：目录骨架、id 顺延、去重、删除、清单损坏、以及**那条实测教训**
/// （读图片必须 <c>FileShare.ReadWrite</c>，见《实施进度与待办》风险 9）。
/// </summary>
public class ImageAssetStoreTests : IDisposable
{
    private static readonly byte[] PngA =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04];

    private static readonly byte[] PngB =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x09, 0x08, 0x07, 0x06];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "meetingroom-images-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ImageAssetStore NewStore() => new(Path.Combine(_root, "images"));

    private static ImageSource Source(byte[] bytes) =>
        ImageSource.FromStream("test.png", new MemoryStream(bytes), 1024 * 1024);

    [Fact]
    public void Constructor_CreatesRefsOutAndManifest()
    {
        var store = NewStore();

        Assert.True(Directory.Exists(store.RefsDirectory));
        Assert.True(Directory.Exists(store.OutDirectory));
        Assert.True(File.Exists(store.ManifestPath));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Import_AssignsSequentialIdsAndWritesFiles()
    {
        var store = NewStore();

        var (first, reusedFirst) = await store.ImportAsync(Source(PngA), CancellationToken.None);
        var (second, reusedSecond) = await store.ImportAsync(Source(PngB), CancellationToken.None);

        Assert.False(reusedFirst);
        Assert.False(reusedSecond);
        Assert.Equal("img-0001", first.Id);
        Assert.Equal("img-0002", second.Id);
        Assert.Equal(ImageAssetKinds.Reference, first.Kind);
        Assert.Equal("image/png", first.MediaType);
        Assert.Equal(2, store.List().Count);

        foreach (var asset in store.List())
        {
            Assert.True(File.Exists(Path.Combine(store.ImagesDirectory, asset.Path)), asset.Path);
        }
    }

    [Fact]
    public async Task Import_SameBytesTwice_IsIdempotent()
    {
        var store = NewStore();

        var (first, _) = await store.ImportAsync(Source(PngA), CancellationToken.None);
        var (again, reused) = await store.ImportAsync(Source(PngA), CancellationToken.None);

        Assert.True(reused);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(store.List());
    }

    [Fact]
    public async Task Import_ConcurrentSameBytes_LeavesOneAsset()
    {
        var store = NewStore();

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => store.ImportAsync(Source(PngA), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Single(store.List());
        Assert.Single(results.Select(r => r.Asset.Id).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadBytes_NeedsFileShareReadWrite_WhenWriterStillHoldsHandle()
    {
        var store = NewStore();
        var (asset, _) = await store.ImportAsync(Source(PngA), CancellationToken.None);
        var full = Path.Combine(store.ImagesDirectory, asset.Path);

        // 写者以 FileShare.Read 打开（只允许别人读）——正是 TranscriptWriter 的打开方式。
        using var writer = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.Read);

        // ① 读者若用默认共享模式（FileShare.Read）会被拒：它的共享模式不允许写者已持有的 Write 访问。
        Assert.Throws<IOException>(() =>
        {
            using var reader = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        });

        // ② 读者用 FileShare.ReadWrite 就能读 —— 这就是 ImageAssetStore.ReadBytes 的写法。
        var bytes = store.ReadBytes(asset.Id);
        Assert.Equal(PngA, bytes);
    }

    [Fact]
    public async Task Delete_RemovesFileAndManifestEntry()
    {
        var store = NewStore();
        var (asset, _) = await store.ImportAsync(Source(PngA), CancellationToken.None);
        var full = Path.Combine(store.ImagesDirectory, asset.Path);

        Assert.True(await store.DeleteAsync(asset.Id, CancellationToken.None));

        Assert.Null(store.Find(asset.Id));
        Assert.Empty(store.List());
        Assert.False(File.Exists(full));
        Assert.DoesNotContain(asset.Id, File.ReadAllText(store.ManifestPath));
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        var store = NewStore();

        Assert.False(await store.DeleteAsync("img-9999", CancellationToken.None));
    }

    [Fact]
    public async Task Manifest_SurvivesReload()
    {
        var path = Path.Combine(_root, "images");
        var first = new ImageAssetStore(path);
        await first.ImportAsync(Source(PngA), CancellationToken.None);

        var again = new ImageAssetStore(path);

        Assert.Single(again.List());
        Assert.Equal("img-0001", again.List()[0].Id);
    }

    [Fact]
    public void CorruptManifest_Throws()
    {
        var path = Path.Combine(_root, "images");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ImageAssetStore.ManifestFileName), "{ 这不是 JSON");

        Assert.Throws<InvalidDataException>(() => new ImageAssetStore(path));
    }

    [Fact]
    public async Task WireProjection_NeverCarriesBytes()
    {
        var store = NewStore();
        var (asset, _) = await store.ImportAsync(Source(PngA), CancellationToken.None);

        var wire = JsonSerializer.Serialize(store.WireList());

        // 投影里只有元信息 + 缓存键 v；base64 一个字节都不该出现。
        Assert.Contains(asset.Id, wire, StringComparison.Ordinal);
        Assert.Contains("\"v\":\"" + asset.Sha256[..8] + "\"", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(PngA), wire, StringComparison.Ordinal);
    }
}
