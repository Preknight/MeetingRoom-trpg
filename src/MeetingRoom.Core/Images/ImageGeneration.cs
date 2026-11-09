namespace MeetingRoom.Core.Images;

/// <summary>一次生成的输入（对应上游 <c>POST {base}/images</c> 的请求体，见设计方案 §六）。</summary>
public sealed record ImageGenerationRequest
{
    public required string Prompt { get; init; }

    /// <summary>参与本次生成的参考图 id（复现依据；字节由调用方读好传进来）。</summary>
    public IReadOnlyList<string> ReferenceIds { get; init; } = [];

    /// <summary>可空 = 用配置默认。</summary>
    public string? AspectRatio { get; init; }

    /// <summary>可空 = 用配置默认；<c>512</c> / <c>1K</c> / <c>2K</c> / <c>4K</c>。</summary>
    public string? Resolution { get; init; }

    /// <summary>可空 = 用配置默认；<c>auto</c> / <c>low</c> / <c>medium</c> / <c>high</c>。</summary>
    public string? Quality { get; init; }

    public int Count { get; init; } = 1;

    /// <summary>可空 = 用配置里的默认模型。</summary>
    public string? Model { get; init; }

    public int? Seed { get; init; }
}

/// <summary>上游返回的一张图。</summary>
public sealed record GeneratedImage(byte[] Bytes, string MediaType);

/// <summary>
/// 参考图（字节已经在手里）。生成器**不需要知道存储在哪** —— 读盘是调用方（<see cref="ImageAssetStore"/> 的持有者）的事。
/// </summary>
public sealed record ReferenceImage(string MediaType, byte[] Bytes);

/// <summary>
/// 生成失败。Message 一定可读 —— 它会原样显示给用户（设计方案 §九：模型 slug 不对 / 参数不支持 / 余额不足一眼可辨）。
/// </summary>
public sealed class ImageGenerationException(string message) : Exception(message);

/// <summary>
/// 参考图的来源。两条通道共用：
/// <list type="bullet">
///   <item><see cref="FromPath"/>：原生对话框给的是**绝对路径**（只有它能拿到真实路径）；</item>
///   <item><see cref="FromStream"/>：拖拽 / <c>&lt;input type=file&gt;</c> / 粘贴 —— 这些**拿不到路径**，只能给字节。</item>
/// </list>
/// </summary>
public sealed class ImageSource
{
    private readonly string _label;
    private readonly long _maxBytes;
    private readonly Func<CancellationToken, Task<byte[]>> _read;

    private ImageSource(string label, long maxBytes, Func<CancellationToken, Task<byte[]>> read)
    {
        _label = label;
        _maxBytes = maxBytes;
        _read = read;
    }

    public static ImageSource FromPath(string path, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new ImageSource(path, maxBytes, cancellationToken =>
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"文件不存在：{path}", path);
            }

            return File.ReadAllBytesAsync(path, cancellationToken);
        });
    }

    public static ImageSource FromStream(string label, Stream stream, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var name = string.IsNullOrWhiteSpace(label) ? "（未命名上传）" : label;

        return new ImageSource(name, maxBytes, async cancellationToken =>
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        });
    }

    /// <summary>
    /// 读出字节并判定类型；空的 / 超限 / 认不出类型都抛可读异常（导入端点会把原因透给用户）。
    /// <b>public</b>：宿主侧的 <c>--probe-image</c> 也要用它读参考图，而 Host 与 Core 不是同一个程序集。
    /// </summary>
    public async Task<(byte[] Bytes, string MediaType)> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = await _read(cancellationToken).ConfigureAwait(false);

        if (bytes.LongLength == 0)
        {
            throw new NotSupportedException($"内容是空的：{_label}");
        }

        if (bytes.LongLength > _maxBytes)
        {
            throw new NotSupportedException(
                $"图片超过上限（{_maxBytes:N0} 字节，实际 {bytes.LongLength:N0}）：{_label}");
        }

        var head = bytes.AsSpan(0, Math.Min(ImageBytes.SniffLength, bytes.Length));
        var mediaType = ImageBytes.Sniff(head)
            ?? throw new NotSupportedException($"不是可识别的图片（PNG / JPEG / WebP / SVG）：{_label}");

        return (bytes, mediaType);
    }
}
