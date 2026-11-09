namespace MeetingRoom.Agent;

/// <summary>
/// 一次 turn 的输入。装配器由编排层按"当时的"共享节/资料/角色卡构造后传入——
/// 会话本身不持有会议状态，这样资料版本变化不需要重建会话。
/// </summary>
/// <param name="ParticipantId">发言的与会者。</param>
/// <param name="Task">本轮任务指令。</param>
/// <param name="BasedOnMaterialVersion">开始本 turn 时的资料版本，用于迟到产出判定（§6.3）。</param>
/// <param name="Composer">本次 turn 使用的上下文装配器。</param>
public sealed record TurnRequest(
    string ParticipantId,
    string Task,
    int BasedOnMaterialVersion,
    IContextComposer Composer);
