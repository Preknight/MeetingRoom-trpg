namespace MeetingRoom.Core.Context;

/// <summary>一个与会者的缓存命中情况（设计文档 §7.4：让"省 token"从愿望变成指标）。</summary>
public sealed record ParticipantCacheReport(
    string ParticipantId,
    string PrefixFingerprint,
    string RoleFingerprint,
    long CacheHitTokens,
    long CacheMissTokens,
    string? LastMissReason,
    int Turns,
    IReadOnlyList<string>? RecentMissReasons = null)
{
    /// <summary>命中率（0–1）；没有数据时为 0。</summary>
    public double HitRate
    {
        get
        {
            var total = CacheHitTokens + CacheMissTokens;
            return total == 0 ? 0 : (double)CacheHitTokens / total;
        }
    }
}

/// <summary>
/// 前缀缓存诊断（设计文档 §7.4）。没有这个面板，"省 token"只是愿望而不是指标：
/// 它记录每个与会者的前缀指纹、命中/未命中 token 数，以及上一次 miss 的原因。
/// </summary>
public sealed class CacheDiagnostics
{
    private readonly Dictionary<string, ParticipantCacheReport> _reports = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Record(
        string participantId,
        string prefixFingerprint,
        string roleFingerprint,
        long cacheHitTokens,
        long cacheMissTokens,
        string? lastMissReason)
    {
        lock (_gate)
        {
            var existing = _reports.GetValueOrDefault(participantId);
            var turns = existing?.Turns ?? 0;

            // 最近的 miss 原因要留一小段历史：只看最后一轮的话，
            // "上一次 miss 是 steer 造成的"这类信息会被下一轮覆盖掉（§7.4）。
            var reasons = existing?.RecentMissReasons?.ToList() ?? [];
            if (!string.IsNullOrWhiteSpace(lastMissReason))
            {
                reasons.Add(lastMissReason);

                if (reasons.Count > 5)
                {
                    reasons.RemoveRange(0, reasons.Count - 5);
                }
            }

            _reports[participantId] = new ParticipantCacheReport(
                participantId,
                prefixFingerprint,
                roleFingerprint,
                cacheHitTokens,
                cacheMissTokens,
                lastMissReason,
                turns + 1,
                reasons);
        }
    }

    public IReadOnlyList<ParticipantCacheReport> Report()
    {
        lock (_gate)
        {
            return _reports.Values.OrderBy(report => report.ParticipantId, StringComparer.Ordinal).ToArray();
        }
    }
}
