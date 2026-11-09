using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Rules;

using Xunit.Sdk;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// D2 法术位与施法校验：槽位消耗 / 耗尽、升环伤害、戏法档位、长休短休、非施法者拒绝。
/// </summary>
public sealed class SpellSlotTests
{
    [Fact]
    public void SpendingASlotIncrementsUsed()
    {
        var store = NewStore();

        var (ok, code, message, sheet) = store.SpendSlot("ella", 3);

        Assert.True(ok, $"{code} {message}");
        Assert.Equal(1, sheet!.SpellSlots[3].Used);
        Assert.Equal(0, sheet.SpellSlots[3].Remaining);
    }

    [Fact]
    public void ExhaustedSlotIsRejectedWithNoSlot()
    {
        var store = NewStore();
        store.SpendSlot("ella", 3);

        var (ok, code, message) = store.CanSpendSlot("ella", 3);

        Assert.False(ok);
        Assert.Equal("no-slot", code);
        Assert.Contains("3 环", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLevelIsTreatedAsNoSlot()
    {
        var store = NewStore();

        var (ok, code, _) = store.CanSpendSlot("ella", 5);

        Assert.False(ok);
        Assert.Equal("no-slot", code);
    }

    [Fact]
    public void NonSpellcasterIsRejected()
    {
        var store = new SheetStore();
        store.Put(new Sheet { CharacterId = "vecna", MaxHp = 40, CurrentHp = 40, TempHp = 0 });

        var (ok, code, message) = store.CanSpendSlot("vecna", 3);

        Assert.False(ok);
        Assert.Equal("not-a-spellcaster", code);
        Assert.Contains("不是施法者", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UpcastingAddsOneDiePerLevel()
    {
        var request = new CombatActionRequest
        {
            ActorId = "ella",
            TargetId = "vecna",
            Kind = CombatActionKind.CastSpell,
            SpellId = "Fireball|PHB",
            CastAtLevel = 4,
            SaveDc = 15,
            TargetSaveBonus = 0,
        };

        var result = CombatEngine.Resolve(Rules(), request, new Random(1));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(4, result.CastAtLevel);
        Assert.True(result.ScalingApplied);
        Assert.Equal("9d6", result.DamageDice);   // 8d6 基础 + 1 段 1d6
    }

    [Fact]
    public void BaseLevelKeepsTheBaseDice()
    {
        var request = new CombatActionRequest
        {
            ActorId = "ella",
            TargetId = "vecna",
            Kind = CombatActionKind.CastSpell,
            SpellId = "Fireball|PHB",
            SaveDc = 15,
        };

        var result = CombatEngine.Resolve(Rules(), request, new Random(1));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, result.CastAtLevel);
        Assert.Equal("8d6", result.DamageDice);
    }

    [Fact]
    public void TooLowACastLevelIsRejected()
    {
        var request = new CombatActionRequest
        {
            ActorId = "ella",
            TargetId = "vecna",
            Kind = CombatActionKind.CastSpell,
            SpellId = "Fireball|PHB",
            CastAtLevel = 2,
        };

        var result = CombatEngine.Resolve(Rules(), request, new Random(1));

        Assert.False(result.Ok);
        Assert.Equal("slot-too-low", result.Code);
    }

    [Fact]
    public void UnknownSpellIsRejectedBeforeAnythingElse()
    {
        var request = new CombatActionRequest
        {
            ActorId = "ella",
            TargetId = "vecna",
            Kind = CombatActionKind.CastSpell,
            SpellId = "Wish|PHB",
        };

        var (spell, code, message) = CombatEngine.ValidateSpell(Rules(), request);

        Assert.Null(spell);
        Assert.Equal("unknown-spell", code);
        Assert.Contains("Wish|PHB", message!, StringComparison.Ordinal);
    }

    [Fact]
    public void LongRestRestoresSlotsHpAndClearsDeathSaves()
    {
        var store = NewStore();
        store.SpendSlot("ella", 3);
        store.SpendSlot("ella", 4);
        store.ApplyDamage("ella", 10);
        store.ApplyDamage("ella", 10);

        var rested = store.LongRest("ella");

        Assert.NotNull(rested);
        Assert.Equal(0, rested!.SpellSlots[3].Used);
        Assert.Equal(0, rested.SpellSlots[4].Used);
        Assert.Equal(30, rested.CurrentHp);
        Assert.Equal(VitalStatus.Alive, rested.Status);
        Assert.Equal(new DeathSaves(0, 0), rested.DeathSaves);
    }

    [Fact]
    public void ShortRestKeepsSlotsAndSaysPactMagicIsNotSupported()
    {
        var store = NewStore();
        store.SpendSlot("ella", 3);

        var (ok, message, sheet) = store.ShortRest("ella");

        Assert.True(ok);
        Assert.Equal(1, sheet!.SpellSlots[3].Used);
        Assert.Contains("Pact Magic 暂不支持", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CantripScalingFollowsCharacterLevel()
    {
        var path = Environment.GetEnvironmentVariable("MEETINGROOM_RULES_DATA");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw SkipException.ForSkip("真实规则包不可用（MEETINGROOM_RULES_DATA）。");
        }

        var rules = RulesDataLoader.Load(path);

        CombatActionResult Cast(int characterLevel)
        {
            var request = new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "vecna",
                Kind = CombatActionKind.CastSpell,
                SpellId = "Fire Bolt|XPHB",
                AttackBonus = 50,
                TargetArmorClass = 1,
                CharacterLevel = characterLevel,
            };

            return CombatEngine.Resolve(rules, request, new Random(2));
        }

        var low = Cast(1);
        var high = Cast(17);

        Assert.True(low.Ok, low.Message);
        Assert.True(high.Ok, high.Message);
        Assert.Equal("1d10", low.DamageDice);
        Assert.Equal("4d10", high.DamageDice);
        Assert.Equal(0, low.CastAtLevel);
    }

    private static SheetStore NewStore()
    {
        var store = new SheetStore();

        store.Put(new Sheet
        {
            CharacterId = "ella",
            MaxHp = 30,
            CurrentHp = 30,
            TempHp = 0,
            SpellSlots = new Dictionary<int, SpellSlot>
            {
                [3] = new(1, 0),
                [4] = new(1, 0),
            },
        });

        return store;
    }

    private static RulesData Rules() => RulesDataLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "Rules", "rules-sample.json"));
}
