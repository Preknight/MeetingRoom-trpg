using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Vtt;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 地图与骰子的会话级接线（《枭熊地图接入实现方案》V1 / V2）：
/// <b>桥挂了不能中断回合</b>、地图段落按角色视角注入、掷骰结果落 <c>story.jsonl</c> 的 <c>kind=roll</c>。
/// </summary>
public sealed class RoleplayMapAndDiceTests
{
    /// <summary>150 dpi、5 尺/格：横向 3 格 = 15 尺，15 格 = 75 尺。</summary>
    private static readonly VttGrid Grid = new(150, "SQUARE", VttMeasurement.Chessboard, 5, "ft");

    [Fact]
    public async Task ABrokenBridgeDoesNotStopTheTurn()
    {
        var vtt = new FakeVttClient(() => throw new HttpRequestException("由于目标计算机积极拒绝，无法连接。"));
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt);

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        // 回合照常推进，只是没有地图段落——降级是静默的，不进正文。
        Assert.Equal(2, session.Revision);
        Assert.Empty(session.MapSectionFor("ella"));
        Assert.Empty(session.MapSectionFor("director"));

        var status = session.VttStatus();
        Assert.NotNull(status);
    }

    [Fact]
    public async Task EachParticipantGetsTheirOwnSliceOfTheMap()
    {
        var vtt = new FakeVttClient(NewSnapshot);
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt);

        await session.RefreshVttAsync();

        var ella = session.MapSectionFor("ella");

        // 角色：以自己为锚点，登记过的给名字，没登记的只给泛称，隐藏项一个字都不漏。
        Assert.Contains("冉：15 尺", ella, StringComparison.Ordinal);
        Assert.Contains(VttContext.UnregisteredTarget, ella, StringComparison.Ordinal);
        Assert.DoesNotContain("地精", ella, StringComparison.Ordinal);
        Assert.DoesNotContain("潜伏者", ella, StringComparison.Ordinal);

        // 说书人：全量（未登记也标出来）、当前选中与标尺。
        var narrator = session.MapSectionFor("director");
        Assert.Contains("地精", narrator, StringComparison.Ordinal);
        Assert.Contains("当前选中：t1", narrator, StringComparison.Ordinal);
        Assert.Contains("标尺读数：20 ft", narrator, StringComparison.Ordinal);
        Assert.DoesNotContain("潜伏者", narrator, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCardTokenIdDrivesTheAnchorEndToEnd()
    {
        // 角色卡上写了 vttTokenId：即使图上有两个同名「艾拉」，也用卡上指的那个做锚点。
        var snapshot = NewSnapshot() with
        {
            Tokens =
            [
                new VttToken("obr-ella", "艾拉", 150, 150, true),
                new VttToken("t2", "冉", 600, 150, true),
                new VttToken("decoy", "艾拉", 1500, 150, true),
            ],
            Rulers = [],
        };

        var vtt = new FakeVttClient(() => snapshot);
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt, ellaTokenId: "obr-ella");

        await session.RefreshVttAsync();

        var text = session.MapSectionFor("ella");
        Assert.Contains("冉：15 尺", text, StringComparison.Ordinal);
        Assert.DoesNotContain("冉：30 尺", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRoundPullsTheSnapshotOnce()
    {
        var vtt = new FakeVttClient(NewSnapshot);
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt);

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");
        var afterOpening = vtt.Calls;

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        Assert.True(afterOpening >= 1, "开场也该看见地图");
        Assert.True(vtt.Calls > afterOpening, "每轮开头都要刷新一次地图");
    }

    [Fact]
    public async Task TheMapSectionStaysOutOfTheSharedPrefix()
    {
        var vtt = new FakeVttClient(NewSnapshot);
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt);

        await session.RefreshVttAsync();

        // 共享节必须字节稳定（硬约束 6）：地图逐轮变化，只能进角色节。
        Assert.DoesNotContain("[地图]", session.BuildSharedSection(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookAroundReadsDistancesFromTheSnapshot()
    {
        var vtt = new FakeVttClient(NewSnapshot);
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt);

        await session.RefreshVttAsync();

        var outcome = await session.LookAroundAsync("ella", radiusFeet: 90, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Contains("冉：15 尺", outcome.Message, StringComparison.Ordinal);
        // 说书人视角：未登记的也给名字，否则它没法把"那个怪"写进正文。
        Assert.Contains("地精", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookAroundExplainsInsteadOfGuessingWhenThereIsNoMap()
    {
        var vtt = new FakeVttClient(() => VttSnapshot.Unavailable("桥没启动"));
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), vtt);

        await session.RefreshVttAsync();

        var outcome = await session.LookAroundAsync("ella", null, CancellationToken.None);

        Assert.False(outcome.Applied);
        Assert.Contains("地图不可用", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("不要自己估算", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollsLandInTheStoryLogAsKindRoll()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), storyPath: path);

        var revisionBefore = session.Revision;
        var outcome = await session.RollDiceAsync("ella", "3x(1d8+2)", "normal", "三连击", null, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Contains("掷骰 3x(1d8+2)", outcome.Message, StringComparison.Ordinal);

        // 掷骰不改剧情时间线（它是桌面事实，不是叙述）。
        Assert.Equal(revisionBefore, session.Revision);

        var record = Assert.Single(session.Timeline.RollRecords);
        Assert.Equal(3, record.Attacks.Count);
        Assert.Equal(record.Attacks.Sum(attack => attack.Total), record.Total);

        Assert.True(File.Exists(path), "story.jsonl 应当落盘");
        Assert.Contains("\"kind\":\"roll\"", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        // 重开一场：掷骰记录按行恢复，且同样的明细能复算出同样的 total。
        var reloaded = new StoryTimeline(path);
        var replayed = Assert.Single(reloaded.RollRecords);
        Assert.Equal(record.Total, replayed.Total);
        Assert.Equal(
            replayed.Attacks.Sum(attack => attack.Kept.Sum() + attack.Modifier),
            replayed.Total);
    }

    [Fact]
    public async Task ABadExpressionIsFedBackWithoutWritingAnything()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), storyPath: path);

        var outcome = await session.RollDiceAsync("ella", "扔个骰子", "normal", "试试", null, CancellationToken.None);

        Assert.False(outcome.Applied);
        Assert.Contains("看不懂这个表达式", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(session.Timeline.RollRecords);
        Assert.False(File.Exists(path), "失败的掷骰不该落盘");
    }

    [Fact]
    public async Task TheDiceToolIsOnlyForTheNarrator()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        var dispatcher = new NarratorToolDispatcher(session, id => string.Equals(id, "director", StringComparison.Ordinal), session);

        var result = await dispatcher.InvokeAsync(
            "ella",
            new FunctionCallContent("call-1", Narrator.DiceRoll, new Dictionary<string, object?>
            {
                ["who"] = "ella",
                ["expression"] = "1d20+5",
            }),
            CancellationToken.None);

        Assert.Contains("只有说书人", result.Result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(session.Timeline.RollRecords);
    }

    private static RoleplaySession NewSession(
        IChatClient client,
        IVttClient? vtt = null,
        string? storyPath = null,
        string? ellaTokenId = null)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "director", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", VttTokenId = ellaTokenId ?? string.Empty, Body = "灯塔看守人的女儿。" },
            new RoleCard { Id = "ran", Name = "冉", Body = "路过的水手。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                NarratorId = "director",
                StoryPath = storyPath,
                Vtt = new VttOptions { Enabled = vtt is not null, InjectRadiusFeet = 60 },
            },
            client,
            new EventBus(),
            cards,
            vtt);
    }

    /// <summary>
    /// 三个 token：艾拉（已登记）、冉（已登记、15 尺）、地精（未登记、30 尺）、潜伏者（隐藏）。
    /// 另有一条 20 ft 的标尺。
    /// </summary>
    private static VttSnapshot NewSnapshot() => new()
    {
        Available = true,
        Schema = "obr-bridge-state/1",
        ReceivedAt = 1793003931203,
        SceneReady = true,
        Grid = Grid,
        Tokens =
        [
            new VttToken("t1", "艾拉", 150, 150, true),
            new VttToken("t2", "冉", 600, 150, true),
            new VttToken("t3", "地精", 1050, 150, true),
            new VttToken("t5", "潜伏者", 300, 300, false),
        ],
        Selected = ["t1"],
        Rulers = [new VttRuler("r1", "20 ft", new VttPoint(150, 150), new VttPoint(450, 300))],
    };

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

    private sealed class FakeVttClient : IVttClient
    {
        private readonly Func<VttSnapshot> _next;

        public FakeVttClient(Func<VttSnapshot> next) => _next = next;

        public int Calls { get; private set; }

        public VttSnapshot? Last { get; private set; }

        public Task<VttSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = _next();
            return Task.FromResult(Last);
        }
    }
}
