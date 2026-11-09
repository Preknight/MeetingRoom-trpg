using MeetingRoom.Core.Config;
using MeetingRoom.Core.Vtt;

namespace MeetingRoom.Tests.Vtt;

/// <summary>
/// 地图客户端（《枭熊地图接入实现方案》§3 / §5-4）：<b>桥没启动、超时、JSON 异常都必须降级</b>，
/// 而且以"这一轮没有地图段落"收场——绝不抛给回合。缓存则是为了同一轮里的多个角色不再各打一次桥。
/// </summary>
public sealed class HttpVttClientTests
{
    private const string Contract = """
    {
      "schema": "obr-bridge-state/1",
      "receivedAt": 1793003931203,
      "scene": {
        "sceneReady": true,
        "grid": { "dpi": 150, "type": "SQUARE", "measurement": "chessboard",
                  "scale": { "raw": "5 ft", "parsed": { "multiplier": 5, "unit": "ft" } } },
        "tokens": [ { "id": "t1", "name": "艾拉", "x": 150, "y": 150, "visible": true } ],
        "selected": [], "rulers": []
      }
    }
    """;

    private static readonly VttOptions Options = new()
    {
        Enabled = true,
        StateUrl = "http://127.0.0.1:9877/state",
        TimeoutSeconds = 1,
        StaleSeconds = 10,
    };

    [Fact]
    public async Task AHealthyBridgeYieldsAUsableSnapshot()
    {
        var client = NewClient(Json(Contract));

        var snapshot = await client.GetSnapshotAsync();

        Assert.True(snapshot.Available);
        Assert.Equal("艾拉", Assert.Single(snapshot.Tokens).Name);
        Assert.Same(snapshot, client.Last);
    }

    [Fact]
    public async Task ABridgeThatIsNotRunningDegradesInsteadOfThrowing()
    {
        var client = NewClient(_ => throw new HttpRequestException("由于目标计算机积极拒绝，无法连接。"));

        var snapshot = await client.GetSnapshotAsync();

        Assert.False(snapshot.Available);
        Assert.Contains("连不上地图桥", snapshot.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsCappedByTheConfiguredSeconds()
    {
        // 桥挂住不回：不能让回合一直等下去——超时上限是 vtt.timeoutSeconds。
        var client = NewClient(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var started = DateTime.UtcNow;
        var snapshot = await client.GetSnapshotAsync();
        var elapsed = DateTime.UtcNow - started;

        Assert.False(snapshot.Available);
        Assert.Contains("超时", snapshot.UnavailableReason, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"超时没有生效：等了 {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task BrokenJsonDegradesInsteadOfThrowing()
    {
        var client = NewClient(Json("{ \"scene\": "));

        var snapshot = await client.GetSnapshotAsync();

        Assert.False(snapshot.Available);
        Assert.Contains("格式不对", snapshot.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshSnapshotIsServedFromTheCache()
    {
        var handler = new StubHandler(Json(Contract));
        var client = new HttpVttClient(Options, new HttpClient(handler));

        await client.GetSnapshotAsync();
        await client.GetSnapshotAsync();

        // 同一轮里每个角色都会装配一次上下文：新鲜期内不该反复打桥。
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DisabledMeansNoRequestAtAll()
    {
        var handler = new StubHandler(Json(Contract));
        var client = new HttpVttClient(Options with { Enabled = false }, new HttpClient(handler));

        var snapshot = await client.GetSnapshotAsync();

        Assert.False(snapshot.Available);
        Assert.Contains("未启用", snapshot.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    private static HttpVttClient NewClient(Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(Options, new HttpClient(new StubHandler(respond)));

    private static Func<CancellationToken, Task<HttpResponseMessage>> Json(string body) =>
        _ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _respond(cancellationToken);
        }
    }
}
