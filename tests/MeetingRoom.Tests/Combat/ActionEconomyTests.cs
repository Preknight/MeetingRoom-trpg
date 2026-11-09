using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 行动经济与移动（C3b）：action/bonus 各一次、移动尺数、Dash、五类基础动作、Dodge 的劣势。
/// 状态机是纯函数，测试不需要会话或规则包（除了 Dodge 的伤害结算用 <see cref="CombatEngine"/>）。
/// </summary>
public sealed class ActionEconomyTests
{
    [Fact]
    public void MoveWithinBudgetThenRejectedWhenOver()
    {
        var encounter = NewEncounter();

        var five = EncounterEngine.SubmitAction(encounter, "ella", CombatActionKind.Move, distanceFeet: 5);
        Assert.True(five.Ok, five.Message);
        Assert.Equal(25, Remaining(five.Encounter!));

        var thirty = EncounterEngine.SubmitAction(five.Encounter!, "ella", CombatActionKind.Move, distanceFeet: 30);
        Assert.False(thirty.Ok);
        Assert.Equal(EncounterEngine.CodeNotEnoughMovement, thirty.Code);
        Assert.Contains("25", thirty.Message, StringComparison.Ordinal);

        var rest = EncounterEngine.SubmitAction(five.Encounter!, "ella", CombatActionKind.Move, distanceFeet: 25);
        Assert.True(rest.Ok, rest.Message);
        Assert.Equal(0, Remaining(rest.Encounter!));
    }

    [Fact]
    public void OnlyOneActionPerTurn()
    {
        var first = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Attack);
        Assert.True(first.Ok, first.Message);

        var second = EncounterEngine.SubmitAction(first.Encounter!, "ella", CombatActionKind.Attack);

        Assert.False(second.Ok);
        Assert.Equal(EncounterEngine.CodeActionUsed, second.Code);
        Assert.Contains("只能一次", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneBonusActionPerTurn()
    {
        var first = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Disengage);
        Assert.True(first.Ok, first.Message);
        Assert.True(first.Encounter!.Find("ella")!.Disengaged);

        var second = EncounterEngine.SubmitAction(first.Encounter!, "ella", CombatActionKind.Disengage);

        Assert.False(second.Ok);
        Assert.Equal(EncounterEngine.CodeBonusUsed, second.Code);
    }

    [Fact]
    public void DashExtendsTheMovementBudget()
    {
        var dash = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Dash);

        Assert.True(dash.Ok, dash.Message);
        Assert.True(dash.Encounter!.Turn!.ActionUsed);
        Assert.Equal(60, Remaining(dash.Encounter));

        var move = EncounterEngine.SubmitAction(dash.Encounter, "ella", CombatActionKind.Move, distanceFeet: 60);
        Assert.True(move.Ok, move.Message);
    }

    [Fact]
    public void DodgeMarksTheCombatantAndGivesAttackersDisadvantage()
    {
        var dodge = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Dodge);

        Assert.True(dodge.Ok, dodge.Message);
        Assert.True(dodge.Encounter!.Find("ella")!.Dodging);

        var attack = CombatEngine.Resolve(
            Rules(),
            new CombatActionRequest
            {
                ActorId = "vecna",
                TargetId = "ella",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                DamageType = "slashing",
                TargetArmorClass = 1,
                TargetIsDodging = dodge.Encounter.Find("ella")!.Dodging,
            },
            new Random(3));

        Assert.True(attack.Ok, attack.Message);
        Assert.Equal(AdvantageMode.Disadvantage, attack.Attack!.Mode);
        Assert.Equal(2, attack.Attack.Rolled.Count);
        Assert.Equal(Math.Min(attack.Attack.Rolled[0], attack.Attack.Rolled[1]), attack.Attack.Natural);
    }

    [Fact]
    public void DodgeClearsWhenItsOwnTurnStartsAgain()
    {
        var dodge = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Dodge).Encounter!;
        Assert.True(dodge.Find("ella")!.Dodging);

        // ella → vecna → 回到 ella：她自己的新回合开始时 Dodging 应被清掉。
        var vecna = EncounterEngine.Advance(dodge).Encounter!;
        var back = EncounterEngine.Advance(vecna).Encounter!;

        Assert.Equal("ella", back.Turn!.ActorId);
        Assert.False(back.Find("ella")!.Dodging);
    }

    [Fact]
    public void HelpRequiresAnotherCombatant()
    {
        var self = EncounterEngine.SubmitAction(
            NewEncounter(), "ella", CombatActionKind.Help, helpTargetId: "ella");

        Assert.False(self.Ok);
        Assert.Contains("不能帮自己", self.Message, StringComparison.Ordinal);

        var missing = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Help);
        Assert.False(missing.Ok);
        Assert.Equal(EncounterEngine.CodeMissingTarget, missing.Code);

        var ok = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Help, helpTargetId: "vecna");
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("ella", ok.Encounter!.Find("vecna")!.HelpTargetId);
    }

    [Fact]
    public void ActingOutOfTurnIsRejected()
    {
        var result = EncounterEngine.SubmitAction(NewEncounter(), "vecna", CombatActionKind.Attack);

        Assert.False(result.Ok);
        Assert.Equal(EncounterEngine.CodeNotYourTurn, result.Code);
        Assert.Contains("轮到 ella", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HideMarksTheCombatant()
    {
        var hide = EncounterEngine.SubmitAction(NewEncounter(), "ella", CombatActionKind.Hide);

        Assert.True(hide.Ok, hide.Message);
        Assert.True(hide.Encounter!.Find("ella")!.Hidden);
        Assert.True(hide.Encounter.Turn!.ActionUsed);
    }

    private static int Remaining(Encounter encounter) =>
        encounter.Turn!.MovementMaxFeet - encounter.Turn.MovementUsedFeet;

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
