using System.Text.Json;

namespace MeetingRoom.Core.Interaction;

/// <summary>
/// 提问的规范（设计文档 §14.2 / §14.5.1）：<c>ask_user</c> 工具与编排层兜底发问<b>共用同一套</b>校验与构造。
/// <para>
/// 两条硬要求：选项的 <c>description</c> <b>必填</b>——"选哪个"必须有一行能读的说明，
/// 否则用户只能凭标签猜；选项数量受 <c>interaction.maxOptions</c> 限——选项太多反而选不动。
/// </para>
/// </summary>
public static class AskUserSpec
{
    /// <summary>一次最多问几个问题（前端是分步展示，问题多了会变成填表）。</summary>
    private static readonly JsonSerializerOptions ArgumentOptions = new() { PropertyNameCaseInsensitive = true };

    public const int MaxQuestions = 3;

    /// <summary>校验模型给的提问；返回逐条问题描述，空 = 通过。</summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<InteractionQuestion> questions,
        InteractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (questions.Count == 0)
        {
            problems.Add("至少要问一个问题。");
            return problems;
        }

        if (questions.Count > MaxQuestions)
        {
            problems.Add($"一次最多问 {MaxQuestions} 个问题，当前给了 {questions.Count} 个。");
        }

        foreach (var question in questions)
        {
            var prompt = string.IsNullOrWhiteSpace(question.Prompt) ? "(没有写问题)" : question.Prompt.Trim();

            if (string.IsNullOrWhiteSpace(question.Prompt))
            {
                problems.Add("问题的 prompt 不能为空。");
            }

            if (question.Options.Count < 2)
            {
                problems.Add($"「{prompt}」至少要有 2 个选项。");
            }

            if (question.Options.Count > options.MaxOptions)
            {
                problems.Add(
                    $"「{prompt}」给了 {question.Options.Count} 个选项，"
                    + $"超过 interaction.maxOptions（{options.MaxOptions}）。");
            }

            foreach (var option in question.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Description))
                {
                    problems.Add(
                        $"选项「{(string.IsNullOrWhiteSpace(option.Label) ? option.Id : option.Label)}」缺少 description："
                        + "每个选项都要写一行说明，用户才能判断。");
                }
            }
        }

        return problems;
    }

    /// <summary>把模型给的提问规范化：补问题 id、补选项 id 与标签、补默认选项（缺省取第一个）。</summary>
    public static IReadOnlyList<InteractionQuestion> Normalize(IReadOnlyList<InteractionQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var normalized = new List<InteractionQuestion>(questions.Count);

        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];

            var options = question.Options
                .Select((option, optionIndex) => option with
                {
                    Id = string.IsNullOrWhiteSpace(option.Id) ? $"opt{optionIndex + 1}" : option.Id.Trim(),
                    Label = string.IsNullOrWhiteSpace(option.Label) ? (option.Id ?? string.Empty).Trim() : option.Label.Trim(),
                })
                .ToArray();

            var declared = question.DefaultOptionId;
            var defaultId = !string.IsNullOrWhiteSpace(declared)
                && options.Any(option => string.Equals(option.Id, declared, StringComparison.Ordinal))
                    ? declared.Trim()
                    : options.FirstOrDefault()?.Id;

            normalized.Add(new InteractionQuestion(
                string.IsNullOrWhiteSpace(question.Id) ? $"q{index + 1}" : question.Id.Trim(),
                (question.Prompt ?? string.Empty).Trim(),
                options,
                defaultId));
        }

        return normalized;
    }

    /// <summary>
    /// 把收口结果写成一句能给模型看的话（用户选了什么，或者没答、为什么按默认走）。
    /// 回灌给模型的是自然语言——它要据此继续控场，而不是看一份 JSON。
    /// </summary>
    public static string DescribeAnswers(
        InteractionOutcome outcome,
        IReadOnlyList<InteractionQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(questions);

        var parts = new List<string>();

        foreach (var question in questions)
        {
            var answer = outcome.Answers
                .FirstOrDefault(item => string.Equals(item.QuestionId, question.Id, StringComparison.Ordinal));

            var labels = (answer?.Selected ?? [])
                .Select(id => question.Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal)))
                .Where(option => option is not null)
                .Select(option => option!.Label)
                .ToArray();

            var how = outcome.TimedOut ? "超时未答，按默认" : outcome.Skipped ? "跳过了" : "用户选了";
            var what = labels.Length > 0 ? string.Join("、", labels) : "（没有选项）";

            parts.Add($"{question.Prompt} → {how}「{what}」");
        }

        return string.Join("；", parts);
    }

    /// <summary>
    /// 从工具参数里读提问：模型给的是 JSON 数组，测试与脚本里常直接给 JSON 文本或强类型集合。
    /// 形状不对就抛 <see cref="JsonException"/>，由调用方回灌"参数格式不对"，不静默丢掉。
    /// </summary>
    public static IReadOnlyList<InteractionQuestion> ReadQuestions(object? value)
    {
        List<AskQuestion> asked = value switch
        {
            null => [],
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.Deserialize<List<AskQuestion>>(ArgumentOptions) ?? [],
            JsonElement { ValueKind: JsonValueKind.String } text => ParseQuestions(text.GetString()),
            JsonElement { ValueKind: JsonValueKind.Null } => [],
            string text => ParseQuestions(text),
            IEnumerable<AskQuestion> typed => [.. typed],
            _ => throw new JsonException("questions 期望一个数组"),
        };

        return [.. asked.Select(question => new InteractionQuestion(
            string.Empty,
            question.Prompt ?? string.Empty,
            [.. (question.Options ?? []).Select(option => new InteractionOption(
                option.Id ?? string.Empty,
                option.Label ?? string.Empty,
                option.Description))],
            question.DefaultOptionId))];
    }

    private static List<AskQuestion> ParseQuestions(string? json) =>
        JsonSerializer.Deserialize<List<AskQuestion>>(
            string.IsNullOrWhiteSpace(json) ? "[]" : json,
            ArgumentOptions) ?? [];

    /// <summary>
    /// 编排层兜底发问用的默认问题集（设计文档 §14.5.1）：模型这一轮没主动提问、
    /// 而配置又要求"每轮必须问"时，由<b>程序</b>保证"必有交互"，而不是靠提示词。
    /// 默认选项是「进入下一轮」——用户不回答时局面继续往下走。
    /// </summary>
    public static IReadOnlyList<InteractionQuestion> DefaultRoundQuestions() =>
    [
        new InteractionQuestion(
            "round",
            "这一轮就到这里，接下来怎么走？",
            [
                new InteractionOption("deeper", "继续深入", "接着追问同一个方向，把细节再挖一层"),
                new InteractionOption("next", "进入下一轮", "换个角度或话题，继续往下推进"),
                new InteractionOption("conclude", "结束并出结论", "认为已经够了，让会议收口"),
            ],
            DefaultOptionId: "next"),
    ];
}
