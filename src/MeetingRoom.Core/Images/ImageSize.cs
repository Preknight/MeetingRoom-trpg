using System.Buffers.Binary;

namespace MeetingRoom.Core.Images;

/// <summary>
/// 从字节头读图片尺寸（F2）：**不靠前端传**，也不引第三方解码库。
/// <para>
/// 只认 PNG 与 WebP 的三种变体（VP8 / VP8L / VP8X）——地图样例是 <c>4096×2784</c> 的 VP8，
/// 其余格式返回 <c>false</c>，由调用方给可读的拒绝理由。
/// </para>
/// </summary>
public static class ImageSize
{
    /// <summary>PNG 至少在 24 字节内能读到 IHDR 的宽高；WebP 各变体最多 30 字节。</summary>
    public const int SniffLength = 64;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryRead(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature))
        {
            // IHDR：偏移 16 是宽（4 字节大端），20 是高。
            width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
            return width > 0 && height > 0;
        }

        if (data.Length < 30
            || !data[..4].SequenceEqual("RIFF"u8)
            || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var chunk = data.Slice(12, 4);

        if (chunk.SequenceEqual("VP8 "u8))
        {
            // 有损关键帧：20-22 是 3 字节 frame tag，**23-25 才是起始码 9D 01 2A**，
            // 26-27 宽、28-29 高（小端，14 位有效）。实测 VEoR 图（4096×2784）就是这个布局。
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
            {
                return false;
            }

            width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF;
            return width > 0 && height > 0;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // 无损：21-24 是 14 位宽 + 14 位高（都存的是「减 1」）。
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            // 扩展格式：24-26 宽、27-29 高（各 3 字节小端，同样存「减 1」）。
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            return true;
        }

        return false;
    }
}
