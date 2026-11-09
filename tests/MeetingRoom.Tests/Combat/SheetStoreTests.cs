using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Rules;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 数值表仓库（C3a）：<b>HP 的唯一真相</b>。临时 HP 先吸收、0 HP 两态、massive damage、可选落盘。
/// </summary>
public sealed class SheetStoreTests
{
    [Fact]
    public void TempHpAbsorbsDamageBeforeCurrentHp()
    {
        var store = NewStore();
        store.Put(NewSheet("vecna", maxHp: 12, currentHp: 12, tempHp: 5));

        var result = store.ApplyDamage("vecna", 11);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(5, result.AbsorbedByTempHp);
        Assert.Equal(6, result.HpLost);
        Assert.Equal(0, result.TempHpAfter);
        Assert.Equal(6, result.HpAfter);
        Assert.Equal(VitalStatus.Alive, result.Status);
    }

    [Fact]
    public void ReachingZeroHpMakesTheTargetDying()
    {
        var store = NewStore();
        store.Put(NewSheet("vecna", maxHp: 12, currentHp: 12));

        var result = store.ApplyDamage("vecna", 11);
        var down = store.ApplyDamage("vecna", 1);

        Assert.True(down.Ok, down.Message);
        Assert.Equal(0, down.HpAfter);
        Assert.Equal(VitalStatus.Dying, down.Status);
        Assert.Equal(VitalStatus.Dying, store.Get("vecna")!.Status);
        Assert.Equal(new DeathSaves(0, 0), down.Saves);
    }

    [Fact]
    public void MassiveDamageKillsOutright()
    {
        var store = NewStore();
        store.Put(NewSheet("vecna", maxHp: 12, currentHp: 3));

        // 3 点打空，溢出 12 ≥ 最大 HP → 直接死亡（不是昏迷）。
        var result = store.ApplyDamage("vecna", 15);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, result.HpAfter);
        Assert.Equal(VitalStatus.Dead, result.Status);
    }

    [Fact]
    public void SetHpRevivesWhenBackAboveZero()
    {
        var store = NewStore();
        store.Put(NewSheet("vecna", maxHp: 12, currentHp: 0));

        store.ApplyDamage("vecna", 12);
        Assert.Equal(VitalStatus.Dying, store.Get("vecna")!.Status);

        var healed = store.SetHp("vecna", 5);

        Assert.NotNull(healed);
        Assert.Equal(5, healed!.CurrentHp);
        Assert.Equal(VitalStatus.Alive, healed.Status);
    }

    [Fact]
    public void UnknownOrIncompleteSheetsAreRejectedWithAReason()
    {
        var store = NewStore();

        var unknown = store.ApplyDamage("nobody", 5);
        Assert.False(unknown.Ok);
        Assert.Equal("unknown-sheet", unknown.Code);
        Assert.Contains("没有", unknown.Message, StringComparison.Ordinal);

        store.Put(new Sheet { CharacterId = "blank" });
        var incomplete = store.ApplyDamage("blank", 5);
        Assert.False(incomplete.Ok);
        Assert.Equal("incomplete-sheet", incomplete.Code);
        Assert.Contains("HP 字段未填", incomplete.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroOrNegativeDamageChangesNothing()
    {
        var store = NewStore();
        store.Put(NewSheet("vecna", maxHp: 12, currentHp: 12, tempHp: 3));

        var result = store.ApplyDamage("vecna", 0);

        Assert.True(result.Ok);
        Assert.Equal(12, result.HpAfter);
        Assert.Equal(3, result.TempHpAfter);
    }

    [Fact]
    public void SheetsPersistAndReloadFromJson()
    {
        using var temp = new TempDir();
        var path = temp.File("sheets.json");

        var store = new SheetStore(path);
        store.Put(NewSheet("vecna", maxHp: 12, currentHp: 12));
        store.ApplyDamage("vecna", 11);

        var reloaded = new SheetStore(path);
        var sheet = reloaded.Get("vecna");

        Assert.NotNull(sheet);
        Assert.Equal(1, sheet!.CurrentHp);
    }

    private static SheetStore NewStore() => new();

    private static Sheet NewSheet(string id, int maxHp, int currentHp, int tempHp = 0) => new()
    {
        CharacterId = id,
        MaxHp = maxHp,
        CurrentHp = currentHp,
        TempHp = tempHp,
    };
}
