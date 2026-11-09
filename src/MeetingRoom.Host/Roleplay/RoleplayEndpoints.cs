using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Vtt;
using MeetingRoom.Core.Workspaces;
using MeetingRoom.Host.Dev;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Roleplay;

/// <summary>
/// 叙事（角色扮演）控制端点：与会议端点并列，共用唯一那条 SSE（设计文档 §8.2 / §11.6）。
/// <para>
/// 启动与会议对称：用户给出设定（世界观 / 情境 / 卡司）即可开写，**开场由说书人直接写**。
/// <b>不做</b>开局预览与重掷（§11.4-8）——也就没有"候选 → 选定"这一步。
/// 会话由 <see cref="SessionHub"/> 持有，落**当前工作空间**的 <c>stories/&lt;id&gt;/</c> 下。
/// </para>
/// </summary>
public static class RoleplayEndpoints
{
    public static void MapRoleplayEndpoints(this WebApplication app)
    {
        // 创建这一场并立刻开写：设定由用户（导演 / DM）给，开场由说书人直接写（§11.4-8）。
        app.MapPost("/api/roleplay/start", async Task<IResult> (
            StartRoleplayRequest request,
            SessionHub hub,
            DevEventStream dev,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Worldview) || string.IsNullOrWhiteSpace(request.Situation))
            {
                return Results.BadRequest(new { error = "worldview 与 situation 都是必填。" });
            }

            // 已经有一场在跑就别覆盖它——切会话是显式动作（先结束，或点侧栏里的另一场）。
            // F7-④：`Ended` 不是"还在跑"——上一场结束后必须能开新的一场，
            // 否则状态机把 Ended 当成活跃会话，用户只能重启进程。
            if (hub.Meeting is { Status: not (MeetingStatus.Idle or MeetingStatus.Ended) } meeting)
            {
                return Results.Conflict(new { error = $"会议正在进行（第 {meeting.Round} 轮，状态 {meeting.Status}）；先结束它再开叙事。" });
            }

            if (hub.Roleplay is { Status: not (MeetingStatus.Idle or MeetingStatus.Ended) } running)
            {
                return Results.Conflict(new { error = $"已有一场叙事在进行（{running.StoryId}，状态 {running.Status}）；先结束它再开新的。" });
            }

            SessionDescriptor descriptor;

            try
            {
                descriptor = await hub
                    .StartRoleplayAsync(
                        new SessionCreateSpec(
                            Id: request.StoryId,
                            Worldview: request.Worldview,
                            Situation: request.Situation,
                            UserRole: request.UserRole,
                            Cast: request.Cast,
                            NarratorId: request.NarratorId),
                        ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (hub.Roleplay is not { } session)
            {
                return Results.Problem(detail: "叙事会话没装配起来。", statusCode: StatusCodes.Status500InternalServerError);
            }

            // 叙事一开始，演示数据流就退场——两者共用同一条 SSE 与前端 store（与会议同一条规则）。
            dev.Stop();

            // 已有剧情（构造时重放了 story.jsonl）就不再写第二次开场：这是"接着演"，不是"重演一遍"。
            if (session.Revision == 0)
            {
                // 立即返回：开场在后台写成正文（与会议一致，不让 HTTP 请求挂在整场戏上）。
                await session.StartAsync(ct).ConfigureAwait(false);
            }

            return Results.Ok(new { storyId = descriptor.Id, started = true, revision = session.Revision });
        });

        // 导演指令：旁白 + 括号里的元指令（元指令不进剧情时间线，§11.4-9）。
        app.MapPost("/api/roleplay/say", async Task<IResult> (
            RoleplaySayRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (hub.Roleplay is not { } session)
            {
                return NoRoleplay();
            }

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest(new { error = "text 不能为空。" });
            }

            try
            {
                await session.SayAsync(request.Text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { accepted = true, revision = session.Revision });
        });

        app.MapPost("/api/roleplay/pause", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Roleplay is not { } session)
            {
                return NoRoleplay();
            }

            await session.PauseAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "paused" });
        });

        app.MapPost("/api/roleplay/resume", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Roleplay is not { } session)
            {
                return NoRoleplay();
            }

            // r13：resume 不许"返回成功但状态没变"。
            // `SessionCore.ResumeAsync` 只对 Paused 生效，其它状态直接 return——
            // 于是"重启后打开旧叙事再 resume"会拿到 200 却仍是 idle（假成功）。
            var before = RoleplayStatusProbe.StatusOf(session.Snapshot());

            if (before == "running")
            {
                return Results.Ok(new { status = "running" }); // 幂等
            }

            if (before != "paused")
            {
                return Results.Conflict(new
                {
                    error = $"该会话未处于可恢复状态（当前 {before}）。它没有在跑，用「打开」重新进入这一场，而不是 resume。",
                    status = before,
                });
            }

            await session.ResumeAsync(ct).ConfigureAwait(false);
            var after = RoleplayStatusProbe.StatusOf(session.Snapshot());

            return after == "running"
                ? Results.Ok(new { status = "running" })
                : Results.Conflict(new { error = $"恢复失败：状态仍是 {after}。", status = after });
        });

        app.MapPost("/api/roleplay/end", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Roleplay is not { } session)
            {
                return NoRoleplay();
            }

            await session.EndAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "ended" });
        });

        // 战斗条上的「结束战斗」（F1）：DM 直接收场，不依赖说书人调 end_combat——
        // 按钮点了没反应比多一个端点难受得多。
        app.MapPost("/api/roleplay/combat/end", async Task<IResult> (SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Roleplay is not { } session)
            {
                return NoRoleplay();
            }

            var outcome = await session.EndCombatAsync("dm_end", ct).ConfigureAwait(false);

            // 战斗没在打（已经结束 / 压根没有）时回 409：前端禁用按钮，脚本/手滑也能在这里被拦住。
            return outcome.Applied
                ? Results.Ok(new { applied = true, message = outcome.Message })
                : Results.Conflict(new { applied = false, message = outcome.Message });
        });

        // 只读快照：与 SSE 首推的 roleplay.state 同源，便于脚本与诊断直接用。
        app.MapGet("/api/roleplay/state", (IRoleplayStateSource roleplay) => Results.Ok(roleplay.Snapshot()));

        // DM 提醒（§3.3，B5）：导演往观察面投一条旁注。
        // **不进说书人上下文、不暂停剧情、不动 revision**；超时只能是"没人看到"，绝不自己改世界。
        // 三种回应走已有的 /api/interactions/{id}（知道了 ack / 记进设定 apply / 忽略 ignore）。
        app.MapPost("/api/roleplay/dm-note", (DmNoteRequest request, SessionHub hub) =>
        {
            if (hub.Roleplay is not { } session)
            {
                return Results.Conflict(new { error = "还没有叙事会话；先开一场再投提醒。" });
            }

            var result = session.AddDmNote(request.Severity, request.Text);

            return result.Ok
                ? Results.Ok(new { ok = true, id = result.Id, message = "提醒已投到观察面（第五列「待确认事件」）。" })
                : Results.BadRequest(new { ok = false, error = result.Error });
        });

        // 后台观察（§3.1）：说书人与角色的思考 / 工具调用，来自 backstage.jsonl 的内存视图。
        // **只读**、按幕升序；since 是幕号（含），limit 默认 200。刷新/重启都还在——这就是这条通道的意义。
        app.MapGet("/api/roleplay/backstage", (SessionHub hub, long? since, int? limit) =>
            hub.Roleplay is { } session
                ? Results.Ok(session.BackstageView(since ?? 0, limit ?? 200))
                : Results.Conflict(new
                {
                    error = "还没有叙事会话；先 POST /api/roleplay/start 开一场，或 POST /api/sessions/open 切到已有的一场。",
                }));

        // 角色卡清单（r13）：给"在场角色"勾选列表用。**只读**，口径与 RoleCardLoader 一致（roles/*.md 顶层）。
        // 目录不存在返回空数组而不是报错——新工作空间就是还没有卡，那不是错误。
        app.MapGet("/api/roles", (string? workspace, SessionHub hub) =>
        {
            var root = string.IsNullOrWhiteSpace(workspace) ? hub.Workspace?.Path : workspace;

            if (string.IsNullOrWhiteSpace(root))
            {
                return Results.Ok(Array.Empty<object>());
            }

            var rolesDir = Path.Combine(root, "roles");

            if (!Directory.Exists(rolesDir))
            {
                return Results.Ok(Array.Empty<object>());
            }

            var cards = MeetingRoom.Core.Roles.RoleCardLoader.LoadDirectory(rolesDir);

            return Results.Ok(cards.Select(card => new
            {
                id = card.Id,
                name = card.Name,
                role = card.Role == MeetingRoom.Core.Participants.ParticipantRole.Host ? "host" : "member",
            }).ToArray());
        });

        // 地图（《枭熊地图接入实现方案》V1）：最近一次快照 + 连接状态，前端第 1 列据此显示
        // "地图已连接 / 最后更新 / 标尺"。顺手拉一次——刚连上桥时不必等下一轮才有数。
        app.MapGet("/api/roleplay/vtt", async Task<IResult> (
            SessionHub hub,
            IServiceProvider services,
            MeetingRoomOptions options,
            CancellationToken ct) =>
        {
            if (hub.Roleplay is { } session)
            {
                await session.RefreshVttAsync(ct).ConfigureAwait(false);
                return Results.Ok(session.VttStatus());
            }

            // 还没开演也可以先确认桥通不通：用户摆地图的时候就会看这一列。
            if (services.GetService<IVttClient>() is { } client)
            {
                var snapshot = await client.GetSnapshotAsync(ct).ConfigureAwait(false);
                return Results.Ok(VttStatusView.Project(snapshot, enabled: true, options.Vtt.IncludeHidden));
            }

            return Results.Ok(VttStatusView.Idle("还没有叙事会话", enabled: false, options.Vtt.IncludeHidden));
        });
    }

    private static IResult NoRoleplay() =>
        Results.Conflict(new { error = "还没有叙事会话；先 POST /api/roleplay/start 开一场，或 POST /api/sessions/open 切到已有的一场。" });
}

public sealed record StartRoleplayRequest(
    string? StoryId,
    string? Worldview,
    string? Situation,
    string? UserRole = null,
    IReadOnlyList<string>? Cast = null,
    string? NarratorId = null);

public sealed record RoleplaySayRequest(string Text);

/// <summary>DM 提醒的请求体（§3.3）：<c>severity</c> 只接受 <c>info</c> / <c>warn</c>。</summary>
public sealed record DmNoteRequest(string? Severity, string? Text);

/// <summary>
/// 从会话快照里读 <c>status</c>（r13）：`RoleplaySession.Status` 不是公开成员，
/// 而快照里的 <c>status</c> 与它同源（`Status.ToWire()`），用来判断"resume 到底有没有生效"。
/// </summary>
internal static class RoleplayStatusProbe
{
    public static string StatusOf(object snapshot)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(snapshot);
        return node?["status"]?.GetValue<string>() ?? "unknown";
    }
}
