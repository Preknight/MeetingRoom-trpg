using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using MeetingRoom.Agent;
using MeetingRoom.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Context;

/// <summary>
/// 上下文装配器（设计文档 §7.1）。产出的消息序列分四段：
/// <code>
/// [system prompt]   共享，字节稳定 ┐
/// [会议公共节]       共享，字节稳定 ┴─ 同构 provider 下所有与会者完全一致 ← 唯一的变化点
/// ──────────── 以下为各会话私有 ────────────
/// [角色卡节]        各与会者不同
/// [私有对话历史]     各自不同
/// [本轮任务指令]     始终放末尾
/// </code>
/// 前缀三段的字节一致是 provider 前缀缓存能命中的前提；角色差异绝不写进共享前缀。
/// </summary>
public sealed class ContextComposer : IContextComposer
{
    /// <summary>共享前缀的消息条数（system prompt + 会议公共节）。</summary>
    public const int PrefixMessageCount = 2;

    private readonly string _systemPrompt;
    private readonly ComposeRequest _request;
    private readonly string _sharedSectionText;
    private readonly string _roleSectionFingerprint;

    /// <summary>
    /// 构造时就把共享节文本与指纹算好：turn 内共享节不变，逐 step 重算没有意义。
    /// </summary>
    public ContextComposer(
        string participantId,
        string systemPrompt,
        IReadOnlyList<AITool> tools,
        IOrchestrationStrategy strategy,
        ComposeRequest request)
    {
        _systemPrompt = systemPrompt;
        _request = request;
        _sharedSectionText = BuildSharedSection(participantId, strategy, request);

        // 指纹只覆盖 L1（系统提示 + 共享节 + 工具签名）。
        // L2 是**追加层**：provider 的前缀缓存按 token 前缀匹配，末尾追加不会让已缓存部分失效，
        // 所以"追加"不该体现在指纹里——否则每轮都变，什么也验不出来。
        // L2 的稳定性由另一条不变量保证：只追加、不滚动（探针直接比对快照前缀）。
        PrefixFingerprint = Fingerprint(_systemPrompt, _sharedSectionText, ToolSignature(tools));
        _roleSectionFingerprint = Fingerprint(request.RoleSection);
    }

    /// <summary>共享前缀（system prompt + tools + 会议公共节）的指纹，供缓存命中诊断（§7.4）。</summary>
    public string PrefixFingerprint { get; }

    /// <summary>角色卡节的指纹：同构 provider 下应当各会话不同，用来证明角色差异没污染共享前缀。</summary>
    public string RoleSectionFingerprint => _roleSectionFingerprint;

    public ComposedRequest Compose(string participantId, IReadOnlyList<ChatMessage> conversation, string turnInstruction)
    {
        // 角色节：文本在前，图在后（"你在哪、看到什么"）。
        // 图只在角色节里——共享节与系统提示的字节必须保持不变（前缀缓存的前提）。
        var roleContents = new List<AIContent> { new TextContent(_request.RoleSection) };

        foreach (var image in _request.Images)
        {
            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                roleContents.Add(new TextContent(image.Caption));
            }

            roleContents.Add(new DataContent(image.Bytes, image.MediaType));
        }

        var messages = new List<ChatMessage>(conversation.Count + _request.History.Count + 3)
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.System, _sharedSectionText),
        };

        // L2 剧情历史（只追加）：排在共享节之后、角色节之前——它属于"所有与会者共享的前缀"，
        // 所以角色节与各自的私有历史都在它后面，压缩时才真正造成一次 miss。
        foreach (var line in _request.History)
        {
            messages.Add(new ChatMessage(ChatRole.User, line.ToPromptText()));
        }

        messages.Add(new ChatMessage(ChatRole.System, roleContents));
        messages.AddRange(conversation);
        messages.Add(new ChatMessage(ChatRole.User, turnInstruction));

        return new ComposedRequest(messages, PrefixFingerprint);
    }

    /// <summary>
    /// 会议公共节 = 编排层给的文本 + 可见资料索引。
    /// 显式用 <c>'\n'</c> 而不是 <c>Environment.NewLine</c>：跨平台字节稳定才谈得上前缀缓存。
    /// </summary>
    private static string BuildSharedSection(string participantId, IOrchestrationStrategy strategy, ComposeRequest request)
    {
        var builder = new StringBuilder(request.SharedSection.TrimEnd());

        // 可见性判定的唯一入口：会议模式恒 true，角色扮演模式按角色过滤（§11.3-2）。
        var visible = request.Materials
            .Where(entry => strategy.VisibleTo(participantId, entry.Material))
            .ToList();

        if (visible.Count == 0)
        {
            return builder.ToString();
        }

        builder.Append('\n').Append('\n').Append("[常驻资料]");
        foreach (var entry in visible)
        {
            builder.Append('\n').Append("- ").Append(entry.Line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 前缀指纹：把"共享前缀三段"（系统提示 + 公共节 + 工具签名）压成一个哈希。
    /// L2 不参与——它是追加层，追加不影响 provider 的 token 前缀命中。
    /// </summary>
    private static string Fingerprint(string systemPrompt, string sharedSection, string toolSignature) =>
        Fingerprint(string.Join('\u0000', systemPrompt, sharedSection, toolSignature));

    private static string Fingerprint(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    /// <summary>
    /// 工具面指纹（§25.5 的 <c>--probe-tools</c> 用）：类型 + 名称 + 说明 + **参数 schema**。
    /// <para>
    /// 参数 schema 必须算进去（§27）：模型看到的工具定义含 <c>parameters</c>，改参数名 / 必填 / 枚举
    /// 都会真实改变前缀；只算名称与说明会让"批次化 + 指纹对账"漏报。
    /// </para>
    /// </summary>
    public static string ToolFingerprintOf(IReadOnlyList<AITool> tools) => Fingerprint(ToolSignature(tools));

    /// <summary>工具面签名（顺序 = 传入顺序；建表时用 <see cref="Canonicalize"/> 消掉键顺序差异）。</summary>
    private static string ToolSignature(IReadOnlyList<AITool> tools) =>
        tools.Count == 0
            ? string.Empty
            : string.Join('\u0001', tools.Select(tool => $"{tool.GetType().Name}:{tool.Name}:{tool.Description}:{CanonicalSchema(tool)}"));

    /// <summary>把工具的 JSON Schema 规范化成稳定字符串；没有 schema 的工具（纯文本工具）给空串。</summary>
    private static string CanonicalSchema(AITool tool) => tool switch
    {
        AIFunction function => Canonicalize(function.JsonSchema),
        _ => string.Empty,
    };

    /// <summary>
    /// JSON Schema 规范化（§27.2）：**对象属性按名称排序**、`required` 也排序（语义上无序），
    /// 其余数组保持原序（`enum` 的顺序可能有意义）。这样"只改字典插入顺序"不会造假阳性。
    /// </summary>
    private static string Canonicalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{"
            + string.Join(
                ",",
                element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonicalize(property.Value)))
            + "}",
        JsonValueKind.Array => "["
            + string.Join(",", element.EnumerateArray().Select(Canonicalize))
            + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString() ?? string.Empty),
        _ => element.GetRawText(),
    };

    /// <summary>
    /// 共享前缀指纹（提示 + 公共节 + 工具面）。probe 用固定占位提示、空公共节，
    /// 因此它衡量的是"提示模板 + 工具表"，不掺会话数据。
    /// </summary>
    public static string PrefixFingerprintOf(
        string systemPrompt,
        string sharedSection,
        IReadOnlyList<AITool> tools) =>
        Fingerprint(systemPrompt, sharedSection, ToolSignature(tools));
}
