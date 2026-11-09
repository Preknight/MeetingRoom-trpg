using MeetingRoom.Core.Drafts;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roles;
using MeetingRoom.Host.Sessions;

namespace MeetingRoom.Host.Drafts;

/// <summary>
/// 待整理 / 归档端点（设计方案 §15.6 / §15.7）。
/// <para>
/// **手写录入走这里**（落待整理、可改、可归档）；**导入不走这里**——导入直接进归档区
/// （"外部已经编辑好的成品"，§15.6），走 <c>POST /api/workspaces/import</c>。
/// </para>
/// <para>
/// 角色卡的表单字段在这一层渲染成 §5.4 的 frontmatter（<see cref="RoleCardWriter"/>）：
/// 格式只有一个真源，前端不碰。
/// </para>
/// </summary>
public static class DraftEndpoints
{
    public static void MapDraftEndpoints(this WebApplication app)
    {
        // 第 1 列的两段一次给全：待整理（可改）+ 已归档（只读、进上下文）。
        app.MapGet("/api/drafts", (SessionHub hub) =>
        {
            if (hub.Drafts is not { } store || hub.Workspace is not { } workspace)
            {
                return NoWorkspace();
            }

            return Results.Ok(new
            {
                workspacePath = workspace.Path,
                drafts = store.List().Select(DraftProjection.ToWire).ToArray(),
                archived = store.ListArchived().Select(DraftProjection.ToWire).ToArray(),
            });
        });

        // 新建 / 更新一条待整理条目（带 id 就是更新）。
        app.MapPost("/api/drafts", async Task<IResult> (DraftRequest request, SessionHub hub, CancellationToken ct) =>
        {
            if (hub.Drafts is not { } store || hub.Workspace is not { } workspace)
            {
                return NoWorkspace();
            }

            var kind = DraftEntry.ParseKind(request.Kind, DraftKind.Material);

            try
            {
                var entry = request.Id is { Length: > 0 } id && store.Find(id) is { } existing
                    ? Update(store, existing, request)
                    : Create(store, kind, request);

                hub.PublishDrafts();

                return Results.Ok(new
                {
                    workspacePath = workspace.Path,
                    entry = DraftProjection.ToWire(entry),
                    body = store.ReadBody(entry.Id),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 打开一条待整理条目：连正文一起给（编辑回填用）。
        app.MapGet("/api/drafts/content", (string id, SessionHub hub) =>
        {
            if (hub.Drafts is not { } store)
            {
                return NoWorkspace();
            }

            if (store.Find(id) is not { } entry)
            {
                return Results.NotFound(new { error = $"待整理区里没有这条：{id}" });
            }

            var body = store.ReadBody(entry.Id);

            return Results.Ok(new
            {
                id = entry.Id,
                kind = entry.KindWire,
                title = entry.Title,
                body,
                // 角色卡的字段由后端解析出来回填表单——frontmatter 的真源在后端（§15.11），前端不碰格式。
                card = entry.Kind == DraftKind.Role ? CardWire(body, entry.Id) : null,
            });
        });

        // 删掉一条待整理条目（连同它的目录）。
        app.MapPost("/api/drafts/delete", (DraftDeleteRequest request, SessionHub hub) =>
        {
            if (hub.Drafts is not { } store)
            {
                return NoWorkspace();
            }

            if (request.Id is not { Length: > 0 } id || !store.Delete(id))
            {
                return Results.NotFound(new { error = $"待整理区里没有这条：{request.Id}" });
            }

            hub.PublishDrafts();
            return Results.Ok(new { deleted = true, id });
        });

        // 归档：内容文件移进 roles/ / materials/ / worldbook/，进了归档区才会进上下文（§15.6）。
        app.MapPost("/api/drafts/archive", (DraftDeleteRequest request, SessionHub hub) =>
        {
            if (hub.Drafts is not { } store)
            {
                return NoWorkspace();
            }

            if (request.Id is not { Length: > 0 } id || store.Archive(id) is not { } archived)
            {
                return Results.NotFound(new { error = $"待整理区里没有这条：{request.Id}" });
            }

            hub.PublishDrafts();
            return Results.Ok(new { archived = true, item = DraftProjection.ToWire(archived) });
        });

        // 撤销归档：把归档区里的那份移回待整理（要改就得先撤销归档，§15.6）。
        app.MapPost("/api/drafts/unarchive", (DraftUnarchiveRequest request, SessionHub hub) =>
        {
            if (hub.Drafts is not { } store)
            {
                return NoWorkspace();
            }

            var kind = DraftEntry.ParseKind(request.Kind, DraftKind.Material);

            if (store.Unarchive(kind, request.Path ?? string.Empty) is not { } entry)
            {
                return Results.NotFound(new { error = $"归档区里没有这份：{request.Path}" });
            }

            hub.PublishDrafts();
            return Results.Ok(new { unarchived = true, entry = DraftProjection.ToWire(entry) });
        });

        // 只读列出一份归档内容（「查看已归档」与角色列表用它；正文只在点击时拉）。
        app.MapGet("/api/archived/content", (string path, SessionHub hub) =>
        {
            if (hub.Drafts is not { } store || hub.Workspace is not { } workspace)
            {
                return NoWorkspace();
            }

            var full = Path.GetFullPath(path);
            var allowed = store
                .ListArchived()
                .Any(item => string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase));

            // 只认自己列出来的那些文件：这个端点不该变成任意文件读取。
            if (!allowed || !File.Exists(full))
            {
                return Results.NotFound(new { error = $"归档区里没有这份：{path}" });
            }

            var item = store
                .ListArchived()
                .First(candidate => string.Equals(candidate.Path, full, StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new
            {
                id = item.Id,
                title = item.Title,
                path = full,
                content = File.ReadAllText(full),
            });
        });
    }

    /// <summary>把一张角色卡解析成表单字段（编辑回填用）；解析不了就只给正文。</summary>
    private static object? CardWire(string markdown, string fallbackId)
    {
        try
        {
            var card = RoleCardLoader.Parse(markdown, fallbackId);

            return new
            {
                name = card.Name,
                title = card.Title,
                summary = card.Summary,
                role = card.Role == ParticipantRole.Host ? "host" : "member",
                model = card.Model,
                effort = card.Effort,
                allowedTools = card.AllowedTools,
                readOnly = card.ReadOnly,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static DraftEntry Create(DraftStore store, DraftKind kind, DraftRequest request)
    {
        // 先把 id 定下来（Create 内部会顺延），再渲染——否则 markdown 里的 id 会和目录名对不上。
        var entry = store.Create(kind, TitleOf(request), request.Id, body: string.Empty);

        return Finalize(store, entry, kind, request);
    }

    private static DraftEntry Update(DraftStore store, DraftEntry existing, DraftRequest request) =>
        Finalize(store, existing, existing.Kind, request);

    private static DraftEntry Finalize(DraftStore store, DraftEntry entry, DraftKind kind, DraftRequest request)
    {
        if (kind != DraftKind.Role)
        {
            // 资料 / 世界书条目：正文原样落盘（用户自己写 Markdown）。
            return store.Update(entry.Id, TitleOf(request), request.Body ?? store.ReadBody(entry.Id))!;
        }

        var card = BuildCard(request, entry.Id);
        var markdown = RoleCardWriter.Render(card);

        return store.Update(entry.Id, card.Name, markdown)!;
    }

    private static RoleCard BuildCard(DraftRequest request, string id)
    {
        var fields = request.Card;

        if (fields is null)
        {
            throw new InvalidDataException("角色卡要带 card 字段（id / name / title / summary / role / 正文）。");
        }

        var name = fields.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("角色卡的 name 必填——它是卡片上显示的名字。");
        }

        return new RoleCard
        {
            Id = id,
            Name = name,
            Title = fields.Title?.Trim() ?? string.Empty,
            Summary = fields.Summary?.Trim() ?? string.Empty,
            Role = string.Equals(fields.Role?.Trim(), "host", StringComparison.OrdinalIgnoreCase)
                ? ParticipantRole.Host
                : ParticipantRole.Member,
            Model = fields.Model?.Trim() ?? string.Empty,
            Effort = fields.Effort?.Trim() ?? string.Empty,
            AllowedTools = (fields.AllowedTools ?? [])
                .Select(tool => tool.Trim())
                .Where(tool => tool.Length > 0)
                .ToArray(),
            ReadOnly = fields.ReadOnly ?? false,
            Body = request.Body ?? string.Empty,
        };
    }

    private static string? TitleOf(DraftRequest request) =>
        request.Kind is not null && DraftEntry.ParseKind(request.Kind, DraftKind.Material) == DraftKind.Role
            ? request.Card?.Name?.Trim() ?? request.Title
            : request.Title;

    private static IResult NoWorkspace() =>
        Results.Conflict(new { error = "还没有打开工作空间；待整理区属于某个工作空间。" });
}

/// <summary>新建 / 更新待整理条目。</summary>
public sealed record DraftRequest(
    string? Id = null,
    string? Kind = null,
    string? Title = null,
    string? Body = null,
    RoleCardFields? Card = null);

/// <summary>角色卡的表单字段（设计方案 §15.11）；<c>Body</c> 走 <see cref="DraftRequest.Body"/>。</summary>
public sealed record RoleCardFields(
    string? Id = null,
    string? Name = null,
    string? Title = null,
    string? Summary = null,
    string? Role = null,
    string? Model = null,
    string? Effort = null,
    IReadOnlyList<string>? AllowedTools = null,
    bool? ReadOnly = null);

/// <summary>删 / 归档一条待整理条目。</summary>
public sealed record DraftDeleteRequest(string? Id = null);

/// <summary>撤销归档：要给归档区里的完整路径（它不一定还叫当初的 id）。</summary>
public sealed record DraftUnarchiveRequest(string? Kind = null, string? Path = null);
