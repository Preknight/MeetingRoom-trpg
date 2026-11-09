namespace MeetingRoom.Core.Interaction;

/// <summary>一个问题的一个选项。</summary>
public sealed record InteractionOption(string Id, string Label, string? Description = null);

/// <summary>
/// 一个待用户拍板的问题。<see cref="DefaultOptionId"/> 是超时 / 跳过时的收口选项——
/// 它不是"随便挑一个"，而是调用方认为"用户不回答时最安全的那个"。
/// </summary>
public sealed record InteractionQuestion(
    string Id,
    string Prompt,
    IReadOnlyList<InteractionOption> Options,
    string? DefaultOptionId = null);

/// <summary>用户对某个问题的答复：选了哪些选项。</summary>
public sealed record InteractionAnswer(string QuestionId, IReadOnlyList<string> Selected);

/// <summary>待决事项的来源——两种来源走同一条机制，靠它区分。</summary>
public enum PendingDecisionKind
{
    /// <summary>主持人 / 说书人主动提问（<c>ask_user</c>）。</summary>
    AskUser,

    /// <summary>说书人提议改世界状态，等导演确认（批 4）。</summary>
    WorldState,

    /// <summary>
    /// 导演（用户）投给在场观察面的提醒（《叙事模式后台与上下文设计》§3.3，B5）。
    /// <para>
    /// 与另外三种的根本差别：<b>它没有副作用，也不参与剧情推进</b>——不会暂停回合、不进说书人上下文；
    /// 超时只能是"没人看到"，默认选项固定为「知道了」（只 ack）。
    /// </para>
    /// </summary>
    DmNote,

    /// <summary>
    /// 说书人提议揭示迷雾，等导演确认（M6b；P1 起独立成一种）。
    /// <para>
    /// 原来是复用 <see cref="WorldState"/> 的——前端按 kind 分展示时会与真正的世界状态提议糊在一起，
    /// 而且两者的默认选项语义相反（世界状态超时 = 按提议生效，迷雾超时 = 不揭示），
    /// 混用同一个 kind 让这条差异在界面上完全看不出来。
    /// </para>
    /// </summary>
    Fog,
}

/// <summary>
/// 待决事项的收口结果。<see cref="TimedOut"/>（超时）与 <see cref="Skipped"/>（跳过 / 会话结束）
/// 都会用默认选项填 <see cref="Answers"/>，所以调用方永远拿得到一份可直接用的答案。
/// </summary>
public sealed record InteractionOutcome(
    string Id,
    IReadOnlyList<InteractionAnswer> Answers,
    bool TimedOut,
    bool Skipped,
    string? SkipReason);

/// <summary>
/// 一个待用户拍板的事项：登记之后拿到它，await <see cref="Completion"/> 直到答复、跳过、超时或会话结束。
/// </summary>
public sealed class PendingDecision
{
    private readonly TaskCompletionSource<InteractionOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingDecision(
        string id,
        PendingDecisionKind kind,
        string? turnId,
        IReadOnlyList<InteractionQuestion> questions,
        IReadOnlyList<InteractionAnswer> defaultSelection,
        int timeoutSeconds,
        DateTimeOffset createdAt)
    {
        Id = id;
        Kind = kind;
        TurnId = turnId;
        Questions = questions;
        DefaultSelection = defaultSelection;
        TimeoutSeconds = timeoutSeconds;
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public PendingDecisionKind Kind { get; }

    /// <summary>提出这件事的 turn（会议 / 叙事里的一次会话回合），可为空。</summary>
    public string? TurnId { get; }

    public IReadOnlyList<InteractionQuestion> Questions { get; }

    /// <summary>超时 / 跳过时的答案（由 <see cref="ResolveDefault"/> 从问题上推出来）。</summary>
    public IReadOnlyList<InteractionAnswer> DefaultSelection { get; }

    public int TimeoutSeconds { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>答复 / 跳过 / 超时后完成。</summary>
    public Task<InteractionOutcome> Completion => _completion.Task;

    internal bool TryComplete(InteractionOutcome outcome) => _completion.TrySetResult(outcome);

    /// <summary>
    /// 推出默认答案：优先取问题声明的 <see cref="InteractionQuestion.DefaultOptionId"/>，
    /// 没声明（或与选项对不上）就取第一个选项，一个选项都没有就回空。
    /// </summary>
    public static IReadOnlyList<InteractionAnswer> ResolveDefault(IReadOnlyList<InteractionQuestion> questions) =>
        questions
            .Select(question => new InteractionAnswer(
                question.Id,
                question.DefaultOptionId is { } declared
                    && question.Options.Any(option => string.Equals(option.Id, declared, StringComparison.Ordinal))
                        ? [declared]
                        : question.Options.Count > 0 ? [question.Options[0].Id] : []))
            .ToArray();
}
