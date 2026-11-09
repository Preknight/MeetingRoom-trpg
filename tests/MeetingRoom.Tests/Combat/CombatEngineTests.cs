using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 攻击 / 施法结算（C2）：命中与伤害与手算一致（同种子复算）、拒绝路径可叙述、精通显式标注。
/// <para>武器伤害骰与精通从规则包查；伤害类型必须显式给（数据包不含）。</para>
/// </summary>
public sealed class CombatEngineTests
{
    [Fact]
    public void LongswordWithBonusMatchesHandCalculation()
    {
        var rules = LoadFixture();

        // 同一个种子复算：攻击先消耗一次 d20，再掷 1d8+3。
        var expectedRandom = new Random(42);
        expectedRandom.Next(1, 21);
        var expectedDamage = Dice.Roll("1d8+3", random: expectedRandom);

        var result = CombatEngine.Resolve(
            rules,
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "goblin",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                DamageBonus = 3,
                DamageType = "slashing",
                TargetArmorClass = 1,
            },
            new Random(42));

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Attack);
        Assert.Equal(result.Attack!.Natural + 5, result.Attack.Total);
        Assert.True(result.Attack.Success);

        // 命中伤害 = 手算的 1d8+3（非暴击时单骰）。
        Assert.Equal(expectedDamage.Total, result.Damage!.Buckets[0].Raw);
        Assert.Equal(result.Damage.Total, result.DamageApplied);
        Assert.Equal("slashing", result.DamageType);
    }

    [Fact]
    public void VecnaPlusThirteenMatchesHandCalculation()
    {
        var result = CombatEngine.Resolve(
            LoadFixture(),
            new CombatActionRequest
            {
                ActorId = "vecna",
                TargetId = "ella",
                WeaponId = "Longsword|PHB",
                AttackBonus = 13,
                DamageType = "slashing",
                TargetArmorClass = 1,
            },
            new Random(7));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(result.Attack!.Natural + 13, result.Attack.Total);
        Assert.Equal(13, result.Attack.Modifier);
    }

    [Fact]
    public void MissingDamageTypeIsRejectedWithAReadableReason()
    {
        var result = CombatEngine.Resolve(
            LoadFixture(),
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "goblin",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                TargetArmorClass = 10,
            });

        Assert.False(result.Ok);
        Assert.Equal("missing-damage-type", result.Code);
        Assert.Contains("伤害类型", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownWeaponIsRejected()
    {
        var unknown = CombatEngine.Resolve(LoadFixture(), new CombatActionRequest
        {
            ActorId = "ella", TargetId = "goblin", WeaponId = "Nope|PHB", DamageType = "slashing",
        });

        Assert.False(unknown.Ok);
        Assert.Equal("unknown-weapon", unknown.Code);
        Assert.Contains("没有", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MasteriesAreReportedAndUnmodelledOnesDoNotBlockTheAttack()
    {
        var moduled = CombatEngine.Resolve(
            LoadFixture(),
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "goblin",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                DamageType = "slashing",
                TargetArmorClass = 1,
            },
            new Random(3));

        Assert.True(moduled.Ok);
        Assert.Contains(moduled.Masteries, mastery => mastery.Modelled);

        var unmodelled = CombatEngine.Resolve(
            LoadFixture(),
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "goblin",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                DamageType = "slashing",
                TargetArmorClass = 1,
                Masteries = ["Cleave"],
            },
            new Random(3));

        // 未建模精通不阻止攻击，但要显式标注"这次未生效"（不静默）。
        Assert.True(unmodelled.Ok);
        var mastery = Assert.Single(unmodelled.Masteries);
        Assert.False(mastery.Modelled);
        Assert.Contains("未生效", unmodelled.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveSpellHalvesDamageOnASuccessfulSave()
    {
        var rules = LoadFixture();

        // 找一个豁免成功的种子（Fireball 是 dexterity 豁免）。
        for (var seed = 0; seed < 200; seed++)
        {
            var result = CombatEngine.Resolve(
                rules,
                new CombatActionRequest
                {
                    ActorId = "ella",
                    TargetId = "goblin",
                    Kind = CombatActionKind.CastSpell,
                    SpellId = "Fireball|PHB",
                    SaveDc = 10,
                    TargetArmorClass = 10,
                },
                new Random(seed));

            Assert.True(result.Ok, result.Message);

            if (result.Save?.Success == true)
            {
                Assert.Equal(result.Damage!.Total / 2, result.DamageApplied);
                Assert.Empty(result.Masteries);
                return;
            }
        }

        throw new InvalidOperationException("200 个种子里没有一次豁免成功——检查 Save 结算。");
    }

    [Fact]
    public void ASpellWithoutDamageIsStillAValidCast()
    {
        var result = CombatEngine.Resolve(
            LoadFixture(),
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "ella",
                Kind = CombatActionKind.CastSpell,
                SpellId = "Cure Wounds|PHB",
                TargetArmorClass = 10,
            },
            new Random(1));

        Assert.True(result.Ok, result.Message);
        Assert.Null(result.Damage);
        Assert.Equal(0, result.DamageApplied);
    }

    [Fact]
    public void TheAttackScenarioFixtureLoadsActionsAndChecks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Combat", "combat-attack.json");

        Assert.True(File.Exists(path), $"测试夹具没有复制到输出目录：{path}");

        var script = CombatScript.Load(path);

        Assert.Equal(2, script.Actions.Count);
        Assert.Equal(4, script.Checks.Count);
        Assert.Equal(CombatActionKind.Attack, script.Actions[0].Kind);
        Assert.Equal(CheckKind.Skill, script.Checks[0].Kind);
        Assert.Equal(CheckVisibility.Secret, script.Checks[1].Visibility);
    }

    private static RulesData LoadFixture() => RulesDataLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "Rules", "rules-sample.json"));
}
