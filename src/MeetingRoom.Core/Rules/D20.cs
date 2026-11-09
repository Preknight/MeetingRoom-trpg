// 伤害投掷复用 V2 的骰子引擎（Dice 在 Core.Roleplay，同程序集；不复制一份解析逻辑）。
using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Core.Rules;

/// <summary>
/// 四个判定纯函数（V3 单次判定版）：<see cref="RollCheck"/> / <see cref="RollAttack"/> /
/// <see cref="RollSave"/> / <see cref="RollDamage"/>。
/// <para>
/// 全部<b>无副作用、随机源可注入</b>（传固定种子的 <see cref="Random"/> 即可复现）；
/// 结果只描述"这一次判定发生了什么"，不改进程内状态——落盘与广播是调用方的事。
/// </para>
/// <para>
/// 优势/劣势<b>不叠加、相互抵消</b>（5e 规则）：多个优势来源仍只掷两颗；优势与劣势同时存在 → 正常。
/// </para>
/// </summary>
public static class D20
{
    /// <summary>合并多个优势/劣势来源：只要同时有优势与劣势就相互抵消；同类来源不叠加。</summary>
    public static AdvantageMode Merge(IEnumerable<AdvantageMode>? modes)
    {
        if (modes is null)
        {
            return AdvantageMode.Normal;
        }

        var advantage = false;
        var disadvantage = false;

        foreach (var mode in modes)
        {
            switch (mode)
            {
                case AdvantageMode.Advantage:
                    advantage = true;
                    break;
                case AdvantageMode.Disadvantage:
                    disadvantage = true;
                    break;
            }
        }

        return (advantage, disadvantage) switch
        {
            (true, true) => AdvantageMode.Normal,
            (true, false) => AdvantageMode.Advantage,
            (false, true) => AdvantageMode.Disadvantage,
            _ => AdvantageMode.Normal,
        };
    }

    /// <summary>属性/技能检定：total ≥ DC 即成功。</summary>
    public static RollEvent RollCheck(
        int dc,
        IReadOnlyList<RollSource> sources,
        IEnumerable<AdvantageMode>? modes = null,
        Random? random = null) =>
        RollD20(dc, sources, modes, random);

    /// <summary>豁免：与检定同构（total ≥ DC 即成功）；单独立一个方法是为了落盘语义清晰。</summary>
    public static RollEvent RollSave(
        int dc,
        IReadOnlyList<RollSource> sources,
        IEnumerable<AdvantageMode>? modes = null,
        Random? random = null) =>
        RollD20(dc, sources, modes, random);

    /// <summary>
    /// 攻击：total ≥ AC 即命中，但<b>骰面 20 必中、骰面 1 必失</b>（无论加值）。
    /// <see cref="RollEvent.Crit"/> 为真时，调用方应使用 <see cref="RollDamage"/> 的 <c>crit: true</c>。
    /// </summary>
    public static RollEvent RollAttack(
        int armorClass,
        IReadOnlyList<RollSource> sources,
        IEnumerable<AdvantageMode>? modes = null,
        Random? random = null)
    {
        var roll = RollD20(armorClass, sources, modes, random);

        return roll with
        {
            Success = roll.Crit || (!roll.Fumble && roll.Total >= armorClass),
        };
    }

    /// <summary>
    /// 伤害：按伤害类型<b>先分桶求和，再在桶内结算抗性 / 易伤 / 免疫</b>。
    /// 顺序：免疫 → 0；否则先易伤（×2）再抗性（÷2，向下取整）——两者都有时回到原值。
    /// <paramref name="crit"/> 为真时<b>骰子颗数翻倍</b>（不是结果 ×2），固定加值不变。
    /// </summary>
    public static DamageResult RollDamage(
        IReadOnlyList<DamageRollPart> parts,
        DamageDefenses? defenses = null,
        bool crit = false,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count == 0)
        {
            throw new ArgumentException("至少要给出一段伤害投掷。", nameof(parts));
        }

        var rng = random ?? Random.Shared;
        var raw = new List<(string Type, int Value)>(parts.Count);

        foreach (var part in parts)
        {
            var rolled = crit
                ? Dice.RollCritical(part.Dice, random: rng)
                : Dice.Roll(part.Dice, random: rng);

            if (!rolled.Ok)
            {
                throw new ArgumentException($"伤害表达式看不懂：{part.Dice}（{rolled.Error}）", nameof(parts));
            }

            raw.Add((NormalizeType(part.Type), rolled.Total));
        }

        var buckets = new List<DamageBucket>();

        foreach (var group in raw.GroupBy(entry => entry.Type, StringComparer.Ordinal))
        {
            var total = group.Sum(entry => entry.Value);
            var (effective, applied) = ApplyDefenses(total, group.Key, defenses);
            buckets.Add(new DamageBucket(group.Key, total, effective, applied));
        }

        return new DamageResult(
            buckets,
            buckets.Sum(bucket => bucket.Raw),
            buckets.Sum(bucket => bucket.Effective),
            crit);
    }

    private static RollEvent RollD20(
        int target,
        IReadOnlyList<RollSource> sources,
        IEnumerable<AdvantageMode>? modes,
        Random? random)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var mode = Merge(modes);
        var rng = random ?? Random.Shared;

        var first = rng.Next(1, 21);
        var rolled = new List<int>(2) { first };
        var natural = first;

        if (mode != AdvantageMode.Normal)
        {
            var second = rng.Next(1, 21);
            rolled.Add(second);
            natural = mode == AdvantageMode.Advantage ? Math.Max(first, second) : Math.Min(first, second);
        }

        var frozen = sources.ToArray();
        var modifier = frozen.Sum(source => source.Bonus);
        var total = natural + modifier;

        return new RollEvent
        {
            Mode = mode,
            Rolled = rolled,
            Natural = natural,
            Modifier = modifier,
            Sources = frozen,
            Total = total,
            Success = total >= target,
            Crit = natural == 20,
            Fumble = natural == 1,
        };
    }

    private static (int Effective, string Applied) ApplyDefenses(
        int raw,
        string type,
        DamageDefenses? defenses)
    {
        if (defenses is null)
        {
            return (raw, "normal");
        }

        if (Contains(defenses.Immunities, type))
        {
            return (0, "immune");
        }

        var vulnerable = Contains(defenses.Vulnerabilities, type);
        var resistant = Contains(defenses.Resistances, type);

        var value = vulnerable ? raw * 2 : raw;

        if (resistant)
        {
            value = (int)Math.Floor(value / 2.0);
        }

        var applied = (vulnerable, resistant) switch
        {
            (true, true) => "vulnerable+resisted",
            (true, false) => "vulnerable",
            (false, true) => "resisted",
            _ => "normal",
        };

        return (value, applied);
    }

    private static bool Contains(IReadOnlyList<string> list, string type) =>
        list.Any(item => string.Equals(item?.Trim(), type, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim().ToLowerInvariant();
}
