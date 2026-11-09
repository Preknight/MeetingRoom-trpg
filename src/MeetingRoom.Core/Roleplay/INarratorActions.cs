using MeetingRoom.Core.Interaction;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 说书人的控场动作（设计文档 §11.4-5），与会议侧 <c>IHostActions</c> 对称，同样分两类：
/// <list type="bullet">
///   <item><b>提议</b>要等导演（用户）拍板——工具执行期间会挂着等答复，有超时收口；</item>
///   <item><b>登记意图</b>立即返回（点名 / 描写一拍），由编排层在这一轮结束后执行——
///     工具执行期间不应该同步等一整轮对话。</item>
/// </list>
/// <para>
/// <b>剧情决策不由说书人做</b>（§11.4 第 6b 项）：世界状态只有"提议"这一种入口，
/// 它要么由导演的指令触发，要么先问过导演——工具面里没有"直接改状态"的口子，也不打算加。
/// </para>
/// </summary>
public interface INarratorActions
{
    /// <summary>提议一次世界状态变更，等导演确认后落盘（超时按"采用"收口）。</summary>
    Task<NarratorActionOutcome> ProposeWorldStateAsync(
        WorldStateMutation mutation,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// 提议揭示迷雾（M6b）：**只加「已探索」，不碰「当前视野」**（后者只能 DM 亲自判断）。
    /// <b>超时按"不揭示"收口</b>——与 <see cref="ProposeWorldStateAsync"/> 的宽松口径相反：
    /// 迷雾是信息闸门，宁可漏揭也不能替 DM 做决定。
    /// </summary>
    Task<NarratorActionOutcome> ProposeRevealFogAsync(
        IReadOnlyList<Maps.GridCell>? cells,
        Maps.FogShape? shape,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>登记"点名某个角色接话"（维持节奏）。</summary>
    NarratorActionOutcome NudgeCharacter(string characterId, string task);

    /// <summary>登记"自己描写一拍"（导演没输入时不许场面停住）。</summary>
    NarratorActionOutcome IdleBeat(string? hint);

    /// <summary>建议本场收束：把场面停在 <c>Paused</c>，等导演决定下一步（<b>不给结论</b>）。</summary>
    NarratorActionOutcome DeclareSceneEnd(string reason);

    /// <summary>
    /// 向导演（用户）提问（设计文档 §14.2）：与提议同为待决事项，会挂着等答复——超时按默认选项收口。
    /// 校验不过、或交互模式关掉时，回执里说明原因，不抛异常打断它的 turn。
    /// </summary>
    Task<NarratorActionOutcome> AskUserAsync(
        IReadOnlyList<InteractionQuestion> questions,
        CancellationToken cancellationToken);
}

/// <summary>说书人动作的回执（会被回灌给模型，所以是自然语言）。</summary>
public sealed record NarratorActionOutcome(bool Applied, string Message);
