using MeetingRoom.Core.Interaction;

namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 主持人 Agent 的动作（设计文档 §6.5）。它与其他与会者<b>共用同一个 AgentSession</b>，
/// 差别只在权限与产出类型——这些方法由主持人的工具调用触发。
/// <para>
/// 语义分两类：审核立即生效（只动资料状态）；点名 / 推进轮次 / 宣告达成是<b>登记意图</b>，
/// 在当前主持人 turn 结束后由编排层执行——工具执行期间不应该同步等一整轮讨论。
/// </para>
/// </summary>
public interface IHostActions
{
    /// <summary>审核一份资料：通过 / 驳回。<b>不能采纳</b>——采纳权在用户（§4.2）。</summary>
    Task<HostActionOutcome> ReviewMaterialAsync(
        string materialId,
        bool approve,
        string? note,
        CancellationToken cancellationToken);

    /// <summary>登记"点名发言"：让某个与会者就某个任务发言。</summary>
    HostActionOutcome NudgeMember(string participantId, string task);

    /// <summary>登记"推进轮次"：让全体与会者再并行讨论一轮。</summary>
    HostActionOutcome AdvanceRound(string task);

    /// <summary>登记"宣告达成"：建议结束会议（§6.5：结束需用户确认或用户预先授权）。</summary>
    HostActionOutcome DeclareGoalMet(string reason);

    /// <summary>
    /// 向用户提问（设计文档 §14.2）：与提议同为待决事项，会挂着等答复——超时按默认选项收口。
    /// 校验不过、或交互模式关掉时，回执里说明原因，不抛异常打断它的 turn。
    /// </summary>
    Task<HostActionOutcome> AskUserAsync(
        IReadOnlyList<InteractionQuestion> questions,
        CancellationToken cancellationToken);
}

/// <summary>主持人动作的回执（会被回灌给模型，所以是自然语言）。</summary>
public sealed record HostActionOutcome(bool Applied, string Message);
