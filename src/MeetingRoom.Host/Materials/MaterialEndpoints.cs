using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Materials;

/// <summary>
/// 资料相关端点（设计文档 §8.2）。资料库不再是个全局单例——它是**当前会议会话**的资料库，
/// 由 <see cref="SessionHub"/> 从会话目录（<c>&lt;工作空间&gt;/meetings/&lt;id&gt;/</c>）里装配出来。
/// </summary>
public static class MaterialEndpoints
{
    public static void MapMaterialEndpoints(this WebApplication app)
    {
        // 正文只在这里返回给前端。它是"点击链接才看"的通道——绝不进任何 Agent 的上下文（§5.3）。
        app.MapGet("/api/materials/{id}/content", (string id, SessionHub hub) =>
        {
            if (hub.Meeting?.Materials is not { } store)
            {
                return NoMeeting();
            }

            var card = store.Find(id);
            if (card is null)
            {
                return Results.NotFound(new { error = $"没有这份资料：{id}" });
            }

            return Results.Ok(new
            {
                id = card.Id,
                title = card.Title,
                path = card.Path,
                content = store.ReadBody(id),
            });
        });

        // 用户终审（采纳 / 打回 / 搁置 / 重新激活）。
        // 初审（approve/reject 由主持人执行）不在这里——那是主持人 Agent 的权限（§6.5），
        // 阶段 5 接上主持人会话后由它调用，这里的端点只代表"用户本人"。
        app.MapPost("/api/materials/{id}/review", async Task<IResult> (
            string id,
            ReviewRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            if (!TryParseUserDecision(request.Decision, out var decision))
            {
                return Results.BadRequest(new { error = $"用户终审只接受 adopt / reject / park / reopen，收到：{request.Decision}" });
            }

            // 走编排层而不是直接动资料库：采纳要对运行中的会话做软注入（§6.2）。
            var transition = await session
                .ReviewByUserAsync(id, decision, request.Note, ct)
                .ConfigureAwait(false);

            return transition.Applied
                ? Results.Ok(new { applied = true })
                : Results.Conflict(new { applied = false, reason = transition.Rejection });
        });
    }

    private static IResult NoMeeting() =>
        Results.Conflict(new { error = "还没有会议会话（资料库属于某一场会议）；先 POST /api/meeting/start。" });

    private static bool TryParseUserDecision(string? value, out ReviewDecision decision)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "adopt":
                decision = ReviewDecision.Adopt;
                return true;
            case "reject":
                decision = ReviewDecision.Reject;
                return true;
            case "park":
                decision = ReviewDecision.Park;
                return true;
            case "reopen":
                decision = ReviewDecision.Reopen;
                return true;
            default:
                decision = default;
                return false;
        }
    }
}

/// <summary>审核请求体。</summary>
public sealed record ReviewRequest(string Decision, string? Note = null);
