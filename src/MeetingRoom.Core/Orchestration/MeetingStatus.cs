using MeetingRoom.Core.Interaction;

namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 会议与叙事<b>共用</b>的状态机（设计文档 §6.4 / §14.4）：暂停后不再发起新 turn，恢复从暂停点继续；
/// 等用户拍板期间（<see cref="AwaitingInput"/>）同样不发起新 turn，但不打断正在跑的那个。
/// </summary>
public enum MeetingStatus
{
    Idle,
    Running,
    Paused,

    /// <summary>有 Agent 抛出待用户拍板的事项，正在等答复（设计文档 §14.4）。</summary>
    AwaitingInput,

    Ended,
}

/// <summary>状态在快照里的写法（前端按字符串判断，不认枚举名）。</summary>
public static class MeetingStatusExtensions
{
    public static string ToWire(this MeetingStatus status) => status switch
    {
        MeetingStatus.Idle => "idle",
        MeetingStatus.Running => "running",
        MeetingStatus.Paused => "paused",
        MeetingStatus.AwaitingInput => "awaitingInput",
        MeetingStatus.Ended => "ended",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>会议编排的装配选项。</summary>
public sealed record MeetingOptions
{
    public required string MeetingId { get; init; }

    public required string Topic { get; init; }

    /// <summary>同时运行的会话上限（<c>limits.maxConcurrentTurns</c>）。</summary>
    public int MaxConcurrentTurns { get; init; } = 4;

    public int MaxStepsPerTurn { get; init; } = 12;

    /// <summary>打断合并窗口（<c>limits.steerMergeWindowMs</c>）。</summary>
    public int SteerMergeWindowMs { get; init; } = 2000;

    public int MaterialSummaryFallbackChars { get; init; } = 300;

    public int ThoughtSummaryMaxChars { get; init; } = 500;

    /// <summary>常驻资料条数上限（<c>context.alwaysMaterialMaxCount</c>）。</summary>
    public int AlwaysMaterialMaxCount { get; init; } = 12;

    /// <summary>触发式资料的注入预算（<c>context.triggeredTokenBudget</c>）。</summary>
    public int TriggeredTokenBudget { get; init; } = 2000;

    /// <summary>同一份触发式资料的冷却轮数（<c>context.triggeredCooldownRounds</c>）。</summary>
    public int TriggeredCooldownRounds { get; init; } = 10;

    /// <summary>
    /// 轮次上限：主持人每次"推进轮次"都会再跑一整轮，需要一个终止条件防止失控
    /// （与 §13.1-6 的"工具循环必须有终止条件"同理）。达到上限自动结束会议。
    /// </summary>
    public int MaxRounds { get; init; } = 5;

    /// <summary>单次发言的字数上限（0 = 不限制）；会议靠多轮轮流，而不是单条超长发言。</summary>
    public int MaxSpeechChars { get; init; } = 300;

    /// <summary>交互模式（阶段 9）：主持人提问与说书人提议共用同一份配置。</summary>
    public InteractionOptions Interaction { get; init; } = new();

    /// <summary>共享 system prompt；同构 provider 下所有会话必须字节一致（§7.1）。</summary>
    public string SystemPrompt { get; init; } =
        "你正在参加一场多角色会议。严格按角色卡的要求发言，先给结论再给理由；"
        + "需要资料时用工具按需读取，不要凭记忆假设资料内容。"
        + "当你形成可落地的结论时，用 submit_material 把它提交成待审批资料（标题 + 一句话摘要 + 正文）。";
}
