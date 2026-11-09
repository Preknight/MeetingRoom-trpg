using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Rules;

/// <summary>
/// 武器精通（V3）：只建模 Vex / Nick / Topple / Sap；其余<b>显式报告"未建模"</b>，
/// 不静默按普通攻击处理（那是"看起来正常但算错"的来源）。
/// </summary>
public sealed class WeaponMasteryTests
{
    [Theory]
    [InlineData("Vex", WeaponMastery.Vex)]
    [InlineData("vex", WeaponMastery.Vex)]
    [InlineData("Nick", WeaponMastery.Nick)]
    [InlineData("Topple", WeaponMastery.Topple)]
    [InlineData("Sap", WeaponMastery.Sap)]
    public void ModelledMasteriesCarryAnEffect(string name, WeaponMastery expected)
    {
        var resolution = WeaponMasteries.Resolve(name);

        Assert.True(resolution.Modelled);
        Assert.Equal(expected, resolution.Mastery);
        Assert.False(string.IsNullOrWhiteSpace(resolution.Effect));
        Assert.Null(resolution.NotModelledReason);
    }

    [Theory]
    [InlineData("Cleave")]
    [InlineData("Graze")]
    [InlineData("Push")]
    [InlineData("Slow")]
    public void KnownButUnmodelledMasteriesSaySoExplicitly(string name)
    {
        var resolution = WeaponMasteries.Resolve(name);

        Assert.False(resolution.Modelled);
        Assert.Null(resolution.Mastery);
        Assert.Contains("尚未建模", resolution.NotModelledReason, StringComparison.Ordinal);
        Assert.Contains("普通攻击", resolution.NotModelledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownNamesAreReportedAsUnknown()
    {
        var resolution = WeaponMasteries.Resolve("Whirlwind");

        Assert.False(resolution.Modelled);
        Assert.Contains("未知精通", resolution.NotModelledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyNameIsNotSilentlyTreatedAsNormal()
    {
        var resolution = WeaponMasteries.Resolve("  ");

        Assert.False(resolution.Modelled);
        Assert.Contains("未给精通名", resolution.NotModelledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAllKeepsBothModelledAndUnmodelledEntries()
    {
        var resolutions = WeaponMasteries.ResolveAll(["Sap", "Slow", "Vex"]);

        Assert.Equal(3, resolutions.Count);
        Assert.Equal(2, resolutions.Count(item => item.Modelled));
        Assert.Single(resolutions, item => !item.Modelled);
        Assert.Empty(WeaponMasteries.ResolveAll(null));
    }

    [Fact]
    public void ToppleDcIsEightPlusProficiencyPlusModifier()
    {
        // 8 + 熟练 2 + 力量/敏捷调整值 3 = 13
        Assert.Equal(13, WeaponMasteries.ToppleDc(proficiencyBonus: 2, strengthOrDexterityModifier: 3));
    }

    [Fact]
    public void TheModelledListMatchesTheEnum()
    {
        Assert.Equal(
            Enum.GetValues<WeaponMastery>().Select(item => item.ToString()).OrderBy(name => name, StringComparer.Ordinal),
            WeaponMasteries.ModelledNames.OrderBy(name => name, StringComparer.Ordinal));
    }
}
