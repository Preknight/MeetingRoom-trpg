using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetingRoom.Core.Roleplay;

/// <summary>掷骰模式：优势 / 劣势只作用于 d20（5e 的两个 d20 取高 / 取低）。</summary>
public enum DiceMode
{
    Normal,
    Advantage,
    Disadvantage,
}

/// <summary>一次攻击（或一次检定）的结果：掷了什么、留了什么、总共多少。</summary>
public sealed record DiceAttack(
    string Label,
    int Sides,
    int Count,
    int Modifier,
    IReadOnlyList<int> Rolled,
    IReadOnlyList<int> Kept,
    int Total,
    bool? Success);

/// <summary>
/// 一次掷骰的完整结果（《枭熊地图接入实现方案》V2）：<c>{ rolls, kept, total, dc?, success? }</c>。
/// <para>
/// <b>数值只能由工具产生</b>：正文引用它，不允许说书人自己改。落盘到 <c>story.jsonl</c> 的
/// <c>kind=roll</c> 记录里带着全部明细，所以同样的明细能重放出同样的 total。
/// </para>
/// </summary>
public sealed record DiceRollResult(
    bool Ok,
    string Expression,
    DiceMode Mode,
    string? Error,
    IReadOnlyList<DiceAttack> Attacks,
    int Total,
    int? Dc,
    bool? Success)
{
    public static DiceRollResult Failed(string expression, string error) =>
        new(false, expression, DiceMode.Normal, error, [], 0, null, null);

    /// <summary>回灌给说书人的一句话（它要照着写正文，所以把人话写全）。</summary>
    public string Describe()
    {
        if (!Ok)
        {
            return $"这次掷骰没有执行：{Error}";
        }

        var builder = new StringBuilder();
        builder.Append("掷骰 ").Append(Expression).Append("（").Append(ModeText(Mode));

        if (Dc is { } dc)
        {
            builder.Append("，DC ").Append(dc);
        }

        builder.Append("）：共 ").Append(Total).Append('。');

        foreach (var attack in Attacks)
        {
            builder.Append('\n').Append("- ").Append(attack.Label).Append("：d").Append(attack.Sides).Append(' ');

            builder.Append('[').Append(string.Join(", ", attack.Rolled)).Append(']');

            if (attack.Kept.Count != attack.Rolled.Count)
            {
                builder.Append(' ').Append(Mode == DiceMode.Advantage ? "取高" : "取低")
                    .Append(' ').Append(string.Join(", ", attack.Kept));
            }

            if (attack.Modifier != 0)
            {
                builder.Append(' ').Append(attack.Modifier > 0 ? "+" : "-").Append(Math.Abs(attack.Modifier));
            }

            builder.Append(" = ").Append(attack.Total);

            if (attack.Success is { } success)
            {
                builder.Append(success ? "，成功" : "，失败");
            }
        }

        builder.Append('\n').Append("正文里请直接引用这些数字，不要改动。");
        return builder.ToString();
    }

    private static string ModeText(DiceMode mode) => mode switch
    {
        DiceMode.Advantage => "优势",
        DiceMode.Disadvantage => "劣势",
        _ => "正常",
    };
}

/// <summary>
/// 骰子引擎（V2）：支持 <c>NdM±P</c> 与 <c>Nx(NdM+P)</c>（多次攻击），d20 支持优势 / 劣势。
/// <para>
/// 刻意很小：只有"掷"与"读"；命中 / 豁免 / 抗性这些规则判定在 <see cref="MeetingRoom.Core.Rules.D20"/>。
/// <see cref="Roll"/> 接受 <see cref="Random"/> 是为了让测试能用固定种子复算。
/// </para>
/// </summary>
public static partial class Dice
{
    /// <summary>单条表达式里最多几颗骰：防止模型写出 <c>100000d6</c> 把回合卡死。</summary>
    public const int MaxDicePerAttack = 100;

    /// <summary>一次最多几次攻击。</summary>
    public const int MaxAttacks = 20;

    /// <summary>解析并掷骰。表达式看不懂时返回 <see cref="DiceRollResult.Ok"/> = <c>false</c>，不抛异常。</summary>
    public static DiceRollResult Roll(
        string expression,
        string? mode = null,
        int? dc = null,
        Random? random = null) =>
        RollParsed(expression, mode, dc, random, diceMultiplier: 1);

    /// <summary>
    /// 暴击伤害：<b>只把骰子颗数翻倍，固定加值不变</b>（5e 的暴击规则）。
    /// <c>1d8+3</c> 暴击是 <c>2d8+3</c>，不是 <c>(1d8+3)×2</c>。
    /// </summary>
    public static DiceRollResult RollCritical(string expression, Random? random = null) =>
        RollParsed(expression, mode: null, dc: null, random: random, diceMultiplier: 2);

    private static DiceRollResult RollParsed(
        string expression,
        string? mode,
        int? dc,
        Random? random,
        int diceMultiplier)
    {
        var parsedMode = ParseMode(mode);

        if (parsedMode is null)
        {
            return DiceRollResult.Failed(expression ?? string.Empty, "mode 只能是 normal / advantage / disadvantage");
        }

        if (!TryParseExpression(expression, out var attacks, out var error))
        {
            return DiceRollResult.Failed(expression ?? string.Empty, error);
        }

        var rng = random ?? Random.Shared;
        var results = new List<DiceAttack>(attacks.Count);

        for (var i = 0; i < attacks.Count; i++)
        {
            var (count, sides, modifier) = attacks[i];
            results.Add(RollOne(
                attacks.Count == 1 ? "结果" : $"第 {i + 1} 次",
                count * diceMultiplier,
                sides,
                modifier,
                parsedMode.Value,
                dc,
                rng));
        }

        var total = results.Sum(attack => attack.Total);
        var success = dc is null ? (bool?)null : results.All(attack => attack.Success == true);

        return new DiceRollResult(true, expression!.Trim(), parsedMode.Value, null, results, total, dc, success);
    }

    /// <summary>模式归一；认不出的返回 <c>null</c>（由调用方回灌一句"格式不对"）。</summary>
    public static DiceMode? ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return DiceMode.Normal;
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "normal" or "正常" or "普通" => DiceMode.Normal,
            "advantage" or "adv" or "优势" => DiceMode.Advantage,
            "disadvantage" or "dis" or "劣势" => DiceMode.Disadvantage,
            _ => null,
        };
    }

    private static DiceAttack RollOne(
        string label,
        int count,
        int sides,
        int modifier,
        DiceMode mode,
        int? dc,
        Random rng)
    {
        int[] rolled;
        int[] kept;

        // 优势 / 劣势只对 d20 生效：其他骰子（伤害）按正常掷。
        if (sides == 20 && mode != DiceMode.Normal)
        {
            var first = rng.Next(1, 21);
            var second = rng.Next(1, 21);
            rolled = [first, second];
            kept = [mode == DiceMode.Advantage ? Math.Max(first, second) : Math.Min(first, second)];
        }
        else
        {
            rolled = [.. Enumerable.Range(0, count).Select(_ => rng.Next(1, sides + 1))];
            kept = rolled;
        }

        var total = kept.Sum() + modifier;
        return new DiceAttack(label, sides, count, modifier, rolled, kept, total, dc is { } target ? total >= target : null);
    }

    /// <summary>
    /// 表达式解析：<c>NdM±P</c>，或 <c>Nx(NdM+P)</c>（N 次独立的同一条攻击）。
    /// 返回的列表已经按次数展开——掷骰与落盘都不必再关心 <c>x</c> 语法。
    /// </summary>
    private static bool TryParseExpression(
        string? expression,
        out List<(int Count, int Sides, int Modifier)> attacks,
        out string error)
    {
        attacks = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式不能为空（形如 1d20+5、2d6+3 或 3x(1d8+2)）";
            return false;
        }

        var text = expression.Trim();
        var repeat = 1;

        if (RepeatPattern().Match(text) is { Success: true } outer)
        {
            repeat = int.Parse(outer.Groups[1].Value, CultureInfo.InvariantCulture);
            text = outer.Groups[2].Value.Trim();
        }

        if (repeat is < 1 or > MaxAttacks)
        {
            error = $"攻击次数必须在 1..{MaxAttacks} 之间（当前 {repeat}）";
            return false;
        }

        if (TermPattern().Match(text) is not { Success: true } term)
        {
            error = $"看不懂这个表达式：「{expression}」（支持 NdM±P 与 Nx(NdM+P)）";
            return false;
        }

        var count = term.Groups[1].Value.Length == 0
            ? 1
            : int.Parse(term.Groups[1].Value, CultureInfo.InvariantCulture);
        var sides = int.Parse(term.Groups[2].Value, CultureInfo.InvariantCulture);
        var modifier = 0;

        if (term.Groups[3].Success)
        {
            modifier = int.Parse(term.Groups[4].Value, CultureInfo.InvariantCulture)
                * (term.Groups[3].Value == "-" ? -1 : 1);
        }

        if (count is < 1 or > MaxDicePerAttack)
        {
            error = $"骰子颗数必须在 1..{MaxDicePerAttack} 之间（当前 {count}）";
            return false;
        }

        if (sides is < 2 or > 1000)
        {
            error = $"骰子面数必须在 2..1000 之间（当前 {sides}）";
            return false;
        }

        for (var i = 0; i < repeat; i++)
        {
            attacks.Add((count, sides, modifier));
        }

        return true;
    }

    [GeneratedRegex(@"^\s*(\d+)\s*[xX×]\s*\((.*)\)\s*$")]
    private static partial Regex RepeatPattern();

    [GeneratedRegex(@"^\s*(\d*)\s*[dD]\s*(\d+)\s*(?:([+-])\s*(\d+))?\s*$")]
    private static partial Regex TermPattern();
}
