using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Workspaces;

/// <summary>
/// 已注册工作空间的清单（落 <c>appdata/workspaces.json</c>）与"当前打开的是哪一个"（设计方案 §15.7 / §15.8）。
/// <para>
/// 它只管**目录与清单**：不碰会话、不碰资料、不碰角色卡。这样 W1 能独立落地——
/// "打开工作空间"先只改这里的 <see cref="Current"/>，把会话与角色卡切到工作空间根是 W2 的事（§15.10）。
/// </para>
/// </summary>
public sealed class WorkspaceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        // 类型写成 "meeting" / "roleplay"，与工作空间自己的 workspace.json 保持同一种写法。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _registryFile;
    private readonly Lock _gate = new();
    private readonly List<WorkspaceDescriptor> _workspaces = [];

    private string? _currentPath;

    public WorkspaceRegistry(string registryFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryFile);

        _registryFile = registryFile;
        Load();
    }

    /// <summary>注册表文件的位置（诊断用）。</summary>
    public string RegistryFile => _registryFile;

    /// <summary>当前打开的工作空间；没有打开任何工作空间时为 <c>null</c>。</summary>
    public WorkspaceDescriptor? Current
    {
        get
        {
            lock (_gate)
            {
                return _currentPath is null ? null : FindLocked(_currentPath);
            }
        }
    }

    public IReadOnlyList<WorkspaceDescriptor> List()
    {
        lock (_gate)
        {
            return _workspaces.ToArray();
        }
    }

    /// <summary>
    /// 新建一个工作空间：建目录骨架 + 写 <c>workspace.json</c> + 注册并设为当前。
    /// <para>
    /// <b>已经是工作空间的目录（有 <c>workspace.json</c>）直接打开、不覆盖</b>——
    /// 用户的资料比"我想新建"这个意图重要（设计方案 §15.4）。
    /// </para>
    /// </summary>
    public WorkspaceDescriptor Create(string path, WorkspaceKind kind, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var layout = new WorkspaceLayout(path);

        if (layout.Exists)
        {
            return Open(layout.Root, kind);
        }

        var descriptor = new WorkspaceDescriptor(
            layout.Root,
            kind,
            string.IsNullOrWhiteSpace(name) ? DefaultName(layout.Root) : name.Trim(),
            DateTimeOffset.UtcNow);

        layout.EnsureCreated();
        WriteDescriptor(descriptor);
        Register(descriptor);

        return descriptor;
    }

    /// <summary>
    /// 打开一个工作空间：已注册的直接切；没注册过的按目录里的 <c>workspace.json</c> 认，
    /// 认不出来就按 <paramref name="fallbackKind"/> 登记（并把骨架补齐）。
    /// </summary>
    public WorkspaceDescriptor Open(string path, WorkspaceKind? fallbackKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var layout = new WorkspaceLayout(path);

        lock (_gate)
        {
            if (FindLocked(layout.Root) is { } existing)
            {
                SetCurrentLocked(existing);
                return existing;
            }
        }

        // 目录里的元信息说了算；没有就按调用方给的兜底类型建一个"外来的"工作空间。
        var descriptor = ReadDescriptor(layout) ?? new WorkspaceDescriptor(
            layout.Root,
            fallbackKind ?? WorkspaceKind.Roleplay,
            DefaultName(layout.Root),
            DateTimeOffset.UtcNow);

        layout.EnsureCreated();
        Register(descriptor);

        return descriptor;
    }

    /// <summary>从清单里移除。<b>只移除登记，不删磁盘上的任何东西。</b></summary>
    public bool Forget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = new WorkspaceLayout(path).Root;

        lock (_gate)
        {
            var index = _workspaces.FindIndex(item => SamePath(item.Path, full));

            if (index < 0)
            {
                return false;
            }

            _workspaces.RemoveAt(index);

            if (_currentPath is not null && SamePath(_currentPath, full))
            {
                _currentPath = null;
            }

            SaveLocked();
            return true;
        }
    }

    /// <summary>路径比较：Windows 下大小写不敏感，且统一去掉结尾分隔符。</summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string DefaultName(string root)
    {
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }

    private WorkspaceDescriptor? FindLocked(string path) =>
        _workspaces.FirstOrDefault(item => SamePath(item.Path, path));

    private void Register(WorkspaceDescriptor descriptor)
    {
        lock (_gate)
        {
            var index = _workspaces.FindIndex(item => SamePath(item.Path, descriptor.Path));

            if (index >= 0)
            {
                _workspaces[index] = descriptor;
            }
            else
            {
                _workspaces.Add(descriptor);
            }

            SetCurrentLocked(descriptor);
        }
    }

    private void SetCurrentLocked(WorkspaceDescriptor descriptor)
    {
        _currentPath = descriptor.Path;
        SaveLocked();
    }

    private void WriteDescriptor(WorkspaceDescriptor descriptor)
    {
        File.WriteAllText(
            descriptor.Layout.DescriptorFile,
            JsonSerializer.Serialize(
                new
                {
                    name = descriptor.Name,
                    kind = descriptor.KindWire,
                    createdAt = descriptor.CreatedAt,
                },
                JsonOptions));
    }

    private static WorkspaceDescriptor? ReadDescriptor(WorkspaceLayout layout)
    {
        if (!layout.Exists)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(layout.DescriptorFile))?.AsObject();

            if (node is null)
            {
                return null;
            }

            return new WorkspaceDescriptor(
                layout.Root,
                // 旧清单没有 kind 字段：按 Legacy 处理（同时含 meetings/stories），不要套用新建时的默认类型。
                WorkspaceDescriptor.ParseKind(node["kind"]?.GetValue<string>(), WorkspaceKind.Legacy),
                node["name"]?.GetValue<string>() is { Length: > 0 } name ? name : DefaultName(layout.Root),
                node["createdAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or IOException)
        {
            // 坏掉的元信息不该让程序起不来：当作"没有"处理。
            return null;
        }
    }

    private void Load()
    {
        if (!File.Exists(_registryFile))
        {
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(_registryFile), JsonOptions);

            if (data is null)
            {
                return;
            }

            foreach (var descriptor in data.Workspaces ?? [])
            {
                if (!string.IsNullOrWhiteSpace(descriptor.Path))
                {
                    _workspaces.Add(descriptor);
                }
            }

            _currentPath = data.Current;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 注册表坏掉不致命：当空清单跑，用户重新打开一次就恢复了。
        }
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(_registryFile);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _registryFile,
            JsonSerializer.Serialize(new RegistryDocument(_currentPath, _workspaces.ToArray()), JsonOptions));
    }

    private sealed record RegistryDocument(string? Current, IReadOnlyList<WorkspaceDescriptor>? Workspaces);
}
