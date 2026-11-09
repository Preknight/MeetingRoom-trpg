using System.Text.Json;
using MeetingRoom.Core.Config;

namespace MeetingRoom.Core.Vtt;

/// <summary>
/// 地图快照的读取口（《枭熊地图接入实现方案》§3 V1）。
/// <b>它保证不抛异常</b>：任何失败都返回 <see cref="VttSnapshot.Available"/> = <c>false</c> 的快照，
/// 由装配层降级成"本轮没有地图段落"。
/// </summary>
public interface IVttClient
{
    /// <summary>最近一次拉取的结果（成功或降级都记），给只读端点与诊断用。</summary>
    VttSnapshot? Last { get; }

    /// <summary>拉一次快照；缓存未过期时直接给缓存，失败时给降级快照。</summary>
    Task<VttSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 走桥的纯 HTTP 回环口（<c>GET http://127.0.0.1:9877/state</c>）读快照：
/// <b>缓存 + 超时 + 失败降级</b>三件事都在这里，调用方不必再处理异常。
/// <para>
/// 为什么走 9877：桥的另一半是给浏览器里 OBR 扩展用的 HTTPS 自签口，.NET 侧碰它就要处理证书；
/// 纯 HTTP 的回环口只服务于本机进程，证书是浏览器那边的问题。
/// </para>
/// </summary>
public sealed class HttpVttClient : IVttClient
{
    /// <summary>
    /// 进程级共享（每轮一次小请求，不值得每场会话各建一个连接池）。
    /// 超时统一由每次调用的 CTS 控制，所以这里的 <c>Timeout</c> 设成不限。
    /// </summary>
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly VttOptions _options;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();

    private VttSnapshot? _last;
    private DateTimeOffset _lastAt;

    public HttpVttClient(VttOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _http = http ?? SharedHttp;
    }

    public VttSnapshot? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public async Task<VttSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Remember(VttSnapshot.Unavailable("地图未启用（vtt.enabled = false）"));
        }

        if (Fresh() is { } cached)
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        try
        {
            var json = await _http
                .GetStringAsync(_options.StateUrl, timeout.Token)
                .ConfigureAwait(false);

            return Remember(VttSnapshotParser.Parse(json));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // 桥没启动 / 超时 / JSON 异常都走这里：地图挂了不能中断回合。
            return Remember(VttSnapshot.Unavailable(Describe(ex)));
        }
    }

    /// <summary>新鲜期内复用上一次的成功结果——同一轮里的多个角色不该各打一次桥。</summary>
    private VttSnapshot? Fresh()
    {
        lock (_gate)
        {
            if (_last is not { Available: true })
            {
                return null;
            }

            var stale = TimeSpan.FromSeconds(Math.Max(0, _options.StaleSeconds));
            return DateTimeOffset.UtcNow - _lastAt <= stale ? _last : null;
        }
    }

    private VttSnapshot Remember(VttSnapshot snapshot)
    {
        lock (_gate)
        {
            _last = snapshot;
            _lastAt = DateTimeOffset.UtcNow;
        }

        return snapshot;
    }

    /// <summary>可恢复的失败：网络、超时、JSON、URL 配置。这些都是"这次没有地图"，不是"程序坏了"。</summary>
    private static bool IsRecoverable(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or JsonException
            or IOException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException;

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "地图桥超时（桥没启动或场景卡住）",
        HttpRequestException => $"连不上地图桥：{ex.Message}",
        JsonException => $"地图快照格式不对：{ex.Message}",
        _ => $"读取地图失败：{ex.Message}",
    };
}
