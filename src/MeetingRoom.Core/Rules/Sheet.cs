namespace MeetingRoom.Core.Rules;

/// <summary>D&amp;D 5e 的六属性（英文枚举：数据包里的枚举一律英文，中文只做显示）。</summary>
public enum Ability
{
    Strength,
    Dexterity,
    Constitution,
    Intelligence,
    Wisdom,
    Charisma,
}

/// <summary>
/// 一次武器攻击在角色卡上的最小形态（V3 单次判定版：不做战斗托管，所以只存"打出去要用的数"）。
/// <para><see cref="AttackBonus"/> 与 <see cref="SaveDC"/> <b>至少给一个</b>——两者都没有就是一张没填完的卡。</para>
/// </summary>
public sealed record WeaponAttack
{
    /// <summary>对应数据包 <c>items[].id</c>（<c>name|source</c>）。</summary>
    public required string WeaponId { get; init; }

    /// <summary>命中加值（攻击检定用）。</summary>
    public int? AttackBonus { get; init; }

    /// <summary>豁免 DC（有些武器/效果是让目标豁免而不是打 AC）。</summary>
    public int? SaveDC { get; init; }

    /// <summary>伤害表达式（<c>NdM±P</c>）；空串 = 这件武器不掷伤害（纯控制）。</summary>
    public string DamageDice { get; init; } = string.Empty;

    /// <summary>伤害类型（英文枚举：slashing / fire …）。</summary>
    public string DamageType { get; init; } = string.Empty;

    /// <summary>精通名（Sap / Vex / Topple / Nick …）；未建模的由 <see cref="WeaponMasteries"/> 显式报告。</summary>
    public IReadOnlyList<string> Masteries { get; init; } = [];
}

/// <summary>专注（同时只能有一个）：记住专注在哪个法术上，以及维持它要过的 DC（若已算出）。</summary>
public sealed record Concentration(string SpellId, int? SaveDc);

/// <summary>
/// 角色的生死状态（D1 四态）。<c>Unconscious</c> 不再是一个状态：它由 <see cref="Dying"/> /
/// <see cref="Stable"/> 派生，只用于对外文案。
/// </summary>
public enum VitalStatus
{
    /// <summary>HP &gt; 0。</summary>
    Alive,

    /// <summary>HP = 0 且未稳定：每回合开始掷死亡豁免。</summary>
    Dying,

    /// <summary>HP = 0 但已稳定：不再掷死亡豁免。</summary>
    Stable,

    Dead,
}

/// <summary>死亡豁免：三成功即稳定，三失败即死亡。</summary>
public sealed record DeathSaves(int Successes, int Failures);

/// <summary>某一环的法术位：<see cref="Max"/> 是上限，<see cref="Used"/> 是已用（D2）。</summary>
public sealed record SpellSlot(int Max, int Used)
{
    public int Remaining => Math.Max(0, Max - Used);
}

/// <summary>
/// 角色数值表（V3 单次判定版）：HP / 临时 HP / AC / 六属性 / 熟练 / 豁免 / 技能 / 法术位 /
/// 武器攻击 / 状态 / 专注 / 死亡豁免。
/// <para>
/// <b>刻意全部可空</b>：空 = 「未填」，由 <see cref="SheetValidator"/> 报出来，而不是悄悄填 0 或 10。
/// 一张没填完的卡算出错误数字，比拒绝服务更糟。
/// </para>
/// </summary>
public sealed record Sheet
{
    public required string CharacterId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int? MaxHp { get; init; }

    public int? CurrentHp { get; init; }

    /// <summary>临时 HP：可空表示未填；0 是合法值。</summary>
    public int? TempHp { get; init; }

    public int? ArmorClass { get; init; }

    /// <summary>六属性必须齐全（缺哪项报哪项）。</summary>
    public IReadOnlyDictionary<Ability, int>? Abilities { get; init; }

    public int? ProficiencyBonus { get; init; }

    public IReadOnlyList<Ability> SaveProficiencies { get; init; } = [];

    /// <summary>技能 → 加值（如 <c>{"stealth": 5}</c>）。</summary>
    public IReadOnlyDictionary<string, int> Skills { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>法术位：环级（1–9）→ 上限与已用（D2）。</summary>
    public IReadOnlyDictionary<int, SpellSlot> SpellSlots { get; init; } =
        new Dictionary<int, SpellSlot>();

    public IReadOnlyList<WeaponAttack> Attacks { get; init; } = [];

    /// <summary>当前状态（英文枚举或中文名都行；这里只保证非空）。</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];

    public Concentration? Concentration { get; init; }

    public DeathSaves? DeathSaves { get; init; }

    /// <summary>生死状态：扣到 0 HP 变昏迷，单次溢出 ≥ 最大 HP 直接死亡（<see cref="SheetStore"/> 维护）。</summary>
    public VitalStatus Status { get; init; } = VitalStatus.Alive;
}
