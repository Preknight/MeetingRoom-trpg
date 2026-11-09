using MeetingRoom.Core.Workspaces;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Workspaces;

/// <summary>
/// 导入（设计方案 §15.5 的「导入」）：把**工作空间以外**的文件 / 目录复制进来，原文件不动；
/// 工作空间内部的来源拒绝、同名不覆盖、失败一律带原因。
/// </summary>
public sealed class WorkspaceImporterTests
{
    [Fact]
    public void ImportCopiesWithoutTouchingTheOriginal()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var outside = WriteFile(temp, Path.Combine("外面", "侦探.md"), "# 侦探");

        var result = new WorkspaceImporter(layout).Import([outside], WorkspaceTarget.Roles);

        var copied = Assert.Single(result.Copied);
        Assert.Empty(result.Skipped);

        Assert.StartsWith(layout.RolesDir, copied.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# 侦探", File.ReadAllText(copied.Target));

        // 导入 = 复制：原文件一个字节都不动。
        Assert.True(File.Exists(outside));
        Assert.Equal("# 侦探", File.ReadAllText(outside));
    }

    [Fact]
    public void ImportNeverOverwritesAnExistingFile()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var existing = WriteFile(temp, Path.Combine("海港城", "roles", "侦探.md"), "工作空间里的原件");
        var outside = WriteFile(temp, Path.Combine("外面", "侦探.md"), "外面来的");

        var result = new WorkspaceImporter(layout).Import([outside], WorkspaceTarget.Roles);

        Assert.Equal("工作空间里的原件", File.ReadAllText(existing));
        Assert.EndsWith("侦探-2.md", Assert.Single(result.Copied).Target, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcesInsideTheWorkspaceAreRefused()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var inside = WriteFile(temp, Path.Combine("海港城", "drafts", "草稿.md"), "在待整理区里");

        var result = new WorkspaceImporter(layout).Import([inside], WorkspaceTarget.Materials);

        // 自己复制自己没意义，而且会把工作空间的内容再写一遍。
        Assert.Empty(result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("已经在工作空间里", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryIsCopiedRecursively()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var source = Path.Combine(temp.Root, "外面", "一套角色");
        Directory.CreateDirectory(Path.Combine(source, "子目录"));
        File.WriteAllText(Path.Combine(source, "侦探.md"), "侦探");
        File.WriteAllText(Path.Combine(source, "子目录", "法医.md"), "法医");

        var result = new WorkspaceImporter(layout).Import([source], WorkspaceTarget.Roles);

        Assert.Equal(2, result.Copied.Count);
        Assert.True(File.Exists(Path.Combine(layout.RolesDir, "一套角色", "侦探.md")));
        Assert.True(File.Exists(Path.Combine(layout.RolesDir, "一套角色", "子目录", "法医.md")));
    }

    [Fact]
    public void TheFourTargetsAreTheWorkspaceOwnDirectories()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var source = WriteFile(temp, Path.Combine("外面", "资料.md"), "资料");

        foreach (var (target, expected) in new[]
                 {
                     (WorkspaceTarget.Roles, layout.RolesDir),
                     (WorkspaceTarget.Materials, layout.MaterialsDir),
                     (WorkspaceTarget.WorldBook, layout.WorldBookDir),
                     (WorkspaceTarget.Drafts, layout.DraftsDir),
                 })
        {
            var result = new WorkspaceImporter(layout).Import([source], target);

            Assert.StartsWith(expected, Assert.Single(result.Copied).Target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MissingAndEmptySourcesAreReported()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);

        var result = new WorkspaceImporter(layout).Import(
            ["", Path.Combine(temp.Root, "不存在.md")],
            WorkspaceTarget.Drafts);

        // 静默丢弃比失败更糟：跳过必须带原因。
        Assert.Empty(result.Copied);
        Assert.Equal(2, result.Skipped.Count);
    }

    private static WorkspaceLayout NewLayout(TempDir temp)
    {
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();

        return layout;
    }

    private static string WriteFile(TempDir temp, string relativePath, string content)
    {
        var path = Path.Combine(temp.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }
}
