using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>
/// 攻击 / 施法的结算（C2）：武器与法术资料从 <see cref="RulesData"/> 查，掷骰走 <see cref="D20"/>。
/// <para>
/// <b>不缓存 HP</b>：结果只给"命中与否 + 伤害多少"，扣血由接上 <c>Sheet</c> 的那一层负责（C2 不发明 HP 存储）。
/// </para>
/// </summary>
public static class CombatEngine
{
    public static CombatActionResult Resolve(
        RulesData rules,
        CombatActionRequest request,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ActorId) || string.IsNullOrWhiteSpace(request.TargetId))
        {
            return Reject(request, "invalid-target", "攻击者与目标都要给。");
        }

        return request.Kind == CombatActionKind.CastSpell
            ? ResolveSpell(rules, request, random)
            : ResolveAttack(rules, request, random);
    }

    private static CombatActionResult ResolveAttack(RulesData rules, CombatActionRequest request, Random? random)
    {
        if (string.IsNullOrWhiteSpace(request.WeaponId))
        {
            return Reject(request, "missing-weapon", "近战/远程攻击要给 weaponId（数据包里的物品 id）。");
        }

        var weapon = rules.FindItem(request.WeaponId);

        if (weapon is null)
        {
            return Reject(request, "unknown-weapon", $"规则包里没有「{request.WeaponId}」这件武器。");
        }

        if (!string.Equals(weapon.Kind, "weapon", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(request, "not-a-weapon", $"「{request.WeaponId}」不是武器（kind = {weapon.Kind}）。");
        }

        var dice = string.IsNullOrWhiteSpace(request.DamageDice) ? weapon.Dmg1 : request.DamageDice;
        var (normalizedDice, problem) = NormalizeDamageDice(dice, request.DamageBonus);

        if (problem is not null)
        {
            return Reject(request, "no-damage", problem);
        }

        if (string.IsNullOrWhiteSpace(request.DamageType))
        {
            return Reject(request, "missing-damage-type", "伤害类型必须由调用方给出（规则包不含伤害类型）。");
        }

        // 射程（C4）：远程看 range，"超过长射程"直接拒绝、"超过常规射程"劣势；近战看 reach。
        var range = CheckRange(weapon, request.DistanceFeet);

        if (range.Reject is { } rejection)
        {
            return Reject(request, "out-of-range", rejection);
        }

        var attack = RollAttackD20(request, range.Disadvantage, random);

        var masteries = ResolveMasteries(request.Masteries ?? weapon.Mastery);

        if (attack.Success != true)
        {
            return Ok(request, attack: attack, save: null, damage: null, applied: 0, dice: normalizedDice,
                type: request.DamageType, masteries: masteries);
        }

        var damage = D20.RollDamage(
            [new DamageRollPart(normalizedDice!, request.DamageType!)],
            defenses: null,
            crit: attack.Crit,
            random: random);

        return Ok(request, attack: attack, save: null, damage: damage, applied: damage.Total, dice: normalizedDice,
            type: request.DamageType, masteries: masteries);
    }

    /// <summary>
    /// 施法校验的前两关（D2，<b>不掷骰、不改状态</b>）：法术存在（<c>unknown-spell</c>）、
    /// 环级够（<c>slot-too-low</c>）。槽位是否可用 / 是不是施法者由 <see cref="SheetStore.CanSpendSlot"/> 判。
    /// </summary>
    public static (SpellDefinition? Spell, string? Code, string? Message) ValidateSpell(
        RulesData rules,
        CombatActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SpellId))
        {
            return (null, "missing-spell", "施法要给 spellId（数据包里的法术 id）。");
        }

        var spell = rules.FindSpell(request.SpellId);

        if (spell is null)
        {
            return (null, "unknown-spell", $"规则包里没有「{request.SpellId}」这个法术。");
        }

        var castAtLevel = request.CastAtLevel ?? spell.Level;

        if (castAtLevel < spell.Level)
        {
            return (null, "slot-too-low", $"{spell.Name} 是 {spell.Level} 环法术，不能用 {castAtLevel} 环槽施放。");
        }

        return (spell, null, null);
    }

    private static CombatActionResult ResolveSpell(RulesData rules, CombatActionRequest request, Random? random)
    {
        if (string.IsNullOrWhiteSpace(request.SpellId))
        {
            return Reject(request, "missing-spell", "施法要给 spellId（数据包里的法术 id）。");
        }

        var spell = rules.FindSpell(request.SpellId);

        if (spell is null)
        {
            return Reject(request, "unknown-spell", $"规则包里没有「{request.SpellId}」这个法术。");
        }

        var castAtLevel = request.CastAtLevel ?? spell.Level;

        if (castAtLevel < spell.Level)
        {
            return Reject(request, "slot-too-low", $"{spell.Name} 是 {spell.Level} 环法术，不能用 {castAtLevel} 环槽施放。");
        }

        var first = spell.Damage.FirstOrDefault();
        var (scaledDice, scalingApplied) = SpellDamageDice(spell, castAtLevel, request.CharacterLevel ?? 1);
        var dice = string.IsNullOrWhiteSpace(request.DamageDice) ? scaledDice : request.DamageDice;
        var type = string.IsNullOrWhiteSpace(request.DamageType) ? first?.Type : request.DamageType;
        var (normalizedDice, problem) = NormalizeDamageDice(dice, request.DamageBonus);

        // 没有伤害的法术（治疗 / 控制）：只记录"施放"，不掷伤害。
        if (problem is not null)
        {
            return Ok(request, attack: null, save: null, damage: null, applied: 0, dice: null, type: null,
                masteries: [], castAtLevel: castAtLevel, scalingApplied: scalingApplied);
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return Reject(request, "missing-damage-type", "这个法术有伤害但类型为空——请显式给 damageType。");
        }

        // 豁免型：目标掷豁免，成功半伤（向下取整）。
        if (!string.IsNullOrWhiteSpace(spell.Save))
        {
            if (request.SaveDc is not { } dc)
            {
                return Reject(request, "missing-save-dc", $"「{spell.Name}」是豁免型法术，要给豁免 DC。");
            }

            var bonus = request.TargetSaveBonus ?? 0;
            IReadOnlyList<RollSource> saveSources = bonus == 0 ? [] : [new RollSource("豁免加值", bonus)];
            var save = D20.RollSave(dc, saveSources, modes: null, random: random);

            var damage = D20.RollDamage(
                [new DamageRollPart(normalizedDice!, type)],
                defenses: null,
                crit: false,
                random: random);

            var applied = save.Success == true ? damage.Total / 2 : damage.Total;

            return Ok(request, attack: null, save: save, damage: damage, applied: applied, dice: normalizedDice,
                type: type, masteries: [], castAtLevel: castAtLevel, scalingApplied: scalingApplied);
        }

        // 攻击型法术：与武器攻击同构（法术攻击检定）。
        var attack = RollAttackD20(request, disadvantage: false, random);

        if (attack.Success != true)
        {
            return Ok(request, attack: attack, save: null, damage: null, applied: 0, dice: normalizedDice,
                type: type, masteries: [], castAtLevel: castAtLevel, scalingApplied: scalingApplied);
        }

        var hit = D20.RollDamage(
            [new DamageRollPart(normalizedDice!, type)],
            defenses: null,
            crit: attack.Crit,
            random: random);

        return Ok(request, attack: attack, save: null, damage: hit, applied: hit.Total, dice: normalizedDice,
            type: type, masteries: [], castAtLevel: castAtLevel, scalingApplied: scalingApplied);
    }

    /// <summary>
    /// 升环伤害（D2）：<c>variants</c>（戏法档，按角色等级选）优先，其次 <c>perLevel</c>（base + 每环一段）；
    /// 都没有就退回基础伤害并回报 <c>scalingApplied = false</c>。
    /// </summary>
    private static (string? Dice, bool ScalingApplied) SpellDamageDice(
        SpellDefinition spell,
        int castAtLevel,
        int characterLevel)
    {
        var baseDice = spell.Damage.FirstOrDefault()?.Dice;

        if (string.IsNullOrWhiteSpace(baseDice))
        {
            return (baseDice, false);
        }

        foreach (var scaling in spell.Scaling)
        {
            if (scaling.Variants.Count > 0)
            {
                var index = Math.Min(CantripTierIndex(characterLevel), scaling.Variants.Count - 1);
                return (scaling.Variants[index], true);
            }

            if (!string.IsNullOrWhiteSpace(scaling.PerLevel) && castAtLevel > spell.Level)
            {
                var steps = castAtLevel - spell.Level;
                var basePart = string.IsNullOrWhiteSpace(scaling.Base) ? baseDice : scaling.Base;
                return (MergeDice(basePart, scaling.PerLevel, steps), true);
            }
        }

        return (baseDice, false);
    }

    /// <summary>戏法档位：1–4 → 0、5–10 → 1、11–16 → 2、17+ → 3（映射放代码，不塞数据包）。</summary>
    private static int CantripTierIndex(int characterLevel) => characterLevel switch
    {
        >= 17 => 3,
        >= 11 => 2,
        >= 5 => 1,
        _ => 0,
    };

    /// <summary>合并同类骰（<c>8d6</c> + 1 段 <c>1d6</c> → <c>9d6</c>）；面数不同或解析不了就退回基础伤害，不猜。</summary>
    private static string MergeDice(string baseDice, string perLevelDice, int steps)
    {
        if (!TryParseSimpleDice(baseDice, out var baseCount, out var baseSides)
            || !TryParseSimpleDice(perLevelDice, out var stepCount, out var stepSides)
            || baseSides != stepSides)
        {
            return baseDice;
        }

        return $"{baseCount + (stepCount * steps)}d{baseSides}";
    }

    private static bool TryParseSimpleDice(string? text, out int count, out int sides)
    {
        count = 0;
        sides = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            text.Trim(), @"^(\d*)d(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return false;
        }

        count = match.Groups[1].Value.Length == 0 ? 1 : int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        sides = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 射程校验（C4）：远程武器的 <c>range</c> 优先（有 range 就不看 reach）；
    /// 超过长射程拒绝、超过常规射程给劣势；近战用 reach（默认 5）。
    /// 距离未知（没有地图、调用方也没给）时<b>不校验</b>——不猜。
    /// </summary>
    private static (string? Reject, bool Disadvantage) CheckRange(ItemDefinition weapon, double? distanceFeet)
    {
        if (distanceFeet is not { } distance)
        {
            return (null, false);
        }

        if (WeaponRange.TryParse(weapon.Range, out var normal, out var longRange))
        {
            if (distance > longRange)
            {
                return ($"目标在 {distance:0.#} 尺外，{weapon.Name}最远只能打 {longRange} 尺。", false);
            }

            return (null, distance > normal);
        }

        var reach = WeaponRange.ReachOf(weapon);

        return distance > reach
            ? ($"目标在 {distance:0.#} 尺外，{weapon.Name}只能打到 {reach} 尺。", false)
            : (null, false);
    }

    private static RollEvent RollAttackD20(CombatActionRequest request, bool disadvantage, Random? random)
    {
        var bonus = request.AttackBonus ?? 0;
        IReadOnlyList<RollSource> sources = bonus == 0 ? [] : [new RollSource("命中加值", bonus)];

        // 合并来源：请求自带（Help 等）+ 目标 Dodge + 远程超常规射程。
        var modes = new List<AdvantageMode>();

        if (request.Modes is { } extra)
        {
            modes.AddRange(extra);
        }

        if (request.TargetIsDodging)
        {
            modes.Add(AdvantageMode.Disadvantage);
        }

        if (disadvantage)
        {
            modes.Add(AdvantageMode.Disadvantage);
        }

        return D20.RollAttack(request.TargetArmorClass, sources, modes, random);
    }

    /// <summary>
    /// 伤害骰归一：武器只给 <c>1d8</c>，固定加值在 <see cref="CombatActionRequest.DamageBonus"/> 里——
    /// 两者拼成 <c>1d8+3</c>；拼不出来（空 / 无法解析）时返回问题描述，由调用方决定拒绝还是当"无伤害"。
    /// </summary>
    private static (string? Dice, string? Problem) NormalizeDamageDice(string? dice, int? bonus)
    {
        if (string.IsNullOrWhiteSpace(dice))
        {
            return (null, "这件武器 / 法术没有伤害骰；要结算伤害请显式给 damageDice。");
        }

        var text = dice.Trim();

        if (bonus is not { } modifier || modifier == 0)
        {
            return (text, null);
        }

        // 已有 +N/-N 的话以显式加值为准，避免出现 1d8+2+3 这种写法。
        if (text.Contains('+', StringComparison.Ordinal) || text.Contains('-', StringComparison.Ordinal))
        {
            return (text, null);
        }

        return ($"{text}{(modifier > 0 ? "+" : "")}{modifier}", null);
    }

    private static IReadOnlyList<MasteryResolution> ResolveMasteries(IEnumerable<string>? names) =>
        WeaponMasteries.ResolveAll(names);

    private static CombatActionResult Ok(
        CombatActionRequest request,
        RollEvent? attack,
        RollEvent? save,
        DamageResult? damage,
        int applied,
        string? dice,
        string? type,
        IReadOnlyList<MasteryResolution> masteries,
        int castAtLevel = 0,
        bool scalingApplied = false) => new()
    {
        ActorId = request.ActorId,
        TargetId = request.TargetId,
        Kind = request.Kind,
        Ok = true,
        WeaponId = request.WeaponId,
        SpellId = request.SpellId,
        DamageDice = dice,
        DamageType = type,
        Attack = attack,
        Save = save,
        Damage = damage,
        DamageApplied = applied,
        CastAtLevel = castAtLevel,
        ScalingApplied = scalingApplied,
        Masteries = masteries,
    };

    private static CombatActionResult Reject(CombatActionRequest request, string code, string message) => new()
    {
        ActorId = request.ActorId,
        TargetId = request.TargetId,
        Kind = request.Kind,
        Ok = false,
        Code = code,
        Message = message,
        WeaponId = request.WeaponId,
        SpellId = request.SpellId,
    };
}
