using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 一条后台记录（<c>backstage.jsonl</c> 的一行，设计文档 §3.1）。
/// <para>
/// 它只记<b>说书人与角色在"想什么、调了什么工具"</b>——前台剧情另有 <c>story.jsonl</c>，
/// 两者分开落盘：后台是观察用的旁路，绝不能污染剧情时间线。
/// </para>
/// </summary>
/// <param name="Rev">第几幕（与 <c>story.jsonl</c> 的 revision 对齐）。</param>
/// <param name="At">写入时间（UTC）。</param>
/// <param name="Kind"><c>thought</c> 或 <c>tool</c>。</param>
/// <param name="Who">谁（说书人 / 角色 id）。</param>
/// <param name="Name">工具名（只有 <c>kind=tool</c> 有）。</param>
/// <param name="Args">工具参数（JSON 文本；只有 <c>kind=tool</c> 有）。</param>
/// <param name="Result">工具回执（只有 <c>kind=tool</c> 有）。</param>
/// <param name="Text">思考全文（只有 <c>kind=thought</c> 有）。</param>
public sealed record BackstageEntry(
    long Rev,
    DateTimeOffset At,
    string Kind,
    string Who,
    string? Name = null,
    string? Args = null,
    string? Result = null,
    string? Text = null);

/// <summary>
/// <see cref="ParticipantRegistry"/> 往后台记录里送的一条信号（产生点就在 registry 的 callbacks 里）。
/// </summary>
/// <param name="ParticipantId">谁产生的。</param>
/// <param name="TurnId">哪一轮（同一 turn 的思考会被合并成一条）。</param>
/// <param name="Kind"><c>thought</c> / <c>tool</c> / <c>tool-result</c>。</param>
public sealed record BackstageSignal(
    string ParticipantId,
    string? TurnId,
    string Kind,
    string? Name = null,
    string? Args = null,
    string? Result = null,
    string Text = "");

/// <summary>
/// 后台观察记录的落盘与读取（设计文档 §3.1）。
/// <para>
/// 为什么单独一个文件：说书人的思考与工具调用以前只在前端 <c>store.backstage</c> 的内存里，
/// 刷新就没了。<c>story.jsonl</c> 是剧情时间线（权威状态），后台动态塞进去会污染重放。
/// </para>
/// <para>
/// 滚动保留最近 <see cref="KeepEntries"/> 条：超过就裁掉头部并<b>重写整个文件</b>——
/// 只在裁的时候写一次，不会变成每行都重写的负担。
/// </para>
/// </summary>
public sealed class BackstageLog
{
    /// <summary>保留条数（设计文档 §3.1：默认最近 2000 条）。</summary>
    public const int KeepEntries = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly List<BackstageEntry> _entries = [];
    private readonly Lock _gate = new();
    private readonly string? _path;

    /// <summary>正在攒的思考：key = who|turnId（流式思考是逐段来的，攒成一条再落库）。</summary>
    private readonly Dictionary<string, StringBuilder> _thoughts = new(StringComparer.Ordinal);

    /// <summary>已经发起、还没拿到回执的工具调用：key = who|name（按调用顺序配对）。</summary>
    private readonly Dictionary<string, Queue<string?>> _pendingCalls = new(StringComparer.Ordinal);

    public BackstageLog(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>当前内存里的条数（诊断 / 探针）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// 收一条产生点的信号。<paramref name="revision"/> 用"即将提交的那一幕"，
    /// 与前台 <c>story.jsonl</c> 对齐（§3.1 的 <c>rev</c>）。
    /// </summary>
    public void Record(BackstageSignal signal, long revision)
    {
        ArgumentNullException.ThrowIfNull(signal);

        lock (_gate)
        {
            switch (signal.Kind)
            {
                case "thought":
                    AccumulateThought(signal);
                    return;

                case "tool":
                    // 先把手上的思考落一条，保证"想完再动手"的顺序不会被搅乱。
                    FlushThoughtsLocked(revision);
                    _pendingCalls.TryGetValue(Key(signal), out var queue);
                    queue ??= new Queue<string?>();
                    queue.Enqueue(signal.Args);
                    _pendingCalls[Key(signal)] = queue;
                    return;

                case "tool-result":
                    FlushThoughtsLocked(revision);

                    string? args = null;

                    if (_pendingCalls.TryGetValue(Key(signal), out var pending) && pending.Count > 0)
                    {
                        args = pending.Dequeue();
                    }

                    AppendLocked(new BackstageEntry(
                        revision,
                        DateTimeOffset.UtcNow,
                        "tool",
                        signal.ParticipantId,
                        signal.Name,
                        args,
                        signal.Result));
                    return;

                default:
                    return;
            }
        }
    }

    /// <summary>把攒着的思考落成记录（一 turn 一条）。会话层在每轮结束时调用。</summary>
    public void FlushThoughts(long revision)
    {
        lock (_gate)
        {
            FlushThoughtsLocked(revision);
        }
    }

    /// <summary>
    /// 读一段：<paramref name="since"/> 之后（含）的记录，最多 <paramref name="limit"/> 条，按 rev 升序。
    /// 只读——不改任何状态。
    /// </summary>
    public IReadOnlyList<BackstageEntry> View(long since, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 500);

        lock (_gate)
        {
            return _entries
                .Where(entry => entry.Rev >= since)
                .OrderBy(entry => entry.Rev)
                .ThenBy(entry => entry.At)
                .Take(limit)
                .ToArray();
        }
    }

    /// <summary>持久化用的公开路径（诊断用）。</summary>
    public string? Path => _path;

    private static string Key(BackstageSignal signal) => $"{signal.ParticipantId}|{signal.Name}";

    private void AccumulateThought(BackstageSignal signal)
    {
        var key = $"{signal.ParticipantId}|{signal.TurnId}";

        if (!_thoughts.TryGetValue(key, out var builder))
        {
            builder = new StringBuilder();
            _thoughts[key] = builder;
        }

        builder.Append(signal.Text);
    }

    private void FlushThoughtsLocked(long revision)
    {
        if (_thoughts.Count == 0)
        {
            return;
        }

        foreach (var (key, builder) in _thoughts)
        {
            var text = builder.ToString().Trim();

            if (text.Length == 0)
            {
                continue;
            }

            var who = key.Split('|')[0];
            AppendLocked(new BackstageEntry(revision, DateTimeOffset.UtcNow, "thought", who, Text: text));
        }

        _thoughts.Clear();
    }

    private void AppendLocked(BackstageEntry entry)
    {
        _entries.Add(entry);
        AppendToFile(entry);

        if (_entries.Count <= KeepEntries)
        {
            return;
        }

        // 滚动：裁掉头部后重写整份文件（低频，每 2000 条一次）。
        _entries.RemoveRange(0, _entries.Count - KeepEntries);
        RewriteFile();
    }

    private void AppendToFile(BackstageEntry entry)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                _path,
                JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 后台记录是观察用的旁路：写不进去也不能影响剧情推进。
        }
    }

    private void RewriteFile()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var payload = new StringBuilder();

            foreach (var entry in _entries)
            {
                payload.Append(JsonSerializer.Serialize(entry, JsonOptions)).Append('\n');
            }

            File.WriteAllText(_path, payload.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>重启后读回（只保留最后 <see cref="KeepEntries"/> 条）。</summary>
    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<BackstageEntry>(line, JsonOptions) is { } entry)
                    {
                        _entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // 半行（进程被杀时可能留下）直接跳过：后台记录不值得为它中断启动。
                }
            }

            if (_entries.Count > KeepEntries)
            {
                _entries.RemoveRange(0, _entries.Count - KeepEntries);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
