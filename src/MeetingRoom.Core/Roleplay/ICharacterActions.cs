namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 角色 Agent 能用的"桌面动作"（M9b）。
/// <para>
/// 与 <see cref="ITableActions"/> 的区别：这里的每个方法都**带着 actorId**，返回值必须是
/// "这个角色自己能感知到的那一份"——迷雾遮蔽、`visible=false` 的东西一律不出现。
/// </para>
/// <para>
/// <b>只读 + 只请求</b>：`Request*` 两个方法只落一条 <c>kind=request</c> 记录，
/// **不产生任何结算**——真正的 <c>request_check</c> / <c>combat_action</c> 仍只有说书人能调。
/// </para>
/// </summary>
public interface ICharacterActions
{
    /// <summary>看周围有谁、多远（按该角色的视角过滤）。</summary>
    Task<NarratorActionOutcome> LookAroundForCharacterAsync(
        string actorId,
        double? radiusFeet,
        CancellationToken cancellationToken);

    /// <summary>脚下 / 相邻一格内有没有区域效果、是否困难地形、雾是否挡住。</summary>
    Task<NarratorActionOutcome> CheckTerrainAsync(
        string actorId,
        CancellationToken cancellationToken);

    /// <summary>**请求**一次判定（不结算）：说书人看到后决定掷不掷。</summary>
    Task<NarratorActionOutcome> RequestJudgementAsync(
        string actorId,
        string? skill,
        string? ability,
        string? kind,
        int? dcHint,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>**请求**一次战斗动作（不结算）：说书人决定算不算、怎么算。</summary>
    Task<NarratorActionOutcome> RequestCombatActionAsync(
        string actorId,
        string kind,
        string targetId,
        string? weaponId,
        string? spellId,
        string intent,
        string? reason,
        CancellationToken cancellationToken);
}
