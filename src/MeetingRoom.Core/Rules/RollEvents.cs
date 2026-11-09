namespace MeetingRoom.Core.Rules;

/// <summary>优势 / 劣势。多个来源会先合并（见 <see cref="D20.Merge"/>）：不叠加，且相互抵消。</summary>
public enum AdvantageMode
{
    Normal,
    Advantage,
    Disadvantage,
}

/// <summary>加值来源（熟练、属性、法术、装备…）：落盘要能看到"这个 +7 是怎么来的"。</summary>
public sealed record RollSource(string Name, int Bonus);

/// <summary>
/// 一次 d20 判定的事件（check / attack / save 共用）：三项判定只差 success 的判定方式。
/// <para>
/// <see cref="Rolled"/> 是掷出的所有 d20（优势/劣势两颗），<see cref="Natural"/> 是最终采用的那颗；
/// <see cref="Crit"/> / <see cref="Fumble"/> 按"采用的那颗"判（20 / 1）。
/// </para>
/// </summary>
public sealed record RollEvent
{
    public required AdvantageMode Mode { get; init; }

    public required IReadOnlyList<int> Rolled { get; init; }

    /// <summary>最终采用的骰面（优势取高、劣势取低）。</summary>
    public required int Natural { get; init; }

    /// <summary>加值合计（= <see cref="Sources"/> 之和）。</summary>
    public required int Modifier { get; init; }

    public required IReadOnlyList<RollSource> Sources { get; init; }

    public required int Total { get; init; }

    public bool? Success { get; init; }

    /// <summary>骰面 20（攻击时必中，伤害时按暴击掷双倍骰）。</summary>
    public bool Crit { get; init; }

    /// <summary>骰面 1（攻击时必失）。</summary>
    public bool Fumble { get; init; }
}

/// <summary>一次伤害投掷里的一段（对应数据包 <c>spells[].damage[]</c> / <c>items[].dmg1</c>）。</summary>
public sealed record DamageRollPart(string Dice, string Type);

/// <summary>目标的伤害防御三项（英文枚举，忽略大小写）。</summary>
public sealed record DamageDefenses
{
    public IReadOnlyList<string> Resistances { get; init; } = [];

    public IReadOnlyList<string> Vulnerabilities { get; init; } = [];

    public IReadOnlyList<string> Immunities { get; init; } = [];

    public static DamageDefenses None { get; } = new();
}

/// <summary>按伤害类型分桶后的结果（抗性/易伤/免疫只在桶内结算，再求和）。</summary>
public sealed record DamageBucket(string Type, int Raw, int Effective, string Applied);

/// <summary>一次伤害结算：分桶明细 + 原始合计 + 结算后合计 + 是否暴击。</summary>
public sealed record DamageResult(
    IReadOnlyList<DamageBucket> Buckets,
    int RawTotal,
    int Total,
    bool Crit);
