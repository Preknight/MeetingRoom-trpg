using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Interactions;

/// <summary>
/// 待决事项的答复端点（设计文档 §14.3）：说书人的提议与主持人的提问共用这两条口子。
/// <para>
/// 投递目标是"谁在等"——当前活跃的那一场会话（会议或叙事），按事项 id 判定；
/// 找不到（重复答复 / 已超时 / id 写错）一律 404，而不是默默吞掉。
/// </para>
/// </summary>
public static class InteractionEndpoints
{
    public static void MapInteractionEndpoints(this WebApplication app)
    {
        // 答复：{answers: [{questionId, selected: [...]}]}
        app.MapPost("/api/interactions/{id}", (
            string id,
            InteractionAnswerRequest request,
            SessionHub hub) =>
        {
            if (FindWaiting(id, hub) is not { } session)
            {
                return NotFound(id);
            }

            var answers = (request.Answers ?? [])
                .Select(answer => new InteractionAnswer(answer.QuestionId, answer.Selected ?? []))
                .ToArray();

            return session.AnswerPendingDecision(id, answers)
                ? Results.Ok(new { id, answered = true })
                : NotFound(id);
        });

        // 跳过：按默认选项收口（超时也是这条路，区别只在是否带 timedOut）。
        // 原因走查询串而不是 body——空 body 的 POST 在 Minimal API 里会被判成绑定失败。
        app.MapPost("/api/interactions/{id}/skip", (
            string id,
            string? reason,
            SessionHub hub) =>
        {
            if (FindWaiting(id, hub) is not { } session)
            {
                return NotFound(id);
            }

            return session.SkipPendingDecision(id, string.IsNullOrWhiteSpace(reason) ? "用户跳过" : reason.Trim())
                ? Results.Ok(new { id, skipped = true })
                : NotFound(id);
        });
    }

    /// <summary>这件事在哪个会话里等着——同一时刻只有一场活跃会话，先问会议再问叙事。</summary>
    private static SessionCore? FindWaiting(string id, SessionHub hub)
    {
        if (hub.Meeting is { } meeting && meeting.HasPendingDecision(id))
        {
            return meeting;
        }

        var roleplay = hub.Roleplay;

        return roleplay is not null && roleplay.HasPendingDecision(id) ? roleplay : null;
    }

    private static IResult NotFound(string id) => Results.NotFound(new
    {
        error = $"没有这件事在等答复：{id}（可能已经被答复、被跳过或已超时）。",
    });
}

public sealed record InteractionAnswerRequest(IReadOnlyList<InteractionAnswerDto>? Answers);

public sealed record InteractionAnswerDto(string QuestionId, IReadOnlyList<string>? Selected);
