using System.Text;

namespace MeetingRoom.Agent.Thoughts;

/// <summary>
/// 生成摘要所需的、来自<b>程序</b>而非模型的输入（设计文档 §4.4 里"做法"一段主要靠它，
/// 不必去解析模型文本）。
/// </summary>
public sealed record ThoughtSummaryInputs
{
    /// <summary>本轮任务（"目的"的首选来源）。</summary>
    public string Task { get; init; } = string.Empty;

    /// <summary>本轮执行过的工具名，按顺序。</summary>
    public IReadOnlyList<string> ToolCalls { get; init; } = [];

    /// <summary>本轮读过的资料 id（阶段 4 起由工具参数填充）。</summary>
    public IReadOnlyList<string> MaterialIds { get; init; } = [];

    /// <summary>实际执行的 step 数。</summary>
    public int Steps { get; init; }
}

/// <summary>
/// 思考的"有界总结"生成器（设计文档 §4.4）。三条硬约束：
/// <list type="number">
///   <item>由<b>程序</b>按固定模板生成，不额外调用模型（不加 token、不加延迟）；</item>
///   <item>总长不超过 <paramref name="maxChars"/>（默认 500 汉字）；</item>
///   <item>超限时按"结论 → 做法 → 目的"的顺序保留，而不是简单截尾。</item>
/// </list>
/// 这份摘要<b>只进产生它的那个会话的私有历史</b>，绝不进共享前缀——否则所有会话的缓存前缀
/// 都会被同一份思考污染。
/// </summary>
public static class ThoughtSummarizer
{
    /// <summary>默认上限（汉字），对应配置项 <c>summaries.thoughtSummaryMaxChars</c>。</summary>
    public const int DefaultMaxChars = 500;

    /// <summary>给"目的"与"做法"留的最低字数，避免结论段把预算吃光后摘要只剩判断、没有上下文。</summary>
    private const int MinPurposeChars = 24;

    private const int MinApproachChars = 32;

    /// <summary>三段标签（各 3 字）与最多两个换行的固定开销——预算里必须先扣掉，否则结果会超出上限。</summary>
    private const int MaxLabelOverhead = 11;

    public static string Summarize(string thought, ThoughtSummaryInputs inputs, int maxChars = DefaultMaxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        thought ??= string.Empty;
        if (string.IsNullOrWhiteSpace(thought) && inputs.ToolCalls.Count == 0 && inputs.Steps == 0)
        {
            return string.Empty;
        }

        var purpose = BuildPurpose(inputs, thought);
        var approach = BuildApproach(inputs);
        var conclusion = BuildConclusion(thought);

        // 预算分配：结论优先，其次做法，最后目的；前两者各留一个最低保底。
        var budget = maxChars - MaxLabelOverhead;
        if (budget <= 0)
        {
            return string.Empty;
        }

        var reservedForPurpose = Math.Min(MinPurposeChars, purpose.Length);
        var reservedForApproach = Math.Min(MinApproachChars, approach.Length);

        var conclusionText = TakeTail(conclusion, budget - reservedForPurpose - reservedForApproach);
        var used = conclusionText.Length;

        var approachText = TakeHead(approach, budget - used);
        used += approachText.Length;

        var purposeText = TakeHead(purpose, budget - used);

        // 输出顺序仍是"目的 → 做法 → 结论"（参考论文摘要结构）。
        var builder = new StringBuilder();
        AppendSection(builder, "目的", purposeText);
        AppendSection(builder, "做法", approachText);
        AppendSection(builder, "结论", conclusionText);

        return builder.ToString();
    }

    private static string BuildPurpose(ThoughtSummaryInputs inputs, string thought)
    {
        if (!string.IsNullOrWhiteSpace(inputs.Task))
        {
            return Collapse(inputs.Task);
        }

        // 没有任务时退回思考首句（文档 §4.4："取自本轮任务与 turn 首句"）。
        var paragraphs = Paragraphs(thought);
        return paragraphs.Length == 0 ? string.Empty : Collapse(paragraphs[0]);
    }

    private static string BuildApproach(ThoughtSummaryInputs inputs)
    {
        var parts = new List<string>();

        if (inputs.Steps > 0)
        {
            parts.Add($"共 {inputs.Steps} 个 step");
        }

        if (inputs.ToolCalls.Count > 0)
        {
            parts.Add($"调用 {string.Join('、', inputs.ToolCalls.Distinct(StringComparer.Ordinal))}");
        }

        if (inputs.MaterialIds.Count > 0)
        {
            parts.Add($"读取资料 {string.Join('、', inputs.MaterialIds.Distinct(StringComparer.Ordinal))}");
        }

        return string.Join('；', parts);
    }

    /// <summary>模型通常把判断放在末尾，所以"结论"取最后一段。</summary>
    private static string BuildConclusion(string thought)
    {
        var paragraphs = Paragraphs(thought);
        return paragraphs.Length == 0 ? string.Empty : Collapse(paragraphs[^1]);
    }

    private static string[] Paragraphs(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>把段落内的连续空白压成单个空格：摘要要能塞进一行。</summary>
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

    private static void AppendSection(StringBuilder builder, string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(label).Append('：').Append(text);
    }

    private static string TakeHead(string text, int limit) =>
        limit <= 0 ? string.Empty : text.Length <= limit ? text : text[..limit];

    private static string TakeTail(string text, int limit) =>
        limit <= 0 ? string.Empty : text.Length <= limit ? text : text[^limit..];
}
