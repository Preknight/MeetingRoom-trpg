namespace MeetingRoom.Core.Interaction;

/// <summary>
/// 待决事项的登记与收口（批 4 与阶段 9 的<b>共同地基</b>）：登记 → 等答复 / 跳过 / 超时 / 会话结束。
/// <para>
/// 这里只有"谁在等什么"：不管事件、不管状态机、不碰持久化——那些归 <c>SessionCore</c> 与各自的会话外壳。
/// 超时收口用<b>默认选项</b>填答案，所以等待方拿到的一定是一份可直接用的结果。
/// </para>
/// </summary>
public sealed class PendingDecisionRegistry : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _open = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>
    /// 登记一件事并返回句柄。<paramref name="timeoutSeconds"/> 大于 0 时到期按默认选项收口；
    /// 0 表示永久等待（等答复、跳过或会话结束）。
    /// </summary>
    public PendingDecision Open(
        PendingDecisionKind kind,
        IReadOnlyList<InteractionQuestion> questions,
        string? turnId = null,
        IReadOnlyList<InteractionAnswer>? defaultSelection = null,
        int timeoutSeconds = 0,
        Action<PendingDecision, InteractionOutcome>? onResolved = null)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var decision = new PendingDecision(
            Guid.NewGuid().ToString("N"),
            kind,
            turnId,
            questions,
            defaultSelection ?? PendingDecision.ResolveDefault(questions),
            timeoutSeconds,
            DateTimeOffset.UtcNow);

        var entry = new Entry(
            decision,
            timeoutSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)) : null,
            onResolved);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _open[decision.Id] = entry;
        }

        if (entry.Timeout is { } timeout)
        {
            timeout.Token.Register(() => TimeOut(decision.Id));
        }

        return decision;
    }

    /// <summary>答复某一件事；它已经不在（重复答复 / 已超时）时返回 false。</summary>
    public bool Answer(string id, IReadOnlyList<InteractionAnswer> answers)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(answers);

        return Complete(id, answers, timedOut: false, skipped: false, skipReason: null);
    }

    /// <summary>
    /// 跳过某一件事：<b>按默认选项收口</b>并留下原因，而不是让它悬着。
    /// 用户点"跳过"与"会话结束时的一次性收口"都走这里，区别只在原因文案。
    /// </summary>
    public bool Skip(string id, string? reason) =>
        Complete(id, defaultSelection: null, timedOut: false, skipped: true, skipReason: reason);

    /// <summary>这一件事是不是还在等人。</summary>
    public bool HasPending(string id)
    {
        lock (_gate)
        {
            return _open.ContainsKey(id);
        }
    }

    /// <summary>当前还挂着的事项（快照用：前端重连后据此重建浮层与第 3 列）。</summary>
    public IReadOnlyList<PendingDecision> OpenDecisions()
    {
        lock (_gate)
        {
            return _open.Values.Select(entry => entry.Decision).ToArray();
        }
    }

    /// <summary>
    /// 一次性收口所有还挂着的事（会话结束 / 释放时调用）。
    /// 没有它，等在 <c>Completion</c> 上的 turn 会永远挂着——取消令牌只保证"不再等"，不保证"收口干净"。
    /// </summary>
    public void CancelAll(string reason)
    {
        Entry[] entries;

        lock (_gate)
        {
            entries = _open.Values.ToArray();
            _open.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Release();
            entry.Decision.TryComplete(new InteractionOutcome(
                entry.Decision.Id,
                entry.Decision.DefaultSelection,
                TimedOut: false,
                Skipped: true,
                SkipReason: reason));
        }
    }

    public void Dispose()
    {
        Entry[] entries;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _open.Values.ToArray();
            _open.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Release();
            entry.Decision.TryComplete(new InteractionOutcome(
                entry.Decision.Id,
                entry.Decision.DefaultSelection,
                TimedOut: false,
                Skipped: true,
                SkipReason: "会话已释放"));
        }
    }

    private bool Complete(
        string id,
        IReadOnlyList<InteractionAnswer>? defaultSelection,
        bool timedOut,
        bool skipped,
        string? skipReason)
    {
        Entry? entry;

        lock (_gate)
        {
            if (!_open.Remove(id, out entry))
            {
                return false;
            }
        }

        entry.Release();

        var outcome = new InteractionOutcome(
            id,
            defaultSelection ?? entry.Decision.DefaultSelection,
            timedOut,
            skipped,
            skipReason);

        var completed = entry.Decision.TryComplete(outcome);

        // 收口回调**同步**跑（B5 的 DM 提醒靠它把「记进设定」立刻写进摘要）：
        // 若挂在 Task 续体上，答复返回时业务状态可能还没落地，调用方（与探针）就会看到旧值。
        if (completed)
        {
            entry.OnResolved?.Invoke(entry.Decision, outcome);
        }

        return completed;
    }

    private void TimeOut(string id) =>
        Complete(id, defaultSelection: null, timedOut: true, skipped: false, skipReason: "超时未答复");

    private sealed record Entry(
        PendingDecision Decision,
        CancellationTokenSource? Timeout,
        Action<PendingDecision, InteractionOutcome>? OnResolved)
    {
        /// <summary>收口之后把计时器放掉——否则超时回调会一直挂在注册表上。</summary>
        public void Release() => Timeout?.Dispose();
    }
}
