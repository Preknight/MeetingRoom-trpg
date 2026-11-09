using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Rules;

/// <summary>
/// Sheet 校验（V3）：<b>只判断能不能用，不补默认值</b>。
/// 缺字段报「未填」/ 数值越界报范围；空集合（技能、法术位、攻击、状态）不算缺字段。
/// </summary>
public sealed class SheetValidatorTests
{
    [Fact]
    public void ACompleteSheetHasNoProblems()
    {
        Assert.Empty(SheetValidator.Validate(ValidSheet()));
    }

    [Fact]
    public void MissingRequiredFieldsAreReportedAsNotFilled()
    {
        var sheet = new Sheet { CharacterId = "ella" };

        var errors = SheetValidator.Validate(sheet);

        Assert.Contains("未填：maxHp", errors);
        Assert.Contains("未填：currentHp", errors);
        Assert.Contains("未填：tempHp", errors);
        Assert.Contains("未填：armorClass", errors);
        Assert.Contains("未填：proficiencyBonus", errors);
        Assert.Contains("未填：abilities", errors);

        // 不填默认值：不会只报一句"卡不可用"了事，也不会擅自按 10 算。
        Assert.DoesNotContain(errors, error => error.Contains("maxHp 必须大于 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AbilityScoresMustBeWithinOneToThirty()
    {
        var sheet = ValidSheet() with
        {
            Abilities = new Dictionary<Ability, int>
            {
                [Ability.Strength] = 0,
                [Ability.Dexterity] = 31,
                [Ability.Constitution] = 10,
                [Ability.Intelligence] = 10,
                [Ability.Wisdom] = 10,
                [Ability.Charisma] = 10,
            },
        };

        var errors = SheetValidator.Validate(sheet);

        Assert.Contains(errors, error => error.Contains("abilities.strength", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("abilities.dexterity", StringComparison.Ordinal));
    }

    [Fact]
    public void ASheetMissingOneAbilitySaysWhichOne()
    {
        var abilities = new Dictionary<Ability, int>(ValidSheet().Abilities!)
        {
            // 故意漏掉 Charisma
        };
        abilities.Remove(Ability.Charisma);

        var errors = SheetValidator.Validate(ValidSheet() with { Abilities = abilities });

        Assert.Contains("未填：abilities.charisma", errors);
    }

    [Fact]
    public void HpAndArmorClassMustBePositive()
    {
        var errors = SheetValidator.Validate(ValidSheet() with
        {
            MaxHp = 0,
            CurrentHp = 0,
            ArmorClass = 0,
            TempHp = -1,
        });

        Assert.Contains("maxHp 必须大于 0", errors);
        Assert.Contains("armorClass 必须大于 0", errors);
        Assert.Contains("tempHp 不能为负", errors);

        var overMax = SheetValidator.Validate(ValidSheet() with { CurrentHp = 999 });
        Assert.Contains(overMax, error => error.Contains("currentHp 必须在", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAttackNeedsEitherAttackBonusOrSaveDc()
    {
        var errors = SheetValidator.Validate(ValidSheet() with
        {
            Attacks = [new WeaponAttack { WeaponId = "Longsword|PHB" }],
        });

        Assert.Contains(errors, error => error.Contains("attacks[0].attackBonus 或 attacks[0].saveDC", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAttackWithOnlySaveDcIsAccepted()
    {
        var errors = SheetValidator.Validate(ValidSheet() with
        {
            Attacks = [new WeaponAttack { WeaponId = "Sacred Flame|PHB", SaveDC = 13 }],
        });

        Assert.DoesNotContain(errors, error => error.Contains("attackBonus 或", StringComparison.Ordinal));
    }

    [Fact]
    public void SpellSlotsAndDeathSavesAreRangeChecked()
    {
        var errors = SheetValidator.Validate(ValidSheet() with
        {
            SpellSlots = new Dictionary<int, SpellSlot> { [0] = new(1, 0), [10] = new(1, 0), [1] = new(-1, 0) },
            DeathSaves = new DeathSaves(4, -1),
        });

        Assert.Contains(errors, error => error.Contains("法术位环级", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("第 1 环法术位剩余不能为负", StringComparison.Ordinal));
        Assert.Contains("deathSaves.successes 必须在 0..3 之间", errors);
        Assert.Contains("deathSaves.failures 必须在 0..3 之间", errors);
    }

    [Fact]
    public void EmptyCollectionsAreNotMissingFields()
    {
        var sheet = ValidSheet() with
        {
            Skills = new Dictionary<string, int>(),
            SpellSlots = new Dictionary<int, SpellSlot>(),
            Attacks = [],
            Conditions = [],
        };

        Assert.Empty(SheetValidator.Validate(sheet));
    }

    [Fact]
    public void ConcentrationAndConditionsAreChecked()
    {
        var errors = SheetValidator.Validate(ValidSheet() with
        {
            Concentration = new Concentration(" ", 0),
            Conditions = [""],
        });

        Assert.Contains("未填：concentration.spellId", errors);
        Assert.Contains("concentration.saveDc 必须大于 0", errors);
        Assert.Contains("状态名不能为空", errors);
    }

    /// <summary>一张填全的卡：后面的用例只在它上面改一两处。</summary>
    private static Sheet ValidSheet() => new()
    {
        CharacterId = "ella",
        Name = "Ella",
        MaxHp = 32,
        CurrentHp = 32,
        TempHp = 0,
        ArmorClass = 15,
        ProficiencyBonus = 2,
        Abilities = new Dictionary<Ability, int>
        {
            [Ability.Strength] = 10,
            [Ability.Dexterity] = 16,
            [Ability.Constitution] = 14,
            [Ability.Intelligence] = 12,
            [Ability.Wisdom] = 11,
            [Ability.Charisma] = 13,
        },
        SaveProficiencies = [Ability.Dexterity],
        Skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["stealth"] = 5 },
        SpellSlots = new Dictionary<int, SpellSlot> { [1] = new(2, 0) },
        Attacks =
        [
            new WeaponAttack
            {
                WeaponId = "Dagger|PHB",
                AttackBonus = 5,
                DamageDice = "1d4+3",
                DamageType = "piercing",
                Masteries = ["Nick", "Vex"],
            },
        ],
        Conditions = [],
        Concentration = null,
        DeathSaves = new DeathSaves(0, 0),
    };
}
