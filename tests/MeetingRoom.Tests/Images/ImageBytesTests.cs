using MeetingRoom.Core.Images;

namespace MeetingRoom.Tests.Images;

/// <summary>
/// 类型判定以**内容**为准（设计方案 §六 的三个真坑之一）：
/// 上游可能返回 JPEG 却不带 <c>media_type</c>，文件名也可能骗人。
/// </summary>
public class ImageBytesTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    private static readonly byte[] WebpHeader =
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x1A, 0x00, 0x00, 0x00,
        (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)' ',
    ];

    [Fact]
    public void Sniff_RecognizesPng()
    {
        Assert.Equal("image/png", ImageBytes.Sniff(PngHeader));
    }

    [Fact]
    public void Sniff_RecognizesJpeg()
    {
        Assert.Equal("image/jpeg", ImageBytes.Sniff(JpegHeader));
    }

    [Fact]
    public void Sniff_RecognizesWebp()
    {
        Assert.Equal("image/webp", ImageBytes.Sniff(WebpHeader));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("\uFEFF  <svg></svg>")]
    public void Sniff_RecognizesSvg(string text)
    {
        Assert.Equal("image/svg+xml", ImageBytes.Sniff(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("这不是图片，只是一段中文文本")]
    [InlineData("MZ\u0090\u0000\u0003")]
    [InlineData("<html><body>hi</body></html>")]
    public void Sniff_RejectsNonImages(string text)
    {
        Assert.Null(ImageBytes.Sniff(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Sniff_RejectsEmptyInput()
    {
        Assert.Null(ImageBytes.Sniff([]));
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("application/octet-stream", ".bin")]
    public void ExtensionFor_MapsKnownTypes(string mediaType, string expected)
    {
        Assert.Equal(expected, ImageBytes.ExtensionFor(mediaType));
    }

    // ---- ImageSource：内容与上限的校验 ----

    [Fact]
    public async Task ImageSource_ReadAsync_JudgesTypeByContentNotFileName()
    {
        // 文件名说 png，内容其实是 JPEG —— 判定必须按内容。
        var source = ImageSource.FromStream("fake.png", new MemoryStream(JpegHeader), 1024);

        var (bytes, mediaType) = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(JpegHeader.Length, bytes.Length);
        Assert.Equal("image/jpeg", mediaType);
    }

    [Fact]
    public async Task ImageSource_ReadAsync_RejectsEmptyContent()
    {
        var source = ImageSource.FromStream("empty.png", new MemoryStream([]), 1024);

        await Assert.ThrowsAsync<NotSupportedException>(() => source.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ImageSource_ReadAsync_RejectsOversizeContent()
    {
        var source = ImageSource.FromStream("big.png", new MemoryStream(PngHeader), maxBytes: 4);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => source.ReadAsync(CancellationToken.None));
        Assert.Contains("超过上限", error.Message);
    }

    [Fact]
    public async Task ImageSource_ReadAsync_RejectsUnknownContent()
    {
        var source = ImageSource.FromStream("notes.txt", new MemoryStream("hello"u8.ToArray()), 1024);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => source.ReadAsync(CancellationToken.None));
        Assert.Contains("不是可识别的图片", error.Message);
    }
}
