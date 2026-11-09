using System.Text.Json;
using MeetingRoom.Core.Persistence;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Persistence;

/// <summary>
/// transcript.jsonl（设计文档 §5.5）：append-only、一行一条 JSON、Seq 单调，
/// 多与会者并行写也不会串行错误或丢行。
/// </summary>
public sealed class TranscriptWriterTests
{
    [Fact]
    public async Task WritesOneJsonObjectPerLineWithMonotonicSeq()
    {
        using var temp = new TempDir();
        var path = temp.File("transcript.jsonl");
        await using var writer = new TranscriptWriter(path);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => writer.AppendAsync(
            new TranscriptRecord(0, "speech", $"participant-{i % 3}", $"内容 {i}", DateTimeOffset.UtcNow))));

        // Windows 的共享语义不允许读者与写者同时打开，所以先释放句柄再读。
        await writer.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(path);

        Assert.Equal(20, lines.Length);
        Assert.Equal(20, writer.NextSeq);

        var seqs = lines
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64())
            .OrderBy(seq => seq)
            .ToList();

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), seqs);
    }

    [Fact]
    public async Task CreatesDirectoryAndKeepsChineseReadable()
    {
        using var temp = new TempDir();
        var path = temp.File("meetings/meet-001/transcript.jsonl");
        await using var writer = new TranscriptWriter(path);

        await writer.AppendAsync(new TranscriptRecord(0, "thought", "product-manager", "先把目标定清楚。", DateTimeOffset.UtcNow, "turn-1"));
        await writer.DisposeAsync();

        var line = Assert.Single(await File.ReadAllLinesAsync(path));

        // 中文原样落盘（不放宽编码器的话这里会是一串 \uXXXX），且没有 BOM。
        Assert.Contains("先把目标定清楚。", line, StringComparison.Ordinal);

        var root = JsonDocument.Parse(line).RootElement;
        Assert.Equal("thought", root.GetProperty("kind").GetString());
        Assert.Equal("turn-1", root.GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task ImplementsTurnRecorder()
    {
        using var temp = new TempDir();
        var path = temp.File("transcript.jsonl");
        await using var writer = new TranscriptWriter(path);

        await ((MeetingRoom.Agent.ITurnRecorder)writer).RecordAsync(
            new MeetingRoom.Agent.TurnRecord("thought", "engineer", "turn-2", "按两档方案重估工期。"));
        await writer.DisposeAsync();

        var root = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(path))).RootElement;

        Assert.Equal("thought", root.GetProperty("kind").GetString());
        Assert.Equal("engineer", root.GetProperty("author").GetString());
        Assert.Equal("turn-2", root.GetProperty("turnId").GetString());
        Assert.Equal(1, root.GetProperty("seq").GetInt64());
    }
}
