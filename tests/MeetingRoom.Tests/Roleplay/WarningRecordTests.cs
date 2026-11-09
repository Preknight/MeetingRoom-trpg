using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// §30：警告必须落盘（<c>kind=warning</c>），判据是"回执说了什么，日志里就该查得到"。
/// </summary>
public sealed class WarningRecordTests
{
    [Fact]
    public void AppendWarningKeepsCodeMessageAndSource()
    {
        var timeline = new StoryTimeline();

        var record = timeline.AppendWarning(
            "combat-active",
            "战斗还在进行（enc-1，第 1 轮），不能收束；先把这场战斗打完（end_combat）或交回导演。",
            "declare_scene_end");

        Assert.Equal("combat-active", record.Code);
        Assert.Equal("declare_scene_end", record.Source);
        Assert.Contains("不能收束", record.Message, StringComparison.Ordinal);
        Assert.Single(timeline.WarningRecords);
    }

    [Fact]
    public void WarningsAreNotCombatRejections()
    {
        var timeline = new StoryTimeline();

        timeline.AppendWarning("sheet-invalid", "broken.sheet.json 校验不过", "sheet-loader");

        // 战斗域统计不能被警告污染（§30 明确不混进 combat-rejected）。
        Assert.Empty(timeline.CombatRejectedRecords);
        Assert.Single(timeline.WarningRecords);
    }
}
