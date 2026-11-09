using MeetingRoom.Core.Images;

namespace MeetingRoom.Tests.Images;

/// <summary>
/// <see cref="ImageSize"/> 的三种 WebP 变体 + PNG：**每个分支各留一条样本**（§F2 复盘建议）。
/// <para>
/// 样本都是手工构造的最小头部（不依赖真实图片），所以它们锁定的是"偏移与字节序"，
/// 正是上一轮出错的地方（VP8 的起始码在 23-25，不是 20-22）。
/// </para>
/// </summary>
public sealed class ImageSizeTests
{
    [Fact]
    public void ReadsPngSize()
    {
        // 8 字节签名 + 长度(4) + "IHDR" + 宽(4 大端) + 高(4 大端)
        byte[] head =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x03, 0x20,   // 800
            0x00, 0x00, 0x02, 0x58,   // 600
        ];

        Assert.True(ImageSize.TryRead(head, out var width, out var height));
        Assert.Equal(800, width);
        Assert.Equal(600, height);
    }

    [Fact]
    public void ReadsVp8Size()
    {
        // RIFF + size + WEBP + "VP8 " + chunkSize + **3 字节 frame tag** + 起始码 9D 01 2A + 宽 + 高
        // 真实样本：VEoR 图，4096×2784。
        byte[] head =
        [
            0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00,   // RIFF ....
            0x57, 0x45, 0x42, 0x50,                           // WEBP
            0x56, 0x50, 0x38, 0x20, 0x00, 0x00, 0x00, 0x00,   // "VP8 " + size
            0x70, 0x77, 0x57,                                 // frame tag
            0x9D, 0x01, 0x2A,                                 // 起始码
            0x00, 0x10,                                       // 宽 0x1000 = 4096
            0xE0, 0x0A,                                       // 高 0x0AE0 = 2784
        ];

        Assert.True(ImageSize.TryRead(head, out var width, out var height));
        Assert.Equal(4096, width);
        Assert.Equal(2784, height);
    }

    [Fact]
    public void ReadsVp8lSize()
    {
        // "VP8L" + size + 0x2F 签名 + 4 字节：14 位宽-1 + 14 位高-1
        // 100×50 → (99) | (49 << 14) = 0x000C4063
        byte[] head =
        [
            0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50,
            0x56, 0x50, 0x38, 0x4C, 0x00, 0x00, 0x00, 0x00,   // "VP8L" + size
            0x2F,                                             // 签名
            0x63, 0x40, 0x0C, 0x00,                           // bits（小端）
        ];

        Assert.True(ImageSize.TryRead(head, out var width, out var height));
        Assert.Equal(100, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void ReadsVp8xSize()
    {
        // "VP8X" + size + 4 字节 flags + 3 字节宽-1 + 3 字节高-1（都小端）
        // 1920×1080 → 1919 = 0x00077F、1079 = 0x000437
        byte[] head =
        [
            0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50,
            0x56, 0x50, 0x38, 0x58, 0x00, 0x00, 0x00, 0x00,   // "VP8X" + size
            0x00, 0x00, 0x00, 0x00,                           // flags
            0x7F, 0x07, 0x00,                                 // 宽-1 = 1919
            0x37, 0x04, 0x00,                                 // 高-1 = 1079
        ];

        Assert.True(ImageSize.TryRead(head, out var width, out var height));
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void RejectsTruncatedWebp()
    {
        // RIFF/WEBP/"VP8 " 三样都在，但**后面什么都没有**（只有 20 字节）。
        // 这条专门堵"偏移算错了、却正好从越界位置读出个数"的可能。
        byte[] head =
        [
            0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50,
            0x56, 0x50, 0x38, 0x20, 0x00, 0x00, 0x00, 0x00,
        ];

        Assert.False(ImageSize.TryRead(head, out var width, out var height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void RejectsRiffWithoutKnownImageChunk()
    {
        // 长度够（30+ 字节），但 chunk 不是 VP8 / VP8L / VP8X —— 不能瞎猜。
        byte[] head =
        [
            0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50,
            0x41, 0x4C, 0x50, 0x48, 0x00, 0x00, 0x00, 0x00,   // "ALPH"
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,
        ];

        Assert.False(ImageSize.TryRead(head, out _, out _));
    }

    [Fact]
    public void RejectsUnknownBytes()
    {
        byte[] head = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00];

        Assert.False(ImageSize.TryRead(head, out _, out _));
    }
}
