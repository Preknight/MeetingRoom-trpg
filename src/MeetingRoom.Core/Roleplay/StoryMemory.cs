using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MeetingRoom.Core.Roleplay;

/// <summary>分层摘要里的一段：把更早的若干回合压成一段（§11.4-7）。</summary>
public sealed record MemorySummary(long FromRevision, long ToRevision, string Text);

/// <summary>
/// 长程记忆与压缩（设计文档 §11.4-7）：会议是短时的一次性讨论，叙事是几百轮，
/// 不可能把全部原文塞进上下文。分两层：
/// <list type="number">
///   <item><b>近 N 轮</b>保留原文——细节在近处最要紧；</item>
///   <item><b>更早的</b>按批次压成分层摘要。</item>
/// </list>
/// <para>
/// 两层都带<b>角色视角</b>：NPC 只能回忆自己经历过、或别人告诉过他的事。
/// 否则一个角色会突然说出"他从不在场时发生的事"——叙事里这是穿帮，而不是聪明。
/// 所以摘要<b>按角色各存一份</b>（同一批里不同角色看到的内容不同），并落 <c>memory.jsonl</c>：
/// 重启后记忆与剧情一样能回来。
/// </para>
/// </summary>
public sealed class StoryMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<StoryTurn> _recent = [];
    private readonly Dictionary<string, List<MemorySummary>> _byActor = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly int _keepRecentTurns;
    private readonly int _summaryBatch;
    private readonly int _maxSummaryChars;

    public StoryMemory(
        int keepRecentTurns = 8,
        int summaryBatch = 16,
        int maxSummaryChars = 400,
        string? path = null)
    {
        _keepRecentTurns = Math.Max(0, keepRecentTurns);
        _summaryBatch = Math.Max(1, summaryBatch);
        _maxSummaryChars = Math.Max(40, maxSummaryChars);
        _path = path;

        if (path is not null && File.Exists(path))
        {
            Load();
        }
    }

    /// <summary>尚未被压成摘要的保留窗口（原文）。</summary>
    public IReadOnlyList<StoryTurn> RecentTurns
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    /// <summary>某个角色已经压好的那几段摘要（更早的事）。</summary>
    public IReadOnlyList<MemorySummary> SummariesFor(string actorId)
    {
        lock (_gate)
        {
            return _byActor.TryGetValue(actorId, out var summaries) ? summaries.ToArray() : [];
        }
    }

    /// <summary>记录一轮；超出保留窗口的部分按角色压进摘要。</summary>
    public void Record(StoryTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        lock (_gate)
        {
            _recent.Add(turn);

            while (_recent.Count > _keepRecentTurns + _summaryBatch)
            {
                var batch = _recent.Take(_summaryBatch).ToArray();
                _recent.RemoveRange(0, _summaryBatch);
                Archive(batch);
            }
        }
    }

    /// <summary>
    /// 取某个角色视角的回忆（近窗口原文 + 更早的摘要）：本身经历 + 公开发生的 + 被告知的。
    /// <paramref name="maxChars"/> 是预算——从最近往前装，装不下就丢更早的。
    /// </summary>
    public IReadOnlyList<string> RecallFor(string actorId, int maxChars = 1200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        if (maxChars <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            var lines = new List<string>();

            foreach (var turn in _recent)
            {
                foreach (var line in turn.Lines.Where(line => VisibleTo(line, actorId)))
                {
                    lines.Add($"{line.Author}：{line.Text}");
                }
            }

            var picked = new List<string>();
            var used = 0;

            for (var index = lines.Count - 1; index >= 0; index--)
            {
                if (used + lines[index].Length > maxChars)
                {
                    break;
                }

                used += lines[index].Length;
                picked.Add(lines[index]);
            }

            picked.Reverse();

            // 摘要放在最前面（更早的事）；它本身已经在生成时受过长度限制。
            var summaries = SummariesFor(actorId)
                .Select(summary => $"[{summary.FromRevision}-{summary.ToRevision}] {summary.Text}")
                .ToArray();

            return [.. summaries, .. picked];
        }
    }

    /// <summary>这条发言该不该被该角色回忆起来。</summary>
    private static bool VisibleTo(StoryLine line, string actorId) =>
        line.Witnesses.Count == 0
        || string.Equals(line.Author, actorId, StringComparison.Ordinal)
        || line.Witnesses.Contains(actorId, StringComparer.Ordinal);

    /// <summary>
    /// 把一批更早的回合压成摘要——<b>每个在这一批里露过面的人各一份</b>：
    /// 同一批里，在场的人看到全部，不在场的人只看到公开的那部分（§11.4-3）。
    /// </summary>
    private void Archive(IReadOnlyList<StoryTurn> batch)
    {
        var actors = batch
            .SelectMany(turn => turn.Lines)
            .SelectMany(line => line.Witnesses.Append(line.Author))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var actorId in actors)
        {
            var text = Compress(batch, actorId);

            if (text.Length == 0)
            {
                continue;
            }

            var summary = new MemorySummary(batch[0].Revision, batch[^1].Revision, text);

            if (!_byActor.TryGetValue(actorId, out var summaries))
            {
                summaries = [];
                _byActor[actorId] = summaries;
            }

            summaries.Add(summary);
            Persist(actorId, summary);
        }
    }

    /// <summary>
    /// 确定性压缩：取该角色看得到的每条发言的首句拼成一段。
    /// 这里刻意不调模型——与思考摘要同一条原则（程序能做的不花 token），
    /// 也让长程记忆的行为可测、可复现。
    /// </summary>
    private string Compress(IReadOnlyList<StoryTurn> batch, string actorId)
    {
        var builder = new StringBuilder();

        foreach (var line in batch.SelectMany(turn => turn.Lines).Where(line => VisibleTo(line, actorId)))
        {
            var piece = $"{line.Author}：{FirstSentence(line.Text)}";

            if (builder.Length + piece.Length + 2 > _maxSummaryChars)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(piece);
        }

        return builder.ToString().Trim();
    }

    private void Persist(string actorId, MemorySummary summary)
    {
        if (_path is null)
        {
            return;
        }

        EnsureDirectory();

        var line = JsonSerializer.Serialize(
            new
            {
                kind = "summary",
                actor = actorId,
                from = summary.FromRevision,
                to = summary.ToRevision,
                text = summary.Text,
            },
            JsonOptions);

        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void Load()
    {
        foreach (var line in File.ReadLines(_path!))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.GetProperty("kind").GetString() != "summary")
                {
                    continue;
                }

                var actor = root.GetProperty("actor").GetString();
                if (string.IsNullOrWhiteSpace(actor))
                {
                    continue;
                }

                var summary = new MemorySummary(
                    root.GetProperty("from").GetInt64(),
                    root.GetProperty("to").GetInt64(),
                    root.GetProperty("text").GetString() ?? string.Empty);

                if (!_byActor.TryGetValue(actor, out var summaries))
                {
                    summaries = [];
                    _byActor[actor] = summaries;
                }

                summaries.Add(summary);
            }
            catch (JsonException)
            {
                // 半截行（崩溃残留）跳过：恢复不需要完美。
            }
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string FirstSentence(string text)
    {
        const int MaxChars = 40;

        var trimmed = (text ?? string.Empty).Trim();

        foreach (var (index, character) in trimmed.Select((character, index) => (index, character)))
        {
            if (character is '。' or '！' or '？' or '.' or '!' or '?')
            {
                return trimmed[..(index + 1)];
            }
        }

        return trimmed.Length > MaxChars ? trimmed[..MaxChars] + "…" : trimmed;
    }
}
