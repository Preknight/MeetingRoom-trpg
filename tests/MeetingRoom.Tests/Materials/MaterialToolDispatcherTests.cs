using System.Text.Json;
using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Tests.Support;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Materials;

/// <summary>
/// 工具面（实现步骤 §6.2 的验收）：schema 与角色无关、缺 summary 时程序兜底、
/// 资料可见性经过判定点、索引里永远没有正文。
/// </summary>
public sealed class MaterialToolDispatcherTests
{
    [Fact]
    public void ToolSchemaIsIdenticalForEveryParticipantAndSession()
    {
        using var temp = new TempDir();

        // 工具集是常量：换谁调用、换哪场会话，schema 字节完全一致（不按角色裁剪，§7.1）。
        var first = JsonSerializer.Serialize(NewDispatcher(temp).Tools, AIJsonUtilities.DefaultOptions);
        var second = JsonSerializer.Serialize(NewDispatcher(temp).Tools, AIJsonUtilities.DefaultOptions);

        Assert.Equal(first, second);
        Assert.Contains("list_materials", first, StringComparison.Ordinal);
        Assert.Contains("read_material", first, StringComparison.Ordinal);
        Assert.Contains("recall_speech", first, StringComparison.Ordinal);
        Assert.Contains("submit_material", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolSchemaCarriesNoRoleOrCallerFields()
    {
        using var temp = new TempDir();
        var json = JsonSerializer.Serialize(NewDispatcher(temp).Tools, AIJsonUtilities.DefaultOptions);

        Assert.DoesNotContain("role", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("participantId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readOnly", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@me", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmittingWithoutSummaryFallsBackToBodyHeadAndIsMarked()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);
        var dispatcher = new MaterialToolDispatcher(store, new MeetingStrategy(), new InMemorySpeechLog());

        var result = await dispatcher.InvokeAsync(
            "product-manager",
            Call("submit_material", ("title", "定价建议"), ("summary", string.Empty), ("body", new string('正', 500))),
            CancellationToken.None);

        Assert.Contains("摘要来源 fellback", Text(result), StringComparison.Ordinal);

        var card = Assert.Single(store.List());
        Assert.Equal("fellback", card.SummarySource);
        Assert.Equal(300, card.Summary.Length);
        Assert.Equal(new string('正', 300), card.Summary);
        Assert.Equal(MaterialStatus.HostReview, card.Status);
        Assert.Equal("product-manager", card.Author);
    }

    [Fact]
    public async Task ListMaterialsReturnsIndexOnlyAndRespectsVisibility()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);
        await store.SubmitAsync(new SubmitRequest { Author = "pm", Title = "公开资料", Summary = "摘要", Body = "绝密正文内容" });
        await store.SubmitAsync(new SubmitRequest { Author = "pm", Title = "受限资料", Summary = "摘要", Body = "另一段正文" });

        var dispatcher = new MaterialToolDispatcher(store, new BlockingStrategy("mat-002"), new InMemorySpeechLog());

        var result = Text(await dispatcher.InvokeAsync("pm", Call("list_materials"), CancellationToken.None));

        Assert.Contains("mat-001", result, StringComparison.Ordinal);
        Assert.DoesNotContain("mat-002", result, StringComparison.Ordinal);          // 可见性判定点生效
        Assert.DoesNotContain("绝密正文内容", result, StringComparison.Ordinal);      // 索引里没有正文
        Assert.Contains("materials/", result, StringComparison.Ordinal);             // 给的是路径
    }

    [Fact]
    public async Task ReadMaterialRefusesInvisibleMaterialAndReturnsBodyOtherwise()
    {
        using var temp = new TempDir();
        var store = NewStore(temp);
        await store.SubmitAsync(new SubmitRequest { Author = "pm", Title = "受限资料", Summary = "s", Body = "正文内容在这里" });

        var blocked = new MaterialToolDispatcher(store, new BlockingStrategy("mat-001"), new InMemorySpeechLog());
        var refused = Text(await blocked.InvokeAsync("pm", Call("read_material", ("id", "mat-001")), CancellationToken.None));
        Assert.Contains("不可以读取", refused, StringComparison.Ordinal);

        var allowed = new MaterialToolDispatcher(store, new MeetingStrategy(), new InMemorySpeechLog());
        var body = Text(await allowed.InvokeAsync("pm", Call("read_material", ("id", "mat-001")), CancellationToken.None));
        Assert.Contains("正文内容在这里", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecallSpeechSearchesHistory()
    {
        using var temp = new TempDir();
        var speech = new InMemorySpeechLog();
        speech.Append(new SpeechRecord("turn-1", "product-manager", "speech", "先做两档，只考核试用转化率", DateTimeOffset.UtcNow));
        speech.Append(new SpeechRecord("turn-1", "engineer", "speech", "按席位分层要改计费口径", DateTimeOffset.UtcNow));

        var dispatcher = new MaterialToolDispatcher(NewStore(temp), new MeetingStrategy(), speech);

        var hit = Text(await dispatcher.InvokeAsync("pm", Call("recall_speech", ("query", "试用转化率")), CancellationToken.None));

        Assert.Contains("试用转化率", hit, StringComparison.Ordinal);
        Assert.DoesNotContain("计费口径", hit, StringComparison.Ordinal);

        var miss = Text(await dispatcher.InvokeAsync("pm", Call("recall_speech", ("query", "毫无关系的词")), CancellationToken.None));
        Assert.Contains("没有命中", miss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownToolIsReportedInsteadOfThrowing()
    {
        using var temp = new TempDir();
        var dispatcher = NewDispatcher(temp);

        var result = Text(await dispatcher.InvokeAsync("pm", Call("delete_everything"), CancellationToken.None));

        Assert.Contains("未知工具", result, StringComparison.Ordinal);
    }

    private static MaterialStore NewStore(TempDir temp) =>
        new(Path.Combine(temp.Root, "meetings", "meet-001"), new MaterialSummaryPolicy());

    private static MaterialToolDispatcher NewDispatcher(TempDir temp) =>
        new(NewStore(temp), new MeetingStrategy(), new InMemorySpeechLog());

    private static FunctionCallContent Call(string name, params (string Key, object? Value)[] arguments) =>
        new("call-1", name, arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static string Text(FunctionResultContent content) => content.Result?.ToString() ?? string.Empty;

    /// <summary>挡住指定资料的策略：证明装配/工具路径确实经过可见性判定点。</summary>
    private sealed class BlockingStrategy(string blockedId) : IOrchestrationStrategy
    {
        public string Name => "blocking-test";

        public TurnDispatch Dispatch => TurnDispatch.Parallel;

        public CommitBoundary CommitBoundary => CommitBoundary.MaterialAdopted;

        public bool InterruptsOnMaterialChange => true;

        public bool VisibleTo(string participantId, IMaterialView material) => material.Id != blockedId;
    }
}
