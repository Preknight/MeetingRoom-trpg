using MeetingRoom.Core.Events;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 叙事会话接线：创建即由导演写开场 → 导演指令（元指令分流）
/// → 在场角色时序接话 → 提交进剧情时间线。
/// <para>全部用假客户端驱动，结果确定；不依赖真实 provider。</para>
/// </summary>
public sealed class RoleplaySessionTests
{
    [Fact]
    public async Task CreatingTheStoryWritesTheOpening()
    {
        await using var session = NewSession(FakeChatClient.Text(2, TimeSpan.Zero));

        // 与会议对称：给出设定即可开写，没有"候选 → 选定"这一步（§11.4-8）。
        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场写进剧情时间线");

        var turn = Assert.Single(session.Timeline.Turns);
        Assert.Equal(string.Empty, turn.DirectorInput);
        Assert.Equal("director", Assert.Single(turn.Lines).Author);
    }

    [Fact]
    public async Task PlayerInputAdvancesTimelineAndKeepsOocOutOfStory()
    {
        var bus = new EventBus();
        await using var session = NewSession(FakeChatClient.Text(2, TimeSpan.Zero), bus);

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我把外套盖在他身上。（让艾拉也进灯塔）");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var turn = session.Timeline.Turns[^1];

        // 括号里的元指令不进剧情正文，也不该被角色当成台词（§11.4-9）。
        Assert.Equal("我把外套盖在他身上。", turn.DirectorInput);
        Assert.DoesNotContain("让艾拉也进灯塔", turn.DirectorInput);

        var published = bus.Recent(200).Select(ev => ev.Type).ToArray();
        Assert.Contains("roleplay.started", published);
        Assert.Contains("roleplay.ooc", published);
        Assert.Contains("roleplay.turn", published);
    }

    [Fact]
    public async Task ActorsSpeakInSequenceWithoutSteppingOnTheDirector()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我点亮了灯。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var lines = session.Timeline.Turns[^1].Lines;

        // 这一幕末尾还有说书人的收束描写（每轮复盘，§11.4-5），先把它摘出去看角色接话。
        var actors = lines.Where(line => line.Author != "director").ToArray();

        Assert.NotEmpty(actors);
        // 默认上限 = 在场角色数（本夹具里是 ella、ran 两位），所以两位都接话、也不会多出第三个。
        Assert.Equal(2, actors.Length);
        Assert.Equal(actors.Length, actors.Select(line => line.Author).Distinct().Count());
        Assert.Contains(lines, line => line.Author == "director");
    }

    [Fact]
    public async Task TimelineIsPersistedAndReloadableFromJsonl()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), storyPath: path);

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        Assert.True(File.Exists(path), "剧情时间线应当落盘");

        // 同一份 JSONL 重新打开：revision 与回合都按行重建（§11.4-2）。
        var reloaded = new StoryTimeline(path);
        Assert.Equal(session.Revision, reloaded.Revision);
        Assert.Equal(session.Timeline.Turns.Count, reloaded.Turns.Count);
        Assert.Equal("我推门进去。", reloaded.Turns[^1].DirectorInput);
    }

    [Fact]
    public async Task PausedSessionDoesNotAdvanceUntilResumed()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.PauseAsync();
        Assert.Equal(MeetingStatus.Paused, session.Status);

        var say = session.SayAsync("我坐下来。");
        await Task.Delay(150);
        Assert.False(say.IsCompleted, "暂停期间不应当推进剧情");

        await session.ResumeAsync();
        await say;
        await WaitForAsync(() => session.Revision >= 2, "恢复后继续推进");
    }

    private static RoleplaySession NewSession(
        IChatClient client,
        IEventBus? bus = null,
        string? storyPath = null,
        IReadOnlyList<string>? cast = null)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "director", Name = "导演", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" },
            new RoleCard { Id = "ran", Name = "冉", Body = "路过的水手。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                Cast = cast ?? [],
                StoryPath = storyPath,
            },
            client,
            bus ?? new EventBus(),
            cards);
    }

    /// <summary>后台推进（开场 / 接话）没有可 await 的句柄，只能等状态到位；超时即判定失败。</summary>
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"等待「{what}」超时（10 秒）。");
            }

            await Task.Delay(10);
        }
    }
}
