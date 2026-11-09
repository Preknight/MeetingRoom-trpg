using MeetingRoom.Agent;
using MeetingRoom.Agent.Thoughts;
using MeetingRoom.Agent.Tools;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Persistence;
using MeetingRoom.Core.Roles;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Participants;

/// <summary>与会者注册表的装配选项。</summary>
public sealed record ParticipantRegistryOptions
{
    /// <summary>同时运行的会话上限（<c>limits.maxConcurrentTurns</c>）：超出的与会者进入 queued 排队。</summary>
    public int MaxConcurrentTurns { get; init; } = 4;

    public int MaxStepsPerTurn { get; init; } = 12;

    public int ThoughtSummaryMaxChars { get; init; } = ThoughtSummarizer.DefaultMaxChars;

    /// <summary>单次发言的字数上限（0 = 不限制）。</summary>
    public int MaxSpeechChars { get; init; }

    /// <summary>共享 system prompt；同构 provider 下所有会话必须字节一致（阶段 5 会给出正式内容）。</summary>
    public string SystemPrompt { get; init; } = "你是多角色会议中的一名与会者，按角色卡的要求发言。";

    /// <summary>事件出口（状态 / 思考 / 工具）；不传则只更新内存状态。</summary>
    public IEventBus? Bus { get; init; }

    /// <summary>落盘出口：思考全文只走这里（§4.4）。</summary>
    public TranscriptWriter? Transcript { get; init; }

    /// <summary>
    /// 后台观察记录的出口（《叙事模式后台与上下文设计》§3.1）：思考与工具调用在<b>产生点</b>就送出去。
    /// 叙事会话传进来（落到 <c>backstage.jsonl</c>）；会议模式不传——行为与以前完全一致。
    /// </summary>
    public Action<MeetingRoom.Core.Roleplay.BackstageSignal>? Backstage { get; init; }

    public IToolDispatcher? ToolDispatcher { get; init; }

    /// <summary>主持人 / 说书人的工具集：同一模式下所有会话必须完全一致（不按角色裁剪，§7.1）。</summary>
    public IReadOnlyList<AITool> Tools { get; init; } = [];

    /// <summary>
    /// 普通参与者的工具集；null = 与 <see cref="Tools"/> 相同（会议模式的行为）。
    /// 叙事模式下由编排层置空——NPC 不带工具（§11.5-2）。
    /// </summary>
    public IReadOnlyList<AITool>? MemberTools { get; init; }

    /// <summary>
    /// 谁使用"主持人 / 说书人"的工具集（<see cref="Tools"/>）。默认按角色卡的 <c>role: host</c> 判定（会议模式）；
    /// 叙事模式传"id == narratorId"——说书人未必是 host 卡，也可能就是一张普通角色卡。
    /// </summary>
    public Func<RoleCard, bool> CarriesHostTools { get; init; } = card => card.Role == ParticipantRole.Host;

    public Func<int>? CurrentMaterialVersion { get; init; }
}

/// <summary>
/// 与会者注册表（设计文档 §5.1）：把"同时有几个会话在跑"限制在 <see cref="ParticipantRegistryOptions.MaxConcurrentTurns"/>
/// 之内——超出的与会者立即标记 <see cref="ParticipantStatus.Queued"/>，前面的 turn 一结束就自动开始；
/// 同时把状态 / 思考 / 工具事件接到 EventBus（供唯一那条 SSE 推给前端）。
/// </summary>
public sealed class ParticipantRegistry
{
    private readonly ParticipantRegistryOptions _options;
    private readonly IChatClient _chatClient;
    private readonly SemaphoreSlim _turnGate;
    private readonly List<Participant> _participants = [];
    private readonly Lock _gate = new();

    public ParticipantRegistry(ParticipantRegistryOptions options, IChatClient chatClient)
    {
        _options = options;
        _chatClient = chatClient;
        _turnGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentTurns));
    }

    public IReadOnlyList<Participant> Participants
    {
        get
        {
            lock (_gate)
            {
                return _participants.ToArray();
            }
        }
    }

    /// <summary>登记一个与会者。会议进行中不允许增减角色，这条约束由编排层把关（§11.3）。</summary>
    public Participant Add(RoleCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        lock (_gate)
        {
            if (_participants.Any(p => string.Equals(p.Id, card.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"与会者 {card.Id} 已经登记过。");
            }

            AgentSession? session = null;
            var callbacks = BuildCallbacks(card.Id, () => session?.CurrentTurnId);

            session = new AgentSession(
                new AgentSessionOptions
                {
                    ParticipantId = card.Id,
                    MaxStepsPerTurn = _options.MaxStepsPerTurn,
                    CurrentMaterialVersion = _options.CurrentMaterialVersion,
                    ThoughtSummaryMaxChars = _options.ThoughtSummaryMaxChars,
                    MaxSpeechChars = _options.MaxSpeechChars,
                    Recorder = _options.Transcript,
                    Tools = _options.CarriesHostTools(card)
                        ? _options.Tools
                        : _options.MemberTools ?? _options.Tools,
                    Callbacks = callbacks,
                },
                _chatClient,
                _options.ToolDispatcher);

            var participant = new Participant(card, session);
            _participants.Add(participant);
            return participant;
        }
    }

    public Participant? Find(string participantId)
    {
        lock (_gate)
        {
            return _participants.FirstOrDefault(p => string.Equals(p.Id, participantId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 跑一个 turn，必要时排队。超出并发上限时该与会者先进入 queued，
    /// 拿到槽位后自动开始（前一个 turn 结束即释放槽位）。
    /// </summary>
    public async Task<TurnOutcome> RunTurnAsync(
        string participantId,
        TurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var participant = Find(participantId)
            ?? throw new KeyNotFoundException($"未注册的与会者：{participantId}");

        if (!_turnGate.Wait(0))
        {
            SetStatus(participant, ParticipantStatus.Queued);

            try
            {
                await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetStatus(participant, ParticipantStatus.Interrupted);
                throw;
            }
        }

        try
        {
            SetStatus(participant, ParticipantStatus.Thinking);

            var outcome = await participant.Session.RunTurnAsync(request, cancellationToken).ConfigureAwait(false);

            participant.CompletedTurns++;
            SetStatus(participant, outcome.Cancelled ? ParticipantStatus.Interrupted : ParticipantStatus.Idle);
            return outcome;
        }
        catch
        {
            SetStatus(participant, ParticipantStatus.Failed);
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>按该与会者的角色卡构造装配器（补上角色卡节）。阶段 5 的编排层会统一负责这件事。</summary>
    public ContextComposer CreateComposer(
        string participantId,
        IReadOnlyList<AITool> tools,
        IOrchestrationStrategy strategy,
        ComposeRequest request)
    {
        var participant = Find(participantId)
            ?? throw new KeyNotFoundException($"未注册的与会者：{participantId}");

        return new ContextComposer(
            participantId,
            _options.SystemPrompt,
            tools,
            strategy,
            request with { RoleSection = participant.Card.Body });
    }

    private TurnCallbacks BuildCallbacks(string participantId, Func<string?> turnId) => new()
    {
        Delta = text => Publish("participant.delta", new { id = participantId, turnId = turnId(), text }),
        ThoughtDelta = text =>
        {
            Publish("participant.thought", new { id = participantId, turnId = turnId(), text });
            _options.Backstage?.Invoke(new MeetingRoom.Core.Roleplay.BackstageSignal(
                participantId, turnId(), "thought", Text: text));
        },
        ToolCallsRequested = calls =>
        {
            foreach (var call in calls)
            {
                Publish("tool.call", new
                {
                    id = participantId,
                    name = call.Name,
                    args = call.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString()),
                });

                _options.Backstage?.Invoke(new MeetingRoom.Core.Roleplay.BackstageSignal(
                    participantId, turnId(), "tool", call.Name, SerializeArguments(call.Arguments)));
            }
        },
        ToolInvoked = (name, result) =>
        {
            Publish("tool.result", new { id = participantId, name, result });
            _options.Backstage?.Invoke(new MeetingRoom.Core.Roleplay.BackstageSignal(
                participantId, turnId(), "tool-result", name, Result: result));
        },
    };

    /// <summary>工具参数落盘用 JSON 文本（SSE 那份仍是 ToString，保持前端行为不变）。</summary>
    private static string? SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(arguments);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void SetStatus(Participant participant, ParticipantStatus status)
    {
        participant.Status = status;
        Publish("participant.status", new { id = participant.Id, status = status.ToWire() });
    }

    private void Publish(string type, object payload) => _options.Bus?.Publish(type, payload);
}
