using System.Net;
using System.Text;
using System.Text.Json;
using MeetingRoom.Core.Config;
using MeetingRoom.Core.Images;

namespace MeetingRoom.Tests.Images;

/// <summary>
/// OpenRouter 图像端点（<c>POST {base}/images</c>）的请求构造与响应解析。
/// 全程用假的 <see cref="HttpMessageHandler"/>，不起网络。
/// </summary>
public class OpenRouterImageGeneratorTests
{
    private const string KeyEnv = "MEETINGROOM_TEST_IMAGE_KEY";

    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x11, 0x22];

    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static ImageOptions Options(string proxy = "") => new()
    {
        BaseUrl = "https://example.test/api/v1/",
        EndpointPath = "/images",
        Model = "google/gemini-3-pro-image",
        ApiKeyEnv = KeyEnv,
        Proxy = proxy,
        TimeoutSeconds = 0,
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return responder(request);
        }
    }

    private sealed class KeyScope : IDisposable
    {
        public KeyScope(string value) => Environment.SetEnvironmentVariable(KeyEnv, value);

        public void Dispose() => Environment.SetEnvironmentVariable(KeyEnv, null);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ImagesBody(params byte[][] images)
    {
        var items = images.Select(bytes =>
            $"{{\"b64_json\":\"{Convert.ToBase64String(bytes)}\"}}");

        return "{\"data\":[" + string.Join(',', items) + "],\"usage\":{\"cost\":0.0123}}";
    }

    [Fact]
    public async Task GenerateAsync_PostsToBasePlusEndpointPath_WithoutDoubleSlash()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader)));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-test");

        await generator.GenerateAsync(new ImageGenerationRequest { Prompt = "a cube" }, []);

        Assert.Equal("https://example.test/api/v1/images", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
    }

    [Fact]
    public async Task GenerateAsync_SendsBearerTokenFromEnvironment()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader)));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-secret-value");

        await generator.GenerateAsync(new ImageGenerationRequest { Prompt = "a cube" }, []);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-secret-value", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GenerateAsync_WithoutKey_ThrowsReadableError()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader)));
        using var generator = new OpenRouterImageGenerator(Options(), handler);

        var error = await Assert.ThrowsAsync<ImageGenerationException>(
            () => generator.GenerateAsync(new ImageGenerationRequest { Prompt = "a cube" }, []));

        Assert.Contains(KeyEnv, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_SendsSnakeCaseFieldsAndDefaults()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader)));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        // 刻意不叫 _：这个测试里还有 out _ 的 discard，两者同名会撞（CS1657）。
        using var key = new KeyScope("sk-test");

        await generator.GenerateAsync(new ImageGenerationRequest { Prompt = "a cube", Count = 2 }, []);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;

        Assert.Equal("google/gemini-3-pro-image", root.GetProperty("model").GetString());
        Assert.Equal("a cube", root.GetProperty("prompt").GetString());
        Assert.Equal(2, root.GetProperty("n").GetInt32());
        Assert.Equal("1:1", root.GetProperty("aspect_ratio").GetString());
        Assert.Equal("1K", root.GetProperty("resolution").GetString());
        Assert.False(root.TryGetProperty("seed", out _));
        Assert.False(root.TryGetProperty("input_references", out _));

        // quality / output_format 的配置默认是空串 ⇒ **不发**（FLUX 系会拒收这两个参数）。
        Assert.False(root.TryGetProperty("quality", out _));
        Assert.False(root.TryGetProperty("output_format", out _));
    }

    [Fact]
    public async Task GenerateAsync_SendsOptionalParametersOnlyWhenConfigured()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader)));
        var options = Options() with { DefaultQuality = "high", OutputFormat = "webp" };
        using var generator = new OpenRouterImageGenerator(options, handler);
        using var _ = new KeyScope("sk-test");

        await generator.GenerateAsync(new ImageGenerationRequest { Prompt = "a cube" }, []);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;

        Assert.Equal("high", root.GetProperty("quality").GetString());
        Assert.Equal("webp", root.GetProperty("output_format").GetString());
    }

    [Fact]
    public async Task GenerateAsync_InlinesReferencesAsDataUrls()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader)));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-test");

        await generator.GenerateAsync(
            new ImageGenerationRequest { Prompt = "keep the character" },
            [new ReferenceImage("image/png", PngHeader)]);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var reference = body.RootElement.GetProperty("input_references")[0];

        Assert.Equal("image_url", reference.GetProperty("type").GetString());
        var url = reference.GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(PngHeader), url["data:image/png;base64,".Length..]);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsEveryImageInDataArray()
    {
        var handler = new StubHandler(_ => Json(ImagesBody(PngHeader, JpegHeader)));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-test");

        var images = await generator.GenerateAsync(new ImageGenerationRequest { Prompt = "two" }, []);

        Assert.Equal(2, images.Count);
        Assert.Equal("image/png", images[0].MediaType);
        Assert.Equal("image/jpeg", images[1].MediaType);
    }

    [Fact]
    public async Task GenerateAsync_FallsBackToSniffingWhenMediaTypeMissing()
    {
        // 上游返回 JPEG 却不带 media_type —— 这是真实存在的情况，必须以字节为准。
        var body = "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(JpegHeader) + "\"}]}";
        var handler = new StubHandler(_ => Json(body));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-test");

        var images = await generator.GenerateAsync(new ImageGenerationRequest { Prompt = "jpeg" }, []);

        Assert.Equal("image/jpeg", images[0].MediaType);
    }

    [Fact]
    public async Task GenerateAsync_NonSuccessStatus_ThrowsWithUpstreamMessage()
    {
        const string upstream = "{\"error\":{\"message\":\"No endpoints found that can handle the requested parameters\"}}";
        var handler = new StubHandler(_ => Json(upstream, HttpStatusCode.NotFound));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-test");

        var error = await Assert.ThrowsAsync<ImageGenerationException>(
            () => generator.GenerateAsync(new ImageGenerationRequest { Prompt = "x" }, []));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.Contains("No endpoints found", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 400, "上游原因应当被截断，避免把整段响应灌进界面");
    }

    [Fact]
    public async Task GenerateAsync_EmptyDataArray_ThrowsReadableError()
    {
        var handler = new StubHandler(_ => Json("{\"data\":[]}"));
        using var generator = new OpenRouterImageGenerator(Options(), handler);
        using var _ = new KeyScope("sk-test");

        var error = await Assert.ThrowsAsync<ImageGenerationException>(
            () => generator.GenerateAsync(new ImageGenerationRequest { Prompt = "x" }, []));

        Assert.Contains("没有返回任何图片", error.Message, StringComparison.Ordinal);
    }

    // ---- 代理三态（ImageHttp）----

    [Fact]
    public void ImageHttp_Off_DisablesProxy()
    {
        using var handler = ImageHttp.CreateHandler(Options("off"));

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ImageHttp_Empty_UsesSystemDefault()
    {
        using var handler = ImageHttp.CreateHandler(Options(""));

        // 不设 Proxy、也不关 UseProxy ⇒ 走 .NET 默认（环境变量 → WinINET/PAC）。
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void ImageHttp_ExplicitUrl_SetsWebProxy()
    {
        using var handler = ImageHttp.CreateHandler(Options("http://127.0.0.1:10828"));

        Assert.True(handler.UseProxy);
        Assert.Equal(new Uri("http://127.0.0.1:10828"), ((WebProxy)handler.Proxy!).Address);
    }

    [Theory]
    [InlineData("OFF")]
    [InlineData(" off ")]
    public void ImageHttp_Off_IsCaseAndWhitespaceInsensitive(string value)
    {
        using var handler = ImageHttp.CreateHandler(Options(value));

        Assert.False(handler.UseProxy);
    }
}
