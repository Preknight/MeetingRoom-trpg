using MeetingRoom.Core.Roleplay;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 长程记忆与压缩（设计文档 §11.4-7）：分层（近轮原文 + 更早批次摘要）+ 按角色视角检索。
/// </summary>
public sealed class StoryMemoryTests
{
    [Fact]
    public void RecentTurnsKeepTheirOriginalText()
    {
        var memory = new StoryMemory(keepRecentTurns: 4);

        memory.Record(Turn(1, Line("艾拉", "跟我来。")));

        Assert.Equal(["艾拉：跟我来。"], memory.RecallFor("ella"));
    }

    [Fact]
    public void OlderTurnsCollapseIntoSummaries()
    {
        var memory = new StoryMemory(keepRecentTurns: 2, summaryBatch: 3);

        for (var revision = 1; revision <= 6; revision++)
        {
            memory.Record(Turn(revision, Line("ella", $"第 {revision} 句话。")));
        }

        var summary = Assert.Single(memory.SummariesFor("ella"));
        Assert.Equal(1, summary.FromRevision);
        Assert.Equal(3, summary.ToRevision);
        Assert.Contains("第 1 句话。", summary.Text, StringComparison.Ordinal);

        // 摘要带 revision 区间，且排在原文之前（更早的事）。
        Assert.Contains(
            memory.RecallFor("ella"),
            line => line.StartsWith("[1-3]", StringComparison.Ordinal));
    }

    [Fact]
    public void ActorCannotRecallWhatTheyNeverWitnessed()
    {
        var memory = new StoryMemory();

        memory.Record(Turn(1, Line("艾拉", "钥匙在我这里。", "ella")));

        Assert.Contains("钥匙在我这里。", memory.RecallFor("ella")[0], StringComparison.Ordinal);
        Assert.Empty(memory.RecallFor("ran"));
    }

    [Fact]
    public void PublicLinesAreVisibleToEveryone()
    {
        var memory = new StoryMemory();

        memory.Record(Turn(1, Line("旁白", "雨停了。")));

        Assert.Single(memory.RecallFor("ran"));
        Assert.Single(memory.RecallFor("ella"));
    }

    [Fact]
    public void AuthorAlwaysRecallsTheirOwnLines()
    {
        var memory = new StoryMemory();

        memory.Record(Turn(1, Line("ella", "我说过的话。", "ran")));

        Assert.Single(memory.RecallFor("ella"));
    }

    [Fact]
    public void RecallRespectsTheBudget()
    {
        var memory = new StoryMemory();

        for (var revision = 1; revision <= 10; revision++)
        {
            memory.Record(Turn(revision, Line("旁白", new string('雨', 50))));
        }

        var recalled = memory.RecallFor("ran", maxChars: 120);

        Assert.NotEmpty(recalled);
        Assert.True(recalled.Sum(line => line.Length) <= 120);
    }

    [Fact]
    public void EachActorGetsTheirOwnSummaryOfTheSameBatch()
    {
        var memory = new StoryMemory(keepRecentTurns: 0, summaryBatch: 2);

        // 两幕同一批：第一幕只有 ella 在场，第二幕只有 ran 在场。
        memory.Record(Turn(1, Line("ella", "钥匙在我这里。", "ella")));
        memory.Record(Turn(2, Line("ran", "灯塔的门锁着。", "ran")));
        memory.Record(Turn(3, Line("narrator", "雨停了。", "ella", "ran")));

        var forElla = string.Join(" ", memory.SummariesFor("ella").Select(summary => summary.Text));
        var forRan = string.Join(" ", memory.SummariesFor("ran").Select(summary => summary.Text));

        // 同一批，两个人各记得自己经历的那部分——这才是"按角色视角"。
        Assert.Contains("钥匙在我这里。", forElla, StringComparison.Ordinal);
        Assert.DoesNotContain("灯塔的门锁着。", forElla, StringComparison.Ordinal);
        Assert.Contains("灯塔的门锁着。", forRan, StringComparison.Ordinal);
        Assert.DoesNotContain("钥匙在我这里。", forRan, StringComparison.Ordinal);
    }

    [Fact]
    public void SummariesSurviveARestart()
    {
        using var temp = new TempDir();
        var path = temp.File("memory.jsonl");

        var first = new StoryMemory(keepRecentTurns: 0, summaryBatch: 2, path: path);
        first.Record(Turn(1, Line("ella", "钥匙在我这里。", "ella")));
        first.Record(Turn(2, Line("ella", "我们得快点。", "ella")));
        first.Record(Turn(3, Line("narrator", "雨停了。", "ella")));

        Assert.NotEmpty(first.SummariesFor("ella"));

        // 同一份 memory.jsonl 重新打开：摘要按角色回来（§11.4-7）。
        var second = new StoryMemory(keepRecentTurns: 0, summaryBatch: 2, path: path);

        Assert.Equal(
            first.SummariesFor("ella").Select(summary => summary.Text),
            second.SummariesFor("ella").Select(summary => summary.Text));
    }

    private static StoryLine Line(string author, string text, params string[] witnesses) =>
        new(author, text) { Witnesses = witnesses };

    private static StoryTurn Turn(long revision, params StoryLine[] lines) => new(
        Revision: revision,
        DirectorInput: string.Empty,
        Lines: lines,
        At: DateTimeOffset.UnixEpoch);
}
