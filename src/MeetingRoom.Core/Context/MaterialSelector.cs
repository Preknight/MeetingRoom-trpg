using MeetingRoom.Core.Materials;

namespace MeetingRoom.Core.Context;

/// <summary>资料选择结果：要注入哪些、各自属于哪一类、被什么挡掉了。</summary>
public sealed record MaterialSelection(
    IReadOnlyList<MaterialCard> Entries,
    int AlwaysCount,
    int TriggeredCount,
    int SkippedByCooldown,
    int SkippedByBudget);

/// <summary>
/// 常驻 / 触发式资料的选择（设计文档 §7.2，借 dsh-tavern 的世界书模型）：
/// <list type="bullet">
///   <item><b>常驻</b>：最近采纳的若干份，每次请求都带标题 + 摘要（上限 <c>alwaysMaterialMaxCount</c>）；</item>
///   <item><b>触发式</b>：其余资料按当前任务的<b>关键词</b>匹配，命中才带；</item>
///   <item><b>预算</b>：触发式总量受 <c>triggeredTokenBudget</c> 约束，超出时按相关性<b>逐条截断</b>而不是整段丢弃；</item>
///   <item><b>冷却</b>：同一份资料在 <c>triggeredCooldownRounds</c> 轮内不重复注入。</item>
/// </list>
/// 只有 <see cref="MaterialStatus.Adopted"/> 的资料会进索引——没被采纳的内容还不是"公共资料"。
/// </summary>
public sealed class MaterialSelector
{
    private static readonly char[] Separators = [' ', '\t', '，', ',', '、', '；', ';', '：', ':', '。', '.'];

    private readonly int _alwaysMaxCount;
    private readonly int _triggeredBudget;
    private readonly int _cooldownRounds;
    private readonly Dictionary<string, int> _lastInjectedRound = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public MaterialSelector(int alwaysMaxCount, int triggeredTokenBudget, int triggeredCooldownRounds)
    {
        _alwaysMaxCount = Math.Max(0, alwaysMaxCount);
        _triggeredBudget = Math.Max(0, triggeredTokenBudget);
        _cooldownRounds = Math.Max(0, triggeredCooldownRounds);
    }

    /// <param name="task">本轮任务（触发式匹配的关键词来源）。</param>
    /// <param name="round">当前轮次（冷却判定用）。</param>
    /// <param name="candidates">候选资料（通常是全部资料）。</param>
    public MaterialSelection Select(string task, int round, IReadOnlyList<MaterialCard> candidates)
    {
        var adopted = candidates
            .Where(card => card.Status == MaterialStatus.Adopted)
            .ToList();

        // 常驻：最近采纳的若干份（"标记为核心的资料"目前用"最近采纳"近似，记在进度文档待办里）。
        var always = adopted
            .OrderByDescending(card => card.BasedOnVersion)
            .ThenBy(card => card.Id, StringComparer.Ordinal)
            .Take(_alwaysMaxCount)
            .ToList();

        var alwaysIds = always.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
        var terms = Tokenize(task);
        var budget = _triggeredBudget;
        var triggered = new List<MaterialCard>();
        var skippedByCooldown = 0;
        var skippedByBudget = 0;

        // 触发式：按相关性（命中词数）排序后逐条放进预算。预算不够就停在这里，
        // 已经放进去的仍然有效——这就是"按相关性截断，而不是整段丢弃"。
        var hits = adopted
            .Where(card => !alwaysIds.Contains(card.Id))
            .Select(card => (Card: card, Score: Score(card, terms)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Card.BasedOnVersion);

        foreach (var (card, _) in hits)
        {
            if (IsCoolingDown(card.Id, round))
            {
                skippedByCooldown++;
                continue;
            }

            var cost = EstimateTokens(card);
            if (cost > budget)
            {
                skippedByBudget++;
                continue;
            }

            budget -= cost;
            triggered.Add(card);
            Remember(card.Id, round);
        }

        return new MaterialSelection(
            [.. always, .. triggered],
            always.Count,
            triggered.Count,
            skippedByCooldown,
            skippedByBudget);
    }

    private bool IsCoolingDown(string id, int round)
    {
        lock (_gate)
        {
            return _lastInjectedRound.TryGetValue(id, out var last) && round - last < _cooldownRounds;
        }
    }

    private void Remember(string id, int round)
    {
        lock (_gate)
        {
            _lastInjectedRound[id] = round;
        }
    }

    private static int Score(MaterialCard card, IReadOnlyList<string> terms)
    {
        var score = 0;

        foreach (var term in terms)
        {
            if (card.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (card.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
        }

        return score;
    }

    private static IReadOnlyList<string> Tokenize(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term => term.Length >= 2)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// 粗略的 token 估算：中文里 1 个汉字大体对应 1 个 token，这里直接按字符数算——
    /// 不为预算判定引入 tokenizer；预算本身是保守值，宁可少注入也不超额。
    /// </summary>
    private static int EstimateTokens(MaterialCard card) =>
        card.Title.Length + card.Summary.Length + card.Path.Length + 8;
}
