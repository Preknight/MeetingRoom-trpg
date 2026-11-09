using MeetingRoom.Core.Events;

namespace MeetingRoom.Host.Dev;

/// <summary>
/// 阶段 1 的假事件流（临时物：接入真实 Agent 后删除，见实现步骤 3.3）。
/// <c>GET /api/dev/fake</c> 触发后每 200ms 推一步剧本，覆盖五列所需的全部事件类型；
/// 同时充当 <see cref="IMeetingStateSource"/>，用来验证"重连先拿全量快照"。
/// </summary>
public sealed class DevEventStream : IMeetingStateSource
{
    /// <summary>推事件节奏（实现步骤 3.3：每 200ms 一条）。</summary>
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(200);

    private static readonly List<Step> Script = BuildScript();

    private readonly IEventBus _bus;
    private readonly Lock _gate = new();

    private readonly List<DevParticipant> _participants =
    [
        new("host", "主持人", "host", "idle"),
        new("product-manager", "产品经理", "member", "idle"),
        new("sales-manager", "销售经理", "member", "idle"),
        new("engineer", "工程师", "member", "idle"),
    ];

    private readonly List<DevMaterial> _materials = [];
    private readonly List<object> _messages = [];
    private readonly List<object> _userMessages = [];
    private readonly List<object> _hostLog = [];
    private readonly Dictionary<string, string> _streaming = [];
    private readonly Dictionary<string, List<object>> _backstage = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _materialVersion;
    private int _userMessageSeq;

    public DevEventStream(IEventBus bus) => _bus = bus;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    /// <summary>启动剧本循环；已在运行时返回 false（幂等）。</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return false;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(ct), ct);
            return true;
        }
    }

    /// <summary>
    /// 全量快照：设计文档 §8.3 的四项（status / materialVersion / participants / materials），
    /// 外加 messages 与 hostLog——§8.4 要求重连后能"无歧义重建"界面，讨论区与主持人记录同样必须能重建。
    /// </summary>
    public object Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>
    /// 调用方必须已持有 <see cref="_gate"/>。
    /// <b>刻意不返回 materials / messages / hostLog</b>：这份快照是全量替换语义，而本演示流是全局广播的，
    /// 阶段 4 起真实资料库也在往同一个前端 store 里写——返回这三项会把真资料冲掉。
    /// 演示流自己的内容靠增量事件渲染即可（阶段 5 会连同本类一起删除）。
    /// </summary>
    private object SnapshotLocked() => new
    {
        status = "running",
        materialVersion = _materialVersion,
        participants = _participants
            .Select(p => new { id = p.Id, name = p.Name, role = p.Role, status = p.Status })
            .ToArray(),
    };

    /// <summary>
    /// 停止剧本循环。真实会议一开始就调用它：两者共用同一条 SSE 与同一个前端 store，
    /// 同时活跃会互相覆盖（阶段 5 之后本类整体退役）。
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>指令区的用户发言（阶段 5 由真实会议指令取代）。</summary>
    public void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            var id = $"msg-user-{++_userMessageSeq}";
            var message = new { id, author = "user", kind = "speech", text, at = DateTimeOffset.UtcNow };
            _userMessages.Add(message);
            _messages.Add(message);
            _bus.Publish("message.committed", new
            {
                id,
                author = "user",
                kind = "speech",
                text,
                mentions = Array.Empty<string>(),
            });
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var index = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 每轮开播前把假状态复位并推一份全量快照：否则循环播放会让讨论区累积重复消息、
                // 已采纳的资料又被投回待审批（前端收到快照即整体重建）。
                if (index % Script.Count == 0)
                {
                    lock (_gate)
                    {
                        Reset();
                        _bus.Publish("meeting.state", SnapshotLocked());
                    }
                }

                var step = Script[index % Script.Count];
                index++;

                lock (_gate)
                {
                    _bus.Publish(step.Type, step.Payload(this));
                }

                await Task.Delay(StepInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止：正常退出。
        }
    }

    // ——— 剧本动作：每个都更新内存状态并返回事件载荷 ———

    private object Status(string id, string status)
    {
        ReplaceParticipant(id, p => p with { Status = status });
        return new { id, status };
    }

    private object Delta(string id, string turnId, string text)
    {
        _streaming[id] = _streaming.TryGetValue(id, out var accumulated) ? accumulated + text : text;
        return new { id, turnId, text };
    }

    private object Thought(string id, string turnId, string text)
    {
        AppendBackstage(id, "thought", text);
        return new { id, turnId, text };
    }

    private object ToolCall(string id, string name, object args)
    {
        AppendBackstage(id, "tool", $"调用 {name}");
        return new { id, name, args };
    }

    private object ToolResult(string id, string name, string result)
    {
        AppendBackstage(id, "tool", $"{name} → {result}");
        return new { id, name, result };
    }

    private object CommitSpeech(string author, string turnId, string text, string messageId)
    {
        _streaming.Remove(author);
        _messages.Add(new { id = messageId, author, kind = "speech", text, at = DateTimeOffset.UtcNow });
        return new { id = messageId, author, kind = "speech", text, mentions = Array.Empty<string>() };
    }

    private object HostDecision(string action, string target, string reason)
    {
        _hostLog.Add(new { action, target, reason, at = DateTimeOffset.UtcNow });
        return new { action, target, reason };
    }

    private object SubmitMaterial(DevMaterial material)
    {
        _materials.Add(material);
        ReplaceParticipant(material.Author, p => p with { Status = "waiting_approval" });
        return MaterialPayload(material);
    }

    private object ReviewMaterial(string id, string status, string note)
    {
        var index = _materials.FindIndex(m => m.Id == id);
        if (index < 0)
        {
            return new { id, status, note };
        }

        var material = _materials[index] with { Status = status };
        _materials[index] = material;

        if (status == "adopted")
        {
            _materialVersion++;
        }

        return new
        {
            material.Id,
            material.Title,
            material.Summary,
            material.SummarySource,
            material.Path,
            material.Author,
            material.Status,
            material.BasedOnVersion,
            material.SubmittedAt,
            note,
            materialVersion = _materialVersion,
        };
    }

    private object Interrupt(string participantId, string kind, string reason)
    {
        if (kind == "cancel")
        {
            ReplaceParticipant(participantId, p => p with { Status = "interrupted" });
        }

        AppendBackstage(participantId, "interrupt", $"{kind}：{reason}");
        return new { participantId, kind, reason };
    }

    private object MaterialPayload(DevMaterial material) => new
    {
        material.Id,
        material.Title,
        material.Summary,
        material.SummarySource,
        material.Path,
        material.Author,
        material.Status,
        material.BasedOnVersion,
        material.SubmittedAt,
    };

    private void Reset()
    {
        _materials.Clear();
        // 用户自己的发言跨轮保留：否则每轮复位会把它刚说的话一起吞掉。
        _messages.Clear();
        _messages.AddRange(_userMessages);
        _hostLog.Clear();
        _streaming.Clear();
        _backstage.Clear();
        _materialVersion = 0;

        for (var i = 0; i < _participants.Count; i++)
        {
            _participants[i] = _participants[i] with { Status = "idle" };
        }
    }

    private void ReplaceParticipant(string id, Func<DevParticipant, DevParticipant> update)
    {
        var index = _participants.FindIndex(p => p.Id == id);
        if (index >= 0)
        {
            _participants[index] = update(_participants[index]);
        }
    }

    private void AppendBackstage(string participantId, string kind, string text)
    {
        if (!_backstage.TryGetValue(participantId, out var entries))
        {
            entries = [];
            _backstage[participantId] = entries;
        }

        entries.Add(new { kind, text, at = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// 一轮剧本（约 55 步 ≈ 11 秒，循环播放）：主持人点名 → 三人依次发言 → 两份资料提交与审核 → 采纳后软注入。
    /// </summary>
    private static List<Step> BuildScript()
    {
        var steps = new List<Step>();
        const string Turn1 = "turn-1";
        const string Turn2 = "turn-2";

        void Add(string type, Func<DevEventStream, object?> payload) => steps.Add(new Step(type, payload));
        void Status(string id, string status) => Add("participant.status", s => s.Status(id, status));
        void Thought(string id, string turnId, string text) => Add("participant.thought", s => s.Thought(id, turnId, text));

        void Tool(string id, string name, object args, string result)
        {
            Add("tool.call", s => s.ToolCall(id, name, args));
            Add("tool.result", s => s.ToolResult(id, name, result));
        }

        // 逐块吐出正文，模拟流式生成（每块 3 个字 ≈ 600ms）。
        void Speech(string author, string turnId, string text, string messageId)
        {
            const int ChunkSize = 3;
            for (var offset = 0; offset < text.Length; offset += ChunkSize)
            {
                var piece = text.Substring(offset, Math.Min(ChunkSize, text.Length - offset));
                Add("participant.delta", s => s.Delta(author, turnId, piece));
            }

            Add("message.committed", s => s.CommitSpeech(author, turnId, text, messageId));
        }

        // ——— 第一轮：三人依次发言 ———
        Status("host", "thinking");
        Add("host.decision", s => s.HostDecision("点名发言", "product-manager", "先听产品视角，再谈定价落地"));

        Status("product-manager", "thinking");
        Thought("product-manager", Turn1, "先把目标定清楚：本轮要回答“先做哪一块、怎么卖”。");
        Speech("product-manager", Turn1, "结论先行：先做企业版试用转化，按席位分层定价。", "msg-001");
        Status("product-manager", "idle");

        Status("sales-manager", "reading");
        Tool("sales-manager", "list_materials", new { filter = "adopted" }, "命中 0 份");
        Thought("sales-manager", Turn1, "客户更在意能不能一句话讲清价值，档位越少越好卖。");
        Speech("sales-manager", Turn1, "建议先上两档，别一次铺四档，销售讲不清就卖不动。", "msg-002");
        Status("sales-manager", "writing");
        Add("material.submitted", s => s.SubmitMaterial(new DevMaterial(
            "mat-001",
            "定价建议草案",
            "建议按席位分层定价，先做企业版试用转化",
            "tool",
            "materials/sales-定价建议-001.md",
            "sales-manager",
            "host_review",
            0,
            DateTimeOffset.UtcNow)));
        Status("sales-manager", "idle");

        Status("engineer", "thinking");
        Thought("engineer", Turn1, "按席位分层要改计费口径，先估一下改动量。");
        Speech("engineer", Turn1, "按席位分层要动计费口径，估 5 人日；只做两档能省一半。", "msg-003");
        Status("engineer", "idle");

        // ——— 主持人审核与用户采纳 ———
        Add("host.decision", s => s.HostDecision("通过", "mat-001", "摘要清晰、有理由，转用户终审"));
        Add("material.reviewed", s => s.ReviewMaterial("mat-001", "user_review", "派给用户终审"));

        Status("host", "reading");
        Thought("host", Turn1, "两位都指向“先少档”，可以收敛本轮。");
        Add("material.reviewed", s => s.ReviewMaterial("mat-001", "adopted", "用户采纳：先按两档试点"));
        Add("host.decision", s => s.HostDecision("推进轮次", Turn2, "本轮收敛，进入落地细节"));

        // ——— 采纳后：软注入正在思考的工程师（原则 3 的可见证据） ———
        Status("engineer", "thinking");
        Add("interrupt.applied", s => s.Interrupt("engineer", "steer", "mat-001 已采纳，请基于最新资料修订"));
        Thought("engineer", Turn2, "资料版本变了，改成两档方案后工期重估为 3 人日。");
        Speech("engineer", Turn2, "按已采纳的两档方案重估：3 人日，可以放进本期。", "msg-004");
        Status("engineer", "idle");

        Status("product-manager", "reading");
        Thought("product-manager", Turn2, "需要把试用转化指标写进验收标准。");
        Speech("product-manager", Turn2, "补一条验收：企业版试用转化率作为本期唯一硬指标。", "msg-005");
        Status("product-manager", "idle");

        Add("material.submitted", s => s.SubmitMaterial(new DevMaterial(
            "mat-002",
            "验收指标建议",
            "以企业版试用转化率作为本期唯一硬指标，其余指标只观察不考核",
            "tool",
            "materials/pm-验收指标-002.md",
            "product-manager",
            "host_review",
            1,
            DateTimeOffset.UtcNow)));

        Status("host", "thinking");
        Add("host.decision", s => s.HostDecision("维持运行", "mat-002", "等用户终审，暂不结束会议"));

        return steps;
    }

    private sealed record Step(string Type, Func<DevEventStream, object?> Payload);

    private sealed record DevParticipant(string Id, string Name, string Role, string Status);

    private sealed record DevMaterial(
        string Id,
        string Title,
        string Summary,
        string SummarySource,
        string Path,
        string Author,
        string Status,
        int BasedOnVersion,
        DateTimeOffset SubmittedAt);
}
