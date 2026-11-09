using System.ComponentModel;
using System.Text.Json;
using MeetingRoom.Agent.Tools;
using MeetingRoom.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Materials;

/// <summary>
/// 会议模式的工具面（设计文档 §7.3）：<c>list_materials</c> / <c>read_material</c> /
/// <c>recall_speech</c> / <c>submit_material</c>。四条约束：
/// <list type="bullet">
///   <item>工具集<b>不按角色裁剪</b>——同一模式下所有会话的 tools 完全一致，否则 tools hash 会变、前缀缓存全 miss（§7.1）；</item>
///   <item>schema 里<b>没有</b>角色相关字段；</item>
///   <item>可见性统一走 <see cref="IOrchestrationStrategy.VisibleTo"/>（会议模式恒 true，但仍必须经过判定点，§11.3-2）；</item>
///   <item>正文只在 <c>read_material</c> 时按需拉取——卡片只给标题 + 摘要 + 路径（§5.3）。</item>
/// </list>
/// 实现放在 Core 而非 <c>Agent/Tools</c>：它需要资料库，而依赖方向固定为 Core → Agent（见设计文档 §三）。
/// </summary>
public sealed class MaterialToolDispatcher : IToolDispatcher
{
    private readonly MaterialStore _store;
    private readonly IOrchestrationStrategy _strategy;
    private readonly ISpeechLog _speech;
    private readonly int _listLimit;

    public MaterialToolDispatcher(
        MaterialStore store,
        IOrchestrationStrategy strategy,
        ISpeechLog speech,
        int listLimit = 20)
    {
        _store = store;
        _strategy = strategy;
        _speech = speech;
        _listLimit = Math.Clamp(listLimit, 1, 100);
        Tools = BuildTools();
    }

    public const string ListMaterials = "list_materials";

    public const string ReadMaterial = "read_material";

    public const string RecallSpeech = "recall_speech";

    public const string SubmitMaterial = "submit_material";

    /// <summary>工具集：同一模式下所有与会者共用（不按角色裁剪）。</summary>
    public IReadOnlyList<AITool> Tools { get; }

    public Task<FunctionResultContent> InvokeAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken) =>
        call.Name switch
        {
            ListMaterials => ListAsync(participantId, call),
            ReadMaterial => ReadAsync(participantId, call),
            RecallSpeech => RecallAsync(call),
            SubmitMaterial => SubmitAsync(participantId, call, cancellationToken),
            _ => Result(call, $"未知工具：{call.Name}"),
        };

    private Task<FunctionResultContent> ListAsync(string participantId, FunctionCallContent call)
    {
        var status = Text(call.Arguments, "status");
        var author = Text(call.Arguments, "author");
        var keyword = Text(call.Arguments, "keyword");

        var matches = _store.List()
            .Where(card => _strategy.VisibleTo(participantId, card))
            .Where(card => string.IsNullOrWhiteSpace(status) || string.Equals(card.Status.ToWire(), status, StringComparison.OrdinalIgnoreCase))
            .Where(card => string.IsNullOrWhiteSpace(author) || string.Equals(card.Author, author, StringComparison.OrdinalIgnoreCase))
            .Where(card => string.IsNullOrWhiteSpace(keyword)
                || card.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || card.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(_listLimit)
            .Select(IndexLine)
            .ToList();

        return Result(call, matches.Count == 0 ? "没有命中任何资料。" : string.Join('\n', matches));
    }

    private Task<FunctionResultContent> ReadAsync(string participantId, FunctionCallContent call)
    {
        var id = Text(call.Arguments, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return Result(call, "缺少参数 id。");
        }

        var card = _store.Find(id);
        if (card is null)
        {
            return Result(call, $"没有这份资料：{id}");
        }

        if (!_strategy.VisibleTo(participantId, card))
        {
            return Result(call, $"不可以读取 {id} 的正文。");
        }

        return Result(call, _store.ReadBody(id));
    }

    private Task<FunctionResultContent> RecallAsync(FunctionCallContent call)
    {
        var query = Text(call.Arguments, "query") ?? string.Empty;
        var limit = int.TryParse(Text(call.Arguments, "limit"), out var parsed) ? parsed : 5;

        var hits = _speech.Search(query, Math.Clamp(limit, 1, 20));

        return Result(
            call,
            hits.Count == 0 ? "没有命中历史发言。" : string.Join('\n', hits.Select(hit => $"{hit.Author}：{hit.Text}")));
    }

    private async Task<FunctionResultContent> SubmitAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        var title = Text(call.Arguments, "title");
        var summary = Text(call.Arguments, "summary");
        var body = Text(call.Arguments, "body");

        if (string.IsNullOrWhiteSpace(title))
        {
            return new FunctionResultContent(call.CallId, "缺少参数 title。");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new FunctionResultContent(call.CallId, "缺少参数 body（正文）。");
        }

        // summary 为空时由程序截取正文兜底并标 fellback（§5.3）——schema 里它是必填，这里再兜一层。
        var card = await _store.SubmitAsync(
            new SubmitRequest
            {
                Author = participantId,
                Title = title,
                Summary = summary,
                Body = body,
                BasedOnVersion = _store.Version,
            },
            cancellationToken).ConfigureAwait(false);

        return new FunctionResultContent(
            call.CallId,
            $"已提交 {card.Id}（{card.Title}），摘要来源 {card.SummarySource}，等待主持人初审。");
    }

    /// <summary>索引行：只有标题 + 摘要 + 路径，正文永远不在这里出现（渐进式披露，§5.3）。</summary>
    private static string IndexLine(MaterialCard card) =>
        $"{card.Id}｜{card.Title}｜{card.Status.ToWire()}｜{card.Author}｜{card.Summary}｜{card.Path}";

    private static Task<FunctionResultContent> Result(FunctionCallContent call, string text) =>
        Task.FromResult(new FunctionResultContent(call.CallId, text));

    /// <summary>
    /// 只用来生成 schema：真正的执行走 <see cref="InvokeAsync"/>（我们自己的 step 循环调度工具，
    /// 不用会自动跑完工具循环的封装，§6.6）。
    /// </summary>
    private static IReadOnlyList<AITool> BuildTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("按状态过滤：draft / host_review / user_review / adopted / rejected / parked")] string? status,
                [Description("按作者过滤（与会者 id）")] string? author,
                [Description("标题或摘要里的关键词")] string? keyword) => "schema",
            ListMaterials,
            "列出资料索引（只含标题、摘要、路径，不含正文）"),

        AIFunctionFactory.Create(
            ([Description("资料 id，如 mat-001")] string id) => "schema",
            ReadMaterial,
            "按需读取某份资料的正文"),

        AIFunctionFactory.Create(
            ([Description("检索关键词")] string query,
                [Description("最多返回几条，默认 5")] int? limit) => "schema",
            RecallSpeech,
            "检索历史发言"),

        AIFunctionFactory.Create(
            ([Description("资料标题")] string title,
                [Description("一句话摘要，不超过 300 汉字")] string summary,
                [Description("正文内容")] string body) => "schema",
            SubmitMaterial,
            "提交一份待审批资料（进入主持人初审）"),
    ];

    /// <summary>工具参数取值：模型给的多半是 JsonElement，测试里直接给字符串。</summary>
    private static string? Text(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => value.ToString(),
        };
    }
}
