using MeetingRoom.Agent.Thoughts;

namespace MeetingRoom.Tests.Agent;

/// <summary>
/// 思考的有界总结（设计文档 §4.4 / 实现步骤 5.2）：
/// ≤ 上限、三段结构、超限时按"结论 → 做法 → 目的"保留。
/// </summary>
public sealed class ThoughtSummarizerTests
{
    private const string ConclusionMarker = "结论落在末尾：先做两档、只考核转化率。";

    [Fact]
    public void StaysWithinLimitAndKeepsAllThreeSections()
    {
        var thought = new string('想', 3000) + "\n" + ConclusionMarker;

        var summary = ThoughtSummarizer.Summarize(
            thought,
            new ThoughtSummaryInputs
            {
                Task = "回答定价与上线范围的问题",
                ToolCalls = ["read_material", "recall_speech"],
                Steps = 3,
            },
            maxChars: 500);

        Assert.True(summary.Length <= 500, $"摘要长度 {summary.Length} 超过了 500");
        Assert.Contains("目的：", summary, StringComparison.Ordinal);
        Assert.Contains("做法：", summary, StringComparison.Ordinal);
        Assert.Contains("结论：", summary, StringComparison.Ordinal);

        // "结论"段必须完整保留（§4.4 的截断顺序）。
        Assert.Contains(ConclusionMarker, summary, StringComparison.Ordinal);

        // "做法"来自程序而非模型：工具与 step 数都在。
        Assert.Contains("read_material", summary, StringComparison.Ordinal);
        Assert.Contains("3 个 step", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsConclusionEvenWhenBudgetIsTight()
    {
        var thought = new string('想', 2000) + "\n" + ConclusionMarker;

        var summary = ThoughtSummarizer.Summarize(
            thought,
            new ThoughtSummaryInputs { Task = new string('目', 400) },
            maxChars: 60);

        Assert.True(summary.Length <= 60, $"摘要长度 {summary.Length} 超过了 60");
        Assert.Contains(ConclusionMarker, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PurposeFallsBackToTheFirstParagraphWhenTaskIsMissing()
    {
        var summary = ThoughtSummarizer.Summarize(
            "先确认目标是什么。\n中间推理……\n最终判断：可以收敛。",
            new ThoughtSummaryInputs(),
            maxChars: 500);

        Assert.Contains("先确认目标是什么。", summary, StringComparison.Ordinal);
        Assert.Contains("最终判断：可以收敛。", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSignalMeansEmptySummary()
    {
        Assert.Equal(string.Empty, ThoughtSummarizer.Summarize(string.Empty, new ThoughtSummaryInputs()));
        Assert.Equal(string.Empty, ThoughtSummarizer.Summarize("有思考", new ThoughtSummaryInputs(), maxChars: 0));
    }

    [Fact]
    public void ApproachOnlySummaryStillCarriesStepsAndTools()
    {
        var summary = ThoughtSummarizer.Summarize(
            string.Empty,
            new ThoughtSummaryInputs { ToolCalls = ["list_materials"], Steps = 2 },
            maxChars: 500);

        Assert.Contains("做法：", summary, StringComparison.Ordinal);
        Assert.Contains("list_materials", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("结论：", summary, StringComparison.Ordinal);
    }
}
