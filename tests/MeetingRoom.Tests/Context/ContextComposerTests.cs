using MeetingRoom.Core.Context;
using MeetingRoom.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Context;

/// <summary>
/// 上下文装配与前缀指纹（实现步骤 4.3）：
/// 共享前缀三段（system prompt + tools + 会议公共节）对所有会话字节一致，角色差异只进私有节。
/// </summary>
public sealed class ContextComposerTests
{
    [Fact]
    public void SharedPrefixIsIdenticalAcrossParticipantsWhileRoleSectionDiffers()
    {
        var productManager = Composer("product-manager", roleSection: "你是产品经理");
        var salesManager = Composer("sales-manager", roleSection: "你是销售经理");

        Assert.Equal(productManager.PrefixFingerprint, salesManager.PrefixFingerprint);
        Assert.NotEqual(productManager.RoleSectionFingerprint, salesManager.RoleSectionFingerprint);
    }

    [Fact]
    public void ChangingSharedSectionOnlyAffectsThePrefix()
    {
        var before = Composer("product-manager", sharedSection: "议题：定价");
        var after = Composer("product-manager", sharedSection: "议题：定价与上线范围");

        Assert.NotEqual(before.PrefixFingerprint, after.PrefixFingerprint);

        ChatMessage[] history = [new(ChatRole.User, "上一轮：先做企业版")];
        var a = before.Compose("product-manager", history, "请给出结论。");
        var b = after.Compose("product-manager", history, "请给出结论。");

        // 变化点之后的段（角色卡节 / 私有历史 / 本轮任务）完全不受影响。
        Assert.Equal(
            a.Messages.Skip(ContextComposer.PrefixMessageCount).Select(m => m.Text),
            b.Messages.Skip(ContextComposer.PrefixMessageCount).Select(m => m.Text));

        // 而共享节本身确实变了。
        Assert.NotEqual(a.Messages[1].Text, b.Messages[1].Text);
    }

    [Fact]
    public void MessageLayoutMatchesTheFourSections()
    {
        var composer = Composer("product-manager");
        ChatMessage[] history = [new(ChatRole.User, "历史问题"), new(ChatRole.Assistant, "历史回答")];

        var composed = composer.Compose("product-manager", history, "本轮任务");

        // 3 条共享/角色 system（system prompt、会议公共节、角色卡节）+ 2 条私有历史 + 1 条本轮任务
        Assert.Equal(6, composed.Messages.Count);
        Assert.All(composed.Messages.Take(3), message => Assert.Equal(ChatRole.System, message.Role));
        Assert.Equal("历史问题", composed.Messages[3].Text);
        Assert.Equal("历史回答", composed.Messages[4].Text);
        Assert.Equal("本轮任务", composed.Messages[^1].Text);
        Assert.Equal(2, ContextComposer.PrefixMessageCount);
    }

    [Fact]
    public void MaterialIndexGoesThroughTheVisibilityCheck()
    {
        var composer = new ContextComposer(
            "product-manager",
            "系统提示",
            [],
            new BlockingStrategy("mat-001"),
            new ComposeRequest
            {
                SharedSection = "议题：定价",
                RoleSection = "你是产品经理",
                Materials = [Entry("mat-001"), Entry("mat-002")],
            });

        var shared = composer.Compose("product-manager", [], "请给出结论。").Messages[1].Text!;

        // 装配路径确实走了 VisibleTo：被挡住的资料没有进共享节。
        Assert.DoesNotContain("mat-001", shared, StringComparison.Ordinal);
        Assert.Contains("mat-002", shared, StringComparison.Ordinal);
        Assert.Contains("[常驻资料]", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMaterialsMeansNoIndexSection()
    {
        var shared = Composer("product-manager").Compose("product-manager", [], "任务").Messages[1].Text!;

        Assert.DoesNotContain("[常驻资料]", shared, StringComparison.Ordinal);
    }

    private static ContextComposer Composer(
        string participantId,
        string sharedSection = "议题：定价",
        string roleSection = "你是产品经理") =>
        new(
            participantId,
            "同一份系统提示",
            [],
            new MeetingStrategy(),
            new ComposeRequest { SharedSection = sharedSection, RoleSection = roleSection });

    private static MaterialIndexEntry Entry(string id) => new(new TestMaterial(id), $"{id}：标题");

    private sealed record TestMaterial(string Id) : IMaterialView
    {
        public string Author => "sales-manager";

        public string Status => "adopted";
    }

    /// <summary>测试用策略：只挡住指定的一份资料，证明判定点真的在装配路径上。</summary>
    private sealed class BlockingStrategy(string blockedId) : IOrchestrationStrategy
    {
        public string Name => "test";

        public TurnDispatch Dispatch => TurnDispatch.Parallel;

        public CommitBoundary CommitBoundary => CommitBoundary.MaterialAdopted;

        public bool InterruptsOnMaterialChange => true;

        public bool VisibleTo(string participantId, IMaterialView material) => material.Id != blockedId;
    }
}
