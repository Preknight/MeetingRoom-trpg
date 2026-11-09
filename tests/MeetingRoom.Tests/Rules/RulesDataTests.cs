using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Rules;

/// <summary>
/// 规则数据包（schema <c>meetingroom-rules/3</c>）：只读仓库里的手写 fixture
/// （<c>TestData/Rules/rules-sample.json</c>），不引用任何外部路径。
/// </summary>
public sealed class RulesDataTests
{
    [Fact]
    public void FixtureLoadsWithTheContractSchema()
    {
        var data = LoadFixture();

        Assert.Equal(RulesDataLoader.SchemaId, data.Schema);
        Assert.Equal(3, data.Spells.Count);
        Assert.Equal(2, data.Items.Count);
        Assert.Single(data.Conditions);
        Assert.Single(data.MonsterTemplates);
        Assert.Single(data.Actions);
    }

    [Fact]
    public void FindByIdWorksForEverySection()
    {
        var data = LoadFixture();

        Assert.Equal("火球术", data.FindSpell("Fireball|PHB")?.CnName);
        Assert.Equal("weapon", data.FindItem("Dagger|PHB")?.Kind);
        Assert.Equal("humanoid", data.FindMonster("Goblin|MM")?.Type);
        Assert.Equal("Prone", data.FindCondition("Prone|PHB")?.Name);
        Assert.Equal("1 action", data.FindAction("Dash|PHB")?.TimeText);

        // 按 id 找不到就是 null，不猜"最像的那条"。
        Assert.Null(data.FindSpell("Fireball|XGE"));
        Assert.Null(data.FindSpell(""));
    }

    [Fact]
    public void SpellDamageKeepsDiceAndType()
    {
        var fireball = LoadFixture().FindSpell("Fireball|PHB");

        var part = Assert.Single(fireball!.Damage);
        Assert.Equal("8d6", part.Dice);
        Assert.Equal("fire", part.Type);
        Assert.Equal("dexterity", fireball.Save);

        // 治疗类法术没有伤害条目：空列表，不是"缺字段"。
        Assert.Empty(LoadFixture().FindSpell("Cure Wounds|PHB")!.Damage);
    }

    [Fact]
    public void StructuredSpellFieldsAreReadFromTheRealShapes()
    {
        var fireball = LoadFixture().FindSpell("Fireball|PHB")!;

        // time / range / components / duration / scaling 在真实包里都是对象或数组，不是一行字符串。
        Assert.Equal("1 action", Assert.Single(fireball.Time).Describe());
        Assert.Equal("point 150 feet", fireball.RangeText);
        Assert.Contains("V", fireball.ComponentsText, StringComparison.Ordinal);
        Assert.Contains("M", fireball.ComponentsText, StringComparison.Ordinal);
        Assert.Equal("instant", Assert.Single(fireball.Duration).Type);
        Assert.Equal("8d6（每环 +1d6）", fireball.ScalingText);
    }

    [Fact]
    public void ScalingVariantsAreNotTreatedAsExtraDamage()
    {
        // 戏法递增（variants）与 damage[] 是两回事：variants 只描述"到几级换哪段骰"。
        var data = RulesDataLoader.Parse(ReadFixture().Replace(
            "\"scaling\": [{ \"base\": \"3d4+3\", \"perLevel\": \"1d4+1\" }]",
            "\"scaling\": [{ \"variants\": [\"2d10\", \"3d10\", \"4d10\"] }]",
            StringComparison.Ordinal));

        var spell = data.FindSpell("Magic Missile|PHB")!;

        Assert.Single(spell.Damage);
        Assert.Equal(["2d10", "3d10", "4d10"], Assert.Single(spell.Scaling).Variants);
        Assert.Equal("2d10 / 3d10 / 4d10", spell.ScalingText);
    }

    [Fact]
    public void MonsterShapesAreNormalized()
    {
        var goblin = LoadFixture().FindMonster("Goblin|MM")!;

        Assert.Equal("humanoid", goblin.Type);
        Assert.Equal("S", goblin.Size);
        Assert.Equal(["N", "E"], goblin.Alignment);
        Assert.Equal("1/4", goblin.Cr);
        Assert.Null(goblin.Habitat);

        // 怪物模板没有数值字段：不会凭空造出 HP / AC。
        Assert.DoesNotContain("hp", ReadFixture(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("armorClass", ReadFixture(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WeaponMasteryNamesStayEnglishEnums()
    {
        var dagger = LoadFixture().FindItem("Dagger|PHB")!;

        Assert.Equal(["Nick", "Vex"], dagger.Mastery);
        Assert.Contains("F", dagger.Properties);
        Assert.Equal("1d4", dagger.Dmg1);
        Assert.Null(dagger.Dmg2);
        Assert.Equal(1, dagger.Weight);
        Assert.Equal(200, dagger.Value);

        // 数据包里的枚举名要能被精通解析直接吃下（中文名不参与逻辑）。
        Assert.All(dagger.Mastery, name => Assert.True(WeaponMasteries.Resolve(name).Modelled));
    }

    [Fact]
    public void AWrongSchemaIsRejected()
    {
        var json = ReadFixture().Replace(RulesDataLoader.SchemaId, "meetingroom-rules/2", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => RulesDataLoader.Parse(json));
        Assert.Contains("schema 不对", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var json = ReadFixture().Replace(
            "\"id\": \"Dagger|PHB\"",
            "\"id\": \"Longsword|PHB\"",
            StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => RulesDataLoader.Parse(json));
        Assert.Contains("id 重复", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingIdIsRejected()
    {
        var json = ReadFixture().Replace("\"id\": \"Prone|PHB\"", "\"id\": \"\"", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => RulesDataLoader.Parse(json));
        Assert.Contains("缺少 id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenJsonIsReportedAsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => RulesDataLoader.Parse("{ \"schema\": "));
        Assert.Throws<InvalidDataException>(() => RulesDataLoader.Parse(""));
    }

    private static RulesData LoadFixture() => RulesDataLoader.Parse(ReadFixture());

    private static string ReadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Rules", "rules-sample.json");

        Assert.True(File.Exists(path), $"测试夹具没有复制到输出目录：{path}");

        return File.ReadAllText(path);
    }
}
