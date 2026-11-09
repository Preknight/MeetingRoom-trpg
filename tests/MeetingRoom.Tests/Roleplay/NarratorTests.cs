using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 导演叙事化（设计文档 §11.4-5）+ 工具/提示对账：
/// 提示词里必须能找到每一个声明过的工具名，否则模型会去调不存在的工具。
/// </summary>
public sealed class NarrativeDirectorTests
{
    [Fact]
    public void EveryDeclaredToolIsMentionedInThePrompt()
    {
        var prompt = Narrator.BuildSystemPrompt("雨夜的小镇", "你站在钟楼下", "时间：第三日黄昏");

        foreach (var tool in Narrator.ToolNames)
        {
            Assert.Contains(tool, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolNamesAreUnique()
    {
        Assert.Equal(
            Narrator.ToolNames.Count,
            Narrator.ToolNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PromptCoversTheFiveDuties()
    {
        var prompt = Narrator.BuildSystemPrompt("小镇", "钟楼", string.Empty);

        Assert.Contains("描述环境", prompt, StringComparison.Ordinal);
        Assert.Contains("切换时间地点", prompt, StringComparison.Ordinal);
        Assert.Contains("决定谁在场", prompt, StringComparison.Ordinal);
        Assert.Contains("维持节奏", prompt, StringComparison.Ordinal);
        Assert.Contains("自主推进", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptCarriesWorldSituationAndSnapshot()
    {
        var prompt = Narrator.BuildSystemPrompt(
            "雨夜的小镇",
            "你敲响了钟楼的门",
            "时间：第三日黄昏\n地点：钟楼");

        Assert.Contains("雨夜的小镇", prompt, StringComparison.Ordinal);
        Assert.Contains("你敲响了钟楼的门", prompt, StringComparison.Ordinal);
        Assert.Contains("地点：钟楼", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningTaskCarriesSceneAndCast()
    {
        var task = Narrator.BuildOpeningTask(
            "雨夜的小镇",
            "你敲响了钟楼的门",
            "本场导演，兼旁白",
            ["艾拉", "冉"]);

        Assert.Contains("本场刚开始", task, StringComparison.Ordinal);
        Assert.Contains("雨夜的小镇", task, StringComparison.Ordinal);
        Assert.Contains("你敲响了钟楼的门", task, StringComparison.Ordinal);
        Assert.Contains("本场导演，兼旁白", task, StringComparison.Ordinal);
        Assert.Contains("艾拉、冉", task, StringComparison.Ordinal);
    }
}
