using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 骰子引擎（《枭熊地图接入实现方案》V2）：<c>NdM±P</c>、<c>Nx(NdM+P)</c>（多次攻击），
/// d20 支持优势 / 劣势；结果里的明细足以重放出同样的 total。
/// <para>固定种子只用于测试——引擎本身用 <see cref="Random.Shared"/>。</para>
/// </summary>
public sealed class DiceTests
{
    [Fact]
    public void AdvantageKeepsTheHigherOfTwoD20()
    {
        var result = Dice.Roll("1d20+5", "advantage", random: new Random(7));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(DiceMode.Advantage, result.Mode);

        var attack = Assert.Single(result.Attacks);
        Assert.Equal(2, attack.Rolled.Count);
        Assert.Equal(Math.Max(attack.Rolled[0], attack.Rolled[1]), attack.Kept[0]);
        Assert.Equal(attack.Kept[0] + 5, attack.Total);
    }

    [Fact]
    public void DisadvantageKeepsTheLowerOfTwoD20()
    {
        var result = Dice.Roll("1d20", "disadvantage", random: new Random(7));

        var attack = Assert.Single(result.Attacks);
        Assert.Equal(2, attack.Rolled.Count);
        Assert.Equal(Math.Min(attack.Rolled[0], attack.Rolled[1]), attack.Kept[0]);
        Assert.Equal((int)DiceMode.Disadvantage, (int)result.Mode);
    }

    [Fact]
    public void AdvantageAndDisadvantageDifferOnTheSameSeed()
    {
        var advantage = Dice.Roll("1d20", "advantage", random: new Random(11));
        var disadvantage = Dice.Roll("1d20", "disadvantage", random: new Random(11));

        // 同一组骰面：一个取高、一个取低，kept 必然不同（除非两颗相同）。
        Assert.Equal(advantage.Attacks[0].Rolled, disadvantage.Attacks[0].Rolled);
        Assert.Equal(Math.Max(advantage.Attacks[0].Rolled[0], advantage.Attacks[0].Rolled[1]), advantage.Attacks[0].Total);
        Assert.Equal(Math.Min(disadvantage.Attacks[0].Rolled[0], disadvantage.Attacks[0].Rolled[1]), disadvantage.Attacks[0].Total);
    }

    [Fact]
    public void MultipleAttacksRollIndependentlyAndSumUp()
    {
        var result = Dice.Roll("3x(1d8+2)", random: new Random(3));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Attacks.Count);
        Assert.All(result.Attacks, attack =>
        {
            Assert.Equal(8, attack.Sides);
            Assert.Equal(2, attack.Modifier);
            Assert.Equal(attack.Rolled[0] + 2, attack.Total);
        });

        Assert.Equal(result.Attacks.Sum(attack => attack.Total), result.Total);
        Assert.Equal(["第 1 次", "第 2 次", "第 3 次"], result.Attacks.Select(attack => attack.Label));
    }

    [Fact]
    public void MultipleDiceInOneAttackAreAllKept()
    {
        var result = Dice.Roll("2d6+3", random: new Random(5));

        var attack = Assert.Single(result.Attacks);
        Assert.Equal(2, attack.Rolled.Count);
        Assert.Equal(attack.Rolled, attack.Kept);
        Assert.Equal(attack.Rolled.Sum() + 3, attack.Total);
    }

    [Fact]
    public void AFixedSeedReplaysTheSameTotal()
    {
        var first = Dice.Roll("2x(1d6+1)", random: new Random(42));
        var second = Dice.Roll("2x(1d6+1)", random: new Random(42));

        // "日志能重放出同样的 total"：同样的明细（rolls）一定算出同样的总数。
        Assert.Equal(first.Total, second.Total);
        Assert.Equal(
            first.Attacks.SelectMany(attack => attack.Rolled),
            second.Attacks.SelectMany(attack => attack.Rolled));
        Assert.Equal(first.Attacks.Sum(attack => attack.Kept.Sum() + attack.Modifier), first.Total);
    }

    [Fact]
    public void ADcTurnsTheTotalIntoPassOrFail()
    {
        var result = Dice.Roll("1d20+10", "normal", dc: 5, random: new Random(1));

        var attack = Assert.Single(result.Attacks);
        Assert.Equal(attack.Total >= 5, attack.Success);
        Assert.Equal(attack.Total >= 5, result.Success);
    }

    [Fact]
    public void MultipleAttacksOnlySucceedWhenEveryAttackPasses()
    {
        var result = Dice.Roll("2x(1d20+0)", "normal", dc: 1, random: new Random(9));

        // DC 1 时两颗 d20 都必然过：汇总的 success 与逐条一致。
        Assert.All(result.Attacks, attack => Assert.True(attack.Success));
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("", "表达式不能为空")]
    [InlineData("abc", "看不懂这个表达式")]
    [InlineData("d", "看不懂这个表达式")]
    [InlineData("2x(abc)", "看不懂这个表达式")]
    [InlineData("101d6", "骰子颗数")]
    [InlineData("1d1", "骰子面数")]
    [InlineData("99x(1d6)", "攻击次数")]
    public void BadExpressionsAreReportedWithoutThrowing(string expression, string expected)
    {
        var result = Dice.Roll(expression);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
        Assert.Contains("没有执行", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownModeIsReportedInsteadOfSilentlyTreatedAsNormal()
    {
        var result = Dice.Roll("1d20", "lucky");

        Assert.False(result.Ok);
        Assert.Contains("mode", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionCarriesEveryNumberTheNarratorMustQuote()
    {
        var result = Dice.Roll("1d20+5", "advantage", dc: 10, random: new Random(7));

        var text = result.Describe();

        Assert.Contains("掷骰 1d20+5（优势，DC 10）", text, StringComparison.Ordinal);
        Assert.Contains("取高", text, StringComparison.Ordinal);
        Assert.Contains("正文里请直接引用这些数字", text, StringComparison.Ordinal);
    }
}
