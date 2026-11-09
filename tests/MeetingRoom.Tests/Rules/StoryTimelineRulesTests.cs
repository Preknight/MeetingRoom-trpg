using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Rules;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Rules;

/// <summary>
/// V3 的三类判定记录（<c>kind=check / attack / damage</c>）：
/// <b>只追加、不进重放、不推 revision</b>——与 <c>kind=roll</c> 同一条规则。
/// </summary>
public sealed class StoryTimelineRulesTests
{
    [Fact]
    public void RuleRecordsArePersistedWithoutBumpingRevision()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        var timeline = new StoryTimeline(path);
        timeline.Append("导演旁白", [new StoryLine("ella", "我把灯举高。")]);
        Assert.Equal(1, timeline.Revision);

        var check = D20.RollCheck(12, [new RollSource("熟练", 2)], random: new Random(3));
        var attack = D20.RollAttack(10, [new RollSource("命中", 5)], random: new Random(4));
        var damage = D20.RollDamage(
            [new DamageRollPart("1d8+3", "slashing")],
            new DamageDefenses { Resistances = ["slashing"] },
            random: new Random(5));

        timeline.AppendCheck("ella", "save", "抵抗恐惧", 12, check);
        timeline.AppendAttack("ella", "Longsword|PHB", 10, attack);
        timeline.AppendDamage("ella", "Goblin|MM", damage);

        // 三条记录都不推 revision（它们不是"这一幕"）。
        Assert.Equal(1, timeline.Revision);

        var reloaded = new StoryTimeline(path);

        Assert.Equal(1, reloaded.Revision);
        Assert.Single(reloaded.Turns);

        var storedCheck = Assert.Single(reloaded.CheckRecords);
        Assert.Equal("save", storedCheck.Kind);
        Assert.Equal("抵抗恐惧", storedCheck.Purpose);
        Assert.NotNull(storedCheck.Roll);
        Assert.Equal(check.Natural, storedCheck.Roll!.Natural);
        Assert.Equal(check.Total, storedCheck.Roll.Total);

        var storedAttack = Assert.Single(reloaded.AttackRecords);
        Assert.Equal("Longsword|PHB", storedAttack.WeaponId);
        Assert.Equal(10, storedAttack.ArmorClass);

        var storedDamage = Assert.Single(reloaded.DamageRecords);
        Assert.Equal("Goblin|MM", storedDamage.Target);
        Assert.Equal(damage.RawTotal, storedDamage.RawTotal);
        Assert.Equal(damage.Total, storedDamage.Total);
        Assert.Equal("resisted", Assert.Single(storedDamage.Buckets).Applied);
    }

    [Fact]
    public void DamageBucketsSurviveTheRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");

        var timeline = new StoryTimeline(path);
        var damage = D20.RollDamage(
            [new DamageRollPart("2d6", "fire"), new DamageRollPart("2d6", "cold")],
            new DamageDefenses { Resistances = ["fire"], Immunities = ["cold"] },
            random: new Random(21));

        timeline.AppendDamage("ella", "Goblin|MM", damage);

        var reloaded = new StoryTimeline(path);
        var stored = Assert.Single(reloaded.DamageRecords);

        Assert.Equal(2, stored.Buckets.Count);
        Assert.Equal(0, stored.Buckets.Single(bucket => bucket.Type == "cold").Effective);
        Assert.Equal("immune", stored.Buckets.Single(bucket => bucket.Type == "cold").Applied);
        Assert.Equal(damage.Total, stored.Total);
    }
}
