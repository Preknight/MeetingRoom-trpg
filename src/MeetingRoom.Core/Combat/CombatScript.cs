using System.Text.Json;

using MeetingRoom.Core.Rules;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Combat;

/// <summary>
/// 脚本化遭遇（<c>--probe-combat &lt;script.json&gt;</c> 与测试共用）：只描述"谁参战、什么种子、走几轮"。
/// <para>
/// 刻意很小：probe 只验证状态机（先攻顺序 / 换手 / 轮数），不模拟攻击与伤害（那是 C2/C3 的事）。
/// </para>
/// </summary>
public sealed record CombatScript
{
    public int Seed { get; init; } = 20261026;

    /// <summary>走几整轮（每人各行动一次算一轮）。</summary>
    public int Rounds { get; init; } = 3;

    public IReadOnlyList<CombatantSetup> Combatants { get; init; } = [];

    /// <summary>脚本里的攻击 / 施法（C2）：probe 依次结算并打印。</summary>
    public IReadOnlyList<CombatActionRequest> Actions { get; init; } = [];

    /// <summary>脚本里的叙事检定（C2）：probe 依次结算并打印 DM 面与角色面。</summary>
    public IReadOnlyList<CheckRequest> Checks { get; init; } = [];

    /// <summary>脚本里的数值表（C3a）：probe 用它扣血，验证"打中 → 扣血 → 倒地"整条链。</summary>
    public IReadOnlyDictionary<string, Sheet>? Sheets { get; init; }

    /// <summary>
    /// 可编排的步骤（C3b）：支持 <c>advanceTurn</c> 换手、逐条动作、以及"预期被拒"。
    /// 与 <see cref="Actions"/> 并存：老脚本用 Actions，新脚本用 Steps。
    /// </summary>
    public IReadOnlyList<ScriptAction>? Steps { get; init; }


    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // 脚本是我们自己写的：字段名拼错必须立刻报错，不能静默当没写。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>解析脚本；格式不对抛 <see cref="JsonException"/>（由调用方转成可读错误）。</summary>
    public static CombatScript Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize<CombatScript>(json, Options)
            ?? throw new JsonException("遭遇脚本是空的。");
    }

    public static CombatScript Load(string path) => Parse(File.ReadAllText(path));
}

/// <summary>脚本里的一步（C3b）：<c>advanceTurn</c> 或一条动作；<c>expectRejected</c> 表示"这一步就该被拒"。</summary>
public sealed record ScriptAction
{
    public bool AdvanceTurn { get; init; }

    public string? ActorId { get; init; }

    public string? TargetId { get; init; }

    public string? Kind { get; init; }

    public string? WeaponId { get; init; }

    public string? SpellId { get; init; }

    public int? TargetArmorClass { get; init; }

    public int? AttackBonus { get; init; }

    public int? DamageBonus { get; init; }

    public string? DamageDice { get; init; }

    public string? DamageType { get; init; }

    public int? SaveDc { get; init; }

    public int? TargetSaveBonus { get; init; }

    public double? DistanceFeet { get; init; }

    public string? HelpTargetId { get; init; }

    public bool UseBonus { get; init; }

    public IReadOnlyList<string>? ProvokesOpportunityFrom { get; init; }

    public string? ReadyTrigger { get; init; }

    /// <summary>用几环槽施放（D2）。</summary>
    public int? CastAtLevel { get; init; }

    /// <summary>施法者角色等级（D2，戏法档位用）。</summary>
    public int? CharacterLevel { get; init; }

    /// <summary>长休（D2 probe 用）：槽位清零、HP 回满、死亡豁免清零、状态清空。</summary>
    public bool LongRest { get; init; }

    /// <summary>短休（D2 probe 用）：不恢复法术位。</summary>
    public bool ShortRest { get; init; }

    /// <summary>DM 覆盖（D3 probe 用）。</summary>
    public ScriptOverride? Override { get; init; }

    /// <summary>治疗量（D1 probe 用）：≥1 且回到正值 → Alive 并清空死亡豁免。</summary>
    public int? Heal { get; init; }

    /// <summary>医学稳定（D1 probe 用）：濒死 → 已稳定。</summary>
    public bool Stabilize { get; init; }

    public bool ExpectRejected { get; init; }

    public string? Reason { get; init; }

    public CombatActionRequest ToRequest() => new()
    {
        ActorId = ActorId ?? string.Empty,
        TargetId = TargetId ?? string.Empty,
        Kind = ParseKind(Kind),
        WeaponId = WeaponId,
        SpellId = SpellId,
        TargetArmorClass = TargetArmorClass ?? 10,
        AttackBonus = AttackBonus,
        DamageBonus = DamageBonus,
        DamageDice = DamageDice,
        DamageType = DamageType,
        SaveDc = SaveDc,
        TargetSaveBonus = TargetSaveBonus,
        DistanceFeet = DistanceFeet,
        HelpTargetId = HelpTargetId,
        UseBonusAction = UseBonus,
        CastAtLevel = CastAtLevel,
        CharacterLevel = CharacterLevel,
        ProvokesOpportunityFrom = ProvokesOpportunityFrom,
        ReadyTrigger = ReadyTrigger,
        Reason = Reason,
    };

    private static CombatActionKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "cast_spell" or "cast" or "spell" => CombatActionKind.CastSpell,
        "move" => CombatActionKind.Move,
        "dash" => CombatActionKind.Dash,
        "dodge" => CombatActionKind.Dodge,
        "disengage" => CombatActionKind.Disengage,
        "hide" => CombatActionKind.Hide,
        "help" => CombatActionKind.Help,
        _ => CombatActionKind.Attack,
    };
}

/// <summary>probe 里的一次 DM 覆盖（D3）：<c>{"override": {"field": "hp", "value": 0}}</c>。</summary>
public sealed record ScriptOverride
{
    public required string Field { get; init; }

    public System.Text.Json.JsonElement? Value { get; init; }

    public string? Reason { get; init; }

    public string? CheckId { get; init; }
}
