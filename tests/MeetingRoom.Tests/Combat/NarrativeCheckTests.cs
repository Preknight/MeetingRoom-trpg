using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Rules;
using MeetingRoom.Tests.Fakes;

namespace MeetingRoom.Tests.Combat;

/// <summary>
/// 叙事检定（C2）：四形态、暗骰两面、防刷、Stakes，以及"不碰世界状态"。
/// <para>技能 → 属性从规则包的 <c>skills</c> 段查，测试里也只用数据驱动，不硬编码 18 项。</para>
/// </summary>
public sealed class NarrativeCheckTests
{
    [Fact]
    public void SkillResolvesItsAbilityFromRulesData()
    {
        var result = NarrativeCheckEngine.Resolve(
            LoadFixture(),
            new CheckRequest { ActorId = "ella", Kind = CheckKind.Skill, Skill = "游说", Dc = 10, Modifier = 3 },
            new CheckHistory(),
            "chk-1",
            new Random(1));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Persuasion|PHB", result.Skill);
        Assert.Equal("cha", result.Ability);
        Assert.Equal(10, result.Dc);
    }

    [Fact]
    public void UnknownSkillsAreRejected()
    {
        var result = NarrativeCheckEngine.Resolve(
            LoadFixture(),
            new CheckRequest { ActorId = "ella", Kind = CheckKind.Skill, Skill = "Basket Weaving", Dc = 10 },
            new CheckHistory(),
            "chk-1");

        Assert.False(result.Ok);
        Assert.Equal("unknown-skill", result.Code);
        Assert.Contains("Basket Weaving", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassiveIsTenPlusTheModifierAndDoesNotRoll()
    {
        var result = NarrativeCheckEngine.Resolve(
            LoadFixture(),
            new CheckRequest { ActorId = "ella", Kind = CheckKind.Passive, Skill = "perception", Dc = 15, Modifier = 5 },
            new CheckHistory(),
            "chk-1");

        Assert.True(result.Ok, result.Message);
        Assert.Null(result.Roll);
        Assert.Equal(15, result.PassiveValue);
        Assert.True(result.Success);
        Assert.Contains("被动 15", result.DmFacingSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedChecksEscalateThenGetRejected()
    {
        var history = new CheckHistory();
        var rules = LoadFixture();

        var first = NarrativeCheckEngine.Resolve(rules, Request(), history, "chk-1", new Random(1));
        var second = NarrativeCheckEngine.Resolve(rules, Request(), history, "chk-2", new Random(1));
        var third = NarrativeCheckEngine.Resolve(rules, Request(), history, "chk-3", new Random(1));

        Assert.True(first.Ok);
        Assert.False(first.DcAdjusted);
        Assert.Equal(10, first.Dc);

        // 同一目标同一理由：第二次 DC +5。
        Assert.True(second.Ok);
        Assert.True(second.DcAdjusted);
        Assert.Equal(15, second.Dc);

        // 第三次直接拒绝（"你已经试过"）。
        Assert.False(third.Ok);
        Assert.Equal("check-spam", third.Code);
        Assert.Contains("不吃这套", third.Message, StringComparison.Ordinal);

        static CheckRequest Request() => new()
        {
            ActorId = "ella",
            Kind = CheckKind.Skill,
            Skill = "persuasion",
            Dc = 10,
            Modifier = 3,
            TargetId = "guard",
            Reason = "说服守卫放行",
        };
    }

    [Fact]
    public void SecretChecksKeepNumbersOutOfTheCharacterFacingSummary()
    {
        var result = NarrativeCheckEngine.Resolve(
            LoadFixture(),
            new CheckRequest
            {
                ActorId = "ella",
                Kind = CheckKind.Skill,
                Skill = "insight",
                Dc = 14,
                Modifier = 2,
                Visibility = CheckVisibility.Secret,
                Reason = "看穿商人是否在撒谎",
            },
            new CheckHistory(),
            "chk-1",
            new Random(2));

        Assert.True(result.Ok, result.Message);
        Assert.True(result.IsSecret);

        var character = result.CharacterFacingSummary();

        Assert.Contains("无法确定", character, StringComparison.Ordinal);
        Assert.DoesNotContain("成功", character);
        Assert.DoesNotContain("失败", character);
        Assert.DoesNotContain(result.Roll!.Total.ToString(), character);

        // DM 面照记数值（暗骰不是不记，只是不给角色）。
        Assert.Contains(result.Roll.Total.ToString(), result.DmFacingSummary(), StringComparison.Ordinal);
        Assert.Contains("暗骰", result.DmFacingSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContestTiesGoToTheDefender()
    {
        var result = NarrativeCheckEngine.Resolve(
            LoadFixture(),
            new CheckRequest
            {
                ActorId = "ella",
                Kind = CheckKind.Contest,
                Skill = "Sleight of Hand",
                Modifier = 3,
                Reason = "顺走钥匙",
                OpposedBy = new CheckOpposition { ActorId = "guard", Skill = "perception", Modifier = 3 },
            },
            new CheckHistory(),
            "chk-1",
            new Random(5));

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Roll);
        Assert.NotNull(result.OppositionRoll);

        // 平手按防守方胜：只有严格大于才算成功。
        Assert.Equal(result.Roll!.Total > result.OppositionRoll!.Total, result.Success);
    }

    [Fact]
    public async Task RequestCheckDoesNotTouchWorldState()
    {
        await using var session = NewSession();

        var before = session.World.Version;

        var outcome = await session.RequestCheckAsync(
            new CheckRequest
            {
                ActorId = "ella",
                Kind = CheckKind.Skill,
                Skill = "perception",
                Dc = 10,
                Modifier = 3,
            },
            CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(before, session.World.Version);
        Assert.Single(session.Timeline.CheckRecords);
    }

    [Fact]
    public async Task RequestCheckWithoutRulesSaysSoInsteadOfGuessing()
    {
        await using var session = NewSession(withRules: false);

        var outcome = await session.RequestCheckAsync(
            new CheckRequest { ActorId = "ella", Kind = CheckKind.Skill, Skill = "perception", Dc = 10 },
            CancellationToken.None);

        Assert.False(outcome.Applied);
        Assert.Contains("规则包未加载", outcome.Message, StringComparison.Ordinal);
    }

    private static RulesData LoadFixture() => RulesDataLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "Rules", "rules-sample.json"));

    private static RoleplaySession NewSession(bool withRules = true)
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
                Rules = withRules ? LoadFixture() : null,
            },
            FakeChatClient.Text(1, TimeSpan.Zero),
            new EventBus(),
            cards);
    }
}
