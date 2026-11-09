using System.Text;

namespace MeetingRoom.Core.Materials;

/// <summary>
/// 摘要策略（设计文档 §5.3）：与会者在 <c>submit_material</c> 给的摘要优先；
/// 没给就由程序截取正文前 N 字兜底，并标记 <c>summarySource=fellback</c>（便于事后发现偷懒的摘要）；
/// 给得太长同样由程序截断。
/// </summary>
public sealed class MaterialSummaryPolicy
{
    public MaterialSummaryPolicy(int fallbackChars = 300)
    {
        FallbackChars = fallbackChars > 0
            ? fallbackChars
            : throw new ArgumentOutOfRangeException(nameof(fallbackChars), fallbackChars, "摘要兜底长度必须大于 0。");
    }

    /// <summary>兜底与截断的字数上限，对应 <c>summaries.materialSummaryFallbackChars</c>。</summary>
    public int FallbackChars { get; }

    /// <summary>返回（摘要, 来源）。</summary>
    public (string Summary, string Source) Resolve(string? provided, string body)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            return (Truncate(Collapse(body), FallbackChars), "fellback");
        }

        return (Truncate(Collapse(provided), FallbackChars), "tool");
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit];

    /// <summary>摘要要能塞进一行：连续空白折叠成单个空格。</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
