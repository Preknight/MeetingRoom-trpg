namespace MeetingRoom.Core.Images;

/// <summary>
/// 出网抽象：给一段提示词（可带参考图）拿回图片字节。
/// <para>
/// 目前只有一个实现（OpenRouter 的 <c>{base}/images</c>），留接口是为了单测能塞假实现，
/// 以及将来换通道（OpenAI 的 <c>/images/generations</c>）时不动调用方。
/// </para>
/// </summary>
public interface IImageGenerator
{
    /// <param name="request">提示词与参数。</param>
    /// <param name="references">已读好字节的参考图（空 = 纯文生图）。</param>
    Task<IReadOnlyList<GeneratedImage>> GenerateAsync(
        ImageGenerationRequest request,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken cancellationToken = default);
}
