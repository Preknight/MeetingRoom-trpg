using MeetingRoom.Core.Workspaces;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Workspaces;

/// <summary>
/// 会话目录与元信息（设计方案 §15.2 / §15.8 的 W2）：会议进 <c>meetings/</c>、叙事进 <c>stories/</c>，
/// 每场一份 <c>session.json</c>；撞名顺延、坏元信息兜底、id 不能带路径分隔符。
/// </summary>
public sealed class SessionStoreTests
{
    [Fact]
    public void SessionsLiveUnderTheWorkspace()
    {
        using var temp = new TempDir();
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();
        var store = new SessionStore(layout);

        var meeting = store.Create(WorkspaceKind.Meeting, title: "定价评审", topic: "定价评审");
        var story = store.Create(WorkspaceKind.Roleplay, worldview: "雨夜的海港城", situation: "码头的灯灭了");

        Assert.Equal(Path.Combine(layout.MeetingsDir, meeting.Id), store.SessionDirectory(WorkspaceKind.Meeting, meeting.Id));
        Assert.Equal(Path.Combine(layout.StoriesDir, story.Id), store.SessionDirectory(WorkspaceKind.Roleplay, story.Id));
        Assert.True(
            File.Exists(Path.Combine(store.SessionDirectory(WorkspaceKind.Meeting, meeting.Id), "session.json")),
            "每场会话要落一份 session.json");

        Assert.Equal("定价评审", meeting.Title);
        Assert.Equal(WorkspaceKind.Roleplay, story.Kind);
        Assert.Single(store.List(WorkspaceKind.Meeting));
        Assert.Single(store.List(WorkspaceKind.Roleplay));
    }

    [Fact]
    public void RepeatingATitleGetsItsOwnDirectory()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var first = store.Create(WorkspaceKind.Meeting, title: "每周例会");
        var second = store.Create(WorkspaceKind.Meeting, title: "每周例会");

        // 绝不覆盖已有会话：撞名就顺延。
        Assert.Equal("每周例会", first.Id);
        Assert.Equal("每周例会-2", second.Id);
        Assert.Equal(2, store.List(WorkspaceKind.Meeting).Count);
    }

    [Fact]
    public void TheDescriptorRoundTrips()
    {
        using var temp = new TempDir();
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();

        new SessionStore(layout).Create(
            WorkspaceKind.Roleplay,
            id: "海港城",
            worldview: "雨夜，海港城的雾还没散",
            situation: "码头尽头的灯灭了",
            userRole: "警探",
            cast: ["侦探", "法医"],
            narratorId: "说书人");

        // 重开一场会话要能复原设定（所以元信息必须落盘）。
        var reloaded = new SessionStore(layout).Find(WorkspaceKind.Roleplay, "海港城");

        Assert.NotNull(reloaded);
        Assert.Equal(WorkspaceKind.Roleplay, reloaded!.Kind);
        Assert.Equal("雨夜，海港城的雾还没散", reloaded.Worldview);
        Assert.Equal("码头尽头的灯灭了", reloaded.Situation);
        Assert.Equal("警探", reloaded.UserRole);
        Assert.Equal(["侦探", "法医"], reloaded.Cast);
        Assert.Equal("说书人", reloaded.NarratorId);
    }

    [Fact]
    public void ABrokenDescriptorFallsBackToTheDirectoryName()
    {
        using var temp = new TempDir();
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();

        var directory = Path.Combine(layout.MeetingsDir, "旧的会议");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "session.json"), "{ 这不是 JSON");

        // 坏掉的元信息不该让整个列表挂掉：按目录名兜底，仍能打开它。
        var listed = Assert.Single(new SessionStore(layout).List(WorkspaceKind.Meeting));

        Assert.Equal("旧的会议", listed.Id);
        Assert.Equal("旧的会议", listed.Title);
    }

    [Fact]
    public void PathSeparatorsNeverReachTheDirectoryName()
    {
        // id 直接当目录名用：净化不严就能把会话目录写到工作空间外面去。
        Assert.Equal("a-b", SessionIds.Clean("a/b"));
        Assert.Equal("a-b", SessionIds.Clean(@"a\b"));
        Assert.DoesNotContain("..", SessionIds.Clean("../../etc/passwd"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, SessionIds.Clean(".."));
        Assert.Equal(string.Empty, SessionIds.Clean("   "));
        Assert.StartsWith("session-", SessionIds.FromTitle(null), StringComparison.Ordinal);
    }

    private static SessionStore NewStore(TempDir temp)
    {
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();

        return new SessionStore(layout);
    }
}
