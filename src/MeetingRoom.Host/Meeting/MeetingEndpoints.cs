using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Host.Dev;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Meeting;

/// <summary>
/// 会议控制端点（设计文档 §8.2）。会话本身由 <see cref="SessionHub"/> 持有——
/// 会议落在**当前工作空间**的 <c>meetings/&lt;id&gt;/</c> 下，与会者用该工作空间共享的角色卡。
/// </summary>
public static class MeetingEndpoints
{
    public static void MapMeetingEndpoints(this WebApplication app)
    {
        // 开始会议：开一场（或接着已有的那场）。与会者是该工作空间角色卡里的全部角色。
        app.MapPost("/api/meeting/start", async Task<IResult> (
            StartMeetingRequest request,
            SessionHub hub,
            DevEventStream dev,
            CancellationToken ct) =>
        {
            // 已经有一场在跑就别再开一场——切会话是显式动作，不该由"开始"按钮代劳。
            if (hub.Meeting is { Status: not (MeetingStatus.Idle or MeetingStatus.Ended) } running)
            {
                return Results.Conflict(new { error = $"会议已经处于 {running.Status} 状态。" });
            }

            if (hub.Roleplay is { Status: not (MeetingStatus.Idle or MeetingStatus.Ended) } story)
            {
                return Results.Conflict(new { error = $"叙事正在进行（{story.StoryId}，状态 {story.Status}）；先结束它再开会。" });
            }

            string sessionId;
            try
            {
                var descriptor = await hub.StartMeetingAsync(request.Topic, request.SessionId, ct).ConfigureAwait(false);
                sessionId = descriptor.Id;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (hub.Meeting is not { } session)
            {
                return Results.Problem(detail: "会议会话没装配起来。", statusCode: StatusCodes.Status500InternalServerError);
            }

            // 真实会议一开始，演示数据流就退场——两者共用同一条 SSE 与前端 store。
            dev.Stop();

            try
            {
                await session.StartAsync(request.Topic, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { started = true, topic = request.Topic, sessionId });
        });

        app.MapPost("/api/meeting/pause", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            await session.PauseAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "paused" });
        });

        app.MapPost("/api/meeting/resume", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            await session.ResumeAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "running" });
        });

        app.MapPost("/api/meeting/end", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            await session.EndAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "ended" });
        });

        // 用户发言（带 @ 点名的会直接触发目标会话）。
        app.MapPost("/api/meeting/say", async Task<IResult> (SayRequest request, SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            await session.SayAsync(request.Text, request.Mentions, ct).ConfigureAwait(false);
            return Results.Ok(new { accepted = true });
        });

        // 补料/纠偏：进共享节，并对运行中的会话做软注入（§6.4）。
        app.MapPost("/api/meeting/broadcast", async Task<IResult> (BroadcastRequest request, SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            await session.BroadcastAsync(request.Text, ct).ConfigureAwait(false);
            return Results.Ok(new { accepted = true });
        });

        // 生效的可调参数（只读，供诊断面板展示；§10.5 规则 5）。密钥只暴露变量名，不暴露值。
        app.MapGet("/api/config", (MeetingRoomOptions options) => Results.Ok(new
        {
            server = new { options.Server.Port, options.Server.Bind, options.Server.HeartbeatSeconds, options.Server.OpenBrowser },
            provider = new { options.Provider.Name, options.Provider.BaseUrl, options.Provider.Model, options.Provider.ApiKeyEnv },
            limits = new { options.Limits.MaxStepsPerTurn, options.Limits.MaxConcurrentTurns, options.Limits.SteerMergeWindowMs },
            summaries = new { options.Summaries.MaterialSummaryFallbackChars, options.Summaries.ThoughtSummaryMaxChars, options.Summaries.ThoughtSummaryStyle },
            context = new { options.Context.AlwaysMaterialMaxCount, options.Context.TriggeredTokenBudget, options.Context.TriggeredCooldownRounds },
            interaction = new
            {
                options.Interaction.Enabled,
                options.Interaction.EveryRound,
                options.Interaction.AllowSelfSkip,
                options.Interaction.MaxConsecutiveSkips,
                options.Interaction.TimeoutSeconds,
                options.Interaction.MaxOptions,
            },
        }));

        // 用户直接点名（绕过主持人）。
        app.MapPost("/api/participants/{id}/nudge", async Task<IResult> (
            string id,
            NudgeRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (hub.Meeting is not { } session)
            {
                return NoMeeting();
            }

            await session.NudgeAsync(id, request.Task ?? "请就当前议题继续发言。", ct).ConfigureAwait(false);
            return Results.Ok(new { accepted = true });
        });
    }

    private static IResult NoMeeting() =>
        Results.Conflict(new { error = "还没有会议会话；先 POST /api/meeting/start 开一场。" });
}

public sealed record StartMeetingRequest(string Topic, string? SessionId = null);

public sealed record SayRequest(string Text, IReadOnlyList<string>? Mentions = null);

public sealed record BroadcastRequest(string Text);

public sealed record NudgeRequest(string? Task = null);
