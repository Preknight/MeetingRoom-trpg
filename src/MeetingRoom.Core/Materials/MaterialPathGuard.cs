namespace MeetingRoom.Core.Materials;

/// <summary>正文路径越界（绝对路径、<c>..</c> 穿越、符号链接逃逸）。</summary>
public sealed class MaterialPathException : Exception
{
    public MaterialPathException(string message) : base(message)
    {
    }
}

/// <summary>
/// 正文路径防护（实现步骤 §6.1）：提交的路径必须落在
/// <c>&lt;安装目录&gt;/data/meetings/&lt;id&gt;/materials/</c> 内——拒绝绝对路径、<c>..</c> 穿越与符号链接逃逸。
/// </summary>
internal static class MaterialPathGuard
{
    public static string ResolveWithin(string materialsDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MaterialPathException("正文路径不能为空。");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new MaterialPathException($"正文路径必须是相对路径：{relativePath}");
        }

        var root = Path.GetFullPath(materialsDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsWithin(root, full))
        {
            throw new MaterialPathException($"正文路径越出了资料目录：{relativePath}");
        }

        // 符号链接：目标存在时解析到真实位置，真实位置仍须落在资料目录内。
        var target = ResolveLinkTarget(full);
        if (target is not null && !IsWithin(root, target))
        {
            throw new MaterialPathException($"正文路径经符号链接逃出了资料目录：{relativePath} → {target}");
        }

        return full;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            }

            if (Directory.Exists(path))
            {
                return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            }
        }
        catch (IOException)
        {
            // 解析不了就按"不是链接"处理：路径本身已在资料目录内，安全性不受影响。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
