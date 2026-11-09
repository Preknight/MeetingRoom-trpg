namespace MeetingRoom.Core.Images;

/// <summary>
/// 一条图片资产（设计方案 §三）。这是**落盘形态**：<see cref="Path"/> 相对 <c>images/</c>、
/// 一律正斜杠 —— 前端与 URL 都能直接用。
/// <para>
/// <see cref="Width"/> / <see cref="Height"/> 可空：本项目不引第三方解码库，嗅探只看头部签名，
/// 读不出尺寸就留空（界面显示"—"），绝不猜。
/// </para>
/// </summary>
public sealed record ImageAsset
{
    /// <summary>资产 id（<c>img-0001</c>），由 <see cref="ImageAssetStore"/> 顺延分配。</summary>
    public required string Id { get; init; }

    /// <summary><see cref="ImageAssetKinds.Reference"/> 或 <see cref="ImageAssetKinds.Generated"/>。</summary>
    public required string Kind { get; init; }

    /// <summary>相对 <c>images/</c> 的路径：<c>refs/ab12….png</c> / <c>out/img-0001.png</c>。</summary>
    public required string Path { get; init; }

    /// <summary>以字节嗅探结果为准的 MIME（见 <see cref="ImageBytes"/>），不信文件名。</summary>
    public required string MediaType { get; init; }

    public long Bytes { get; init; }

    /// <summary>内容哈希：参考图按它去重，也给前端当 URL 缓存键。</summary>
    public required string Sha256 { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>生成用的提示词（参考图为空）。</summary>
    public string? Prompt { get; init; }

    /// <summary>生成用的模型 slug（参考图为空）。</summary>
    public string? Model { get; init; }

    /// <summary>本次生成用到的参考图 id（复现依据）。</summary>
    public IReadOnlyList<string> ReferenceIds { get; init; } = [];

    /// <summary>出网端点（便于日后换通道时辨认这批资产是谁产的）。</summary>
    public string? Provider { get; init; }

    /// <summary>上游回传的费用（可空）。</summary>
    public double? Cost { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>资产的两种来路（跨层用 wire 字符串）。</summary>
public static class ImageAssetKinds
{
    /// <summary>用户导入的参考图。</summary>
    public const string Reference = "reference";

    /// <summary>模型产出的图。</summary>
    public const string Generated = "generated";
}

/// <summary>
/// <c>images/manifest.json</c> 的形状。只存索引、不存字节（同 <c>MaterialManifest</c> 的做法）。
/// </summary>
public sealed record ImageManifest
{
    /// <summary>下一个资产编号；<c>img-{NextImageNumber:D4}</c>。</summary>
    public int NextImageNumber { get; init; } = 1;

    public IReadOnlyList<ImageAsset> Images { get; init; } = [];
}

/// <summary>
/// 资产给前端 / SSE 的投影（手写，同 <c>MaterialStore.Payload(...)</c> 的做法）：
/// 领域字段不直接漏出去，且**绝不带字节**。
/// <para>
/// 放在 Core 而不是 Host：<see cref="ImageAssetStore"/> 自己就要广播 <c>image.list</c>，
/// 两处各写一份投影迟早会分叉 —— 所以投影只有这一份，Host 的端点直接用。
/// </para>
/// </summary>
public static class ImageAssetWire
{
    public static object Of(ImageAsset asset) => new
    {
        id = asset.Id,
        kind = asset.Kind,
        path = asset.Path,
        mediaType = asset.MediaType,
        bytes = asset.Bytes,
        sha256 = asset.Sha256,
        prompt = asset.Prompt,
        model = asset.Model,
        referenceIds = asset.ReferenceIds,
        provider = asset.Provider,
        cost = asset.Cost,
        createdAt = asset.CreatedAt,

        // 前端拿它当 URL 缓存键：/api/images/{id}/content?v={v}
        // 内容变了 v 才变，避免"改了没生效"那类缓存假象（设计方案 §九）。
        v = asset.Sha256[..8],
    };
}
