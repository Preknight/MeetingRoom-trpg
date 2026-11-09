using System.Text;
using System.Text.RegularExpressions;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 叙事输出净化（设计文档 §11.4-10）：把模型偶尔带出来的<b>元话语</b>剥掉——
/// "作为 AI 助手"、工具残留标记（<c>{{...}}</c>、<c>&lt;|...|&gt;</c>）、Markdown 标题/围栏、
/// 以及重复标出的说话人前缀。
/// <para>纯规则层处理，不额外调用模型（与思考摘要同一条原则：程序能做的不花 token）。</para>
/// </summary>
public static class NarrativeOutputSanitizer
{
    private static readonly Regex ResidualMarkers = new(
        @"\{\{[^{}]{0,200}\}\}|<\|[^|]{0,60}\|>|【系统[^】]{0,60}】",
        RegexOptions.Compiled);

    private static readonly Regex MetaTalk = new(
        @"(作为一个?(AI|人工智能|语言模型|助手|助理)|作为\s*AI)[，,。：:]?",
        RegexOptions.Compiled);

    private static readonly Regex Headings = new(@"^#{1,6}\s*", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CodeFence = new(@"^\s*```[a-zA-Z0-9]*\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex BlankRuns = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>净化一段叙事正文；<paramref name="speakerName"/> 给出时会去掉重复的"角色名："前缀。</summary>
    public static string Sanitize(string? text, string? speakerName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text.Replace("\r\n", "\n", StringComparison.Ordinal);

        result = CodeFence.Replace(result, string.Empty);
        result = Headings.Replace(result, string.Empty);
        result = ResidualMarkers.Replace(result, string.Empty);
        result = MetaTalk.Replace(result, string.Empty);
        result = BlankRuns.Replace(result, "\n\n");

        if (!string.IsNullOrWhiteSpace(speakerName))
        {
            var prefix = speakerName.Trim() + "：";
            var englishPrefix = speakerName.Trim() + ":";

            if (result.StartsWith(prefix, StringComparison.Ordinal))
            {
                result = result[prefix.Length..];
            }
            else if (result.StartsWith(englishPrefix, StringComparison.Ordinal))
            {
                result = result[englishPrefix.Length..];
            }

            result = result
                .Replace("\n" + prefix, "\n", StringComparison.Ordinal)
                .Replace("\n" + englishPrefix, "\n", StringComparison.Ordinal);
        }

        return result.Trim();
    }

    /// <summary>这段文本是否带明显的元话语痕迹（用于诊断，不改变输出）。</summary>
    public static bool LooksLikeMetaTalk(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (MetaTalk.IsMatch(text) || ResidualMarkers.IsMatch(text));
}
