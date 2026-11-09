using System.ComponentModel;
using System.Text.Json;
using MeetingRoom.Agent.Tools;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Materials;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 会议模式的完整工具面 = 材料工具 + 主持人工具（设计文档 §7.3 / §6.5）。
/// <para>
/// <b>工具集不按角色裁剪</b>：主持人的工具对所有人都可见——否则 tools hash 会变、所有会话的前缀缓存
/// 全部 miss（硬约束 6）。权限在<b>工具内部</b>校验：只有主持人的调用会被受理。
/// </para>
/// </summary>
public sealed class MeetingToolDispatcher : IToolDispatcher
{
    public const string ApproveMaterial = "approve_material";

    public const string RejectMaterial = "reject_material";

    public const string NudgeMember = "nudge_member";

    public const string AdvanceRound = "advance_round";

    public const string DeclareGoalMet = "declare_goal_met";

    /// <summary>向用户提问（设计文档 §14.2）：主持人也可以，但权限同样只有它自己。</summary>
    public const string AskUser = "ask_user";

    private readonly MaterialToolDispatcher _materials;
    private readonly IHostActions _host;
    private readonly Func<string, bool> _isHost;

    public MeetingToolDispatcher(
        IHostActions host,
        Func<string, bool> isHost,
        MaterialStore store,
        IOrchestrationStrategy strategy,
        ISpeechLog speech)
    {
        _host = host;
        _isHost = isHost;
        _materials = new MaterialToolDispatcher(store, strategy, speech);
        Tools = [.. _materials.Tools, .. BuildHostTools()];
    }

    /// <summary>全员一致的完整工具集。</summary>
    public IReadOnlyList<AITool> Tools { get; }

    public async Task<FunctionResultContent> InvokeAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (IsHostOnly(call.Name) && !_isHost(participantId))
        {
            return new FunctionResultContent(call.CallId, $"只有主持人可以调用 {call.Name}。");
        }

        switch (call.Name)
        {
            case ApproveMaterial:
                return await ReviewAsync(call, approve: true, cancellationToken).ConfigureAwait(false);

            case RejectMaterial:
                return await ReviewAsync(call, approve: false, cancellationToken).ConfigureAwait(false);

            case NudgeMember:
            {
                var target = Text(call.Arguments, "participantId");
                var task = Text(call.Arguments, "task");
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(task))
                {
                    return new FunctionResultContent(call.CallId, "缺少 participantId 或 task。");
                }

                var outcome = _host.NudgeMember(target, task);
                return new FunctionResultContent(call.CallId, outcome.Message);
            }

            case AdvanceRound:
            {
                var task = Text(call.Arguments, "task") ?? "请就当前议题继续讨论并给出结论。";
                var outcome = _host.AdvanceRound(task);
                return new FunctionResultContent(call.CallId, outcome.Message);
            }

            case AskUser:
            {
                IReadOnlyList<InteractionQuestion> questions;

                try
                {
                    var raw = call.Arguments is { } askArguments && askArguments.TryGetValue("questions", out var value)
                        ? value
                        : null;

                    questions = AskUserSpec.ReadQuestions(raw);
                }
                catch (JsonException ex)
                {
                    return new FunctionResultContent(call.CallId, $"参数格式不对，这次提问没有发出去：{ex.Message}");
                }

                var asked = await _host.AskUserAsync(questions, cancellationToken).ConfigureAwait(false);
                return new FunctionResultContent(call.CallId, asked.Message);
            }

            case DeclareGoalMet:
            {
                var reason = Text(call.Arguments, "reason") ?? "主持人判定目标已达成。";
                var outcome = _host.DeclareGoalMet(reason);
                return new FunctionResultContent(call.CallId, outcome.Message);
            }

            default:
                // 其余都是材料工具（list_materials / read_material / recall_speech / submit_material）。
                return await _materials.InvokeAsync(participantId, call, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsHostOnly(string name) =>
        name is ApproveMaterial or RejectMaterial or NudgeMember or AdvanceRound or DeclareGoalMet or AskUser;

    private async Task<FunctionResultContent> ReviewAsync(
        FunctionCallContent call,
        bool approve,
        CancellationToken cancellationToken)
    {
        var id = Text(call.Arguments, "materialId") ?? Text(call.Arguments, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new FunctionResultContent(call.CallId, "缺少 materialId。");
        }

        var outcome = await _host
            .ReviewMaterialAsync(id, approve, Text(call.Arguments, "note"), cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>
    /// 只用来生成 schema：真正的执行走 <see cref="InvokeAsync"/>（step 循环自己调度工具，§6.6）。
    /// </summary>
    private static IReadOnlyList<AITool> BuildHostTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("资料 id，如 mat-001")] string materialId,
                [Description("审核意见")] string? note) => "schema",
            ApproveMaterial,
            "（仅主持人）通过资料的初审，转交用户终审"),

        AIFunctionFactory.Create(
            ([Description("资料 id")] string materialId,
                [Description("驳回理由，会展示给作者")] string note) => "schema",
            RejectMaterial,
            "（仅主持人）驳回资料，请作者修改后重提"),

        AIFunctionFactory.Create(
            ([Description("与会者 id")] string participantId,
                [Description("给他的任务")] string task) => "schema",
            NudgeMember,
            "（仅主持人）点名某个与会者就某个任务发言"),

        AIFunctionFactory.Create(
            ([Description("下一轮的任务描述")] string task) => "schema",
            AdvanceRound,
            "（仅主持人）让全体与会者再并行讨论一轮"),

        AIFunctionFactory.Create(
            ([Description("为什么判断目标已经达成")] string reason) => "schema",
            DeclareGoalMet,
            "（仅主持人）宣告目标达成，建议结束会议"),

        AIFunctionFactory.Create(
            ([Description("要问用户的问题，一次最多 3 个；每个选项都要有 description")] AskQuestion[]? questions) => "schema",
            AskUser,
            "（仅主持人）向用户提问，请他拍板；会挂着等答复，超时按默认选项收口"),
    ];

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
