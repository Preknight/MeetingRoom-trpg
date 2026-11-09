using System.Text.Json;
using System.Threading.Channels;

namespace MeetingRoom.Core.Materials;

/// <summary>一条历史发言（<c>recall_speech</c> 的检索单位）。</summary>
public sealed record SpeechRecord(string Id, string Author, string Kind, string Text, DateTimeOffset At);

/// <summary>
/// 历史发言的检索源。<b>刻意用内存实现</b>：<c>transcript.jsonl</c> 的写入端持有文件句柄，
/// 边写边读会撞上 Windows 的共享语义（见 <c>docs/实施进度与待办.md</c> 风险 9）；
/// 重启后可由 <see cref="InMemorySpeechLog.LoadFromAsync"/> 在启动阶段（无并发写入时）从落盘记录恢复。
/// </summary>
public interface ISpeechLog
{
    int Count { get; }

    void Append(SpeechRecord record);

    /// <summary>按关键词检索：命中词数多的优先，其次按时间倒序。<paramref name="query"/> 为空时返回最近的几条。</summary>
    IReadOnlyList<SpeechRecord> Search(string query, int limit);
}

public sealed class InMemorySpeechLog : ISpeechLog
{
    private static readonly char[] Separators = [' ', '\t', '，', ',', '、', '；', ';'];

    private readonly List<SpeechRecord> _records = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public void Append(SpeechRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Text))
        {
            return;
        }

        lock (_gate)
        {
            _records.Add(record);
        }
    }

    public IReadOnlyList<SpeechRecord> Search(string query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return _records.TakeLast(limit).Reverse().ToArray();
            }

            var terms = query.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return _records
                .Select(record => (Record: record, Score: terms.Count(term => record.Text.Contains(term, StringComparison.OrdinalIgnoreCase))))
                .Where(hit => hit.Score > 0)
                .OrderByDescending(hit => hit.Score)
                .ThenByDescending(hit => hit.Record.At)
                .Take(limit)
                .Select(hit => hit.Record)
                .ToArray();
        }
    }

    /// <summary>从 transcript.jsonl 恢复发言历史（只在启动阶段调用，此时不会有并发写入）。</summary>
    public async Task<int> LoadFromAsync(string transcriptPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(transcriptPath))
        {
            return 0;
        }

        var loaded = 0;

        await foreach (var line in File.ReadLinesAsync(transcriptPath, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var element = JsonDocument.Parse(line).RootElement;
                if (element.GetProperty("kind").GetString() != "speech")
                {
                    continue;
                }

                Append(new SpeechRecord(
                    element.TryGetProperty("turnId", out var turn) ? turn.GetString() ?? string.Empty : string.Empty,
                    element.GetProperty("author").GetString() ?? string.Empty,
                    "speech",
                    element.GetProperty("text").GetString() ?? string.Empty,
                    element.GetProperty("at").GetDateTimeOffset()));

                loaded++;
            }
            catch (JsonException)
            {
                // 半截行（崩溃残留）跳过即可，恢复不需要完美。
            }
        }

        return loaded;
    }
}
