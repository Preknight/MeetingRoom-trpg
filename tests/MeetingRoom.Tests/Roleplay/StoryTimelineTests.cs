using MeetingRoom.Core.Roleplay;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 剧情时间线（设计文档 §11.4-2）：revision 只增不减、落盘可重建。
/// <para>
/// 刻意<b>不测</b>回退 / 分支 / 正文候选切换——本项目不做这些：设计文档 §11.4-4 已明确，
/// 角色扮演里用户是导演（DM）、剧情由角色 Agent 推进，跑偏由导演下指令纠正。
/// </para>
/// </summary>
public sealed class StoryTimelineTests
{
    [Fact]
    public void RevisionOnlyGrows()
    {
        var timeline = new StoryTimeline();

        timeline.Append("我推开门。", [new StoryLine("旁白", "门后有风。")]);
        timeline.Append("我继续走。", [new StoryLine("艾拉", "别动。")]);

        Assert.Equal(2, timeline.Revision);

        timeline.Append("我停下。", [new StoryLine("艾拉", "很好。")]);
        Assert.Equal(3, timeline.Revision);
    }

    [Fact]
    public void TimelineIsRebuiltFromJsonl()
    {
        using var temp = new TempDir();
        var path = temp.File("timeline.jsonl");

        new StoryTimeline(path).Append("开场", [new StoryLine("旁白", "雨下得很大。")]);
        new StoryTimeline(path).Append("走一步", [new StoryLine("艾拉", "跟我来。")]);

        var reloaded = new StoryTimeline(path);

        Assert.Equal(2, reloaded.Turns.Count);
        Assert.Equal("跟我来。", reloaded.Turns[^1].Lines[0].Text);
    }

    [Fact]
    public void EveryTurnIsAppendedToDisk()
    {
        using var temp = new TempDir();
        var path = temp.File("timeline.jsonl");
        var timeline = new StoryTimeline(path);

        timeline.Append("一", [new StoryLine("A", "1")]);
        timeline.Append("二", [new StoryLine("A", "2")]);

        // append-only：两个回合 = 两行，历史只增不减。
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }
}
