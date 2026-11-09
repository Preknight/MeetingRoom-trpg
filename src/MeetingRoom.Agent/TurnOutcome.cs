namespace MeetingRoom.Agent;

/// <summary>一次 turn 的结果。</summary>
/// <param name="Text">产出的正文（被取消时是已完成的部分，调用方应丢弃并重开）。</param>
/// <param name="Steps">实际执行的 step 数。</param>
/// <param name="Stale">产出期间资料版本已变化——编排层据此补一轮，而不是丢弃产出（设计文档 §6.3）。</param>
/// <param name="HitStepLimit">跑到 maxSteps 上限仍没产出正文（弱模型空转保护）。</param>
/// <param name="Cancelled">本 turn 被取消；取消不是异常，不向宿主抛出。</param>
/// <param name="Thought">模型直接输出的思考过程全文（只落盘与展示，绝不进上下文，§4.4）。</param>
/// <param name="ToolCalls">本轮执行过的工具名，按顺序。</param>
/// <param name="CacheHitTokens">provider 报告的前缀缓存命中 token 数。</param>
/// <param name="CacheMissTokens">provider 报告的前缀缓存未命中 token 数。</param>
/// <param name="SteerCount">本轮在 step 边界消费掉的中途注入条数。</param>
public sealed record TurnOutcome(
    string Text,
    int Steps,
    bool Stale,
    bool HitStepLimit,
    bool Cancelled,
    string Thought = "",
    IReadOnlyList<string>? ToolCalls = null,
    long CacheHitTokens = 0,
    long CacheMissTokens = 0,
    int SteerCount = 0)
{
    /// <summary>是否拿到了可用产出。</summary>
    public bool Produced => !Cancelled && !HitStepLimit;

    /// <summary>本轮执行过的工具名（思考摘要里"做法"一段的来源）。</summary>
    public IReadOnlyList<string> Tools => ToolCalls ?? [];
}
