namespace MeetingRoom.Core.Roleplay;

/// <summary>一次发言机会：谁来发言、给他什么任务。</summary>
public sealed record SpeechTurn(string ParticipantId, string Task, bool Simultaneous = false);

/// <summary>调度时能看到的现场信息。</summary>
public sealed record SchedulingContext
{
    /// <summary>本轮的候选发言者（按角色卡顺序）。</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];

    /// <summary>本轮已经发言过的角色（时序调度据此推进）。</summary>
    public IReadOnlyList<string> Spoken { get; init; } = [];

    /// <summary>导演的输入（若有）——叙事模式下导演指令是调度的起点。</summary>
    public string? DirectorInput { get; init; }

    /// <summary>用户 @ 点名的角色（最高优先级）。</summary>
    public IReadOnlyList<string> Mentions { get; init; } = [];

    /// <summary>说书人指定的下一个发言者（若有）。</summary>
    public string? DirectorPick { get; init; }
}

/// <summary>
/// 发言调度器（设计文档 §11.4-1）。会议与叙事的内核一致，差别就在"谁先说、能不能同时说"：
/// <list type="bullet">
///   <item>会议模式：全员并行，各自独立 turn（<see cref="ParallelTurnScheduler"/>）；</item>
///   <item>叙事模式：<b>时序</b>对话，后者能看到前者刚说的话（<see cref="SequentialTurnScheduler"/>）——
///         否则所有 NPC 并行生成时会各说各的、重复回答同一句话。</item>
/// </list>
/// </summary>
public interface ITurnScheduler
{
    /// <summary>决定本轮的下一个（或全部）发言者；返回空表示本轮没有可发言的人。</summary>
    IReadOnlyList<SpeechTurn> Plan(SchedulingContext context);
}

/// <summary>会议模式：全员并行。</summary>
public sealed class ParallelTurnScheduler : ITurnScheduler
{
    public IReadOnlyList<SpeechTurn> Plan(SchedulingContext context) =>
        context.Candidates
            .Where(id => !context.Spoken.Contains(id, StringComparer.Ordinal))
            .Select(id => new SpeechTurn(id, Task: string.Empty))
            .ToArray();
}

/// <summary>
/// 叙事模式：一次一个，按"用户点名 → 导演指定 → 角色卡顺序"的优先级推进。
/// <see cref="SpeechTurn.Simultaneous"/> 用于嘈杂场景（允许两人同时开口）。
/// </summary>
public sealed class SequentialTurnScheduler : ITurnScheduler
{
    /// <summary>一轮（一次导演指令）里最多容纳几个角色发言，避免长局里无限展开。</summary>
    private readonly int _maxSpeakersPerTurn;

    public SequentialTurnScheduler(int maxSpeakersPerTurn = 3)
    {
        _maxSpeakersPerTurn = Math.Clamp(maxSpeakersPerTurn, 1, 12);
    }

    public IReadOnlyList<SpeechTurn> Plan(SchedulingContext context)
    {
        // 上限必须在这里兜住：调用方只按"Plan 返回空"判断本轮结束，
        // 若不在调度器内部检查，一次导演指令会把在场角色挨个排一遍（上限形同虚设）。
        if (!CanContinue(context.Spoken.Count))
        {
            return [];
        }

        var remaining = context.Candidates
            .Where(id => !context.Spoken.Contains(id, StringComparer.Ordinal))
            .ToList();

        if (remaining.Count == 0)
        {
            return [];
        }

        // ① 用户点名优先；② 其次导演指定；③ 其余按角色卡顺序。
        var next = context.Mentions.FirstOrDefault(remaining.Contains)
            ?? (context.DirectorPick is not null && remaining.Contains(context.DirectorPick) ? context.DirectorPick : null)
            ?? remaining[0];

        return [new SpeechTurn(next, context.DirectorInput ?? string.Empty)];
    }

    /// <summary>本轮是否还可以继续安排人发言。</summary>
    public bool CanContinue(int spokenCount) => spokenCount < _maxSpeakersPerTurn;
}
