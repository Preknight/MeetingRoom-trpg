using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Images;
using MeetingRoom.Core.Logging;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Images;

/// <summary>
/// 图片模块在 Host 侧的组合根：按**当前工作空间**取图片库、跑后台生成任务、把结果广播出去。
/// <para>
/// 为什么要有它：图片资产属于当前工作空间，而工作空间**会切换** —— 所以 store 不能是启动时定死的单例。
/// 它只做三件事：取库（<see cref="TryGetStore"/>）、发起生成（<see cref="Start"/>）、给快照（<see cref="Snapshot"/>）；
/// 领域逻辑都在 <see cref="ImageAssetStore"/> 与 <see cref="OpenRouterImageGenerator"/> 里。
/// </para>
/// </summary>
public sealed class ImageStudio
{
    private readonly SessionHub _hub;
    private readonly IEventBus _bus;
    private readonly MeetingRoomOptions _options;
    private readonly FileLog _log;
    private readonly ConcurrentDictionary<string, byte> _runningJobs = new();

    public ImageStudio(SessionHub hub, IEventBus bus, MeetingRoomOptions options, FileLog log)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _hub = hub;
        _bus = bus;
        _options = options;
        _log = log;
    }

    /// <summary>生成的开关（界面据此禁用入口）。</summary>
    public bool Enabled => _options.Images.Enabled;

    /// <summary>参考图的体积上限（客户端预检与服务端校验用同一个数）。</summary>
    public long MaxReferenceBytes => _options.Images.MaxReferenceBytes;

    /// <summary>单次生成的张数上限。</summary>
    public int MaxCount => _options.Images.MaxCount;

    /// <summary>当前工作空间的图片库；没有工作空间或目录用不了时给出**可读原因**（端点把它原样透给前端）。</summary>
    public bool TryGetStore([NotNullWhen(true)] out ImageAssetStore? store, out string error)
    {
        store = null;

        if (_hub.Workspace is not { } workspace)
        {
            error = "还没有打开工作空间；图片存在工作空间的 images/ 下。";
            return false;
        }

        try
        {
            store = new ImageAssetStore(workspace.Layout.ImagesDir, _bus);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"图片目录不可用（{workspace.Layout.ImagesDir}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>全量快照（SSE 首推与重连校准用）。没有工作空间时给合法的空列表。</summary>
    public object Snapshot() =>
        new { images = TryGetStore(out var store, out _) ? store.WireList().ToArray() : [] };

    /// <summary>
    /// 发起一次生成：**立刻返回 jobId**，实际请求在后台跑（与 <c>POST /api/roleplay/start</c> 同一条风格）。
    /// 返回的第二项非空表示"没接受"，调用方把它当 409/400 的原因。
    /// </summary>
    public (string JobId, string? Error) Start(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.Images.Enabled)
        {
            return (string.Empty, "图片生成已关闭（images.enabled = false）。");
        }

        if (!TryGetStore(out var store, out var storeError))
        {
            return (string.Empty, storeError);
        }

        if (request.Count < 1 || request.Count > _options.Images.MaxCount)
        {
            return (string.Empty, $"张数必须在 1..{_options.Images.MaxCount} 之间，收到 {request.Count}。");
        }

        if (request.ReferenceIds.Count > _options.Images.MaxReferenceImages)
        {
            return (string.Empty, $"参考图最多 {_options.Images.MaxReferenceImages} 张，收到 {request.ReferenceIds.Count} 张。");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.Images.Model : request.Model!;
        var endpoint = _options.Images.BaseUrl.TrimEnd('/') + _options.Images.EndpointPath;
        var jobId = "job-" + Guid.NewGuid().ToString("N")[..8];

        _runningJobs[jobId] = 1;
        _bus.Publish("image.job", new
        {
            jobId,
            status = "running",
            prompt = request.Prompt,
            model,
            referenceIds = request.ReferenceIds,
        });

        var effective = request with { Model = model };

        _ = Task.Run(async () =>
        {
            try
            {
                var references = store.ReadReferenceImages(request.ReferenceIds);

                using var generator = new OpenRouterImageGenerator(_options.Images);
                var images = await generator.GenerateAsync(effective, references).ConfigureAwait(false);

                var assets = new List<object>(images.Count);
                foreach (var image in images)
                {
                    if (image.Bytes.LongLength > _options.Images.MaxOutputBytes)
                    {
                        throw new ImageGenerationException(
                            $"返回的图片超过 images.maxOutputBytes（{image.Bytes.LongLength:N0} 字节）。");
                    }

                    var asset = await store.AddGeneratedAsync(
                        new GeneratedAssetSpec(
                            image.Bytes,
                            image.MediaType,
                            request.Prompt,
                            model,
                            endpoint,
                            request.ReferenceIds,
                            null),
                        CancellationToken.None).ConfigureAwait(false);

                    assets.Add(ImageAssetWire.Of(asset));
                }

                _bus.Publish("image.generated", new { jobId, images = assets });
            }
            catch (Exception ex) when (ex is ImageGenerationException or HttpRequestException or IOException
                                          or TaskCanceledException or NotSupportedException)
            {
                // 上游原因原样带出去（模型 slug 不对 / 参数不支持 / 余额不足 / 网络不通一眼可辨）。
                _log.Warn($"图片生成失败（{jobId}）：{ex.Message}");
                _bus.Publish("image.failed", new { jobId, code = "generate", message = ex.Message });
            }
            finally
            {
                _runningJobs.TryRemove(jobId, out _);
            }
        });

        return (jobId, null);
    }
}
