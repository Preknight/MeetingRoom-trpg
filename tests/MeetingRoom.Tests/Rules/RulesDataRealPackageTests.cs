using MeetingRoom.Core.Rules;
using Xunit.Sdk;

namespace MeetingRoom.Tests.Rules;

/// <summary>
/// 真实规则包冒烟测试（约 1.9 MB，<b>不进仓库</b>——体积与版权都不合适）。
/// <para>
/// 路径只从环境变量 <c>MEETINGROOM_RULES_DATA</c> 取；变量缺失或文件不存在时 <c>Skip</c>，
/// <b>不硬编码任何本机路径</b>。设置方式（PowerShell）：
/// <c>$env:MEETINGROOM_RULES_DATA = '&lt;rules-data.json&gt;'</c>。
/// </para>
/// </summary>
public sealed class RulesDataRealPackageTests
{
    private const string EnvVar = "MEETINGROOM_RULES_DATA";

    [Fact]
    public void RealPackageCountsMatchTheGenerator()
    {
        var data = LoadOrSkip();

        // 与生成器报的计数一致；不一致说明契约或生成脚本变了。
        Assert.Equal(488, data.Spells.Count);
        Assert.Equal(965, data.Items.Count);
        Assert.Equal(706, data.MonsterTemplates.Count);
        Assert.Equal(27, data.Conditions.Count);
        Assert.Equal(48, data.Actions.Count);
    }

    [Fact]
    public void KeyEntriesAreFindableByIdAndMasteryStaysEnglish()
    {
        var data = LoadOrSkip();

        var fireball = data.FindSpell("Fireball|XPHB");
        Assert.NotNull(fireball);
        Assert.Equal("火球术", fireball.CnName);
        Assert.Equal("fire", Assert.Single(fireball.Damage).Type);
        Assert.Equal("1 action", Assert.Single(fireball.Time).Describe());

        var longsword = data.FindItem("Longsword|XPHB");
        Assert.NotNull(longsword);
        Assert.Equal("weapon", longsword.Kind);
        Assert.Contains("Sap", longsword.Mastery);
        Assert.True(WeaponMasteries.Resolve("Sap").Modelled);

        var vecna = data.FindMonster("Vecna the Archlich|VEoR");
        Assert.NotNull(vecna);
        Assert.Equal("26", vecna.Cr);
        Assert.Equal("维克那，大巫妖", vecna.CnName);
    }

    [Fact]
    public void EveryEntryHasIdAndChineseName()
    {
        var data = LoadOrSkip();

        // 生成器报告"缺中文名 0"——这里也钉住，中文名是显示与匹配的唯一来源。
        Assert.All(data.Spells, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.All(data.Spells, item => Assert.False(string.IsNullOrWhiteSpace(item.CnName)));
        Assert.All(data.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.CnName)));
        Assert.All(data.MonsterTemplates, item => Assert.False(string.IsNullOrWhiteSpace(item.CnName)));
        Assert.All(data.Conditions, item => Assert.False(string.IsNullOrWhiteSpace(item.CnName)));
        Assert.All(data.Actions, item => Assert.False(string.IsNullOrWhiteSpace(item.CnName)));
    }

    [Fact]
    public void SpellShapesHandleEveryRealVariant()
    {
        var data = LoadOrSkip();

        // 戏法递增是 variants（不是多段伤害）；火球是 base + perLevel。
        var fireBolt = data.FindSpell("Fire Bolt|XPHB");
        Assert.NotNull(fireBolt);
        Assert.Equal(0, fireBolt.Level);
        Assert.Null(fireBolt.Save);
        Assert.Equal(3, Assert.Single(fireBolt.Scaling).Variants.Count);

        var fireball = data.FindSpell("Fireball|XPHB");
        Assert.NotNull(fireball);
        Assert.Equal("8d6", Assert.Single(fireball.Scaling).Base);
        Assert.Equal("dexterity", fireball.Save);

        // 反应法术的 time 带 condition；施法成分三个键不一定齐。
        Assert.All(data.Spells, spell =>
        {
            Assert.NotNull(spell.Id);
            Assert.True(spell.Level >= 0);
            foreach (var time in spell.Time)
            {
                Assert.False(string.IsNullOrWhiteSpace(time.Unit));
            }
        });
    }

    /// <summary>环境变量缺失或文件不存在 → 跳过（不硬编码路径、不让 CI 红灯）。</summary>
    private static RulesData LoadOrSkip()
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw SkipException.ForSkip($"{EnvVar} 未设置或文件不存在：跳过真实规则包冒烟测试（真实包不进仓库）。");
        }

        return RulesDataLoader.Load(path);
    }
}
