namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 会议模式的编排策略——本期（阶段 0–6）的实现目标：
/// 全员并行、资料全员公开、以"资料被采纳"为提交边界、资料变更即打断（原则 3 是特性）。
/// </summary>
public sealed class MeetingStrategy : IOrchestrationStrategy
{
    public string Name => "meeting";

    public TurnDispatch Dispatch => TurnDispatch.Parallel;

    public CommitBoundary CommitBoundary => CommitBoundary.MaterialAdopted;

    public bool InterruptsOnMaterialChange => true;

    /// <summary>
    /// 会议模式：全员带工具（§7.1 要求同一模式下工具集完全一致，不按角色裁剪）。
    /// 叙事模式相反——NPC 一个都不带（§11.5-2）。
    /// </summary>
    public bool MembersCarryTools => true;

    /// <summary>
    /// 会议模式下恒为 true（零成本），但注入路径仍然必须经过这个判定点：
    /// 角色扮演模式会在这里按角色过滤（设计文档 §11.3-2）。
    /// </summary>
    public bool VisibleTo(string participantId, IMaterialView material) => true;
}
