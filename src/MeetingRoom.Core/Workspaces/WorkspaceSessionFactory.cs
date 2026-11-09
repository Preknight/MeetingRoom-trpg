using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Materials;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Paths;
using MeetingRoom.Core.Persistence;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Rules;
using MeetingRoom.Core.Vtt;
using Microsoft.Extensions.AI;

using MeetingRoom.Core.Maps;

namespace MeetingRoom.Core.Workspaces;

/// <summary>
/// 按**当前工作空间**装配一场会话（设计方案 §15.2 / §15.10 的 W2）：
/// 会话目录来自 <see cref="WorkspaceLayout"/>，角色卡来自工作空间根的 <c>roles/</c>——
/// 前者让"两场会话的资料互不干扰"，后者让"公共角色卡共享"。
/// </summary>
public sealed class WorkspaceSessionFactory
{
    private readonly IChatClient _chatClient;
    private readonly IEventBus _bus;
    private readonly MeetingRoomOptions _options;
    private readonly IVttClient? _vtt;

    public WorkspaceSessionFactory(
        IChatClient chatClient,
        IEventBus bus,
        MeetingRoomOptions options,
        IVttClient? vtt = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);

        _chatClient = chatClient;
        _bus = bus;
        _options = options;
        _vtt = vtt;
    }

    /// <summary>
    /// 工作空间共享的角色卡；一张都没有时写入内置默认卡（开箱可用，之后用户可以随意改，§5.4）。
    /// <b>每次装配都重新读盘</b>：换了工作空间就该换角色卡，不做缓存。
    /// </summary>
    public IReadOnlyList<RoleCard> LoadSharedRoles(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var cards = RoleCardLoader.LoadDirectory(layout.RolesDir);

        if (cards.Count > 0)
        {
            return cards;
        }

        DefaultRoles.WriteTo(layout.RolesDir);
        return RoleCardLoader.LoadDirectory(layout.RolesDir);
    }

    /// <summary>
    /// 装配一场会议：资料库、<c>transcript.jsonl</c> 与发言检索都在**这场自己的目录**里；
    /// 历史发言在此处从落盘记录恢复（此时没有并发写入，见风险 9）。
    /// </summary>
    public async Task<WorkspaceMeetingSession> CreateMeetingAsync(
        WorkspaceLayout layout,
        SessionDescriptor session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(session);

        var directory = layout.SessionDir(WorkspaceKind.Meeting, session.Id);
        Directory.CreateDirectory(directory);

        var transcriptPath = Path.Combine(directory, "transcript.jsonl");
        var materials = new MaterialStore(
            directory,
            new MaterialSummaryPolicy(_options.Summaries.MaterialSummaryFallbackChars),
            _bus);
        var transcript = new TranscriptWriter(transcriptPath);
        var speech = new InMemorySpeechLog();
        var recovered = await speech.LoadFromAsync(transcriptPath, cancellationToken).ConfigureAwait(false);

        var meeting = new MeetingSession(
            new MeetingOptions
            {
                MeetingId = session.Id,
                Topic = string.IsNullOrWhiteSpace(session.Topic) ? "待定议题" : session.Topic!,
                MaxConcurrentTurns = _options.Limits.MaxConcurrentTurns,
                MaxStepsPerTurn = _options.Limits.MaxStepsPerTurn,
                SteerMergeWindowMs = _options.Limits.SteerMergeWindowMs,
                MaterialSummaryFallbackChars = _options.Summaries.MaterialSummaryFallbackChars,
                ThoughtSummaryMaxChars = _options.Summaries.ThoughtSummaryMaxChars,
                AlwaysMaterialMaxCount = _options.Context.AlwaysMaterialMaxCount,
                TriggeredTokenBudget = _options.Context.TriggeredTokenBudget,
                TriggeredCooldownRounds = _options.Context.TriggeredCooldownRounds,
                MaxRounds = _options.Limits.MaxRounds,
                MaxSpeechChars = _options.Speech.MaxCharsPerSpeech,
                Interaction = _options.Interaction,
            },
            _chatClient,
            _bus,
            materials,
            speech,
            new MeetingStrategy(),
            transcript: transcript);

        foreach (var card in LoadSharedRoles(layout))
        {
            meeting.AddParticipant(card);
        }

        return new WorkspaceMeetingSession(meeting, transcript, recovered);
    }

    /// <summary>
    /// 装配一场叙事：剧情时间线（<c>story.jsonl</c>）与长程记忆（<c>memory.jsonl</c>）都在会话目录里，
    /// 构造时按行重放——所以"打开一场已有叙事"就是再装配一次（§11.4-2 / B3）。
    /// </summary>
    public RoleplaySession CreateRoleplay(WorkspaceLayout layout, SessionDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(session);

        var directory = layout.SessionDir(WorkspaceKind.Roleplay, session.Id);
        Directory.CreateDirectory(directory);

        var options = new RoleplayOptions
        {
            StoryId = session.Id,
            World = session.Worldview ?? string.Empty,
            Situation = session.Situation ?? string.Empty,
            UserRole = session.UserRole ?? string.Empty,
            Cast = session.Cast,
            NarratorId = string.IsNullOrWhiteSpace(session.NarratorId) ? "narrator" : session.NarratorId!,
            StoryPath = Path.Combine(directory, "story.jsonl"),
            // 前台三层上下文（《叙事模式后台与上下文设计》§1.3）：L2 上限 / 压缩比例 / 单条截断。
            Context = _options.Roleplay.Context,
            // §12：0 = 只写不推进（复盘照常，不点名不描写）。
            NarratorBeatsPerTurn = _options.Roleplay.NarratorBeatsPerTurn,
            Interaction = _options.Interaction,
            MaxConcurrentTurns = _options.Limits.MaxConcurrentTurns,
            MaxStepsPerTurn = _options.Limits.MaxStepsPerTurn,
            ThoughtSummaryMaxChars = _options.Summaries.ThoughtSummaryMaxChars,
            Vtt = _options.Vtt,
            // M3：地图文档来源 —— 工作空间 maps/ 下"第一张已标定的"（与编辑器/战术板同一口径）。
            // M8b：地图视觉——取当前地图（第一张已标定的）合成 PNG；不出图时返回 null。
            Vision = _options.Vision,
            MapViewImageProvider = playerView =>
            {
                var store = new MapStore(layout.MapsDir);

                if (store.List().FirstOrDefault(asset => asset.Calibrated) is not { } asset ||
                    store.GetDocument(asset.Id) is not { } document)
                {
                    return null;
                }

                MapImageSource? source = null;

                if (document.Image is not null && store.ContentPath(asset.Id) is { } path && File.Exists(path))
                {
                    source = new MapImageSource(File.ReadAllBytes(path));
                }

                return MapViewRenderer.Render(document, source, playerView, _options.Vision.MaxPx);
            },
            // M6b：迷雾写入——把格索引并进 explored（不碰 visible），返回实际新增格数。
            MapFogRevealer = cells =>
            {
                var store = new MapStore(layout.MapsDir);

                if (store.List().FirstOrDefault(asset => asset.Calibrated) is not { } asset ||
                    store.GetDocument(asset.Id) is not { } document)
                {
                    return null;
                }

                var explored = new HashSet<MeetingRoom.Core.Maps.GridCell>(document.Fog?.ExploredCells ?? []);
                var before = explored.Count;
                explored.UnionWith(cells);

                if (explored.Count == before)
                {
                    return 0;
                }

                store.SaveDocument(
                    document.Id,
                    document.Walls,
                    document.Rooms,
                    document.Tokens,
                    new MeetingRoom.Core.Maps.MapFog(true, explored.ToArray(), document.Fog?.VisibleCells ?? []));

                return explored.Count - before;
            },
            MapDocumentProvider = () => new MapStore(layout.MapsDir).List()
                .FirstOrDefault(asset => asset.Calibrated) is { } asset
                    ? new MapStore(layout.MapsDir).GetDocument(asset.Id)
                    : null,
        };

        // 规则包与数值表（C1–C4 / D1–D3）：没有它们，战斗与检定工具只能回"规则包未加载"。
        // 加载失败**不抛**：降级为"未加载"，由工具的拒绝理由说明，而不是让整场会话起不来。
        var sheets = new SheetStore(Path.Combine(directory, "sheets.json"));

        // 第 1 层来源（§26.2）：roles/<id>.sheet.json 预加载 + 校验；
        // 校验不过的**不进表**，问题写进启动诊断（§26.4）——不允许"能扣血但检定算不了"的半残表。
        var loaded = RoleSheetLoader.Load(layout.RolesDir);

        foreach (var sheet in loaded.Sheets)
        {
            sheets.Put(sheet);
        }

        return new RoleplaySession(
            options,
            _chatClient,
            _bus,
            LoadSharedRoles(layout),
            _vtt,
            LoadRules(),
            sheets,
            loaded.Problems);
    }

    /// <summary>
    /// 规则包路径：配置优先，环境变量 <c>MEETINGROOM_RULES_DATA</c> 兜底（与 <c>--probe-*</c> 同一来源）。
    /// <b>相对路径按安装目录解析</b>（默认值就是相对的 <c>data/rules/rules-data.json</c>）——
    /// 从别处启动 Host 时 cwd 不是 exe 目录，按 cwd 找会静默找不到包。
    /// 读不到 / 解析失败一律返回 <c>null</c>，让工具用可叙述的拒绝说明，不中断会话。
    /// </summary>
    private RulesData? LoadRules()
    {
        var path = _options.Rules.DataPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("MEETINGROOM_RULES_DATA");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(PathLayout.InstallDir, path);

        if (!File.Exists(fullPath))
        {
            return null;
        }

        // 进程内按路径缓存：规则包是只读静态资料（4.2 MB / 解析 ~80 ms），
        // 每开一场会话就重解析一次纯属浪费。改了包要重启进程才生效。
        var lazy = RulesCache.GetOrAdd(
            fullPath,
            file => new Lazy<RulesData?>(
                () =>
                {
                    try
                    {
                        return RulesDataLoader.Load(file);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>规则包缓存：路径 → 解析结果（解析失败也缓存，避免每次会话都去读一遍坏包）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<RulesData?>> RulesCache =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 一场装配好的会议与它的落盘附属物：释放时要一起收（<c>transcript.jsonl</c> 的写句柄不在
/// <see cref="MeetingSession"/> 手里）。
/// </summary>
public sealed record WorkspaceMeetingSession(MeetingSession Session, TranscriptWriter Transcript, int RecoveredSpeeches)
    : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
        await Transcript.DisposeAsync().ConfigureAwait(false);
    }
}
