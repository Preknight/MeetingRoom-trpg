using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 阶段 8 的叙事文本处理（设计文档 §11.4 的 9/10）：导演的元指令不进剧情、输出净化剥掉元话语。
/// </summary>
public sealed class NarrativeTextTests
{
    [Fact]
    public void OocDirectivesAreSeparatedFromTheStoryText()
    {
        var parsed = OocChannel.Parse("我推开门。（让艾拉出场）雨很大。");

        Assert.Equal("我推开门。雨很大。", parsed.Text);
        Assert.Equal("让艾拉出场", Assert.Single(parsed.Directives));
        Assert.False(parsed.IsPureDirective);
    }

    [Fact]
    public void PureDirectiveHasNoStoryText()
    {
        var parsed = OocChannel.Parse("((跳过这段))");

        Assert.True(parsed.IsPureDirective);
        Assert.Empty(parsed.Text);
        Assert.Equal("跳过这段", Assert.Single(parsed.Directives));
    }

    [Fact]
    public void BlankInputYieldsNothing()
    {
        var parsed = OocChannel.Parse("   ");

        Assert.Empty(parsed.Text);
        Assert.Empty(parsed.Directives);
        Assert.False(parsed.IsPureDirective);
    }

    [Fact]
    public void SanitizerStripsMetaTalkAndMarkers()
    {
        var raw = "作为 AI 助手，我来描述一下。\n\n{{tool_call}}\n\n## 场景\n\n雨停了。\n\n\n\n艾拉点头。";

        var clean = NarrativeOutputSanitizer.Sanitize(raw);

        Assert.DoesNotContain("作为 AI", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("##", clean, StringComparison.Ordinal);
        Assert.Contains("雨停了。", clean, StringComparison.Ordinal);
        Assert.Contains("艾拉点头。", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizerDropsRepeatedSpeakerPrefix()
    {
        var clean = NarrativeOutputSanitizer.Sanitize("艾拉：跟我来。\n艾拉：别回头。", speakerName: "艾拉");

        Assert.Equal("跟我来。\n别回头。", clean);
    }

    [Fact]
    public void MetaTalkDetectionDoesNotChangeOutput()
    {
        Assert.True(NarrativeOutputSanitizer.LooksLikeMetaTalk("作为一个语言模型，我无法…"));
        Assert.False(NarrativeOutputSanitizer.LooksLikeMetaTalk("雨下得很大。"));
    }
}
