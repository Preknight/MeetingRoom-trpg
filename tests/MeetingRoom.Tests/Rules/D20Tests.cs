using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Rules;

/// <summary>
/// 四个判定纯函数（V3）：事件字段、优势/劣势的合并规则、攻击的 20/1 特例、伤害的分桶与抗性结算、暴击双骰。
/// <para>随机源一律注入固定种子的 <see cref="Random"/>，或循环找一个"骰面正好是 N"的种子（比猜种子可靠）。</para>
/// </summary>
public sealed class D20Tests
{
    [Fact]
    public void CheckAddsEverySourceAndComparesToDc()
    {
        var sources = new[] { new RollSource("熟练", 2), new RollSource("敏捷", 3) };

        var roll = D20.RollCheck(12, sources, random: new Random(7));

        Assert.Equal(5, roll.Modifier);
        Assert.Equal(roll.Natural + 5, roll.Total);
        Assert.Equal(roll.Total >= 12, roll.Success);
        Assert.Equal(AdvantageMode.Normal, roll.Mode);
        Assert.Single(roll.Rolled);
        Assert.Equal(2, roll.Sources.Count);
        Assert.Equal(roll.Natural == 20, roll.Crit);
        Assert.Equal(roll.Natural == 1, roll.Fumble);
    }

    [Fact]
    public void AdvantageRollsTwoDiceAndKeepsTheHigher()
    {
        var roll = D20.RollCheck(10, [], [AdvantageMode.Advantage], new Random(11));

        Assert.Equal(AdvantageMode.Advantage, roll.Mode);
        Assert.Equal(2, roll.Rolled.Count);
        Assert.Equal(Math.Max(roll.Rolled[0], roll.Rolled[1]), roll.Natural);
    }

    [Fact]
    public void DisadvantageKeepsTheLower()
    {
        var roll = D20.RollCheck(10, [], [AdvantageMode.Disadvantage], new Random(11));

        Assert.Equal(2, roll.Rolled.Count);
        Assert.Equal(Math.Min(roll.Rolled[0], roll.Rolled[1]), roll.Natural);
    }

    [Fact]
    public void AdvantageAndDisadvantageCancelOut()
    {
        Assert.Equal(AdvantageMode.Normal, D20.Merge([AdvantageMode.Advantage, AdvantageMode.Disadvantage]));

        // 抵消后只掷一颗：不是"掷两颗再互相比较"。
        var roll = D20.RollCheck(10, [], [AdvantageMode.Advantage, AdvantageMode.Disadvantage], new Random(3));
        Assert.Single(roll.Rolled);
    }

    [Fact]
    public void AdvantagesDoNotStack()
    {
        Assert.Equal(AdvantageMode.Advantage, D20.Merge([AdvantageMode.Advantage, AdvantageMode.Advantage]));

        var roll = D20.RollCheck(10, [], [AdvantageMode.Advantage, AdvantageMode.Advantage], new Random(5));

        // 两个优势来源仍然只掷两颗（不是三颗、更不是加两次）。
        Assert.Equal(2, roll.Rolled.Count);
    }

    [Fact]
    public void ANaturalTwentyAlwaysHits()
    {
        var roll = FindAttack(20, armorClass: 99, sources: []);

        Assert.True(roll.Crit);
        Assert.True(roll.Success);
        Assert.True(roll.Total < 99, "这个用例要求加值不足以普通命中，才能证明是 20 起的作用");
    }

    [Fact]
    public void ANaturalOneAlwaysMisses()
    {
        var roll = FindAttack(1, armorClass: 1, sources: [new RollSource("巨额加值", 50)]);

        Assert.True(roll.Fumble);
        Assert.False(roll.Success);
        Assert.True(roll.Total >= 1, "这个用例要求加值足以普通命中，才能证明是 1 起的作用");
    }

    [Fact]
    public void DamageIsHalvedByResistance()
    {
        var result = D20.RollDamage(
            [new DamageRollPart("2d6", "slashing")],
            new DamageDefenses { Resistances = ["slashing"] },
            random: new Random(9));

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal("slashing", bucket.Type);
        Assert.Equal((int)Math.Floor(bucket.Raw / 2.0), bucket.Effective);
        Assert.Equal("resisted", bucket.Applied);
        Assert.Equal(bucket.Effective, result.Total);
    }

    [Fact]
    public void DamageIsDoubledByVulnerability()
    {
        var result = D20.RollDamage(
            [new DamageRollPart("2d6", "fire")],
            new DamageDefenses { Vulnerabilities = ["FIRE"] },
            random: new Random(9));

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(bucket.Raw * 2, bucket.Effective);
        Assert.Equal("vulnerable", bucket.Applied);
    }

    [Fact]
    public void DamageIsZeroedByImmunity()
    {
        var result = D20.RollDamage(
            [new DamageRollPart("2d6", "poison")],
            new DamageDefenses { Immunities = ["poison"] },
            random: new Random(9));

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(0, bucket.Effective);
        Assert.Equal("immune", bucket.Applied);
        Assert.True(bucket.Raw > 0);
    }

    [Fact]
    public void ResistanceAndVulnerabilityCancelOut()
    {
        var result = D20.RollDamage(
            [new DamageRollPart("2d6", "cold")],
            new DamageDefenses { Resistances = ["cold"], Vulnerabilities = ["cold"] },
            random: new Random(9));

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(bucket.Raw, bucket.Effective);
        Assert.Equal("vulnerable+resisted", bucket.Applied);
    }

    [Fact]
    public void DefensesAreAppliedPerDamageTypeBucket()
    {
        var result = D20.RollDamage(
            [new DamageRollPart("2d6", "fire"), new DamageRollPart("2d6", "cold")],
            new DamageDefenses { Resistances = ["fire"] },
            random: new Random(21));

        Assert.Equal(2, result.Buckets.Count);

        var fire = result.Buckets.Single(bucket => bucket.Type == "fire");
        var cold = result.Buckets.Single(bucket => bucket.Type == "cold");

        Assert.Equal((int)Math.Floor(fire.Raw / 2.0), fire.Effective);
        Assert.Equal(cold.Raw, cold.Effective);
        Assert.Equal(fire.Effective + cold.Effective, result.Total);
        Assert.Equal(fire.Raw + cold.Raw, result.RawTotal);
    }

    [Fact]
    public void CriticalDamageDoublesTheDiceButNotTheModifier()
    {
        // 直接验证骰子引擎的暴击规则：1d8+3 暴击是 2d8+3。
        var critical = Dice.RollCritical("1d8+3", random: new Random(4));
        var attack = Assert.Single(critical.Attacks);

        Assert.Equal(2, attack.Rolled.Count);
        Assert.Equal(attack.Rolled.Sum() + 3, attack.Total);

        // 走一遍伤害结算：暴击时原始合计落在 2d8+3 的范围里（5..19），非暴击在 1d8+3（4..11）。
        var crit = D20.RollDamage([new DamageRollPart("1d8+3", "slashing")], crit: true, random: new Random(4));
        Assert.True(crit.Crit);
        Assert.InRange(crit.RawTotal, 5, 19);

        var normal = D20.RollDamage([new DamageRollPart("1d8+3", "slashing")], random: new Random(4));
        Assert.False(normal.Crit);
        Assert.InRange(normal.RawTotal, 4, 11);
    }

    [Fact]
    public void AnUnknownDamageExpressionIsRejectedInsteadOfGuessed()
    {
        Assert.Throws<ArgumentException>(() =>
            D20.RollDamage([new DamageRollPart("扔一下", "fire")], random: new Random(1)));
    }

    /// <summary>循环找一颗骰面正好是 <paramref name="natural"/> 的种子（比猜种子稳）。</summary>
    private static RollEvent FindAttack(int natural, int armorClass, IReadOnlyList<RollSource> sources)
    {
        for (var seed = 0; seed < 5000; seed++)
        {
            var roll = D20.RollAttack(armorClass, sources, random: new Random(seed));

            if (roll.Natural == natural)
            {
                return roll;
            }
        }

        throw new InvalidOperationException($"在 5000 个种子里找不到骰面 {natural}。");
    }
}
