using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Workspaces;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Workspaces;

/// <summary>
/// 会话装配（设计方案 §15.10 的 W2 验收）：<b>一个工作空间下两场会话，资料互不干扰，而公共角色卡共享</b>。
/// </summary>
public sealed class WorkspaceSessionFactoryTests
{
    [Fact]
    public async Task TwoSessionsInOneWorkspaceKeepTheirOwnMaterials()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new SessionStore(layout);
        var factory = NewFactory();

        var first = await factory.CreateMeetingAsync(layout, store.Create(WorkspaceKind.Meeting, title: "第一场", topic: "第一场"));
        var second = await factory.CreateMeetingAsync(layout, store.Create(WorkspaceKind.Meeting, title: "第二场", topic: "第二场"));

        await first.Session.Materials.SubmitAsync(new SubmitRequest
        {
            Author = "销售经理",
            Title = "只有第一场有",
            Summary = "只提交给第一场",
            Body = "正文",
        });

        await first.DisposeAsync();
        await second.DisposeAsync();

        // 两场的资料各有各的目录与版本：这是"每场一个会话目录"的直接后果。
        Assert.Single(first.Session.Materials.List());
        Assert.Empty(second.Session.Materials.List());
        Assert.NotEqual(first.Transcript.FilePath, second.Transcript.FilePath);
    }

    [Fact]
    public async Task SessionsShareTheWorkspaceRoleCards()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new SessionStore(layout);
        var factory = NewFactory();

        // roles/ 为空时写入内置默认卡（开箱可用），之后两场会话都用这一份。
        var cards = factory.LoadSharedRoles(layout);

        Assert.NotEmpty(cards);
        Assert.True(File.Exists(Path.Combine(layout.RolesDir, "narrator.md")), "要写入默认说书人卡");

        var first = await factory.CreateMeetingAsync(layout, store.Create(WorkspaceKind.Meeting, title: "第一场"));
        var second = await factory.CreateMeetingAsync(layout, store.Create(WorkspaceKind.Meeting, title: "第二场"));

        await first.DisposeAsync();
        await second.DisposeAsync();

        Assert.Equal(cards.Count, first.Session.Participants.Count);
        Assert.Equal(cards.Count, second.Session.Participants.Count);
    }

    [Fact]
    public async Task RoleplaySessionsLiveUnderStories()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new SessionStore(layout);
        var factory = NewFactory();

        var descriptor = store.Create(
            WorkspaceKind.Roleplay,
            id: "海港城",
            worldview: "雨夜，海港城的雾还没散",
            situation: "码头尽头的灯灭了",
            narratorId: "narrator");

        var session = factory.CreateRoleplay(layout, descriptor);

        try
        {
            Assert.Equal("海港城", session.StoryId);
            Assert.Equal(0, session.Revision);
            Assert.True(Directory.Exists(Path.Combine(layout.StoriesDir, "海港城")), "剧情落在工作空间的 stories/<id>/ 下");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task AMeetingSessionRecoversItsOwnTranscript()
    {
        using var temp = new TempDir();
        var layout = NewLayout(temp);
        var store = new SessionStore(layout);
        var factory = NewFactory();

        // 先建出这一场（id 才定下来），再往它的目录里写一条历史发言——
        // 顺序反过来会先占住目录名，Create 就会顺延成 "第一场-2"。
        var descriptor = store.Create(WorkspaceKind.Meeting, id: "第一场", topic: "第一场");
        var transcriptPath = Path.Combine(store.SessionDirectory(WorkspaceKind.Meeting, descriptor.Id), "transcript.jsonl");
        File.WriteAllText(
            transcriptPath,
            """
            {"seq":1,"kind":"speech","author":"销售经理","text":"上一轮说过的话","turnId":"turn-1","at":"2026-09-23T10:00:00+08:00"}
            """);

        var session = await factory.CreateMeetingAsync(layout, descriptor);

        try
        {
            // 重开一场会议时历史发言要回来（recall_speech 的检索源），而且只回这一场的。
            Assert.Equal(1, session.RecoveredSpeeches);
            Assert.Equal(transcriptPath, session.Transcript.FilePath);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static WorkspaceLayout NewLayout(TempDir temp)
    {
        var layout = new WorkspaceLayout(Path.Combine(temp.Root, "海港城"));
        layout.EnsureCreated();

        return layout;
    }

    private static WorkspaceSessionFactory NewFactory() =>
        new(FakeChatClient.Text(1, TimeSpan.Zero), new EventBus(), new MeetingRoomOptions());
}
