namespace MeetingRoom.Core.Orchestration;

/// <summary>发言调度形态（设计文档 §11.2）。</summary>
public enum TurnDispatch
{
    /// <summary>全员并行，各自独立 turn（会议模式）。</summary>
    Parallel,

    /// <summary>时序对话：后发言者能看到前者的发言（角色扮演模式，§11.4-1）。</summary>
    Sequential,
}

/// <summary>提交边界：模型产出在哪里固化为领域状态（设计文档 §11.3-4）。</summary>
public enum CommitBoundary
{
    /// <summary>资料被采纳（会议模式）。</summary>
    MaterialAdopted,

    /// <summary>一轮导演指令 + 正文结束（角色扮演的前台回合）。</summary>
    PlayerTurnCompleted,
}

/// <summary>
/// 编排策略：会议与角色扮演共用同一个内核，差异全部收在这一层（设计文档 §11.1–§11.3）。
/// 本期只实现会议模式。接口刻意保持最小——阶段 7 真正需要复杂调度时再扩展，而不是现在去猜。
/// </summary>
public interface IOrchestrationStrategy
{
    string Name { get; }

    /// <summary>发言调度形态。</summary>
    TurnDispatch Dispatch { get; }

    /// <summary>提交边界。</summary>
    CommitBoundary CommitBoundary { get; }

    /// <summary>
    /// 资料变更是否打断正在思考的会话。
    /// 会议模式下这是特性（原则 3）；叙事模式下是破坏性的，默认只软注入（§11.5-1）。
    /// </summary>
    bool InterruptsOnMaterialChange { get; }

    /// <summary>
    /// 可见性判定的<b>唯一入口</b>：某与会者能否看到某份资料。
    /// 所有资料注入都必须经过它——绝不要把"全员可见"写死在装配逻辑里（§11.3-2）。
    /// </summary>
    bool VisibleTo(string participantId, IMaterialView material);

    /// <summary>
    /// 普通参与者（非主持人 / 非说书人）是否携带工具。
    /// <para>
    /// 会议模式恒为 true（全员带资料工具）；叙事模式下 NPC <b>一个都不带</b>——
    /// 工具调用会产生元话语、破坏沉浸（§11.5-2）。这并不违反 §7.1 的缓存约束：
    /// 同一模式下所有会话的工具集仍然一致，只是两种模式的 tools schema 不同（各自内部字节稳定）。
    /// </para>
    /// <para>主持人 / 说书人始终带工具——它本来就是控制面。</para>
    /// </summary>
    bool MembersCarryTools => true;
}
