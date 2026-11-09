namespace MeetingRoom.Core.Images;

/// <summary>图片路径越界（绝对路径、<c>..</c> 穿越、符号链接逃逸）。</summary>
public sealed class ImagePathException(string message) : Exception(message);

/// <summary>
/// 图片路径防护：清单里的路径必须落在 <c>images/</c> 内 —— 拒绝绝对路径、<c>..</c> 穿越与符号链接逃逸。
/// <para>
/// 与 <c>Core/Materials/MaterialPathGuard.cs</c> 是同一条思路，但**故意各留一份**：
/// 那个类是资料库的保密性落点（设计文档 §5.3 / §11.4-3），把它改 public 复用会动到既有语义。
/// </para>
/// </summary>
internal static class ImagePathGuard
{
    public static string ResolveWithin(string imagesDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ImagePathException("图片路径不能为空。");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ImagePathException($"图片路径必须是相对路径：{relativePath}");
        }

        var root = Path.GetFullPath(imagesDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsWithin(root, full))
        {
            throw new ImagePathException($"图片路径越出了 images 目录：{relativePath}");
        }

        // 符号链接：目标存在时解析到真实位置，真实位置仍须落在 images/ 内。
        var target = ResolveLinkTarget(full);
        if (target is not null && !IsWithin(root, target))
        {
            throw new ImagePathException($"图片路径经符号链接逃出了 images 目录：{relativePath} → {target}");
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
            // 解析不了就按"不是链接"处理：路径本身已在 images/ 内，安全性不受影响。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
