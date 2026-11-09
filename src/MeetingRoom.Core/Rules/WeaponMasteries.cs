namespace MeetingRoom.Core.Rules;

/// <summary>已建模的武器精通（V3 只做这四个；其余显式报告"未建模"）。</summary>
public enum WeaponMastery
{
    /// <summary>命中后，下一次对同一目标的攻击有优势。</summary>
    Vex,

    /// <summary>轻型武器可额外攻击一次，不占附赠动作。</summary>
    Nick,

    /// <summary>命中后目标做体质豁免，失败则倒地。</summary>
    Topple,

    /// <summary>命中后，目标下一次攻击有劣势。</summary>
    Sap,
}

/// <summary>一次精通解析的结果：要么已建模（给出效果），要么明确"未建模/未知"——<b>绝不静默当普通攻击</b>。</summary>
public sealed record MasteryResolution
{
    public required string Requested { get; init; }

    public WeaponMastery? Mastery { get; init; }

    public bool Modelled { get; init; }

    /// <summary>已建模时的效果说明（一句话，供说书人引用）。</summary>
    public string Effect { get; init; } = string.Empty;

    /// <summary>未建模或未知时的原因（会被回灌给调用方，所以写成能读懂的话）。</summary>
    public string? NotModelledReason { get; init; }
}

/// <summary>
/// 武器精通（V3 单次判定版）：只建模 <see cref="WeaponMastery"/> 里的四个。
/// <para>
/// 2024 规则里还有 Cleave / Graze / Push / Slow 等，本轮<b>刻意不做</b>——但必须显式返回"未建模"，
/// 否则调用方会把一件带精通加成的武器当成普通攻击结算，结果看起来正常却是错的。
/// </para>
/// </summary>
public static class WeaponMasteries
{
    /// <summary>已建模的精通名（与数据包 <c>items[].mastery[]</c> 的英文枚举一致）。</summary>
    public static IReadOnlyList<string> ModelledNames { get; } = ["Nick", "Sap", "Topple", "Vex"];

    /// <summary>规则里存在、但本轮不建模的精通名：解析时明确报"尚未建模"。</summary>
    public static IReadOnlyList<string> KnownButNotModelled { get; } = ["Cleave", "Graze", "Push", "Slow"];

    /// <summary>解析一个精通名（大小写不敏感）。</summary>
    public static MasteryResolution Resolve(string? name)
    {
        var requested = (name ?? string.Empty).Trim();

        if (requested.Length == 0)
        {
            return new MasteryResolution
            {
                Requested = requested,
                NotModelledReason = "未给精通名。",
            };
        }

        if (TryParse(requested, out var mastery))
        {
            return new MasteryResolution
            {
                Requested = requested,
                Mastery = mastery,
                Modelled = true,
                Effect = EffectOf(mastery),
            };
        }

        var known = KnownButNotModelled.Any(item => string.Equals(item, requested, StringComparison.OrdinalIgnoreCase));

        return new MasteryResolution
        {
            Requested = requested,
            NotModelledReason = known
                ? $"「{requested}」尚未建模（V3 只做了 Vex / Nick / Topple / Sap），不要按普通攻击处理。"
                : $"未知精通：「{requested}」。",
        };
    }

    /// <summary>一次解析多个（武器卡上的 <c>mastery[]</c>）。</summary>
    public static IReadOnlyList<MasteryResolution> ResolveAll(IEnumerable<string>? names) =>
        names is null ? [] : names.Select(Resolve).ToArray();

    /// <summary>Topple 的豁免 DC：8 + 熟练加值 + 力量或敏捷调整值。</summary>
    public static int ToppleDc(int proficiencyBonus, int strengthOrDexterityModifier) =>
        8 + proficiencyBonus + strengthOrDexterityModifier;

    private static bool TryParse(string name, out WeaponMastery mastery)
    {
        foreach (var candidate in Enum.GetValues<WeaponMastery>())
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                mastery = candidate;
                return true;
            }
        }

        mastery = default;
        return false;
    }

    private static string EffectOf(WeaponMastery mastery) => mastery switch
    {
        WeaponMastery.Vex => "命中后，下一次对同一目标的攻击有优势。",
        WeaponMastery.Nick => "轻型武器可额外攻击一次，不占附赠动作。",
        WeaponMastery.Topple => "命中后目标做体质豁免（DC 8 + 熟练 + 力量或敏捷调整值），失败则倒地。",
        WeaponMastery.Sap => "命中后，目标下一次攻击有劣势。",
        _ => string.Empty,
    };
}
