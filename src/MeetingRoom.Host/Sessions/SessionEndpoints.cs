using MeetingRoom.Core.Workspaces;
using MeetingRoom.Host.Workspaces;

namespace MeetingRoom.Host.Sessions;

/// <summary>
/// 会话端点（设计方案 §15.7）：列表 / 新建 / 切换。
/// <para>
/// 新建与"开跑"是两件事：这里把会话目录建出来（<c>session.json</c>）并切过去，
/// 真正开跑仍是 <c>POST /api/meeting/start</c> 与 <c>POST /api/roleplay/start</c> ——
/// 那两个也可以一步到位（带上 <c>sessionId</c> / <c>storyId</c>）。
/// </para>
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        // 列出会话：默认列当前工作空间的，可用 ?kind=meeting|roleplay 过滤。
        app.MapGet("/api/sessions", (string? kind, SessionHub hub, WorkspaceRegistry registry) =>
        {
            if (registry.Current is not { } workspace)
            {
                return Results.Ok(new { workspacePath = (string?)null, sessions = Array.Empty<object>() });
            }

            var sessions = hub.ListSessions(workspace);

            if (!string.IsNullOrWhiteSpace(kind))
            {
                var wanted = WorkspaceDescriptor.ParseKind(kind, workspace.Kind);
                sessions = sessions.Where(session => session.Kind == wanted).ToArray();
            }

            return Results.Ok(new
            {
                workspacePath = workspace.Path,
                sessions = sessions.Select(WorkspaceProjection.SessionToWire).ToArray(),
            });
        });

        // 新建一场会话并切过去（不自动开跑）。
        app.MapPost("/api/sessions", async Task<IResult> (
            CreateSessionRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            WorkspaceDescriptor workspace;

            try
            {
                workspace = string.IsNullOrWhiteSpace(request.WorkspacePath)
                    ? hub.RequireWorkspace()
                    : await hub.UseWorkspaceAsync(request.WorkspacePath).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            var defaultKind = workspace.Kind == WorkspaceKind.Roleplay ? WorkspaceKind.Roleplay : WorkspaceKind.Meeting;
            var kind = WorkspaceDescriptor.ParseKind(request.Kind, defaultKind);

            if (kind == WorkspaceKind.Legacy)
            {
                return Results.BadRequest(new { error = "legacy 是旧数据的过渡类型，不能拿来新建。" });
            }

            if (kind == WorkspaceKind.Roleplay
                && (string.IsNullOrWhiteSpace(request.Worldview) || string.IsNullOrWhiteSpace(request.Situation)))
            {
                return Results.BadRequest(new { error = "叙事会话要 worldview 与 situation 都填。" });
            }

            try
            {
                var descriptor = await hub
                    .CreateSessionAsync(
                        workspace,
                        kind,
                        new SessionCreateSpec(
                            Id: request.Id,
                            Title: request.Title,
                            Topic: request.Topic,
                            Worldview: request.Worldview,
                            Situation: request.Situation,
                            UserRole: request.UserRole,
                            Cast: request.Cast,
                            NarratorId: request.NarratorId),
                        ct)
                    .ConfigureAwait(false);

                return Results.Ok(new
                {
                    workspacePath = workspace.Path,
                    session = WorkspaceProjection.SessionToWire(descriptor),
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 切到一场已有会话（侧栏点选；不自动开跑）。
        app.MapPost("/api/sessions/open", async Task<IResult> (
            OpenSessionRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return Results.BadRequest(new { error = "id 不能为空。" });
            }

            WorkspaceDescriptor workspace;

            try
            {
                workspace = string.IsNullOrWhiteSpace(request.WorkspacePath)
                    ? hub.RequireWorkspace()
                    : await hub.UseWorkspaceAsync(request.WorkspacePath).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            var defaultKind = workspace.Kind == WorkspaceKind.Roleplay ? WorkspaceKind.Roleplay : WorkspaceKind.Meeting;
            var kind = WorkspaceDescriptor.ParseKind(request.Kind, defaultKind);

            try
            {
                var descriptor = await hub.OpenSessionAsync(kind, request.Id, ct).ConfigureAwait(false);

                return Results.Ok(new
                {
                    workspacePath = workspace.Path,
                    session = WorkspaceProjection.SessionToWire(descriptor),
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 删掉一场会话（连同它的目录）。删除不可逆，二次确认在前端（§15.3）。
        app.MapPost("/api/sessions/delete", async Task<IResult> (
            OpenSessionRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return Results.BadRequest(new { error = "id 不能为空。" });
            }

            WorkspaceDescriptor workspace;

            try
            {
                workspace = string.IsNullOrWhiteSpace(request.WorkspacePath)
                    ? hub.RequireWorkspace()
                    : await hub.UseWorkspaceAsync(request.WorkspacePath).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            var defaultKind = workspace.Kind == WorkspaceKind.Roleplay ? WorkspaceKind.Roleplay : WorkspaceKind.Meeting;
            var kind = WorkspaceDescriptor.ParseKind(request.Kind, defaultKind);

            try
            {
                return await hub.DeleteSessionAsync(kind, request.Id, ct).ConfigureAwait(false)
                    ? Results.Ok(new { deleted = true, id = SessionIds.Clean(request.Id) })
                    : Results.NotFound(new { error = $"这个工作空间里没有这场会话：{request.Id}" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

/// <summary>新建会话的请求体：会议只填 <c>topic</c>，叙事填世界观 / 情境 /（可选）身份与卡司。</summary>
public sealed record CreateSessionRequest(
    string? WorkspacePath = null,
    string? Kind = null,
    string? Id = null,
    string? Title = null,
    string? Topic = null,
    string? Worldview = null,
    string? Situation = null,
    string? UserRole = null,
    IReadOnlyList<string>? Cast = null,
    string? NarratorId = null);

/// <summary>切换会话的请求体。</summary>
public sealed record OpenSessionRequest(string? WorkspacePath = null, string? Kind = null, string? Id = null);
