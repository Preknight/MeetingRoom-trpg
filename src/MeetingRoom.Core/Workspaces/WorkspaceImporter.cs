namespace MeetingRoom.Core.Workspaces;

/// <summary>导入的落点（设计方案 §15.7 / §15.6）：只能落在工作空间自己的四个区里。</summary>
public enum WorkspaceTarget
{
    /// <summary>角色卡（跨会话共享）。</summary>
    Roles,

    /// <summary>已归档资料（§15.6 的上半区）。</summary>
    Materials,

    /// <summary>世界书条目（叙事型）。</summary>
    WorldBook,

    /// <summary>待整理区（§15.6 的下半区）：引导页导入的东西先落这里。</summary>
    Drafts,
}

/// <summary>复制进来的一个文件：<paramref name="Source"/> 原文件不动，<paramref name="Target"/> 是副本。</summary>
public sealed record ImportedFile(string Source, string Target, long Bytes);

/// <summary>导入结果：复制了什么、跳过了什么（跳过一律带原因，不静默丢弃）。</summary>
public sealed record ImportResult(IReadOnlyList<ImportedFile> Copied, IReadOnlyList<string> Skipped);

/// <summary>
/// 把**工作空间以外**的文件 / 目录复制进来（设计方案 §15.5 的「导入」）。
/// <para>
/// 两条硬规则：① 原文件不动，只复制；② 落点只能是工作空间自己的区——
/// 工作空间内部的路径不接受（那等于自己复制自己），工作空间外的任何地方都不写。
/// </para>
/// </summary>
public sealed class WorkspaceImporter
{
    /// <summary>单次导入的文件数上限：防止误选 <c>C:\</c> 之类把界面卡死。</summary>
    private const int MaxFiles = 2000;

    private readonly WorkspaceLayout _layout;

    public WorkspaceImporter(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _layout = layout;
    }

    public ImportResult Import(IReadOnlyList<string> sources, WorkspaceTarget target)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var targetDirectory = TargetDirectory(target);
        Directory.CreateDirectory(targetDirectory);

        var copied = new List<ImportedFile>();
        var skipped = new List<string>();

        foreach (var source in sources)
        {
            if (copied.Count >= MaxFiles)
            {
                skipped.Add($"（本次最多导入 {MaxFiles} 个文件，剩下的没有复制）");
                break;
            }

            ImportOne(source, targetDirectory, copied, skipped);
        }

        return new ImportResult(copied, skipped);
    }

    private static string SafeName(string path, string fallback)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    /// <summary>目标名被占用时顺延（<c>a.md</c> → <c>a-2.md</c>）：导入绝不覆盖用户已有文件。</summary>
    private static string UniquePath(string directory, string name)
    {
        var candidate = name;

        for (var suffix = 2; Exists(Path.Combine(directory, candidate)) && suffix < 1000; suffix++)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            candidate = $"{stem}-{suffix}{extension}";
        }

        return Path.Combine(directory, candidate);

        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    }

    private string TargetDirectory(WorkspaceTarget target) => target switch
    {
        WorkspaceTarget.Roles => _layout.RolesDir,
        WorkspaceTarget.Materials => _layout.MaterialsDir,
        WorkspaceTarget.WorldBook => _layout.WorldBookDir,
        _ => _layout.DraftsDir,
    };

    private void ImportOne(string? source, string targetDirectory, List<ImportedFile> copied, List<string> skipped)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            skipped.Add("（空路径）");
            return;
        }

        string full;

        try
        {
            full = Path.GetFullPath(source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            skipped.Add($"{source}（路径不合法：{ex.Message}）");
            return;
        }

        if (IsInsideWorkspace(full))
        {
            skipped.Add($"{full}（已经在工作空间里——导入只收工作空间以外的文件）");
            return;
        }

        try
        {
            if (Directory.Exists(full))
            {
                CopyDirectory(full, targetDirectory, copied, skipped);
                return;
            }

            if (File.Exists(full))
            {
                CopyFile(full, targetDirectory, copied);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            skipped.Add($"{full}（复制失败：{ex.Message}）");
            return;
        }

        skipped.Add($"{full}（不存在）");
    }

    private static void CopyFile(string source, string targetDirectory, List<ImportedFile> copied)
    {
        var destination = UniquePath(targetDirectory, SafeName(source, "imported"));

        File.Copy(source, destination, overwrite: false);
        copied.Add(new ImportedFile(source, destination, new FileInfo(destination).Length));
    }

    private static void CopyDirectory(string source, string targetDirectory, List<ImportedFile> copied, List<string> skipped)
    {
        var root = UniquePath(targetDirectory, SafeName(source, "imported"));
        Directory.CreateDirectory(root);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (copied.Count >= MaxFiles)
            {
                skipped.Add($"（本次最多导入 {MaxFiles} 个文件，{source} 里剩下的没有复制）");
                return;
            }

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            copied.Add(new ImportedFile(file, destination, new FileInfo(destination).Length));
        }
    }

    private bool IsInsideWorkspace(string fullPath)
    {
        var root = _layout.Root;

        if (WorkspaceRegistry.SamePath(root, fullPath))
        {
            return true;
        }

        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
