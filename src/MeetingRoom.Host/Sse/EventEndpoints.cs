using System.Threading.Channels;
using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Host.Images;

namespace MeetingRoom.Host.Sse;

/// <summary>
/// 唯一的一条 SSE 流（设计文档 §8.1 / §8.2 / §8.4）。
/// 全部与会者的事件都在这一条上按 <c>event:</c> 类型区分——浏览器同源 HTTP/1.1 的连接上限约 6 条，
/// "每个与会者一条连接"会在多与会者时直接卡死。
/// </summary>
public static class EventEndpoints
{
    public static void MapEventStream(this WebApplication app) =>
        app.MapGet("/api/events", StreamAsync);

    private static async Task StreamAsync(
        HttpContext ctx,
        IEventBus bus,
        IMeetingStateSource state,
        IRoleplayStateSource roleplay,
        ImageStudio images,
        MeetingRoomOptions options,
        CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.Body.FlushAsync(ct);

        using var subscription = bus.Subscribe(ct);

        // 任何一次连接/重连都先推一份全量快照：前端 store 是纯事件归约，快照即可无歧义重建，
        // 因此服务端不做逐条补发（设计文档 §8.4）。
        // 只经过订阅通道输出（不要另外直接写一次响应，否则同一条快照会被写两遍）；
        // 快照是全量状态，其他连接收到同样的内容无副作用。
        bus.Publish("meeting.state", state.Snapshot());

        // 叙事快照与会议快照并列推送：两种模式各自的 store 只认自己那一条（§11.6）。
        // 没有活跃叙事时给的是合法的空快照，前端据此得到 "idle"。
        bus.Publish("roleplay.state", roleplay.Snapshot());

        // 图片资产快照（图片模块 A7）：刷新 / 重连要能重建结果网格，所以它也必须进快照 ——
        // 不能只靠 image.generated 增量（《实施进度与待办》风险 13 的同一条方向）。
        // 没有工作空间时给的是合法的空列表。
        bus.Publish("image.list", images.Snapshot());

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 超过一个心跳间隔没有任何事件，就写一行注释帧，避免中间层/休眠策略掐断空闲连接。
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(options.Server.HeartbeatSeconds));

                try
                {
                    await foreach (var ev in subscription.Reader.ReadAllAsync(idle.Token))
                    {
                        await WriteAsync(ctx, ev, ct);
                    }

                    // 通道被完成（服务正在停止）时收尾退出。
                    break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或服务停止：正常关闭路径，不记错误。
        }
        catch (IOException)
        {
            // 连接被对端重置。
        }
    }

    private static async Task WriteAsync(HttpContext ctx, MeetingEvent ev, CancellationToken ct)
    {
        var data = ev.Payload?.ToJsonString(EventJson.Options) ?? "null";
        await ctx.Response.WriteAsync($"id: {ev.Seq}\nevent: {ev.Type}\ndata: {data}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
