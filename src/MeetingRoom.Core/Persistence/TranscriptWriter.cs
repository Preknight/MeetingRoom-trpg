using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeetingRoom.Agent;

namespace MeetingRoom.Core.Persistence;

/// <summary>transcript.jsonl 的一行（设计文档 §5.5）：append-only、每行一条 JSON、<see cref="Seq"/> 单调。</summary>
public sealed record TranscriptRecord(
    long Seq,
    string Kind,
    string Author,
    string Text,
    DateTimeOffset At,
    string? TurnId = null);

/// <summary>
/// transcript.jsonl 写入器：讨论区消息与<b>思考全文</b>都写这里——思考只落盘、默认不进上下文（§4.4）。
/// 多与会者并行写同一份 transcript，所以写入一路串行化，崩溃后可按行重建。
/// </summary>
public sealed class TranscriptWriter : ITurnRecorder, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter? _writer;
    private long _seq;

    public TranscriptWriter(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>已分配的最大序号。</summary>
    public long NextSeq => Interlocked.Read(ref _seq);

    /// <summary>追加一条记录，返回它分到的 Seq。</summary>
    public async Task<long> AppendAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var line = JsonSerializer.Serialize(record with { Seq = seq }, JsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWriter();
            await _writer!.WriteLineAsync(line).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return seq;
    }

    public ValueTask RecordAsync(TurnRecord record, CancellationToken cancellationToken = default) =>
        new(AppendAsync(
            new TranscriptRecord(0, record.Kind, record.ParticipantId, record.Text, DateTimeOffset.UtcNow, record.TurnId),
            cancellationToken));

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 不带 BOM：jsonl 的每一行都要能被独立解析。
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
                _writer = null;
            }
        }
        finally
        {
            // 只释放文件句柄、不销毁信号量：DisposeAsync 允许重复调用，
            // 而且释放后再写入会重新打开文件（Append 模式）。
            _gate.Release();
        }
    }
}
