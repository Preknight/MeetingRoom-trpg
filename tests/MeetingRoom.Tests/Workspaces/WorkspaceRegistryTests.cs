using MeetingRoom.Core.Workspaces;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Workspaces;

/// <summary>
/// 工作空间注册表（设计方案 §15.7 / §15.8）：建 / 列 / 打开 / 忘掉，以及"重启后清单还在"。
/// 这一层只管目录与清单——不碰会话、不碰资料，所以能独立验（W1 的边界）。
/// </summary>
public sealed class WorkspaceRegistryTests
{
    [Fact]
    public void CreateBuildsTheSkeletonAndWritesTheDescriptor()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Root, "spaces", "海港城");

        var created = NewRegistry(temp).Create(path, WorkspaceKind.Roleplay, "海港城");

        Assert.Equal(WorkspaceKind.Roleplay, created.Kind);
        Assert.Equal("海港城", created.Name);
        Assert.True(File.Exists(created.Layout.DescriptorFile), "要写出 workspace.json");

        foreach (var directory in new[]
                 {
                     created.Layout.RolesDir,
                     created.Layout.MaterialsDir,
                     created.Layout.WorldBookDir,
                     created.Layout.DraftsDir,
                     created.Layout.StoriesDir,
                 })
        {
            Assert.True(Directory.Exists(directory), $"要建出 {directory}");
        }
    }

    [Fact]
    public void TwoWorkspacesCoexistAndTheLastOpenedWins()
    {
        using var temp = new TempDir();
        var registry = NewRegistry(temp);

        var meeting = registry.Create(Path.Combine(temp.Root, "spaces", "每周例会"), WorkspaceKind.Meeting);
        var story = registry.Create(Path.Combine(temp.Root, "spaces", "海港城"), WorkspaceKind.Roleplay);

        // 类型在创建时就定，两类各自独立（§15.2 的硬规则 1）。
        Assert.Equal(2, registry.List().Count);
        Assert.Equal(WorkspaceKind.Meeting, meeting.Kind);
        Assert.Equal(story.Path, registry.Current?.Path);

        registry.Open(meeting.Path);

        Assert.Equal(meeting.Path, registry.Current?.Path);
    }

    [Fact]
    public void TheRegistrySurvivesARestart()
    {
        using var temp = new TempDir();
        var registry = NewRegistry(temp);

        registry.Create(Path.Combine(temp.Root, "spaces", "每周例会"), WorkspaceKind.Meeting);
        var story = registry.Create(Path.Combine(temp.Root, "spaces", "海港城"), WorkspaceKind.Roleplay, "海港城");
        registry.Open(story.Path);

        // 重开一个注册表（同一份清单文件）：列表与"当前"都还在。
        var reopened = NewRegistry(temp);

        Assert.Equal(2, reopened.List().Count);
        Assert.Equal("海港城", reopened.Current?.Name);
        Assert.Equal(WorkspaceKind.Roleplay, reopened.Current?.Kind);
    }

    [Fact]
    public void OpeningTheSameDirectoryTwiceDoesNotDuplicate()
    {
        using var temp = new TempDir();
        var registry = NewRegistry(temp);
        var path = Path.Combine(temp.Root, "spaces", "海港城");

        registry.Create(path, WorkspaceKind.Roleplay);
        registry.Open(path);
        registry.Create(path, WorkspaceKind.Roleplay); // 对同一个目录再"新建" = 打开它

        Assert.Single(registry.List());
    }

    [Fact]
    public void CreatingOverAnExistingWorkspaceOpensItWithoutOverwriting()
    {
        using var temp = new TempDir();
        var registry = NewRegistry(temp);
        var path = Path.Combine(temp.Root, "spaces", "海港城");

        var first = registry.Create(path, WorkspaceKind.Roleplay, "原名");
        var again = registry.Create(path, WorkspaceKind.Roleplay, "想改成别的");

        // 用户的资料比"我想新建"这个意图重要：workspace.json 一个字节都不动。
        Assert.Equal(first.Name, again.Name);
        Assert.Equal("原名", again.Name);
    }

    [Fact]
    public void ABrokenRegistryFileIsTreatedAsEmpty()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Root, "appdata", "workspaces.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ 这不是 JSON");

        var registry = new WorkspaceRegistry(file);

        Assert.Empty(registry.List());
        Assert.Null(registry.Current);

        // 坏掉之后还要能正常用：重新建一个即可。
        registry.Create(Path.Combine(temp.Root, "spaces", "新的"), WorkspaceKind.Meeting);

        Assert.Single(registry.List());
    }

    [Fact]
    public void ForgetOnlyRemovesItFromTheList()
    {
        using var temp = new TempDir();
        var registry = NewRegistry(temp);
        var created = registry.Create(Path.Combine(temp.Root, "spaces", "海港城"), WorkspaceKind.Roleplay);

        Assert.True(registry.Forget(created.Path));

        Assert.Empty(registry.List());
        Assert.Null(registry.Current);

        // 只移除登记——磁盘上的东西一个都不动。
        Assert.True(Directory.Exists(created.Layout.RolesDir));
        Assert.True(File.Exists(created.Layout.DescriptorFile));
        Assert.False(registry.Forget(created.Path));
    }

    [Fact]
    public void AnUnregisteredDirectoryWithADescriptorIsAdopted()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Root, "spaces", "别人给的");
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "workspace.json"),
            """{ "name": "别人给的", "kind": "roleplay" }""");

        var adopted = NewRegistry(temp).Open(path);

        // 打开一个"外面来的"工作空间：按它自己的 workspace.json 认类型与名字。
        Assert.Equal(WorkspaceKind.Roleplay, adopted.Kind);
        Assert.Equal("别人给的", adopted.Name);
    }

    [Fact]
    public void TheRegistryFileStaysSmallAndReadable()
    {
        using var temp = new TempDir();
        NewRegistry(temp).Create(Path.Combine(temp.Root, "spaces", "海港城"), WorkspaceKind.Roleplay, "海港城");

        var text = File.ReadAllText(Path.Combine(temp.Root, "appdata", "workspaces.json"));

        // 目录布局与展示名都能从 path 与 kind 推导，落进清单只会让文件随工作空间数量膨胀（真机抓到的）。
        Assert.DoesNotContain("layout", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rolesDir", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kindWire", text, StringComparison.OrdinalIgnoreCase);

        // 类型写成字符串，与工作空间自己的 workspace.json 同一种写法。
        Assert.Contains("\"roleplay\"", text, StringComparison.Ordinal);
    }

    private static WorkspaceRegistry NewRegistry(TempDir temp) =>
        new(Path.Combine(temp.Root, "appdata", "workspaces.json"));
}
