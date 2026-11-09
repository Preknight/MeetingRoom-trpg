using MeetingRoom.Core.Events;
using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Persistence;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;

namespace MeetingRoom.Tests.Orchestration;

/// <summary>
/// 会议编排（实现步骤 7.1 / 7.3 / 7.4）：
/// 主持人的审核权限、自主点名、暂停与恢复、补料的软注入、迟到产出的补轮。
/// </summary>
public sealed class MeetingSessionTests
{
    [Fact]
    public async Task SpeechLandsInTheTranscript()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Root, "transcript.jsonl");
        var (session, _, _) = NewSession(temp, FakeSilent(), transcriptPath: path);
        session.AddParticipant(Member("product-manager", "产品经理"));

        await session.SayAsync("大家看一眼这个方案。");

        // transcript.jsonl 是 append-only 的审计面：用户发言与角色发言都写这里（§5.5）。
        // 写者以 FileShare.Read 打开（"别人只能读我"），所以读者必须显式请求 ReadWrite——
        // 这正是风险 9 说的那条 Windows 文件共享语义。
        var lines = ReadAllLines(path);

        Assert.Contains(lines, line => line.Contains("\"kind\":\"speech\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("大家看一眼这个方案。", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Contains("\"seq\":", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostCanApproveButOnlyTheUserCanAdopt()
    {
        using var temp = new TempDir();
        var (session, _, store) = NewSession(temp, FakeSilent());
        session.AddParticipant(Host());
        session.AddParticipant(Member("product-manager", "产品经理"));

        var card = await store.SubmitAsync(NewSubmit("定价建议"));

        // ① 主持人能驳回并给出理由。
        var rejected = await session.ReviewMaterialAsync(card.Id, approve: false, "摘要不够具体，请补数据", CancellationToken.None);
        Assert.True(rejected.Applied);
        Assert.Equal(MaterialStatus.Rejected, store.Find(card.Id)!.Status);
        Assert.Equal("摘要不够具体，请补数据", store.Find(card.Id)!.ReviewNote);

        // ② 主持人"通过"只到用户终审，绝不会变成 adopted。
        await store.ReviewAsync(card.Id, ReviewDecision.Reopen, ReviewActorKind.Member);
        await store.ReviewAsync(card.Id, ReviewDecision.Submit, ReviewActorKind.Member);

        var approved = await session.ReviewMaterialAsync(card.Id, approve: true, "转用户终审", CancellationToken.None);
        Assert.True(approved.Applied);
        Assert.Equal(MaterialStatus.UserReview, store.Find(card.Id)!.Status);

        // ③ 工具集里根本没有"采纳"这种工具——采纳权在用户（§4.2）。
        Assert.DoesNotContain(session.Tools, tool => tool.Name.Contains("adopt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, store.Version);
    }

    [Fact]
    public async Task HostNudgesAMemberWithoutAnyUserInput()
    {
        using var temp = new TempDir();

        // 第 1 次调用是主持人开局 → 它点名 product-manager；之后是正常发言。
        var client = new FakeChatClient((call, ct) => call == 1
            ? FakeChatClient.ToolCallStream(
                "nudge_member",
                new Dictionary<string, object?>
                {
                    ["participantId"] = "product-manager",
                    ["task"] = "先给出定价结论",
                },
                ct)
            : FakeChatClient.TextStream(2, TimeSpan.Zero, null, ct));

        var (session, bus, _) = NewSession(temp, client);
        session.AddParticipant(Host());
        session.AddParticipant(Member("product-manager", "产品经理"));

        // 用户没有任何输入。会议在后台推进，所以等"产品经理发过言"这个条件。
        await session.StartAsync();
        await WaitUntilAsync(
            () => bus.Recent(200).Any(e =>
                e.Type == "message.committed" && e.Payload!["author"]!.GetValue<string>() == "product-manager"),
            TimeSpan.FromSeconds(5));

        var events = bus.Recent(200);
        Assert.Contains(events, e => e.Type == "host.decision");
        Assert.Contains(
            events,
            e => e.Type == "message.committed" && e.Payload!["author"]!.GetValue<string>() == "product-manager");

        Assert.Equal(MeetingStatus.Running, session.Status);
    }

    [Fact]
    public async Task PauseStopsNewTurnsAndResumeContinuesWithoutReplay()
    {
        using var temp = new TempDir();
        var client = FakeChatClient.Text(chunks: 2, delay: TimeSpan.Zero);
        var (session, _, _) = NewSession(temp, client, mergeWindowMs: 200);
        session.AddParticipant(Member("product-manager", "产品经理"));

        // 先把会议按住（未开始时也允许），这样不会有后台轮次干扰断言。
        await session.PauseAsync();

        var nudge = session.NudgeAsync("product-manager", "请给出结论。");
        await Task.Delay(200);

        // 暂停期间不发起新的 turn。
        Assert.Equal(0, client.StreamCalls);

        await session.ResumeAsync();
        await nudge;

        // 恢复后从暂停点继续；已完成的 turn 不会被重放（只多了这一次）。
        Assert.Equal(1, client.StreamCalls);
    }

    [Fact]
    public async Task BroadcastDuringARunningTurnBecomesASteer()
    {
        using var temp = new TempDir();
        var started = new TaskCompletionSource();

        var client = new FakeChatClient((_, ct) => FakeChatClient.TextStream(
            chunks: 20,
            delay: TimeSpan.FromMilliseconds(50),
            onChunk: index =>
            {
                if (index == 1)
                {
                    started.TrySetResult();
                }
            },
            ct));

        var (session, bus, _) = NewSession(temp, client, mergeWindowMs: 300, pump: TimeSpan.FromMilliseconds(50));
        session.AddParticipant(Member("product-manager", "产品经理"));

        // 打断泵随会话常驻，不需要先 StartAsync。
        var turn = session.NudgeAsync("product-manager", "请给出结论。");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.BroadcastAsync("试点缩短到两周，先看数据。");
        await Task.Delay(700); // 等合并窗口到期 + 泵应用

        var applied = bus.Recent(200)
            .Where(e => e.Type == "interrupt.applied")
            .Select(e => e.Payload!["kind"]!.GetValue<string>())
            .ToList();

        Assert.Contains("steer", applied);

        await turn;
    }

    [Fact]
    public async Task StaleTurnTriggersACatchUpRoundWithTheNewMaterial()
    {
        using var temp = new TempDir();
        var started = new TaskCompletionSource();

        var client = new FakeChatClient((_, ct) => FakeChatClient.TextStream(
            chunks: 20,
            delay: TimeSpan.FromMilliseconds(50),
            onChunk: index =>
            {
                if (index == 1)
                {
                    started.TrySetResult();
                }
            },
            ct));

        var (session, bus, store) = NewSession(temp, client, mergeWindowMs: 100, pump: TimeSpan.FromMilliseconds(50));
        session.AddParticipant(Member("product-manager", "产品经理"));

        // A 开始思考。
        var turn = session.NudgeAsync("product-manager", "请给出定价结论。");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // B 产出并被采纳（版本 0 → 1），此时 A 仍在思考。
        var card = await store.SubmitAsync(NewSubmit("销售给的定价草案"));
        await store.ReviewAsync(card.Id, ReviewDecision.Approve, ReviewActorKind.Host);
        await store.ReviewAsync(card.Id, ReviewDecision.Adopt, ReviewActorKind.User);

        await turn;

        // A 的产出被判为陈旧 → 追加了一轮（而不是丢弃产出）。
        var caughtUp = bus.Recent(300)
            .Count(e => e.Type == "interrupt.applied" && e.Payload!["kind"]!.GetValue<string>() == "catchup");

        Assert.Equal(1, caughtUp);
        Assert.True(client.StreamCalls >= 2, $"预期至少两次流式请求（原轮 + 补轮），实际 {client.StreamCalls}");
    }

    [Fact]
    public async Task CacheDiagnosticsReportsSteerAsTheMissReason()
    {
        using var temp = new TempDir();
        // 会话要跑得比"补料 + 合并窗口"更久，注入才落得进去。
        var client = FakeChatClient.Text(chunks: 6, delay: TimeSpan.FromMilliseconds(200));
        var (session, bus, _) = NewSession(temp, client, mergeWindowMs: 150, pump: TimeSpan.FromMilliseconds(40));
        session.AddParticipant(Member("product-manager", "产品经理"));

        await session.StartAsync();
        await Task.Delay(300);

        // 会话正在思考时补料 → 走软注入；它下一轮既有 steer，前缀又变了。
        await session.BroadcastAsync("补充：只看转化率，不看 GMV。");
        Assert.True(
            session.Participants[0].Session.IsRunning,
            "补料时会话应当正在运行，否则注入落不下去");
        await Task.Delay(400);

        var steers = bus.Recent(200)
            .Count(e => e.Type == "interrupt.applied" && e.Payload!["kind"]!.GetValue<string>() == "steer");
        Assert.True(steers > 0, "补料应当对运行中的会话产生一次 steer 注入");

        await WaitUntilAsync(() => session.Status == MeetingStatus.Ended, TimeSpan.FromSeconds(20));

        var report = Assert.Single(session.CacheReport());
        Assert.True(report.Turns >= 3, $"预期至少跑了 3 轮，实际 {report.Turns}");
        Assert.Contains("首次请求", report.RecentMissReasons!, StringComparer.Ordinal);
        Assert.Contains("steer", report.RecentMissReasons!, StringComparer.Ordinal);
    }

    /// <summary>边写边读：必须显式允许别人写，否则会撞上"文件正被另一个进程使用"（风险 9）。</summary>
    private static string[] ReadAllLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"等待条件超时（{timeout.TotalSeconds:0.#}s）");
    }

    private static FakeChatClient FakeSilent() => FakeChatClient.Text(chunks: 1, delay: TimeSpan.Zero);

    private static RoleCard Host() => new()
    {
        Id = "host",
        Name = "主持人",
        Role = ParticipantRole.Host,
        Body = "你负责推进会议：审核资料、点名发言、推进轮次。",
    };

    private static RoleCard Member(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Body = $"你是{name}，先给结论再给理由。",
    };

    private static SubmitRequest NewSubmit(string title) => new()
    {
        Author = "sales-manager",
        Title = title,
        Summary = "建议按席位分层定价",
        Body = "建议按席位分层定价，先做企业版试用转化。",
    };

    private static (MeetingSession Session, EventBus Bus, MaterialStore Store) NewSession(
        TempDir temp,
        FakeChatClient client,
        int mergeWindowMs = 2000,
        TimeSpan? pump = null,
        EventBus? bus = null,
        string? transcriptPath = null)
    {
        var eventBus = bus ?? new EventBus();
        var store = new MaterialStore(
            Path.Combine(temp.Root, "meetings", "meet-001"),
            new MaterialSummaryPolicy(),
            eventBus);

        var session = new MeetingSession(
            new MeetingOptions
            {
                MeetingId = "meet-001",
                Topic = "定价与上线范围",
                SteerMergeWindowMs = mergeWindowMs,
                // 这些用例关心的是调度与注入，不是交互模式：关掉它，免得跑到第 3 轮被兜底提问拦住。
                Interaction = new InteractionOptions { Enabled = false },
            },
            client,
            eventBus,
            store,
            new InMemorySpeechLog(),
            new MeetingStrategy(),
            pump,
            transcriptPath is null ? null : new TranscriptWriter(transcriptPath));

        return (session, eventBus, store);
    }
}
