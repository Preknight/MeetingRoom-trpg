using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRoom.Core.Config;

namespace MeetingRoom.Core.Images;

/// <summary>
/// OpenRouter 的图像端点（<c>POST {base}/images</c>，设计方案 §六）。
/// <para>
/// 为什么自己发 HTTP、不用 SDK 的 <c>OpenAIClient.GetImageClient</c>：那个固定打 OpenAI 的
/// <c>/images/generations</c>，与 OpenRouter 的 <c>{base}/images</c> 在**路径**与**响应形状**上都不一致
/// （上游返回 <c>data[].b64_json</c>，没有 hosted URL）。自己发反而更短、更可控。
/// </para>
/// </summary>
public sealed class OpenRouterImageGenerator : IImageGenerator, IDisposable
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ImageOptions _options;
    private readonly HttpClient _client;

    /// <param name="handler">仅为单测注入：不传就用 <see cref="ImageHttp.CreateHandler"/>（含代理三态）。</param>
    public OpenRouterImageGenerator(ImageOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _client = new HttpClient(handler ?? ImageHttp.CreateHandler(options), disposeHandler: true);

        if (options.TimeoutSeconds > 0)
        {
            _client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }
    }

    public async Task<IReadOnlyList<GeneratedImage>> GenerateAsync(
        ImageGenerationRequest request,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(references);

        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ImageGenerationException(
                $"环境变量 {_options.ApiKeyEnv} 未设置：密钥只从进程环境读（配置里存的是变量名），"
                + "而且它必须在宿主**启动之前**就存在。");
        }

        var url = _options.BaseUrl.TrimEnd('/') + _options.EndpointPath;
        var body = BuildBody(request, references);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(body, options: RequestJsonOptions);

        using var response = await _client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 上游的原因原样带出去（截断）：模型 slug 不对 / 参数不支持 / 余额不足一眼可辨。
            throw new ImageGenerationException(
                $"HTTP {(int)response.StatusCode}（{url}）：{Truncate(payload, 300)}");
        }

        return ParseImages(payload, _options.OutputFormat);
    }

    /// <summary>请求体字段是 snake_case；参考图以 data URL 内联。</summary>
    private Dictionary<string, object?> BuildBody(ImageGenerationRequest request, IReadOnlyList<ReferenceImage> references)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model!,
            ["prompt"] = request.Prompt,
            ["n"] = request.Count,
            ["aspect_ratio"] = request.AspectRatio ?? _options.DefaultAspectRatio,
            ["resolution"] = request.Resolution ?? _options.DefaultResolution,
            ["quality"] = request.Quality ?? _options.DefaultQuality,
            ["output_format"] = _options.OutputFormat,
        };

        // 空 = **不发**这个参数：不同模型支持的参数集不一样 —— FLUX.3 明确拒绝 output_format 与 quality
        // （上游原话："does not support: output_format, quality"），一律发过去会白白换来 400。
        foreach (var key in new[] { "aspect_ratio", "resolution", "quality", "output_format" })
        {
            if (body[key] is string text && string.IsNullOrWhiteSpace(text))
            {
                body.Remove(key);
            }
        }

        if (references.Count > 0)
        {
            // 本机 URL 对上游不可达，让上游来取还会多一条暴露面 —— 所以内联字节。
            body["input_references"] = references
                .Select(reference => (object)new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = $"data:{reference.MediaType};base64,{Convert.ToBase64String(reference.Bytes)}",
                    },
                })
                .ToArray();
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        return body;
    }

    /// <summary>
    /// 解析 <c>{ "data": [ { "b64_json": …, "media_type": … } ] }</c>。两个真实坑：
    /// <c>data</c> 是**数组**（一次可能回多张，不能硬取 <c>[0]</c>）；<c>media_type</c> 可能**缺失**
    /// （例如返回 JPEG 却不带该字段）—— 那就以**字节嗅探**为准，再退到声明的类型、最后退到配置的 output_format。
    /// </summary>
    private static IReadOnlyList<GeneratedImage> ParseImages(string payload, string fallbackFormat)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ImageGenerationException($"上游返回的不是合法 JSON：{Truncate(ex.Message, 200)}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new ImageGenerationException($"上游响应里没有 data 数组：{Truncate(payload, 300)}");
            }

            var images = new List<GeneratedImage>();
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("b64_json", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(encoded.GetString()!);
                }
                catch (FormatException)
                {
                    continue;   // 单张坏了不拖垮整批
                }

                var declared = item.TryGetProperty("media_type", out var mediaType)
                               && mediaType.ValueKind == JsonValueKind.String
                    ? mediaType.GetString()
                    : null;

                var head = bytes.AsSpan(0, Math.Min(ImageBytes.SniffLength, bytes.Length));
                var media = ImageBytes.Sniff(head) ?? declared ?? FallbackMediaType(fallbackFormat);

                images.Add(new GeneratedImage(bytes, media));
            }

            if (images.Count == 0)
            {
                throw new ImageGenerationException(
                    $"上游没有返回任何图片（data 里有 {data.GetArrayLength()} 项）：{Truncate(payload, 200)}");
            }

            return images;
        }
    }

    private static string FallbackMediaType(string format) => format.Trim().ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => "image/jpeg",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        _ => "image/png",
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => _client.Dispose();
}
