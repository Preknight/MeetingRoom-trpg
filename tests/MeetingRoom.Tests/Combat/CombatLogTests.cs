using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 战斗记录的落盘与重放（C1）：<c>kind=combat-start / combat-turn / combat-end / combat-rejected</c>
/// 与 <c>roll</c> 同规则——只追加、不进重放、不推 revision；<c>combat-start</c> 必带 seed 与参战名单。
/// </summary>
public sealed class CombatLogTests
{
    [Fact]
    public void CombatRecordsPersistAndReplayWithoutBumpingRevision()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        var timeline = new StoryTimeline(path);
        timeline.Append("导演旁白", [new StoryLine("ella", "我握紧了刀。")]);
        Assert.Equal(1, timeline.Revision);

        var encounter = EncounterEngine
            .Start("enc-1", TwoCombatants(), seed: 20261026)
            .Encounter!;

        timeline.AppendCombatStart(new CombatStartEvent(
            encounter.Id,
            encounter.Round,
            encounter.Seed,
            EncounterEngine.OrderOf(encounter),
            encounter.Combatants));

        timeline.AppendCombatTurn(new CombatTurnEvent(
            encounter.Id,
            encounter.Round,
            encounter.Turn!.ActorId,
            CombatBudget.Of(encounter.Turn)));

        var advanced = EncounterEngine.Advance(encounter).Encounter!;

        timeline.AppendCombatTurn(new CombatTurnEvent(
            advanced.Id,
            advanced.Round,
            advanced.Turn!.ActorId,
            CombatBudget.Of(advanced.Turn)));

        timeline.AppendCombatRejected(new CombatRejectedEvent("enc-1", "out-of-range", "目标在 60 尺外，弯刀只能打到 5 尺"));

        var ended = EncounterEngine.End(advanced, CombatOutcome.Victory).Encounter!;

        timeline.AppendCombatEnd(new CombatEndEvent(ended.Id, "victory", ended.Round));

        // 战斗记录不推 revision（它们不是"这一幕"）。
        Assert.Equal(1, timeline.Revision);

        var reloaded = new StoryTimeline(path);

        Assert.Equal(1, reloaded.Revision);

        var start = Assert.Single(reloaded.CombatStartRecords);
        Assert.Equal(20261026, start.Seed);
        Assert.Equal(encounter.Order, start.Combatants.Select(combatant => combatant.Id));
        Assert.Equal(encounter.Order, start.Order.Select(entry => entry.Actor));

        Assert.Equal(2, reloaded.CombatTurnRecords.Count);
        Assert.Equal(encounter.Order[0], reloaded.CombatTurnRecords[0].Actor);
        Assert.Equal(encounter.Order[1], reloaded.CombatTurnRecords[1].Actor);

        var rejected = Assert.Single(reloaded.CombatRejectedRecords);
        Assert.Equal("out-of-range", rejected.Code);
        Assert.Contains("60 尺", rejected.Message, StringComparison.Ordinal);

        var end = Assert.Single(reloaded.CombatEndRecords);
        Assert.Equal("victory", end.Outcome);
        Assert.Equal(1, end.Rounds);
    }

    [Fact]
    public void ReplayingTwiceProducesTheSameEncounterSnapshot()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        var timeline = new StoryTimeline(path);
        var encounter = EncounterEngine.Start("enc-1", TwoCombatants(), seed: 99).Encounter!;

        timeline.AppendCombatStart(new CombatStartEvent(
            encounter.Id, encounter.Round, encounter.Seed, EncounterEngine.OrderOf(encounter), encounter.Combatants));

        var first = new StoryTimeline(path);
        var second = new StoryTimeline(path);

        var rebuiltFirst = Rebuild(first);
        var rebuiltSecond = Rebuild(second);

        Assert.Equal(rebuiltFirst.Order, rebuiltSecond.Order);
        Assert.Equal(rebuiltFirst.Round, rebuiltSecond.Round);
        Assert.Equal(rebuiltFirst.Phase, rebuiltSecond.Phase);
        Assert.Equal(rebuiltFirst.Seed, rebuiltSecond.Seed);

        static Encounter Rebuild(StoryTimeline source)
        {
            var start = source.CombatStartRecords[0];
            var result = EncounterEngine.Start(start.EncounterId, start.Combatants.Select(ToSetup).ToArray(), start.Seed);

            Assert.True(result.Ok, result.Message);

            return result.Encounter!;
        }

        static CombatantSetup ToSetup(Combatant combatant) => new()
        {
            Id = combatant.Id,
            Name = combatant.DisplayName,
            CnName = combatant.CnName,
            Side = combatant.Side,
            SheetId = combatant.SheetId,
            TemplateId = combatant.TemplateId,
            DexterityModifier = combatant.DexterityModifier,
            SpeedFeet = combatant.SpeedFeet,
            VttTokenId = combatant.VttTokenId,
        };
    }

    private static IReadOnlyList<CombatantSetup> TwoCombatants() =>
    [
        new CombatantSetup { Id = "ella", Name = "Ella", Side = CombatSide.Party, DexterityModifier = 3 },
        new CombatantSetup { Id = "goblin-1", Name = "Goblin", CnName = "地精", Side = CombatSide.Foe, DexterityModifier = 2 },
    ];
}
