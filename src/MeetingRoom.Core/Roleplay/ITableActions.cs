using MeetingRoom.Core.Combat;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 说书人对"桌面事实"的动作：<b>查地图</b>、<b>掷骰</b>、以及（C1 起）<b>战斗托管</b>。
/// 它们与 <see cref="INarratorActions"/> 分开，因为它们依赖外部事实来源
/// （地图桥 / 骰子引擎 / 战斗状态机），而不是剧情决策。
/// </summary>
public interface ITableActions
{
    /// <summary>
    /// DM 覆盖（D3）：九个白名单字段（hp/tempHp/maxHp/ac/status/condition/initiative/budget/dc）。
    /// 调用方只给 <c>to</c>，<c>from</c> 由引擎填；成功落 <c>kind=combat-override</c>。
    /// </summary>
    Task<NarratorActionOutcome> DmOverrideAsync(
        DmOverrideRequest request,
        CancellationToken cancellationToken);

    /// <summary>某个角色周围有谁、各在多远；不给角色就返回整张图（未接入时回一句原因）。</summary>
    Task<NarratorActionOutcome> LookAroundAsync(
        string? characterId,
        double? radiusFeet,
        CancellationToken cancellationToken);

    /// <summary>掷骰并把结果写进剧情日志（<c>kind=roll</c>）；表达式非法时回灌原因，不抛异常。</summary>
    Task<NarratorActionOutcome> RollDiceAsync(
        string who,
        string expression,
        string? mode,
        string reason,
        int? dc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 开战（C1）：掷先攻并进入 Active，第一个回合立即开始。<b>只能同时有一场</b>；
    /// 已有进行中的战斗时返回拒绝（并落 <c>combat-rejected</c>），由说书人叙述原因。
    /// </summary>
    Task<NarratorActionOutcome> StartCombatAsync(
        IReadOnlyList<CombatantSetup> combatants,
        int? seed,
        string? encounterId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 结束当前回合并把场面交给下一位（C1）：索引回到 0 时轮数 +1。
    /// 没在战斗中 / 已结束时返回拒绝。
    /// </summary>
    Task<NarratorActionOutcome> EndTurnAsync(CancellationToken cancellationToken);

    /// <summary>结束整场战斗（C1）：<paramref name="outcome"/> 取 victory / defeat / retreat / dm_end。</summary>
    Task<NarratorActionOutcome> EndCombatAsync(string outcome, CancellationToken cancellationToken);

    /// <summary>
    /// 战斗内的一次攻击 / 施法（C2）：武器与法术从规则包查，掷骰走 D20；
    /// <b>不扣 HP</b>（C2 不发明 HP 存储，接上 Sheet 后再扣）。
    /// </summary>
    Task<NarratorActionOutcome> SubmitIntentAsync(CombatActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 叙事检定（C2）：四种形态、暗骰、Stakes、防刷。结果两个面——
    /// DM/日志面带数值，角色面（<see cref="CheckResult.CharacterFacingSummary"/>）暗骰不含数值也不含结论。
    /// </summary>
    Task<NarratorActionOutcome> RequestCheckAsync(CheckRequest request, CancellationToken cancellationToken);
}
