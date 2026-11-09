using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Workspaces;

namespace MeetingRoom.Core.Drafts;

/// <summary>待整理条目的类型（设计方案 §15.6）：归档时各进各的区。</summary>
public enum DraftKind
{
    /// <summary>角色卡 → <c>roles/</c>。</summary>
    Role,

    /// <summary>资料 → <c>materials/</c>。</summary>
    Material,

    /// <summary>世界书条目 → <c>worldbook/</c>。</summary>
    WorldBook,
}

/// <summary>
/// 一条待整理条目：<c>drafts/&lt;id&gt;/entry.json</c> + 一份内容文件（设计方案 §15.6）。
/// <b>不存表单字段</b>——内容文件本身就是那张卡 / 那份资料，编辑时再解析回来（"已有内容自动带入"）。
/// </summary>
public sealed record DraftEntry
{
    public required string Id { get; init; }

    public required DraftKind Kind { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>内容文件名（默认 <c>&lt;id&gt;.md</c>）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>类型的线上写法（与端点 / 前端同一套）。</summary>
    [JsonIgnore]
    public string KindWire => WireOf(Kind);

    public static string WireOf(DraftKind kind) => kind switch
    {
        DraftKind.Role => "role",
        DraftKind.Material => "material",
        _ => "worldbook",
    };

    public static DraftKind ParseKind(string? wire, DraftKind fallback = DraftKind.Material) => wire?.Trim().ToLowerInvariant() switch
    {
        "role" => DraftKind.Role,
        "material" => DraftKind.Material,
        "worldbook" => DraftKind.WorldBook,
        _ => fallback,
    };
}

/// <summary>归档区里的一份东西（只读列出用）。</summary>
public sealed record ArchivedItem(DraftKind Kind, string Id, string Title, string Path, long Bytes)
{
    public string KindWire => DraftEntry.WireOf(Kind);
}

/// <summary>
/// 待整理区（<c>drafts/</c>）与三个归档区（<c>roles/</c> / <c>materials/</c> / <c>worldbook/</c>）的仓库
/// （设计方案 §15.6）：**归档 = 把内容文件移进归档区**，撤销归档 = 移回来。
/// <para>
/// 手写的落这里（可改、可归档）；**导入不经过这里**——它直接进归档区（外部已编辑好的成品，§15.6）。
/// </para>
/// </summary>
public sealed class DraftStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WorkspaceLayout _layout;

    public DraftStore(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _layout = layout;
    }

    /// <summary>这个工作空间的待整理区根目录。</summary>
    public string Root => _layout.DraftsDir;

    /// <summary>待整理条目（按标题排序，结果稳定）。</summary>
    public IReadOnlyList<DraftEntry> List()
    {
        var directory = _layout.DraftsDir;

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .GetDirectories(directory)
            .Select(path => Read(path) ?? Fallback(path))
            .OrderBy(entry => entry.CreatedAt)
            .ToArray();
    }

    public DraftEntry? Find(string id)
    {
        if (SessionIds.Clean(id) is not { Length: > 0 } safeId)
        {
            return null;
        }

        var directory = Path.Combine(_layout.DraftsDir, safeId);

        return Directory.Exists(directory) ? Read(directory) ?? Fallback(directory) : null;
    }

    /// <summary>条目目录（<c>drafts/&lt;id&gt;/</c>）。<b>不叫 <c>Directory</c></b>——那会与 <c>System.IO.Directory</c> 撞名。
    /// </summary>
    public string EntryDirectory(string id) => Path.Combine(_layout.DraftsDir, SessionIds.Clean(id));

    /// <summary>条目的内容文件路径。</summary>
    public string ContentPath(DraftEntry entry) =>
        Path.Combine(EntryDirectory(entry.Id), string.IsNullOrWhiteSpace(entry.FileName) ? $"{entry.Id}.md" : entry.FileName);

    /// <summary>读内容文件全文（不存在就是空串）。</summary>
    public string ReadBody(string id) =>
        Find(id) is { } entry && File.Exists(ContentPath(entry)) ? File.ReadAllText(ContentPath(entry)) : string.Empty;

    /// <summary>新建一条待整理条目：写 <c>entry.json</c> + 内容文件。撞名顺延，不覆盖。</summary>
    public DraftEntry Create(DraftKind kind, string? title, string? id, string? body)
    {
        Directory.CreateDirectory(_layout.DraftsDir);

        var desired = SessionIds.Clean(id) is { Length: > 0 } cleaned
            ? cleaned
            : SessionIds.FromTitle(title);

        var slug = SessionIds.Unique(_layout.DraftsDir, desired);
        var directory = Path.Combine(_layout.DraftsDir, slug);
        Directory.CreateDirectory(directory);

        var entry = new DraftEntry
        {
            Id = slug,
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? slug : title.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            FileName = $"{slug}.md",
        };

        File.WriteAllText(Path.Combine(directory, entry.FileName), body ?? string.Empty, Utf8NoBom);
        Save(entry);

        return entry;
    }

    /// <summary>改标题 / 正文（<paramref name="body"/> 为 <c>null</c> 表示只改标题）。类型不可改——要换类型就删了重建。</summary>
    public DraftEntry? Update(string id, string? title, string? body)
    {
        if (Find(id) is not { } entry)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            entry = entry with { Title = title.Trim() };
        }

        if (body is not null)
        {
            File.WriteAllText(ContentPath(entry), body, Utf8NoBom);
        }

        Save(entry);
        return entry;
    }

    /// <summary>删掉一条待整理条目（连同它的目录）。</summary>
    public bool Delete(string id)
    {
        if (Find(id) is not { } entry || !IsInside(_layout.DraftsDir, EntryDirectory(entry.Id)))
        {
            return false;
        }

        Directory.Delete(EntryDirectory(entry.Id), recursive: true);
        return true;
    }

    /// <summary>归档：内容文件移进 <c>roles/</c> / <c>materials/</c> / <c>worldbook/</c>，删掉待整理目录。</summary>
    public ArchivedItem? Archive(string id)
    {
        if (Find(id) is not { } entry)
        {
            return null;
        }

        var target = TargetDirectory(entry.Kind);
        Directory.CreateDirectory(target);

        var destination = UniqueFile(target, entry.FileName, entry.Id);
        File.Move(ContentPath(entry), destination);
        Directory.Delete(EntryDirectory(entry.Id), recursive: true);

        return new ArchivedItem(entry.Kind, entry.Id, entry.Title, destination, new FileInfo(destination).Length);
    }

    /// <summary>撤销归档：把归档区里的那份移回待整理（撞名顺延）。</summary>
    public DraftEntry? Unarchive(DraftKind kind, string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return null;
        }

        var path = Path.GetFullPath(archivePath);

        if (!IsInside(TargetDirectory(kind), path) || !File.Exists(path))
        {
            return null;
        }

        var id = Path.GetFileNameWithoutExtension(path);
        var body = File.ReadAllText(path);
        var entry = Create(kind, TitleOf(kind, path, id, body), id, body);

        File.Delete(path);
        return entry;
    }

    /// <summary>列已归档的东西（<paramref name="kind"/> 为 <c>null</c> 时三个区都列）。</summary>
    public IReadOnlyList<ArchivedItem> ListArchived(DraftKind? kind = null)
    {
        var items = new List<ArchivedItem>();

        foreach (var target in new[] { DraftKind.Role, DraftKind.Material, DraftKind.WorldBook })
        {
            if (kind is { } wanted && wanted != target)
            {
                continue;
            }

            var directory = TargetDirectory(target);

            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                items.Add(new ArchivedItem(target, id, TitleOf(target, file, id, null), file, new FileInfo(file).Length));
            }
        }

        return items;
    }

    /// <summary>设计文档说"归档 = 移进归档区"，所以撤销归档要能认出归档区里的路径。</summary>
    public string TargetDirectory(DraftKind kind) => kind switch
    {
        DraftKind.Role => _layout.RolesDir,
        DraftKind.Material => _layout.MaterialsDir,
        _ => _layout.WorldBookDir,
    };

    /// <summary>标题：角色卡解析出 <c>name</c>，其余用文件名（资料 / 世界书条目没有更强的元信息）。</summary>
    private static string TitleOf(DraftKind kind, string path, string fallbackId, string? body)
    {
        if (kind != DraftKind.Role)
        {
            return fallbackId;
        }

        try
        {
            var markdown = body ?? File.ReadAllText(path);
            var name = RoleCardLoader.Parse(markdown, fallbackId).Name;

            return string.IsNullOrWhiteSpace(name) ? fallbackId : name;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return fallbackId;
        }
    }

    /// <summary>归档目标名被占用时顺延（<c>a.md</c> → <c>a-2.md</c>）：归档绝不覆盖已有文件。</summary>
    private static string UniqueFile(string directory, string fileName, string fallbackId)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? $"{fallbackId}.md" : fileName;
        var candidate = name;

        for (var suffix = 2; File.Exists(Path.Combine(directory, candidate)) && suffix < 1000; suffix++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)}-{suffix}{Path.GetExtension(name)}";
        }

        return Path.Combine(directory, candidate);
    }

    /// <summary>路径复核：只认工作空间自己的目录（防 id 里混进路径）。</summary>
    private static bool IsInside(string parent, string path) =>
        WorkspaceRegistry.SamePath(Path.GetDirectoryName(path) ?? string.Empty, parent);

    private void Save(DraftEntry entry)
    {
        var directory = EntryDirectory(entry.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "entry.json"), JsonSerializer.Serialize(entry, JsonOptions), Utf8NoBom);
    }

    private static DraftEntry? Read(string directory)
    {
        var path = Path.Combine(directory, "entry.json");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();

            if (node is null)
            {
                return null;
            }

            var name = Path.GetFileName(directory);

            return new DraftEntry
            {
                Id = node["id"]?.GetValue<string>() is { Length: > 0 } id ? id : name,
                Kind = DraftEntry.ParseKind(node["kind"]?.GetValue<string>()),
                Title = node["title"]?.GetValue<string>() is { Length: > 0 } title ? title : name,
                CreatedAt = node["createdAt"]?.GetValue<DateTimeOffset>() ?? Directory.GetCreationTimeUtc(directory),
                FileName = node["fileName"]?.GetValue<string>() is { Length: > 0 } file ? file : $"{name}.md",
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or IOException)
        {
            // 坏掉的元信息不该让整个列表挂掉：按目录名兜底。
            return null;
        }
    }

    private static DraftEntry Fallback(string directory)
    {
        var name = Path.GetFileName(directory);

        return new DraftEntry
        {
            Id = name,
            Kind = DraftKind.Material,
            Title = name,
            CreatedAt = Directory.GetCreationTimeUtc(directory),
            FileName = $"{name}.md",
        };
    }
}
