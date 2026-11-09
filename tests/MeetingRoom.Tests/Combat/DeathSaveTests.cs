using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// D1 死亡豁免与 0 HP 状态机：四态迁移、濒死受伤累计、暴击 +2、稳定、治疗、医学稳定、
/// 以及死亡豁免的 20 / 1 / 成功 3 / 失败 3 四条特殊规则。
/// </summary>
public sealed class DeathSaveTests
{
    [Fact]
    public void ReachingZeroHpEntersDyingWithClearedSaves()
    {
        var store = NewStore();

        var result = store.ApplyDamage("vecna", 12);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(VitalStatus.Dying, result.Status);
        Assert.Equal(new DeathSaves(0, 0), result.Saves);
    }

    [Fact]
    public void DyingTakesOneFailurePerHit()
    {
        var store = NewStore();
        store.ApplyDamage("vecna", 12);

        var second = store.ApplyDamage("vecna", 4);

        Assert.Equal(VitalStatus.Dying, second.Status);
        Assert.Equal(1, second.Saves!.Failures);
    }

    [Fact]
    public void CriticalHitOnDyingAddsTwoFailures()
    {
        var store = NewStore();
        store.ApplyDamage("vecna", 12);

        var crit = store.ApplyDamage("vecna", 4, critical: true);

        Assert.Equal(2, crit.Saves!.Failures);
        Assert.Equal(VitalStatus.Dying, crit.Status);
    }

    [Fact]
    public void StableTakesDamageBackToDying()
    {
        var store = NewStore();
        store.ApplyDamage("vecna", 12);
        store.Stabilize("vecna");

        var hit = store.ApplyDamage("vecna", 3);

        Assert.Equal(VitalStatus.Dying, hit.Status);
        Assert.Equal(new DeathSaves(0, 1), hit.Saves);
    }

    [Fact]
    public void MassiveDamageFromDyingKills()
    {
        var store = NewStore();
        store.ApplyDamage("vecna", 12);

        // 单次伤害 ≥ 最大 HP：直接从 Dying → Dead（不再累计失败）。
        var massive = store.ApplyDamage("vecna", 12);

        Assert.Equal(VitalStatus.Dead, massive.Status);
    }

    [Fact]
    public void HealBringsBackAliveAndClearsSaves()
    {
        var store = NewStore();
        store.ApplyDamage("vecna", 12);
        store.ApplyDamage("vecna", 3);

        var healed = store.Heal("vecna", 1);

        Assert.NotNull(healed);
        Assert.Equal(1, healed!.CurrentHp);
        Assert.Equal(VitalStatus.Alive, healed.Status);
        Assert.Equal(new DeathSaves(0, 0), healed.DeathSaves);
    }

    [Fact]
    public void MedicineStabilizesOnlyDying()
    {
        var store = NewStore();
        store.ApplyDamage("vecna", 12);

        var stabilized = store.Stabilize("vecna");
        Assert.True(stabilized.Ok, stabilized.Message);
        Assert.Equal(VitalStatus.Stable, stabilized.Sheet!.Status);
        Assert.Equal(new DeathSaves(0, 0), stabilized.Sheet.DeathSaves);

        var again = store.Stabilize("vecna");
        Assert.False(again.Ok);
        Assert.Contains("不是濒死", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeSuccessesMakeTheTargetStable()
    {
        var sheet = Dying();

        for (var i = 0; i < DeathSaveEngine.SavesToResolve; i++)
        {
            var result = DeathSaveEngine.Roll(sheet, new FixedRandom(12));
            sheet = result.Sheet;

            if (i < DeathSaveEngine.SavesToResolve - 1)
            {
                Assert.Equal(VitalStatus.Dying, result.Status);
            }
        }

        Assert.Equal(VitalStatus.Stable, sheet.Status);

        // PHB：成为 Stable 时成功/失败都清零（不是保留 3/3）。
        Assert.Equal(new DeathSaves(0, 0), sheet.DeathSaves);
    }

    [Fact]
    public void ThreeFailuresKill()
    {
        var sheet = Dying();

        for (var i = 0; i < DeathSaveEngine.SavesToResolve; i++)
        {
            sheet = DeathSaveEngine.Roll(sheet, new FixedRandom(9)).Sheet;
        }

        Assert.Equal(VitalStatus.Dead, sheet.Status);
        Assert.Equal(DeathSaveEngine.SavesToResolve, sheet.DeathSaves!.Failures);
    }

    [Fact]
    public void NaturalOneCountsTwoFailures()
    {
        var result = DeathSaveEngine.Roll(Dying(), new FixedRandom(1));

        Assert.Equal(2, result.Saves.Failures);
        Assert.Equal(VitalStatus.Dying, result.Status);
        Assert.Contains("两次失败", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NaturalTwentyRevivesWithOneHp()
    {
        var result = DeathSaveEngine.Roll(Dying(), new FixedRandom(20));

        Assert.Equal(VitalStatus.Alive, result.Status);
        Assert.Equal(1, result.Sheet.CurrentHp);
        Assert.Equal(new DeathSaves(0, 0), result.Sheet.DeathSaves);
    }

    [Fact]
    public void OnlyDyingTargetsRoll()
    {
        var alive = new Sheet { CharacterId = "vecna", MaxHp = 12, CurrentHp = 12, Status = VitalStatus.Alive };

        var result = DeathSaveEngine.Roll(alive, new FixedRandom(12));

        Assert.False(result.Required);
        Assert.Equal(VitalStatus.Alive, result.Status);
    }

    private static Sheet Dying() => new()
    {
        CharacterId = "vecna",
        MaxHp = 12,
        CurrentHp = 0,
        TempHp = 0,
        Status = VitalStatus.Dying,
        DeathSaves = new DeathSaves(0, 0),
    };

    private static SheetStore NewStore()
    {
        var store = new SheetStore();

        store.Put(new Sheet
        {
            CharacterId = "vecna",
            MaxHp = 12,
            CurrentHp = 12,
            TempHp = 0,
        });

        return store;
    }

    /// <summary>固定骰面：<see cref="DeathSaveEngine.Roll"/> 只调 <c>Next(1, 21)</c>，覆写它就够。</summary>
    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }
}
