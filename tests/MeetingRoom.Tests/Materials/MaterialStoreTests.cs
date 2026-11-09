using System.Text.Json;
using MeetingRoom.Core.Materials;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Materials;

/// <summary>
/// 资料库（实现步骤 §6.1 的验收）：
/// 六态两级状态机、采纳权在用户、正文路径防护、并发提交下 manifest 与 materialVersion 的一致性、
/// 摘要兜底与提交失败隔离。
/// </summary>
public sealed class MaterialStoreTests
{
    [Fact]
    public async Task HappyPathSubmitApproveAdopt()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var card = await store.SubmitAsync(NewSubmit());
        Assert.Equal(MaterialStatus.HostReview, card.Status);
        Assert.Equal(0, store.Version);

        var approved = await store.ReviewAsync(card.Id, ReviewDecision.Approve, ReviewActorKind.Host, "转用户终审");
        Assert.True(approved.Applied);
        Assert.Equal(MaterialStatus.UserReview, approved.Card!.Status);
        Assert.Equal("host", approved.Card.Reviewer);
        Assert.Equal(0, store.Version);

        var adopted = await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.User, "采纳");
        Assert.True(adopted.Applied);
        Assert.Equal(MaterialStatus.Adopted, adopted.Card!.Status);
        Assert.Equal("user", adopted.Card.Reviewer);
        Assert.Equal(1, store.Version);
    }

    [Fact]
    public async Task HostRejectThenAuthorReopenReturnsToDraft()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var card = await store.SubmitAsync(NewSubmit());

        var rejected = await store.ReviewAsync(card.Id, ReviewDecision.Reject, ReviewActorKind.Host, "摘要不够具体");
        Assert.True(rejected.Applied);
        Assert.Equal(MaterialStatus.Rejected, rejected.Card!.Status);
        Assert.Equal("摘要不够具体", rejected.Card.ReviewNote);

        var reopened = await store.ReviewAsync(card.Id, ReviewDecision.Reopen, ReviewActorKind.Member);
        Assert.True(reopened.Applied);
        Assert.Equal(MaterialStatus.Draft, reopened.Card!.Status);

        // draft → host_review 也是合法转移（作者重新提交）。
        var resubmitted = await store.ReviewAsync(card.Id, ReviewDecision.Submit, ReviewActorKind.Member);
        Assert.True(resubmitted.Applied);
        Assert.Equal(MaterialStatus.HostReview, resubmitted.Card!.Status);
    }

    [Fact]
    public async Task UserRejectAndParkBranchesWork()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var parked = await store.SubmitAsync(NewSubmit("会被搁置"));
        await store.ReviewAsync(parked.Id, ReviewDecision.Approve, ReviewActorKind.Host);
        var parkResult = await store.ReviewAsync(parked.Id, ReviewDecision.Park, ReviewActorKind.User, "等下一轮");
        Assert.Equal(MaterialStatus.Parked, parkResult.Card!.Status);

        // parked → draft → host_review 也能走通。
        Assert.Equal(MaterialStatus.Draft, (await store.ReviewAsync(parked.Id, ReviewDecision.Reopen, ReviewActorKind.User)).Card!.Status);

        var rejected = await store.SubmitAsync(NewSubmit("会被打回"));
        await store.ReviewAsync(rejected.Id, ReviewDecision.Approve, ReviewActorKind.Host);
        var rejectResult = await store.ReviewAsync(rejected.Id, ReviewDecision.Reject, ReviewActorKind.User);
        Assert.Equal(MaterialStatus.Rejected, rejectResult.Card!.Status);
        Assert.Equal("user", rejectResult.Card.Reviewer);
    }

    [Fact]
    public async Task IllegalTransitionsAndOverreachAreRejected()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var card = await store.SubmitAsync(NewSubmit());

        // ① 主持人越过用户直接采纳（host_review --adopt-->）。
        AssertRejected(await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.Host), "非法转移");

        // ② 主持人做终审才有的"搁置"。
        AssertRejected(await store.ReviewAsync(card.Id, ReviewDecision.Park, ReviewActorKind.Host), "非法转移");

        // ③ 初审阶段用户直接采纳（跳过主持人）。
        AssertRejected(await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.User), "非法转移");

        await store.ReviewAsync(card.Id, ReviewDecision.Approve, ReviewActorKind.Host);

        // ④ 终审阶段主持人替用户采纳 —— 状态合法但越权。
        AssertRejected(await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.Host), "越权");

        await store.ReviewAsync(card.Id, ReviewDecision.Reject, ReviewActorKind.User);
        await store.ReviewAsync(card.Id, ReviewDecision.Reopen, ReviewActorKind.User);

        // ⑤ 打回并重新激活回 draft 后，采纳依然非法（draft --adopt--> adopted 跳过两级）。
        AssertRejected(await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.User), "非法转移");

        // 不存在的资料。
        AssertRejected(await store.ReviewAsync("mat-999", ReviewDecision.Adopt, ReviewActorKind.User), "不存在");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../../../escape.md")]
    [InlineData("sub/../../escape.md")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\server\share\body.md")]
    public async Task BodyPathOutsideMaterialsDirectoryIsRejected(string bodyPath)
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        await Assert.ThrowsAsync<MaterialPathException>(() => store.SubmitAsync(new SubmitRequest
        {
            Author = "product-manager",
            Title = "越界尝试",
            BodyPath = bodyPath,
        }));
    }

    [Fact]
    public async Task SymlinkEscapingMaterialsDirectoryIsRejected()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var outside = temp.File("outside.md");
        await File.WriteAllTextAsync(outside, "外部内容");

        try
        {
            File.CreateSymbolicLink(Path.Combine(store.MaterialsDirectory, "link.md"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 没有创建符号链接的权限（Windows 需要开发者模式或管理员）：这条只能跳过。
            return;
        }

        await Assert.ThrowsAsync<MaterialPathException>(() => store.SubmitAsync(new SubmitRequest
        {
            Author = "product-manager",
            Title = "符号链接逃逸",
            BodyPath = "link.md",
        }));
    }

    [Fact]
    public async Task ConcurrentSubmissionsProduceMonotonicVersionAndValidManifest()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var cards = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SubmitAsync(new SubmitRequest
        {
            Author = $"participant-{i % 3}",
            Title = $"资料 {i}",
            Body = $"正文 {i}",
        })));

        Assert.Equal(20, store.List().Count);
        Assert.Equal(20, cards.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());

        // manifest 一直是合法 JSON，20 条资料都在。
        var json = await File.ReadAllTextAsync(store.ManifestPath);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(20, document.RootElement.GetProperty("materials").GetArrayLength());

        // 逐条走完两级审批 → materialVersion 每次采纳都严格 +1。
        var expected = 0;
        foreach (var card in cards)
        {
            await store.ReviewAsync(card.Id, ReviewDecision.Approve, ReviewActorKind.Host);
            var adopted = await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.User);
            Assert.True(adopted.Applied);
            expected++;
            Assert.Equal(expected, store.Version);
        }
    }

    [Fact]
    public async Task MissingSummaryFallsBackToBodyHeadAndIsMarked()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var card = await store.SubmitAsync(new SubmitRequest
        {
            Author = "product-manager",
            Title = "长正文资料",
            Body = new string('正', 500),
        });

        Assert.Equal("fellback", card.SummarySource);
        Assert.Equal(300, card.Summary.Length);
        Assert.Equal(new string('正', 300), card.Summary);
    }

    [Fact]
    public async Task ProvidedSummaryIsKeptButTruncated()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        var card = await store.SubmitAsync(new SubmitRequest
        {
            Author = "product-manager",
            Title = "带摘要资料",
            Summary = new string('摘', 400),
            Body = "正文",
        });

        Assert.Equal("tool", card.SummarySource);
        Assert.Equal(300, card.Summary.Length);
    }

    [Fact]
    public async Task BodySurvivesWhenManifestWriteFails()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);

        await store.SubmitAsync(NewSubmit("第一份"));

        // 用目录占住 manifest 的临时文件路径 → 状态结算必然失败。
        var blocked = store.ManifestPath + ".tmp";
        Directory.CreateDirectory(blocked);

        try
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                store.SubmitAsync(new SubmitRequest { Author = "product-manager", Title = "第二份", Body = "正文 2" }));
        }
        finally
        {
            Directory.Delete(blocked);
        }

        // 提交失败隔离（§11.3-4）：资料登记没成功，但正文没有被回滚——它留在磁盘上。
        Assert.Single(store.List());
        Assert.Equal(2, Directory.GetFiles(store.MaterialsDirectory, "*.md").Length);
    }

    private static MaterialStore NewStore(TempDir temp) =>
        new(Path.Combine(temp.Root, "meetings", "meet-001"), new MaterialSummaryPolicy());

    private static SubmitRequest NewSubmit(string title = "定价建议草案") => new()
    {
        Author = "product-manager",
        Title = title,
        Summary = "先做两档，只考核试用转化率",
        Body = "先做两档，只考核试用转化率。",
    };

    private static void AssertRejected(MaterialTransition transition, string keyword)
    {
        Assert.False(transition.Applied);
        Assert.NotNull(transition.Rejection);
        Assert.Contains(keyword, transition.Rejection!, StringComparison.Ordinal);
    }
}
