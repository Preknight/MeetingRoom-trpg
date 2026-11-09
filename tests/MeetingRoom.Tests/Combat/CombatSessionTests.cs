using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Rules;
using MeetingRoom.Tests.Fakes;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 会话级 HP 结算（C3a）：攻击命中后真正扣目标 HP，并落进 <c>kind=damage</c>（含扣血前后）。
/// </summary>
public sealed class CombatSessionTests
{
    [Fact]
    public async Task AnAttackReducesTheTargetsHpAndRecordsIt()
    {
        var rules = LoadRules();
        var sheets = new SheetStore();
        sheets.Put(new Sheet { CharacterId = "vecna", MaxHp = 12, CurrentHp = 12, TempHp = 0 });

        await using var session = NewSession(rules, sheets);

        var started = await session.StartCombatAsync(
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, DexterityModifier = 3 },
                new CombatantSetup { Id = "vecna", Side = CombatSide.Foe, DexterityModifier = 2 },
            ],
            seed: 4,
            encounterId: "enc-1",
            CancellationToken.None);

        Assert.True(started.Applied, started.Message);

        var outcome = await session.SubmitIntentAsync(
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "vecna",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                DamageBonus = 3,
                DamageType = "slashing",
                TargetArmorClass = 1,
            },
            CancellationToken.None);

        Assert.True(outcome.Applied, outcome.Message);
        Assert.Contains("目标 HP", outcome.Message, StringComparison.Ordinal);

        var after = sheets.Get("vecna")!;
        Assert.True(after.CurrentHp < 12, $"目标应该掉血，实际还是 {after.CurrentHp}");

        var damage = Assert.Single(session.Timeline.DamageRecords);
        Assert.Equal(12, damage.HpBefore);
        Assert.Equal(after.CurrentHp, damage.HpAfter);
        Assert.Equal("alive", damage.Status);
    }

    [Fact]
    public async Task ATargetWithoutASheetIsToldSoInsteadOfSilentlyDroppingDamage()
    {
        await using var session = NewSession(LoadRules(), sheets: null);

        await session.StartCombatAsync(
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, DexterityModifier = 3 },
                new CombatantSetup { Id = "vecna", Side = CombatSide.Foe, DexterityModifier = 2 },
            ],
            seed: 4,
            encounterId: "enc-1",
            CancellationToken.None);

        var outcome = await session.SubmitIntentAsync(
            new CombatActionRequest
            {
                ActorId = "ella",
                TargetId = "vecna",
                WeaponId = "Longsword|PHB",
                AttackBonus = 5,
                DamageBonus = 3,
                DamageType = "slashing",
                TargetArmorClass = 1,
            },
            CancellationToken.None);

        Assert.True(outcome.Applied, outcome.Message);
        Assert.Contains("没有数值表", outcome.Message, StringComparison.Ordinal);

        var damage = Assert.Single(session.Timeline.DamageRecords);
        Assert.Null(damage.HpBefore);
        Assert.Null(damage.HpAfter);
    }

    private static RulesData LoadRules() => RulesDataLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "Rules", "rules-sample.json"));

    private static RoleplaySession NewSession(RulesData rules, SheetStore? sheets)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场。" },
            new RoleCard { Id = "ella", Name = "Ella", Body = "灯塔看守人的女儿。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇。",
                Situation = "暴雨夜。",
                NarratorId = "narrator",
                Rules = rules,
            },
            FakeChatClient.Text(1, TimeSpan.Zero),
            new EventBus(),
            cards,
            sheets: sheets);
    }
}
