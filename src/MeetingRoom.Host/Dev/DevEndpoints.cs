using MeetingRoom.Core.Materials;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Dev;

/// <summary>阶段 1 的临时演示端点（接入真实 Agent 后删除，见实现步骤 3.3）。</summary>
public static class DevEndpoints
{
    public static void MapDevEndpoints(this WebApplication app)
    {
        // 触发假事件流：每 200ms 推一步剧本，用来验证五列的实时渲染。
        app.MapGet("/api/dev/fake", (DevEventStream dev) => Results.Ok(new { started = dev.Start(), running = dev.IsRunning }));

        // 用户在指令区的发言（阶段 5 由 /api/meeting/say 取代）。
        app.MapPost("/api/dev/say", (DevSayRequest request, DevEventStream dev) =>
        {
            dev.Say(request.Text);
            return Results.Ok(new { accepted = !string.IsNullOrWhiteSpace(request.Text) });
        });

        // 阶段 4 的临时演示：在**真实**资料库上走一遍完整链路
        // （提交 → 主持人驳回 → 作者重开重提 → 主持人通过 → 用户采纳），每步之间留出停顿，
        // 让五列界面能看清状态依次变化。正式编排在阶段 5。
        // W2 之后资料库属于**当前会议会话**，所以要先有一场会议。
        app.MapGet("/api/dev/material-flow", async (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Meeting?.Materials is not { } store)
            {
                return Results.Conflict(new { error = "还没有会议会话（资料库属于某一场会议）；先 POST /api/meeting/start。" });
            }

            var card = await store.SubmitAsync(new SubmitRequest
            {
                Author = "sales-manager",
                Title = "定价建议草案",
                Summary = "建议按席位分层定价，先做企业版试用转化",
                Body = "先做两档，只考核企业版试用转化率。这份正文只在你点“正文”时才会被读取。",
            }, ct);

            await Task.Delay(1200, ct);
            await store.ReviewAsync(card.Id, ReviewDecision.Reject, ReviewActorKind.Host, "摘要不够具体，请补数据", ct);

            await Task.Delay(1200, ct);
            await store.ReviewAsync(card.Id, ReviewDecision.Reopen, ReviewActorKind.Member, null, ct);
            await store.ReviewAsync(card.Id, ReviewDecision.Submit, ReviewActorKind.Member, null, ct);

            await Task.Delay(1200, ct);
            await store.ReviewAsync(card.Id, ReviewDecision.Approve, ReviewActorKind.Host, "转用户终审", ct);

            await Task.Delay(1200, ct);
            await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.User, "采纳：先按两档试点", ct);

            return Results.Ok(new { id = card.Id, version = store.Version });
        });
    }
}

/// <summary>指令区提交体。</summary>
public sealed record DevSayRequest(string Text);
