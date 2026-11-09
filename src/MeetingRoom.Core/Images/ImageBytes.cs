using System.Text;

namespace MeetingRoom.Core.Images;

/// <summary>
/// 按**内容**判定图片类型（不信文件名，也不全信上游返回的 <c>media_type</c>）。
/// 只认四种：PNG / JPEG / WebP / SVG —— 其余一律拒绝，避免把非图片写进资产目录。
/// <para>
/// 为什么手写而不引库：本项目不引入第三方图片库（设计文档 §2.4 的取舍），
/// 而"认头部签名"这件事用 BCL 就够（几十行）。
/// </para>
/// </summary>
public static class ImageBytes
{
    /// <summary>嗅探只读前这么多字节 —— 够覆盖四种签名的头部，也够看清 SVG 的开头。</summary>
    public const int SniffLength = 512;

    /// <summary>认不出就返回 <c>null</c>（调用方据此拒绝，绝不猜成 png）。</summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(PngSignature))
        {
            return "image/png";
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (head.Length >= 12 && head[..4].SequenceEqual(RiffTag) && head[8..12].SequenceEqual(WebpTag))
        {
            return "image/webp";
        }

        return LooksLikeSvg(head) ? "image/svg+xml" : null;
    }

    /// <summary>落盘扩展名：按嗅探结果取，**不能**一律写 <c>.png</c>（上游可能返回 JPEG / WebP）。</summary>
    public static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        _ => ".bin",
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> RiffTag => "RIFF"u8;

    private static ReadOnlySpan<byte> WebpTag => "WEBP"u8;

    /// <summary>
    /// SVG 是文本：跳过 BOM 与空白后以 <c>&lt;svg</c> 开头，或先有 <c>&lt;?xml</c> 声明随后出现 <c>&lt;svg</c>。
    /// </summary>
    private static bool LooksLikeSvg(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(head).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            && text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
