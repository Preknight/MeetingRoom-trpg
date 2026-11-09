using MeetingRoom.Core.Combat;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 战斗状态机（C1）：先攻顺序（平手 DEX → id）、回合交替与轮数、拒绝路径、同 seed 可重放。
/// 纯函数测试——不依赖 provider、不依赖地图桥。
/// </summary>
public sealed class EncounterTests
{
    [Fact]
    public void TwoCombatantsWalkThreeRoundsInOrder()
    {
        var start = EncounterEngine.Start("enc-1", TwoCombatants(), seed: 20261026);

        Assert.True(start.Ok, start.Message);
        Assert.NotNull(start.Encounter);

        var encounter = start.Encounter!;

        Assert.Equal(EncounterPhase.Active, encounter.Phase);
        Assert.Equal(1, encounter.Round);
        Assert.Equal(2, encounter.Order.Count);
        Assert.NotNull(encounter.Turn);
        Assert.Equal(encounter.Order[0], encounter.Turn!.ActorId);
        Assert.Equal(30, encounter.Turn.MovementMaxFeet);

        var first = encounter.Order[0];
        var second = encounter.Order[1];
        var seen = new List<(int Round, string Actor)> { (encounter.Round, encounter.Turn.ActorId) };

        for (var step = 0; step < 6; step++)
        {
            var advance = EncounterEngine.Advance(encounter);
            Assert.True(advance.Ok, advance.Message);
            encounter = advance.Encounter!;
            seen.Add((encounter.Round, encounter.Turn!.ActorId));
        }

        // 交替行动；每走到队首轮数 +1（一轮 = 每人各行动一次）。
        Assert.Equal(
            new[] { (1, first), (1, second), (2, first), (2, second), (3, first), (3, second), (4, first) },
            seen);
        Assert.Equal(4, encounter.Round);

        var end = EncounterEngine.End(encounter, CombatOutcome.Victory);

        Assert.True(end.Ok, end.Message);
        Assert.Equal(EncounterPhase.Ended, end.Encounter!.Phase);
        Assert.Equal(CombatOutcome.Victory, end.Encounter.Outcome);
    }

    [Fact]
    public void TiesAreBrokenByDexterityFirst()
    {
        // 同先攻 10：DEX 4 的先手，DEX 2 的排后，平手归因于 dex。
        var start = EncounterEngine.Start(
            "enc-1",
            [
                new CombatantSetup { Id = "a", DexterityModifier = 2, Initiative = 10 },
                new CombatantSetup { Id = "b", DexterityModifier = 4, Initiative = 10 },
            ],
            seed: 1);

        Assert.True(start.Ok, start.Message);
        Assert.Equal(["b", "a"], start.Encounter!.Order);
        Assert.Equal("dex", start.Encounter.Find("a")!.InitiativeTiebreak);
        Assert.Equal(string.Empty, start.Encounter.Find("b")!.InitiativeTiebreak);
    }

    [Fact]
    public void TiesAreBrokenByEntityIdWhenDexterityAlsoTies()
    {
        var start = EncounterEngine.Start(
            "enc-1",
            [
                new CombatantSetup { Id = "zeta", DexterityModifier = 2, Initiative = 10 },
                new CombatantSetup { Id = "alpha", DexterityModifier = 2, Initiative = 10 },
            ],
            seed: 1);

        Assert.True(start.Ok, start.Message);
        Assert.Equal(["alpha", "zeta"], start.Encounter!.Order);
        Assert.Equal("id", start.Encounter.Find("zeta")!.InitiativeTiebreak);
    }

    [Fact]
    public void TheSameSeedProducesTheSameInitiativeOrder()
    {
        var first = EncounterEngine.Start("enc-1", TwoCombatants(), seed: 42).Encounter!;
        var second = EncounterEngine.Start("enc-1", TwoCombatants(), seed: 42).Encounter!;

        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Order, second.Order);
        Assert.Equal(
            first.Combatants.Select(combatant => combatant.Initiative),
            second.Combatants.Select(combatant => combatant.Initiative));
    }

    [Fact]
    public void DuplicateIdsAreRejectedWithAReadableReason()
    {
        var result = EncounterEngine.Start(
            "enc-1",
            [
                new CombatantSetup { Id = "ella" },
                new CombatantSetup { Id = "ella" },
            ],
            seed: 1);

        Assert.False(result.Ok);
        Assert.Equal(EncounterEngine.CodeDuplicateId, result.Code);
        Assert.Contains("重复参战", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancingWithoutAStartedEncounterIsRejected()
    {
        var result = EncounterEngine.Advance(new Encounter { Id = "enc-1" });

        Assert.False(result.Ok);
        Assert.Equal(EncounterEngine.CodeNotActive, result.Code);
    }

    [Fact]
    public void EndingTwiceIsRejected()
    {
        var encounter = EncounterEngine.Start("enc-1", TwoCombatants(), seed: 7).Encounter!;
        var ended = EncounterEngine.End(encounter, CombatOutcome.Retreat).Encounter!;

        var again = EncounterEngine.End(ended, CombatOutcome.Victory);

        Assert.False(again.Ok);
        Assert.Equal(EncounterEngine.CodeAlreadyEnded, again.Code);
    }

    [Fact]
    public void EndingReleasesTheTurnAndKeepsTheOutcome()
    {
        var encounter = EncounterEngine.Start("enc-1", TwoCombatants(), seed: 7).Encounter!;

        var ended = EncounterEngine.End(encounter, CombatOutcome.DmEnded).Encounter!;

        Assert.Equal(EncounterPhase.Ended, ended.Phase);
        Assert.Equal(CombatOutcome.DmEnded, ended.Outcome);

        // 回合快照保留（知道"谁在行动时收的场"），但标记为 DM 结束。
        Assert.NotNull(ended.Turn);
        Assert.Equal("dm", ended.Turn!.EndedBy);
        Assert.NotNull(ended.Current);
    }

    [Fact]
    public void TheFixtureScriptLoadsWithEnumsAndNumbers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Combat", "combat-sample.json");

        Assert.True(File.Exists(path), $"测试夹具没有复制到输出目录：{path}");

        var script = CombatScript.Load(path);

        Assert.Equal(2, script.Combatants.Count);
        Assert.Equal(3, script.Rounds);
        Assert.Equal(CombatSide.Party, script.Combatants[0].Side);
        Assert.Equal(3, script.Combatants[0].DexterityModifier);
        Assert.Equal(CombatSide.Foe, script.Combatants[1].Side);
        Assert.Equal("地精", script.Combatants[1].CnName);

        // 用脚本开战也能走通（probe 与测试共用同一条路径）。
        var start = EncounterEngine.Start("enc-1", script.Combatants, script.Seed);
        Assert.True(start.Ok, start.Message);
    }

    private static IReadOnlyList<CombatantSetup> TwoCombatants() =>
    [
        new CombatantSetup { Id = "ella", Name = "Ella", Side = CombatSide.Party, DexterityModifier = 3 },
        new CombatantSetup { Id = "goblin-1", Name = "Goblin", CnName = "地精", Side = CombatSide.Foe, DexterityModifier = 2 },
    ];
}
