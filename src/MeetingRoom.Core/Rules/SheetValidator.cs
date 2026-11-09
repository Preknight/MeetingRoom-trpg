namespace MeetingRoom.Core.Rules;

/// <summary>
/// Sheet 的合法性校验（V3 单次判定版）：<b>只判断"能不能用"，不补默认值</b>。
/// <para>
/// 缺字段一律报「未填：xxx」——模型或导入脚本漏了字段时，宁可拒绝结算，也不要拿 0 / 10 顶上去
/// 算出一个看起来正常的错数。空集合（技能 / 法术位 / 攻击 / 状态）不算"缺字段"，是"没有条目"。
/// </para>
/// </summary>
public static class SheetValidator
{
    public const int MinAbilityScore = 1;
    public const int MaxAbilityScore = 30;

    /// <summary>返回全部问题（一次报完，便于一轮改好）；空列表表示这张卡可用。</summary>
    public static IReadOnlyList<string> Validate(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var errors = new List<string>();

        ValidateVitals(sheet, errors);
        ValidateAbilities(sheet, errors);
        ValidateSkills(sheet, errors);
        ValidateSpellSlots(sheet, errors);
        ValidateAttacks(sheet, errors);
        ValidateConditions(sheet, errors);
        ValidateConcentration(sheet, errors);
        ValidateDeathSaves(sheet, errors);

        return errors;
    }

    private static void ValidateVitals(Sheet sheet, List<string> errors)
    {
        RequireFilled(errors, sheet.MaxHp, "maxHp");
        RequireFilled(errors, sheet.CurrentHp, "currentHp");
        RequireFilled(errors, sheet.TempHp, "tempHp");
        RequireFilled(errors, sheet.ArmorClass, "armorClass");
        RequireFilled(errors, sheet.ProficiencyBonus, "proficiencyBonus");

        if (sheet.MaxHp is { } maxHp && maxHp <= 0)
        {
            errors.Add("maxHp 必须大于 0");
        }

        if (sheet.MaxHp is { } max && sheet.CurrentHp is { } current && (current < 0 || current > max))
        {
            errors.Add($"currentHp 必须在 0..{max} 之间");
        }

        if (sheet.TempHp is { } temp && temp < 0)
        {
            errors.Add("tempHp 不能为负");
        }

        if (sheet.ArmorClass is { } ac && ac <= 0)
        {
            errors.Add("armorClass 必须大于 0");
        }

        if (sheet.ProficiencyBonus is { } proficiency && proficiency is < 1 or > 10)
        {
            errors.Add("proficiencyBonus 必须在 1..10 之间");
        }
    }

    private static void ValidateAbilities(Sheet sheet, List<string> errors)
    {
        if (sheet.Abilities is null)
        {
            errors.Add("未填：abilities");
            return;
        }

        foreach (var ability in Enum.GetValues<Ability>())
        {
            if (!sheet.Abilities.TryGetValue(ability, out var score))
            {
                errors.Add($"未填：abilities.{Wire(ability)}");
                continue;
            }

            if (score is < MinAbilityScore or > MaxAbilityScore)
            {
                errors.Add($"abilities.{Wire(ability)} 必须在 {MinAbilityScore}..{MaxAbilityScore} 之间（当前 {score}）");
            }
        }
    }

    private static void ValidateSkills(Sheet sheet, List<string> errors)
    {
        foreach (var (skill, _) in sheet.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill))
            {
                errors.Add("技能名不能为空");
            }
        }
    }

    private static void ValidateSpellSlots(Sheet sheet, List<string> errors)
    {
        foreach (var (level, slot) in sheet.SpellSlots)
        {
            if (level is < 1 or > 9)
            {
                errors.Add($"法术位环级必须在 1..9 之间（当前 {level}）");
            }

            if (slot.Max < 0)
            {
                errors.Add($"第 {level} 环法术位上限不能为负（当前 {slot.Max}）");
            }

            if (slot.Used < 0 || slot.Used > slot.Max)
            {
                errors.Add($"第 {level} 环法术位已用必须在 0..{slot.Max} 之间（当前 {slot.Used}）");
            }
        }
    }

    private static void ValidateAttacks(Sheet sheet, List<string> errors)
    {
        for (var i = 0; i < sheet.Attacks.Count; i++)
        {
            var attack = sheet.Attacks[i];

            if (string.IsNullOrWhiteSpace(attack.WeaponId))
            {
                errors.Add($"未填：attacks[{i}].weaponId");
            }

            // 至少给命中加值或豁免 DC：两者都没有，这次攻击没法结算。
            if (attack.AttackBonus is null && attack.SaveDC is null)
            {
                errors.Add($"未填：attacks[{i}].attackBonus 或 attacks[{i}].saveDC");
            }

            if (attack.AttackBonus is { } bonus && bonus is < -10 or > 30)
            {
                errors.Add($"attacks[{i}].attackBonus 超出合理范围（当前 {bonus}）");
            }

            if (attack.SaveDC is { } dc && dc <= 0)
            {
                errors.Add($"attacks[{i}].saveDC 必须大于 0（当前 {dc}）");
            }
        }
    }

    private static void ValidateConditions(Sheet sheet, List<string> errors)
    {
        if (sheet.Conditions.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("状态名不能为空");
        }
    }

    private static void ValidateConcentration(Sheet sheet, List<string> errors)
    {
        if (sheet.Concentration is not { } concentration)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(concentration.SpellId))
        {
            errors.Add("未填：concentration.spellId");
        }

        if (concentration.SaveDc is { } dc && dc <= 0)
        {
            errors.Add("concentration.saveDc 必须大于 0");
        }
    }

    private static void ValidateDeathSaves(Sheet sheet, List<string> errors)
    {
        if (sheet.DeathSaves is not { } saves)
        {
            return;
        }

        if (saves.Successes is < 0 or > 3)
        {
            errors.Add("deathSaves.successes 必须在 0..3 之间");
        }

        if (saves.Failures is < 0 or > 3)
        {
            errors.Add("deathSaves.failures 必须在 0..3 之间");
        }
    }

    private static void RequireFilled(List<string> errors, int? value, string name)
    {
        if (value is null)
        {
            errors.Add($"未填：{name}");
        }
    }

    /// <summary>枚举落进错误文案时用英文小写（与数据包、日志里的写法一致）。</summary>
    private static string Wire(Ability ability) => ability.ToString().ToLowerInvariant();
}
