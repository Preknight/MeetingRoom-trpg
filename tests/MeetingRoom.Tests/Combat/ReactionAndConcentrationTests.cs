using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// C4：射程校验、借机攻击、Disengage 压制、Ready、状态到期、Help 消费、专注检定。
/// 与 C3b 一样，状态机部分不需要规则包；只有射程与专注需要武器/角色数值。
/// </summary>
public sealed class ReactionAndConcentrationTests
{
    [Fact]
    public void MeleeBeyondReachIsRejected()
    {
        var result = CombatEngine.Resolve(Rules(), Attack("vecna", distanceFeet: 10), new Random(1));

        Assert.False(result.Ok);
        Assert.Equal("out-of-range", result.Code);
        Assert.Contains("10", result.Message, StringComparison.Ordinal);
        Assert.Contains("5", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MeleeWithinReachIsAllowed()
    {
        var result = CombatEngine.Resolve(Rules(), Attack("vecna", distanceFeet: 5), new Random(1));

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Attack);
    }

    [Fact]
    public void OpportunityAttackFiresOncePerRoundAgainstTheSameTarget()
    {
        var encounter = NewEncounter();

        var first = EncounterEngine.SubmitAction(
            encounter, "ella", CombatActionKind.Move, distanceFeet: 5, provokesOpportunityFrom: ["vecna"]);

        Assert.True(first.Ok, first.Message);
        Assert.Single(first.Opportunities);
        Assert.Equal("vecna", first.Opportunities[0].AttackerId);
        Assert.Equal("ella", first.Opportunities[0].TargetId);

        // 同一轮、同一目标：不再触发（reaction 也已经被第一条用完）。
        var second = EncounterEngine.SubmitAction(
            first.Encounter!, "ella", CombatActionKind.Move, distanceFeet: 5, provokesOpportunityFrom: ["vecna"]);

        Assert.True(second.Ok, second.Message);
        Assert.Empty(second.Opportunities);
    }

    [Fact]
    public void DisengageSuppressesOpportunityAttack()
    {
        var disengage = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Disengage);
        Assert.True(disengage.Ok, disengage.Message);

        var move = EncounterEngine.SubmitAction(
            disengage.Encounter!, "ella", CombatActionKind.Move, distanceFeet: 5, provokesOpportunityFrom: ["vecna"]);

        Assert.True(move.Ok, move.Message);
        Assert.Empty(move.Opportunities);
    }

    [Fact]
    public void HelpAdvantageIsConsumedOnlyOnce()
    {
        var help = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Help, helpTargetId: "vecna");
        Assert.True(help.Ok, help.Message);
        Assert.Equal("ella", help.Encounter!.Find("vecna")!.HelpTargetId);

        var (afterFirst, consumed) = EncounterEngine.ConsumeHelp(help.Encounter, "vecna");
        Assert.True(consumed);
        Assert.Null(afterFirst.Find("vecna")!.HelpTargetId);

        var (afterSecond, consumedAgain) = EncounterEngine.ConsumeHelp(afterFirst, "vecna");
        Assert.False(consumedAgain);
        Assert.Null(afterSecond.Find("vecna")!.HelpTargetId);
    }

    [Fact]
    public void ReadyStoresTriggerAndExpiresAtNextTurn()
    {
        var ready = EncounterEngine.SubmitAction(
            NewEncounter(), "ella", CombatActionKind.Ready, readyTrigger: "有人进入 5 尺");

        Assert.True(ready.Ok, ready.Message);
        Assert.Equal("有人进入 5 尺", ready.Encounter!.Find("ella")!.ReadyTrigger);
        Assert.True(ready.Encounter.Turn!.ActionUsed);

        // ella → vecna → 回到 ella：Ready 已过期。
        var vecna = EncounterEngine.Advance(ready.Encounter).Encounter!;
        var back = EncounterEngine.Advance(vecna).Encounter!;

        Assert.Equal("ella", back.Turn!.ActorId);
        Assert.Null(back.Find("ella")!.ReadyTrigger);
    }

    [Fact]
    public void MissingReadyTriggerIsRejected()
    {
        var result = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Ready);

        Assert.False(result.Ok);
        Assert.Equal("missing-trigger", result.Code);
    }

    [Fact]
    public void ConditionRoundsTickDownAndExpire()
    {
        var withCondition = EncounterEngine.ApplyCondition(NewEncounter(), "ella", "Prone", rounds: 2);
        Assert.Equal(2, withCondition.Find("ella")!.ConditionRounds["Prone"]);

        // ella 自己的下一个回合：2 → 1，不删。
        var once = EncounterEngine.Advance(EncounterEngine.Advance(withCondition).Encounter!).Encounter!;
        Assert.Equal(1, once.Find("ella")!.ConditionRounds["Prone"]);
        Assert.Empty(once.ExpiredConditions);

        // 再一圈：1 → 0，移除并记一笔过期。
        var twice = EncounterEngine.Advance(EncounterEngine.Advance(once).Encounter!).Encounter!;
        Assert.False(twice.Find("ella")!.ConditionRounds.ContainsKey("Prone"));
        Assert.Contains("ella:Prone", twice.ExpiredConditions);
    }

    [Fact]
    public void ConcentrationRequiresASaveOnlyWhenFocused()
    {
        var plain = new Sheet { CharacterId = "vecna", MaxHp = 12, CurrentHp = 12 };
        var noFocus = ConcentrationCheck.Check(plain, 20, new Random(1));
        Assert.False(noFocus.Required);
        Assert.True(noFocus.Maintained);

        var focused = plain with { Concentration = new Concentration("Fly|PHB", null) };
        var check = ConcentrationCheck.Check(focused, 20, new Random(1));
        Assert.True(check.Required);
        Assert.Equal(10, check.Dc);
        Assert.NotNull(check.Roll);
    }

    [Fact]
    public void ConcentrationDcScalesWithDamage()
    {
        var focused = new Sheet
        {
            CharacterId = "vecna",
            MaxHp = 12,
            CurrentHp = 12,
            Concentration = new Concentration("Fly|PHB", null),
            Abilities = new Dictionary<Ability, int> { [Ability.Constitution] = 14 },
        };

        // 伤害 40 → DC 20（伤害/2），不是固定的 10。
        var check = ConcentrationCheck.Check(focused, 40, new Random(1));

        Assert.True(check.Required);
        Assert.Equal(20, check.Dc);
    }

    private static CombatActionRequest Attack(string target, int distanceFeet) => new()
    {
        ActorId = "ella",
        TargetId = target,
        Kind = CombatActionKind.Attack,
        WeaponId = "Longsword|PHB",
        AttackBonus = 5,
        DamageType = "slashing",
        TargetArmorClass = 10,
        DistanceFeet = distanceFeet,
    };

    private static Encounter NewEncounter() =>
        EncounterEngine.Start(
            "enc-1",
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, DexterityModifier = 3, SpeedFeet = 30 },
                new CombatantSetup { Id = "vecna", Side = CombatSide.Foe, DexterityModifier = 2, SpeedFeet = 30 },
            ],
            seed: 4).Encounter!;

    private static RulesData Rules() => RulesDataLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "Rules", "rules-sample.json"));
}
