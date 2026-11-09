using System.Text.Json;

using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// D3 的 DM 覆盖：九字段白名单、from 由引擎填、hp 走 D1 迁移、status 不动 DeathSaves、
/// 未知字段列出支持项、dc 只改指定检定。
/// </summary>
public sealed class DmOverrideTests
{
    [Fact]
    public void SettingHpToZeroEntersDyingNotDead()
    {
        var sheets = NewSheets();
        var result = DmOverrideEngine.Apply(null, sheets, new DmOverrideRequest
        {
            Target = "vecna",
            Field = "hp",
            Value = Number(0),
            Reason = "DM 判定它直接倒下",
        });

        Assert.True(result.Ok, result.Message);
        Assert.Equal("12", result.From);
        Assert.Equal("0", result.To);
        Assert.Equal(VitalStatus.Dying, sheets.Get("vecna")!.Status);
    }

    [Fact]
    public void SettingHpFromStableKeepsStable()
    {
        var sheets = NewSheets();
        sheets.ApplyDamage("vecna", 12);
        sheets.Stabilize("vecna");

        var result = DmOverrideEngine.Apply(null, sheets, new DmOverrideRequest
        {
            Target = "vecna",
            Field = "hp",
            Value = Number(0),
        });

        Assert.True(result.Ok, result.Message);
        Assert.Equal(VitalStatus.Stable, sheets.Get("vecna")!.Status);
    }

    [Fact]
    public void SettingStatusToDeadKeepsHpAndDeathSaves()
    {
        var sheets = NewSheets();
        sheets.ApplyDamage("vecna", 12);
        sheets.ApplyDamage("vecna", 3);
        var before = sheets.Get("vecna")!;

        var result = DmOverrideEngine.Apply(null, sheets, new DmOverrideRequest
        {
            Target = "vecna",
            Field = "status",
            Value = Text("dead"),
        });

        var after = sheets.Get("vecna")!;

        Assert.True(result.Ok, result.Message);
        Assert.Equal(VitalStatus.Dead, after.Status);
        Assert.Equal(before.CurrentHp, after.CurrentHp);
        Assert.Equal(before.DeathSaves, after.DeathSaves);
    }

    [Fact]
    public void UnknownFieldListsSupportedFields()
    {
        var result = DmOverrideEngine.Apply(null, NewSheets(), new DmOverrideRequest
        {
            Target = "vecna",
            Field = "luck",
            Value = Number(1),
        });

        Assert.False(result.Ok);
        Assert.Equal("unknown-field", result.Code);
        Assert.Contains("hp", result.Message!, StringComparison.Ordinal);
        Assert.Contains("budget", result.Message!, StringComparison.Ordinal);
        Assert.Contains("dc", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void InitiativeChangeReordersWithoutBreakingTheCurrentTurn()
    {
        var encounter = NewEncounter();

        var result = DmOverrideEngine.Apply(encounter, NewSheets(), new DmOverrideRequest
        {
            Target = "vecna",
            Field = "initiative",
            Value = Number(30),
        });

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["vecna", "ella"], result.Encounter!.Order);
        Assert.Equal("ella", result.Encounter.Turn!.ActorId);   // 当前回合没被抢走
    }

    [Fact]
    public void ConditionCanBeAddedAndRemoved()
    {
        var encounter = NewEncounter();

        var added = DmOverrideEngine.Apply(encounter, NewSheets(), new DmOverrideRequest
        {
            Target = "ella",
            Field = "condition",
            Value = Json("""{"name":"Prone","rounds":2}"""),
        });

        Assert.True(added.Ok, added.Message);
        Assert.Equal(2, added.Encounter!.Find("ella")!.ConditionRounds["Prone"]);

        var removed = DmOverrideEngine.Apply(added.Encounter, NewSheets(), new DmOverrideRequest
        {
            Target = "ella",
            Field = "condition",
            Value = Json("""{"name":"Prone","remove":true}"""),
        });

        Assert.True(removed.Ok, removed.Message);
        Assert.False(removed.Encounter!.Find("ella")!.ConditionRounds.ContainsKey("Prone"));
    }

    [Fact]
    public void BudgetOverridesTheCurrentTurnOnly()
    {
        var encounter = NewEncounter();

        var result = DmOverrideEngine.Apply(encounter, NewSheets(), new DmOverrideRequest
        {
            Target = "ella",
            Field = "budget",
            Value = Json("""{"action":true,"bonus":true,"reaction":true,"movementFeet":15}"""),
        });

        Assert.True(result.Ok, result.Message);
        Assert.True(result.Encounter!.Turn!.ActionUsed);
        Assert.True(result.Encounter.Turn.BonusUsed);
        Assert.True(result.Encounter.Turn.ReactionUsed);
        Assert.Equal(15, result.Encounter.Turn.MovementUsedFeet);
    }

    [Fact]
    public void DcOverrideOnlyTouchesTheNamedCheck()
    {
        var timeline = new StoryTimeline();
        var first = timeline.AppendCheck(new StoryCheckRecord("ella", "skill", "偷看", 15, null, DateTimeOffset.UtcNow) { CheckId = "chk-1" });
        var second = timeline.AppendCheck(new StoryCheckRecord("ella", "skill", "再偷看", 15, null, DateTimeOffset.UtcNow) { CheckId = "chk-2" });

        Assert.True(timeline.ApplyCheckDcOverride("chk-1", 10));

        Assert.Equal(10, timeline.CheckRecords.Single(record => record.CheckId == "chk-1").Dc);
        Assert.Equal(15, timeline.CheckRecords.Single(record => record.CheckId == "chk-2").Dc);
        Assert.Equal("chk-1", first.CheckId);
        Assert.Equal("chk-2", second.CheckId);
    }

    [Fact]
    public void OverrideEventsCarryFromAndTo()
    {
        var timeline = new StoryTimeline();

        var record = timeline.AppendCombatOverride("enc-1", "vecna", "hp", "12", "0", "DM 判定倒下");

        Assert.Equal("12", record.From);
        Assert.Equal("0", record.To);
        Assert.Equal("hp", record.Field);
        Assert.Single(timeline.CombatOverrideRecords);
    }

    private static JsonElement Number(int value) => Json(value.ToString());

    private static JsonElement Text(string value) => Json($"\"{value}\"");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SheetStore NewSheets()
    {
        var store = new SheetStore();

        store.Put(new Sheet
        {
            CharacterId = "vecna",
            MaxHp = 12,
            CurrentHp = 12,
            TempHp = 0,
            ArmorClass = 12,
        });

        return store;
    }

    private static Encounter NewEncounter() =>
        EncounterEngine.Start(
            "enc-1",
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, DexterityModifier = 3, SpeedFeet = 30 },
                new CombatantSetup { Id = "vecna", Side = CombatSide.Foe, DexterityModifier = 2, SpeedFeet = 30 },
            ],
            seed: 4).Encounter!;
}
