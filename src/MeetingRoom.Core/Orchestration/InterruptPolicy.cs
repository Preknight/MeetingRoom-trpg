namespace MeetingRoom.Core.Orchestration;

/// <summary>一次软注入的候选：为什么注入 + 注入什么文本。</summary>
public sealed record SteerNotice(string Reason, string Text, DateTimeOffset At);

/// <summary>
/// 打断策略（设计文档 §6.2、§7.2）：<b>能软注入就软注入，硬打断是例外路径</b>——
/// 否则每次资料更新都会让所有正在思考的与会者白烧一遍当前 step。
/// <para>
/// 每次软注入都必然造成一次前缀缓存 miss，所以连续变更必须在合并窗口内合并成一次注入：
/// <b>合并窗口不是优化，而是必需</b>（§13.1-4）。
/// </para>
/// <para>
/// 本类只负责"合并"这件事，时间由调用方显式传入，这样去抖语义可以被确定性地测试，
/// 不需要在测试里 sleep。
/// </para>
/// </summary>
public sealed class InterruptPolicy
{
    private readonly TimeSpan _mergeWindow;
    private readonly Lock _gate = new();
    private readonly List<SteerNotice> _pending = [];
    private DateTimeOffset? _windowStartedAt;

    public InterruptPolicy(int mergeWindowMs)
    {
        if (mergeWindowMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeWindowMs), mergeWindowMs, "合并窗口不能为负。");
        }

        _mergeWindow = TimeSpan.FromMilliseconds(mergeWindowMs);
    }

    public TimeSpan MergeWindow => _mergeWindow;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>登记一次注入请求：窗口从最近一次请求重新计时。</summary>
    public void Request(SteerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        lock (_gate)
        {
            _pending.Add(notice);
            _windowStartedAt = notice.At;
        }
    }

    /// <summary>
    /// 窗口到期则返回<b>合并后的一条</b>注入（并清空待处理项），否则返回 <c>null</c>。
    /// 多条的文本按到达顺序拼接，原因取最后一次。
    /// </summary>
    public SteerNotice? Drain(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pending.Count == 0 || _windowStartedAt is null)
            {
                return null;
            }

            return now - _windowStartedAt.Value < _mergeWindow ? null : Take();
        }
    }

    /// <summary>不等窗口，立刻取走（会议结束或关闭时用）。</summary>
    public SteerNotice? DrainImmediately()
    {
        lock (_gate)
        {
            return _pending.Count == 0 ? null : Take();
        }
    }

    private SteerNotice Take()
    {
        var text = string.Join('\n', _pending.Select(notice => notice.Text));
        var last = _pending[^1];

        _pending.Clear();
        _windowStartedAt = null;

        return new SteerNotice(last.Reason, text, last.At);
    }
}
