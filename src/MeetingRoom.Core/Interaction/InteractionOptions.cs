namespace MeetingRoom.Core.Interaction;

/// <summary>
/// 交互模式（阶段 9）的配置（设计文档 §14.5）：Agent 抛出待用户拍板的事项，等答复、跳过或超时收口。
/// <para>
/// 说书人的世界状态提议（批 4）与主持人 / 说书人的 <c>ask_user</c>（阶段 9）<b>共用这一份配置</b>——
/// 它们是同一种机制，只是提议的来源不同。
/// </para>
/// </summary>
public sealed record InteractionOptions
{
    /// <summary>关掉即退回"全自动自主推进"：既不提问，也不提议。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>每轮结束都问；false 时只在主持人 / 说书人判断的关键点问。</summary>
    public bool EveryRound { get; init; } = true;

    /// <summary>是否允许主持人 / 说书人自己判断"这轮没什么可问的"从而跳过提问。</summary>
    public bool AllowSelfSkip { get; init; } = true;

    /// <summary><see cref="AllowSelfSkip"/> 为真时的防呆：连续跳到这个次数就强制问一次；0 = 不限制。</summary>
    public int MaxConsecutiveSkips { get; init; } = 3;

    /// <summary>
    /// 等待答复的上限（秒）；<b>0 = 永久等待</b>（"每次都要我拍板"时用，代价是会议会一直挂着）。
    /// 到期按 <see cref="InteractionQuestion.DefaultOptionId"/> 收口。
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>单个问题的选项上限（选项太多反而选不动）。</summary>
    public int MaxOptions { get; init; } = 4;
}
