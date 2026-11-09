using System.Text.RegularExpressions;

namespace MeetingRoom.Core.Roleplay;

/// <summary>导演输入的解析结果：旁白 + 元指令（OOC）。</summary>
public sealed record DirectorInput(string Text, IReadOnlyList<string> Directives)
{
    /// <summary>整条都是元指令（没有旁白）。</summary>
    public bool IsPureDirective => Text.Length == 0 && Directives.Count > 0;
}

/// <summary>
/// 元指令（OOC）通道（设计文档 §11.4-9）：导演用括号说"（让艾拉出场）""((跳过这段))"，
/// 这类是<b>元指令</b>——不进剧情时间线，也不该被角色当成台词。
/// 支持全角/半角括号与方括号。
/// </summary>
public static class OocChannel
{
    // 允许单层或双层括号（（…）/((…))），方括号同理。
    private static readonly Regex Brackets = new(
        @"[（(]{1,2}(?<round>[^（()）]{1,200})[)）]{1,2}|\[{1,2}(?<square>[^\[\]]{1,200})\]{1,2}",
        RegexOptions.Compiled);

    public static DirectorInput Parse(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new DirectorInput(string.Empty, []);
        }

        var directives = new List<string>();

        var stripped = Brackets.Replace(text, match =>
        {
            var inner = match.Groups["round"].Success ? match.Groups["round"].Value : match.Groups["square"].Value;

            if (!string.IsNullOrWhiteSpace(inner))
            {
                directives.Add(inner.Trim());
            }

            return string.Empty;
        });

        var remained = Collapse(stripped);

        return new DirectorInput(remained, directives);
    }

    private static string Collapse(string text)
    {
        var condensed = Regex.Replace(text, @"\s{2,}", " ");
        return condensed.Trim();
    }
}
