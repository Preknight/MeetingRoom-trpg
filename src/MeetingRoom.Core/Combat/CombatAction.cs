using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>战斗内的一次主动动作（C2 只做攻击与施法；移动/闪避等在 C3）。</summary>
public enum CombatActionKind
{
    Attack,
    CastSpell,
    Move,
    Dash,
    Dodge,
    Disengage,
    Hide,
    Help,

    /// <summary>Ready：存触发条件，DM 判定触发时消耗 reaction 执行（C4）。</summary>
    Ready,
}

/// <summary>
/// 一次攻击 / 施法的请求。<b>数值由调用方给</b>（C1 的延续）：命中加值、目标 AC、伤害类型都由 DM/角色提供；
/// 武器伤害骰与精通从 <see cref="RulesData"/> 查，伤害类型数据包里没有，所以必须显式给。
/// </summary>
public sealed record CombatActionRequest
{
    public required string ActorId { get; init; }

    public required string TargetId { get; init; }

    public CombatActionKind Kind { get; init; } = CombatActionKind.Attack;

    /// <summary>武器 id（<c>Attack</c> 必填），如 <c>Longsword|XPHB</c>。</summary>
    public string? WeaponId { get; init; }

    /// <summary>法术 id（<c>CastSpell</c> 必填），如 <c>Fireball|XPHB</c>。</summary>
    public string? SpellId { get; init; }

    /// <summary>目标 AC（C2 由 DM 给；接 Sheet 后从数值表取）。</summary>
    public int TargetArmorClass { get; init; } = 10;

    /// <summary>命中加值（攻击检定 / 法术攻击）。</summary>
    public int? AttackBonus { get; init; }

    /// <summary>伤害的固定加值（如 Longsword 1d8+3 的 +3）。</summary>
    public int? DamageBonus { get; init; }

    /// <summary>伤害骰（不给就用武器的 <c>dmg1</c> / 法术的首段伤害）。</summary>
    public string? DamageDice { get; init; }

    /// <summary>伤害类型（必填：数据包不含伤害类型）。</summary>
    public string? DamageType { get; init; }

    /// <summary>豁免型法术的 DC（法术 <c>save</c> 非空时必填）。</summary>
    public int? SaveDc { get; init; }

    /// <summary>目标豁免加值（豁免型法术用）。</summary>
    public int? TargetSaveBonus { get; init; }

    /// <summary>精通（不给就用武器卡的 <c>mastery[]</c>）。</summary>
    public IReadOnlyList<string>? Masteries { get; init; }

    /// <summary>
    /// 距离（尺）：移动必填；攻击可选（有地图时由会话层用 <c>VttContext</c> 补，probe 直接给）。
    /// <b>只有一个入口</b>——不搞"脚本一套、地图一套"。
    /// </summary>
    public double? DistanceFeet { get; init; }

    /// <summary>Help 的对象 id。</summary>
    public string? HelpTargetId { get; init; }

    /// <summary>目标是否在 Dodge（由会话层从目标的 Combatant 状态填；攻击时加劣势）。</summary>
    public bool TargetIsDodging { get; init; }

    /// <summary>是否用 Bonus Action 出手（双持 / 2024 Disengage 等）。</summary>
    public bool UseBonusAction { get; init; }

    /// <summary>额外的优势/劣势来源（Help 给的优势、地形等）；与 Dodge 的劣势合并。</summary>
    public IReadOnlyList<AdvantageMode>? Modes { get; init; }

    /// <summary>用几环槽施放（D2）；空 = 法术自身环级。戏法忽略。</summary>
    public int? CastAtLevel { get; init; }

    /// <summary>施法者角色等级（D2，戏法档位用）；空 = 1。</summary>
    public int? CharacterLevel { get; init; }

    /// <summary>Ready 的触发条件（<see cref="CombatActionKind.Ready"/> 用）。</summary>
    public string? ReadyTrigger { get; init; }

    /// <summary>这次移动离开了谁的触及（借机攻击的触发者名单）。</summary>
    public IReadOnlyList<string>? ProvokesOpportunityFrom { get; init; }

    public string? Reason { get; init; }
}

/// <summary>一次攻击 / 施法的结算结果（含拒绝分支）。</summary>
public sealed record CombatActionResult
{
    public required string ActorId { get; init; }

    public required string TargetId { get; init; }

    public CombatActionKind Kind { get; init; }

    public bool Ok { get; init; }

    /// <summary>拒绝原因码（<c>unknown-weapon</c> / <c>missing-damage-type</c> …）。</summary>
    public string? Code { get; init; }

    /// <summary>拒绝原因（能让说书人直接叙述的一句话）。</summary>
    public string? Message { get; init; }

    public string? WeaponId { get; init; }

    public string? SpellId { get; init; }

    /// <summary>伤害表达式（实际用的那条）。</summary>
    public string? DamageDice { get; init; }

    public string? DamageType { get; init; }

    /// <summary>攻击检定（攻击型）或法术攻击检定。</summary>
    public RollEvent? Attack { get; init; }

    /// <summary>目标豁免（豁免型法术）。</summary>
    public RollEvent? Save { get; init; }

    public DamageResult? Damage { get; init; }

    /// <summary>实际生效的伤害（豁免成功时半伤，向下取整）。</summary>
    public int DamageApplied { get; init; }

    /// <summary>实际用的环级（戏法为 0）。</summary>
    public int CastAtLevel { get; init; }

    /// <summary>是否应用了升环 / 戏法档位缩放（D2）。</summary>
    public bool ScalingApplied { get; init; }

    /// <summary>精通解析：未建模的会带 <c>Modelled=false</c>，回执里要写明"这次未生效"。</summary>
    public IReadOnlyList<MasteryResolution> Masteries { get; init; } = [];

    /// <summary>回执文本（说书人照着叙述；拒绝时就是拒绝原因）。</summary>
    public string Describe()
    {
        if (!Ok)
        {
            return string.IsNullOrWhiteSpace(Message) ? $"这次行动没有成立（{Code}）。" : Message;
        }

        var builder = new System.Text.StringBuilder();

        if (Attack is { } attack)
        {
            builder.Append(attack.Success == true ? "命中" : "未命中");
            builder.Append("（d20 ").Append(attack.Natural);
            if (attack.Modifier != 0)
            {
                builder.Append(attack.Modifier > 0 ? "+" : "").Append(attack.Modifier);
            }

            builder.Append(" = ").Append(attack.Total).Append('）');
        }
        else if (Save is { } save)
        {
            builder.Append("目标豁免").Append(save.Success == true ? "成功" : "失败");
            builder.Append("（d20 ").Append(save.Natural).Append(" = ").Append(save.Total).Append('）');
        }

        if (Damage is { } damage)
        {
            builder.Append("；伤害 ").Append(DamageApplied).Append("（").Append(DamageDice).Append(' ').Append(DamageType);

            if (damage.Crit)
            {
                builder.Append("，暴击");
            }

            builder.Append('）');
        }

        foreach (var mastery in Masteries)
        {
            builder.Append('\n').Append("- 精通 ").Append(mastery.Requested).Append("：");

            builder.Append(mastery.Modelled
                ? mastery.Effect
                : mastery.NotModelledReason + "（这次未生效）");
        }

        return builder.ToString();
    }
}
