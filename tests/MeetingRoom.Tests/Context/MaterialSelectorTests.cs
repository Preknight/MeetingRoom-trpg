using MeetingRoom.Core.Context;
using MeetingRoom.Core.Materials;

namespace MeetingRoom.Tests.Context;

/// <summary>
/// 常驻 / 触发式资料的选择（设计文档 §7.2 / 实现步骤 §八）：
/// 常驻有上限、触发式按关键词命中、预算超限时按相关性截断（不整段丢弃）、冷却期内不重复注入。
/// </summary>
public sealed class MaterialSelectorTests
{
    [Fact]
    public void AdoptedMaterialsBecomeAlwaysEntriesUpToTheLimit()
    {
        var selector = new MaterialSelector(alwaysMaxCount: 2, triggeredTokenBudget: 0, triggeredCooldownRounds: 0);
        var candidates = new[] { Adopted("mat-001", "定价"), Adopted("mat-002", "范围"), Adopted("mat-003", "排期") };

        var selection = selector.Select("随便看看", round: 1, candidates);

        Assert.Equal(2, selection.AlwaysCount);
        Assert.Equal(0, selection.TriggeredCount);
        Assert.Equal(2, selection.Entries.Count);
    }

    [Fact]
    public void OnlyAdoptedMaterialsEnterTheIndex()
    {
        var selector = new MaterialSelector(12, 2000, 0);
        var candidates = new[]
        {
            Adopted("mat-001", "已采纳"),
            Card("mat-002", "还在初审", MaterialStatus.HostReview),
            Card("mat-003", "被驳回", MaterialStatus.Rejected),
        };

        var selection = selector.Select("已采纳", round: 1, candidates);

        Assert.Single(selection.Entries);
        Assert.Equal("mat-001", selection.Entries[0].Id);
    }

    [Fact]
    public void TriggeredEntriesAreMatchedByKeywordAndCappedByBudget()
    {
        // 常驻 1 份、触发式预算只够放 1 份（每份约 title+summary+path 的字符数）。
        var selector = new MaterialSelector(alwaysMaxCount: 1, triggeredTokenBudget: 55, triggeredCooldownRounds: 0);

        var candidates = new[]
        {
            Adopted("mat-001", "定价基线"),
            Adopted("mat-002", "定价与上线范围"),
            Adopted("mat-003", "定价与上线范围的排期"),
        };

        var selection = selector.Select("定价 上线范围", round: 1, candidates);

        // 常驻 1 份 + 触发式放进预算内的若干份；放不下的计入 skippedByBudget 而不是整段丢弃。
        Assert.Equal(1, selection.AlwaysCount);
        Assert.True(selection.TriggeredCount >= 1, "至少应有一份触发式资料命中并进入预算");
        Assert.Equal(selection.AlwaysCount + selection.TriggeredCount, selection.Entries.Count);
        Assert.True(selection.SkippedByBudget > 0, "预算不足的资料应当被计入 skippedByBudget");
    }

    [Fact]
    public void TriggeredEntriesAreNotRepeatedWithinTheCooldownWindow()
    {
        var selector = new MaterialSelector(alwaysMaxCount: 0, triggeredTokenBudget: 2000, triggeredCooldownRounds: 3);
        var candidates = new[] { Adopted("mat-001", "定价基线") };

        var first = selector.Select("定价", round: 1, candidates);
        var second = selector.Select("定价", round: 2, candidates);
        var fourth = selector.Select("定价", round: 4, candidates);

        Assert.Equal(1, first.TriggeredCount);
        Assert.Equal(0, second.TriggeredCount);
        Assert.Equal(1, second.SkippedByCooldown);
        Assert.Equal(1, fourth.TriggeredCount); // 冷期已过
    }

    [Fact]
    public void UnrelatedTaskMatchesNothing()
    {
        var selector = new MaterialSelector(0, 2000, 0);
        var selection = selector.Select("完全不相关的词", round: 1, [Adopted("mat-001", "定价基线")]);

        Assert.Empty(selection.Entries);
    }

    private static MaterialCard Adopted(string id, string title) => Card(id, title, MaterialStatus.Adopted);

    private static MaterialCard Card(string id, string title, MaterialStatus status) => new()
    {
        Id = id,
        Title = title,
        Summary = $"{title}的摘要",
        Path = $"materials/{id}.md",
        Author = "sales-manager",
        Status = status,
    };
}
