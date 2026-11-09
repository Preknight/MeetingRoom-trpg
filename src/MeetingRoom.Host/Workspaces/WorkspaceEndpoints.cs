using MeetingRoom.Core.Logging;
using MeetingRoom.Core.Paths;
using MeetingRoom.Core.Workspaces;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Workspaces;

/// <summary>
/// 工作空间端点（设计方案 §15.7）：列表 / 新建 / 打开 / 忘掉 / 目录浏览 / 导入。
/// <para>
/// 新建与打开都经过 <see cref="SessionHub"/>——它会顺带让旧会话退场，并广播
/// <c>workspace.list</c> / <c>workspace.opened</c> / <c>session.list</c>（侧栏据此重建）。
/// </para>
/// <para>
/// 目录浏览<b>只列目录与文件摘要</b>，绝不返回文件内容——它是"选一个目录当工作空间"的辅助，
/// 不是文件管理器。服务只绑回环 + 一次性 token（§10.4），所以这个能力等价于"同一台机器上的目录浏览"。
/// </para>
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        // 列表 + 当前打开的是哪一个 + 它的会话（重连后侧栏据此重建，不必再多一次往返）。
        app.MapGet("/api/workspaces", (WorkspaceRegistry registry, SessionHub hub) => Results.Ok(new
        {
            current = registry.Current?.Path,
            registryFile = registry.RegistryFile,
            workspaces = registry.List().Select(WorkspaceProjection.ToWire).ToArray(),
            sessions = registry.Current is { } current
                ? hub.ListSessions(current).Select(WorkspaceProjection.SessionToWire).ToArray()
                : [],
        }));

        // 新建：已是一个工作空间（有 workspace.json）时直接打开、不覆盖（§15.4）。
        app.MapPost("/api/workspaces", async Task<IResult> (
            CreateWorkspaceRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { error = "path 不能为空。" });
            }

            var kind = WorkspaceDescriptor.ParseKind(request.Kind, WorkspaceKind.Roleplay);

            if (kind == WorkspaceKind.Legacy)
            {
                return Results.BadRequest(new { error = "legacy 是旧数据的过渡类型，不能拿来新建。" });
            }

            try
            {
                var descriptor = await hub.CreateWorkspaceAsync(request.Path, kind, request.Name).ConfigureAwait(false);
                return Results.Ok(Project(descriptor, hub));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 打开已存在的：已注册直接切；没注册过就按目录里的 workspace.json 认。
        app.MapPost("/api/workspaces/open", async Task<IResult> (
            OpenWorkspaceRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { error = "path 不能为空。" });
            }

            try
            {
                var kind = request.Kind is null
                    ? (WorkspaceKind?)null
                    : WorkspaceDescriptor.ParseKind(request.Kind, WorkspaceKind.Roleplay);

                var descriptor = await hub.OpenWorkspaceAsync(request.Path, kind).ConfigureAwait(false);
                return Results.Ok(Project(descriptor, hub));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 从清单移除（不删磁盘上的任何东西；移除的若是当前工作空间，会话一并退场）。
        app.MapPost("/api/workspaces/forget", async Task<IResult> (
            OpenWorkspaceRequest request,
            SessionHub hub,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { error = "path 不能为空。" });
            }

            return await hub.ForgetWorkspaceAsync(request.Path).ConfigureAwait(false)
                ? Results.Ok(new { forgotten = request.Path })
                : Results.NotFound(new { error = $"清单里没有这个工作空间：{request.Path}" });
        });

        // 目录浏览：给"选目录"对话框用。只列一层，给名字 + 类型，不给文件内容。
        // 起点默认是**用户主目录**（选工作空间一般发生在文档 / 桌面附近），取不到就退回安装目录。
        app.MapGet("/api/workspaces/browse", (string? path) =>
        {
            var target = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
                    ? home
                    : PathLayout.InstallDir
                : path;

            string full;

            try
            {
                full = Path.GetFullPath(target);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { error = $"路径不合法：{ex.Message}" });
            }

            if (!Directory.Exists(full))
            {
                return Results.NotFound(new { error = $"目录不存在：{full}" });
            }

            try
            {
                var directories = Directory
                    .GetDirectories(full)
                    .Select(directory => new
                    {
                        name = Path.GetFileName(directory),
                        path = directory,
                        isWorkspace = File.Exists(Path.Combine(directory, "workspace.json")),
                    })
                    .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var files = Directory
                    .GetFiles(full)
                    .Select(file => new { name = Path.GetFileName(file), size = new FileInfo(file).Length })
                    .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return Results.Ok(new
                {
                    path = full,
                    parent = Path.GetDirectoryName(full),
                    isWorkspace = File.Exists(Path.Combine(full, "workspace.json")),
                    directories,
                    // 只给名字与大小：这是"选目录"的辅助，不做文件预览。
                    files,
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Results.BadRequest(new { error = $"读不了这个目录：{ex.Message}" });
            }
        });

        // 导入：把工作空间**以外**的文件 / 目录复制进来（原文件不动，§15.5）。
        app.MapPost("/api/workspaces/import", (
            ImportRequest request,
            WorkspaceRegistry registry,
            SessionHub hub,
            FileLog log) =>
        {
            var root = string.IsNullOrWhiteSpace(request.WorkspacePath)
                ? registry.Current?.Path
                : request.WorkspacePath;

            if (string.IsNullOrWhiteSpace(root))
            {
                return Results.BadRequest(new { error = "没有指定 workspacePath，当前也没有打开工作空间。" });
            }

            if (!TryParseTarget(request.Target, out var target))
            {
                return Results.BadRequest(new { error = $"target 只接受 roles / materials / worldbook / drafts，收到：{request.Target}" });
            }

            if (request.Sources is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "sources 不能为空。" });
            }

            try
            {
                var layout = new WorkspaceLayout(root);
                var result = new WorkspaceImporter(layout).Import(request.Sources, target);
                log.Info($"导入：复制 {result.Copied.Count} 个文件到 {request.Target}（跳过 {result.Skipped.Count} 个）→ {layout.Root}");

                // 导入直接进归档区（成品语义）：第 1 列的上半段要立刻刷新。
                hub.PublishDrafts();

                return Results.Ok(new
                {
                    workspacePath = layout.Root,
                    target = request.Target,
                    copied = result.Copied
                        .Select(item => new { source = item.Source, target = item.Target, bytes = item.Bytes })
                        .ToArray(),
                    // 跳过一律带原因：导入是"复制用户的东西"，静默丢弃比失败更糟。
                    skipped = result.Skipped,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static object Project(WorkspaceDescriptor descriptor, SessionHub hub) => new
    {
        path = descriptor.Path,
        kind = descriptor.KindWire,
        name = descriptor.Name,
        createdAt = descriptor.CreatedAt,
        sessionCount = WorkspaceProjection.SessionCount(descriptor),
        sessions = hub.ListSessions(descriptor).Select(WorkspaceProjection.SessionToWire).ToArray(),
    };

    private static bool TryParseTarget(string? value, out WorkspaceTarget target)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "roles":
                target = WorkspaceTarget.Roles;
                return true;
            case "materials":
                target = WorkspaceTarget.Materials;
                return true;
            case "worldbook":
                target = WorkspaceTarget.WorldBook;
                return true;
            case "drafts":
                target = WorkspaceTarget.Drafts;
                return true;
            default:
                target = default;
                return false;
        }
    }
}

public sealed record CreateWorkspaceRequest(string Path, string? Kind = null, string? Name = null);

public sealed record OpenWorkspaceRequest(string Path, string? Kind = null);

public sealed record ImportRequest(string? WorkspacePath, IReadOnlyList<string>? Sources, string? Target);
