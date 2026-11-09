using System.Text.Json;
using MeetingRoom.Agent;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Persistence;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Agent;

/// <summary>
/// 思考的落盘策略（设计文档 §4.4 / 实现步骤 5.2 的验收）：
/// 全文只落盘与展示，绝不进上下文；进私有历史的只有程序生成的有界摘要。
/// </summary>
public sealed class ThoughtRecordingTests
{
    private const string SecretMarker = "SECRET-THOUGHT-MARKER";

    [Fact]
    public async Task ThoughtFullTextIsPersistedAndSummarizedOnlyIntoPrivateHistory()
    {
        using var temp = new TempDir();
        var transcriptPath = temp.File("transcript.jsonl");
        await using var transcript = new TranscriptWriter(transcriptPath);

        var thought = SecretMarker + "\n" + new string('想', 800) + "\n结论：先做两档，只考核试用转化率。";
        var composer = new RecordingComposer(RealComposer());
        var session = new AgentSession(
            new AgentSessionOptions
            {
                ParticipantId = "product-manager",
                Recorder = transcript,
                ThoughtSummaryMaxChars = 500,
            },
            FakeChatClient.ThoughtThenText(thought, "先做两档。"));

        // 第一轮：产出思考 + 正文。
        var first = await session.RunTurnAsync(new TurnRequest("product-manager", "请给出定价结论。", 0, composer));
        Assert.True(first.Produced);
        Assert.Equal(thought, first.Thought);

        // ① 思考全文进了 transcript.jsonl（先释放写入句柄再读）。
        await transcript.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(transcriptPath);
        var thoughts = lines
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(element => element.GetProperty("kind").GetString() == "thought")
            .ToList();

        Assert.Single(thoughts);
        Assert.Contains(SecretMarker, thoughts[0].GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.Equal("product-manager", thoughts[0].GetProperty("author").GetString());

        // ② 全文（含标记）从未出现在发给模型的消息里——摘要取的是"结论"段（尾部）。
        Assert.NotEmpty(composer.SentMessages);
        Assert.All(composer.SentMessages, messages => Assert.DoesNotContain(
            messages,
            message => (message.Text ?? string.Empty).Contains(SecretMarker, StringComparison.Ordinal)));

        // 第二轮：私有历史里多了一条摘要，而全文依然不被注入。
        composer.SentMessages.Clear();
        await session.RunTurnAsync(new TurnRequest("product-manager", "继续。", 0, composer));

        var secondRequest = composer.SentMessages[^1];
        Assert.Contains(secondRequest, message => (message.Text ?? string.Empty).Contains("[本轮思考摘要]", StringComparison.Ordinal));
        Assert.Contains(secondRequest, message => (message.Text ?? string.Empty).Contains("结论：先做两档", StringComparison.Ordinal));
        Assert.DoesNotContain(secondRequest, message => (message.Text ?? string.Empty).Contains(SecretMarker, StringComparison.Ordinal));

        // 摘要只进这个会话的私有历史，不进"共享前缀"（前两条消息）。
        var prefix = secondRequest.Take(ContextComposer.PrefixMessageCount);
        Assert.DoesNotContain(prefix, message => (message.Text ?? string.Empty).Contains("[本轮思考摘要]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledTurnIsNotPersisted()
    {
        using var temp = new TempDir();
        var transcriptPath = temp.File("transcript.jsonl");
        await using var transcript = new TranscriptWriter(transcriptPath);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var session = new AgentSession(
            new AgentSessionOptions { ParticipantId = "pm", Recorder = transcript },
            FakeChatClient.ThoughtThenText("思考", "正文"));

        var outcome = await session.RunTurnAsync(
            new TurnRequest("pm", "任务", 0, new RecordingComposer(RealComposer())),
            cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.False(File.Exists(transcriptPath));
        Assert.Equal(0, transcript.NextSeq);
    }

    private static ContextComposer RealComposer() =>
        new(
            "product-manager",
            "你是与会者。",
            [],
            new MeetingStrategy(),
            new ComposeRequest { SharedSection = "议题：定价", RoleSection = "产品经理视角" });
}
