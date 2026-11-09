using MeetingRoom.Core.Drafts;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Workspaces;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Drafts;

/// <summary>
/// 待整理区与归档区（设计方案 §15.6）：<c>drafts/&lt;id&gt;/</c> 一条，**归档 = 内容文件移进归档区**、
/// 撤销归档 = 移回来；撞名顺延、id 不许跑出工作空间。
/// </summary>
public sealed class DraftStoreTests
{
    [Fact]
    public void HandwrittenItemsLandInDrafts()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);

        var entry = store.Create(DraftKind.Material, "定价草案", id: null, body: "# 定价草案\n\n按席位分层。");

        Assert.Equal("定价草案", entry.Id);
        Assert.Equal("定价草案", entry.Title);
        Assert.Equal("# 定价草案\n\n按席位分层。", store.ReadBody(entry.Id));
        Assert.True(File.Exists(Path.Combine(layout.DraftsDir, entry.Id, "entry.json")));
        Assert.Single(store.List());
    }

    [Fact]
    public void ArchivingMovesTheContentIntoItsArea()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);
        var entry = store.Create(DraftKind.Role, "侦探", id: null, body: "# 侦探");

        var archived = store.Archive(entry.Id);

        Assert.NotNull(archived);
        Assert.Equal(DraftKind.Role, archived!.Kind);
        Assert.Equal(Path.Combine(layout.RolesDir, "侦探.md"), archived.Path);
        Assert.True(File.Exists(archived.Path));
        Assert.Equal("# 侦探", File.ReadAllText(archived.Path));

        // 归档是"移"，不是"复制"：待整理区那份目录没了。
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(Path.Combine(layout.DraftsDir, "侦探")));

        var item = Assert.Single(store.ListArchived(DraftKind.Role));
        Assert.Equal("侦探", item.Id);
    }

    [Fact]
    public void UnarchivingMovesItBack()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);
        var entry = store.Create(DraftKind.WorldBook, "灯塔", id: null, body: "灯塔在夜里亮着。");
        var archived = store.Archive(entry.Id)!;

        var back = store.Unarchive(archived.Kind, archived.Path);

        Assert.NotNull(back);
        Assert.Equal("灯塔", back!.Id);
        Assert.Equal("灯塔在夜里亮着。", store.ReadBody(back.Id));
        Assert.Empty(store.ListArchived(DraftKind.WorldBook));
        Assert.Single(store.List());
    }

    [Fact]
    public void ArchivingNeverOverwritesAnExistingFile()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);
        var entry = store.Create(DraftKind.Role, "侦探", id: null, body: "新的");

        // 归档区已经有一份同名的（例如导入进来的成品）。
        File.WriteAllText(Path.Combine(layout.RolesDir, "侦探.md"), "旧的");

        var archived = store.Archive(entry.Id)!;

        Assert.EndsWith("侦探-2.md", archived.Path, StringComparison.Ordinal);
        Assert.Equal("旧的", File.ReadAllText(Path.Combine(layout.RolesDir, "侦探.md")));
    }

    [Fact]
    public void DeleteRemovesTheWholeEntry()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);
        var entry = store.Create(DraftKind.Material, "草稿", id: null, body: "x");

        Assert.True(store.Delete(entry.Id));
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(Path.Combine(layout.DraftsDir, entry.Id)));
        Assert.False(store.Delete(entry.Id));
    }

    [Fact]
    public void IdsCannotEscapeTheWorkspace()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);

        var entry = store.Create(DraftKind.Material, "x", id: "../../跑出去", body: "x");

        Assert.DoesNotContain("..", entry.Id, StringComparison.Ordinal);
        Assert.StartsWith(layout.DraftsDir, store.EntryDirectory(entry.Id), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(store.EntryDirectory(entry.Id)));
    }

    [Fact]
    public void RepeatingATitleGetsItsOwnEntry()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);

        var first = store.Create(DraftKind.Material, "同名", id: null, body: "一");
        var second = store.Create(DraftKind.Material, "同名", id: null, body: "二");

        Assert.Equal("同名", first.Id);
        Assert.Equal("同名-2", second.Id);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void ArchivedRolesShowTheirCardName()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new DraftStore(layout);

        // 归档区里的角色卡：标题取卡里的 name（不是文件名）。
        File.WriteAllText(
            Path.Combine(layout.RolesDir, "detective.md"),
            RoleCardWriter.Render(new RoleCard
            {
                Id = "detective",
                Name = "侦探",
                Title = "查案",
                Summary = "老练",
                Body = "你是个侦探。",
            }));

        var item = Assert.Single(store.ListArchived(DraftKind.Role));

        Assert.Equal("detective", item.Id);
        Assert.Equal("侦探", item.Title);
    }

    [Fact]
    public void ABrokenEntryFallsBackToTheDirectoryName()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        Directory.CreateDirectory(Path.Combine(layout.DraftsDir, "老条目"));
        File.WriteAllText(Path.Combine(layout.DraftsDir, "老条目", "entry.json"), "{ 这不是 JSON");

        var entry = Assert.Single(new DraftStore(layout).List());

        Assert.Equal("老条目", entry.Id);
        Assert.Equal("老条目", entry.Title);
    }

    private static WorkspaceLayout NewLayout(TempDir temp)
    {
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();

        return layout;
    }
}
