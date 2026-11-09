using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Workspaces;

/// <summary>
/// 一个工作空间里的**会话目录**（设计方案 §15.2）：会议在 <c>meetings/&lt;id&gt;/</c>、叙事在
/// <c>stories/&lt;id&gt;/</c>，每场一份 <c>session.json</c> 记名字与设定。
/// <para>
/// 它只管目录与元信息（建 / 列 / 读 / 写）；把一场会话真正装配起来（角色卡、资料库、事件总线）
/// 是 <see cref="WorkspaceSessionFactory"/> 的事。
/// </para>
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        // 与 workspace.json / 工作空间清单保持同一种写法（"meeting" / "roleplay"）。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WorkspaceLayout _layout;
    private readonly Lock _gate = new();

    public SessionStore(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _layout = layout;
    }

    /// <summary>本会话目录所属的工作空间根。</summary>
    public string Root => _layout.Root;

    /// <summary>某场会话的目录（会议进 <c>meetings/</c>、叙事进 <c>stories/</c>）。</summary>
    public string SessionDirectory(WorkspaceKind kind, string id) => _layout.SessionDir(Normalize(kind), id);

    /// <summary>列出这个工作空间下的会话，最近动过的排前面。</summary>
    public IReadOnlyList<SessionDescriptor> List(WorkspaceKind kind)
    {
        var actual = Normalize(kind);
        var parent = Parent(actual);

        if (!Directory.Exists(parent))
        {
            return [];
        }

        return Directory
            .GetDirectories(parent)
            .Select(directory => Read(directory, actual) ?? Fallback(directory, actual))
            .OrderByDescending(session => session.CreatedAt)
            .ToArray();
    }

    /// <summary>按 id 找一场会话；目录在但元信息坏了就按目录名兜底（不假装它不存在）。</summary>
    public SessionDescriptor? Find(WorkspaceKind kind, string id)
    {
        var actual = Normalize(kind);
        var directory = SessionDirectory(actual, id);

        return Directory.Exists(directory) ? Read(directory, actual) ?? Fallback(directory, actual) : null;
    }

    public bool Exists(WorkspaceKind kind, string id) => Directory.Exists(SessionDirectory(kind, id));

    /// <summary>
    /// 删掉一场会话（连同它的目录）。**只认这个工作空间自己的会话目录**：id 先净化，
    /// 拼出来的路径还要复核"父目录正好是 meetings/ 或 stories/"——别的地方一个字节都不动。
    /// </summary>
    public bool Delete(WorkspaceKind kind, string id)
    {
        var actual = Normalize(kind);
        var safeId = SessionIds.Clean(id);

        if (safeId.Length == 0)
        {
            return false;
        }

        var directory = SessionDirectory(actual, safeId);

        if (!WorkspaceRegistry.SamePath(Path.GetDirectoryName(directory) ?? string.Empty, Parent(actual)))
        {
            return false;
        }

        if (!Directory.Exists(directory))
        {
            return false;
        }

        Directory.Delete(directory, recursive: true);
        return true;
    }

    /// <summary>
    /// 新建会话目录并写 <c>session.json</c>。<paramref name="id"/> 为空时从标题 / 议题 / 世界观生成；
    /// 与已有会话撞名就顺延（<c>-2</c>、<c>-3</c>…），**绝不覆盖已有会话**。
    /// </summary>
    public SessionDescriptor Create(
        WorkspaceKind kind,
        string? title = null,
        string? id = null,
        string? topic = null,
        string? worldview = null,
        string? situation = null,
        string? userRole = null,
        IReadOnlyList<string>? cast = null,
        string? narratorId = null)
    {
        var actual = Normalize(kind);
        var parent = Parent(actual);

        Directory.CreateDirectory(parent);

        var desired = SessionIds.Clean(id) is { Length: > 0 } cleaned
            ? cleaned
            : SessionIds.FromTitle(title ?? topic ?? worldview);

        var descriptor = new SessionDescriptor
        {
            Id = SessionIds.Unique(parent, desired),
            Kind = actual,
            Title = TitleFor(actual, title, topic, worldview),
            CreatedAt = DateTimeOffset.UtcNow,
            Topic = topic,
            Worldview = worldview,
            Situation = situation,
            UserRole = userRole,
            Cast = cast ?? [],
            NarratorId = narratorId,
        };

        Save(descriptor);
        return descriptor;
    }

    /// <summary>写回元信息（会话目录会被一并建出来）。</summary>
    public void Save(SessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var directory = SessionDirectory(descriptor.Kind, descriptor.Id);
        Directory.CreateDirectory(directory);

        lock (_gate)
        {
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(descriptor, JsonOptions));
        }
    }

    /// <summary>Legacy 工作空间（旧的 <c>data/</c>）里两种会话都在，但目录一律按会议号排（§15.8）。</summary>
    private static WorkspaceKind Normalize(WorkspaceKind kind) =>
        kind == WorkspaceKind.Legacy ? WorkspaceKind.Meeting : kind;

    private string Parent(WorkspaceKind kind) => kind == WorkspaceKind.Roleplay ? _layout.StoriesDir : _layout.MeetingsDir;

    private static string TitleFor(WorkspaceKind kind, string? title, string? topic, string? worldview)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return Excerpt(title);
        }

        if (kind == WorkspaceKind.Roleplay ? !string.IsNullOrWhiteSpace(worldview) : !string.IsNullOrWhiteSpace(topic))
        {
            return Excerpt(kind == WorkspaceKind.Roleplay ? worldview! : topic!);
        }

        return kind == WorkspaceKind.Roleplay ? "未命名叙事" : "未命名会议";
    }

    private static string Excerpt(string value)
    {
        var text = value.Trim().ReplaceLineEndings(" ");
        return text.Length <= 60 ? text : text[..60];
    }

    private static SessionDescriptor? Read(string directory, WorkspaceKind kind)
    {
        var path = Path.Combine(directory, "session.json");

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

            return new SessionDescriptor
            {
                Id = node["id"]?.GetValue<string>() is { Length: > 0 } id ? id : name,
                Kind = WorkspaceDescriptor.ParseKind(node["kind"]?.GetValue<string>(), kind),
                Title = node["title"]?.GetValue<string>() is { Length: > 0 } title ? title : name,
                CreatedAt = node["createdAt"]?.GetValue<DateTimeOffset>() ?? Directory.GetCreationTimeUtc(directory),
                Topic = node["topic"]?.GetValue<string>(),
                Worldview = node["worldview"]?.GetValue<string>(),
                Situation = node["situation"]?.GetValue<string>(),
                UserRole = node["userRole"]?.GetValue<string>(),
                Cast = node["cast"] is JsonArray cast ? cast.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray() : [],
                NarratorId = node["narratorId"]?.GetValue<string>(),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or IOException)
        {
            // 坏掉的元信息不该让整个列表挂掉：当作"没有元信息"，按目录名兜底。
            return null;
        }
    }

    private static SessionDescriptor Fallback(string directory, WorkspaceKind kind)
    {
        var name = Path.GetFileName(directory);

        return new SessionDescriptor
        {
            Id = name,
            Kind = kind,
            Title = name,
            CreatedAt = Directory.GetCreationTimeUtc(directory),
        };
    }
}
