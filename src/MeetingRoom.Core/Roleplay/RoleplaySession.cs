using System.Text;
using MeetingRoom.Agent;
using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Rules;
using MeetingRoom.Core.Config;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roles;
using MeetingRoom.Core.Vtt;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Roleplay;

/// <summary>叙事会话的装配选项（设计文档 §11.4 / §11.6）。</summary>
public sealed record RoleplayOptions
{
    public required string StoryId { get; init; }

    /// <summary>世界观 / 背景（一段话即可）。</summary>
    public required string World { get; init; }

    /// <summary>开场情境（此刻的局面）。</summary>
    public required string Situation { get; init; }

    /// <summary>用户在故事里的身份 / 立场（空 = 由用户在开场里说明；用户是导演 DM，不是玩家）。</summary>
    public string UserRole { get; init; } = string.Empty;

    /// <summary>说书人的角色卡 id——叙事里唯一的控制面（§11.4-5）。</summary>
    public string NarratorId { get; init; } = "narrator";

    /// <summary>开局在场角色 id；为空 = 全部角色都在场。</summary>
    public IReadOnlyList<string> Cast { get; init; } = [];

    public int MaxConcurrentTurns { get; init; } = 2;

    public int MaxStepsPerTurn { get; init; } = 12;

    public int ThoughtSummaryMaxChars { get; init; } = 500;

    /// <summary>单条正文的字数上限（0 = 不限制）。叙事正文比会议发言长，默认不限。</summary>
    public int MaxNarrationChars { get; init; }

    /// <summary>
    /// 一轮里最多几个角色接话（时序调度，§11.4-1）。
    /// <c>0</c>（默认）= 与<b>在场角色数</b>相同：一次导演指令让所有在场角色都表态，而不是人为截断成前几个；
    /// 只有确实要压场面时才给一个更小的正数。说书人（主持人侧）不计入。
    /// </summary>
    public int MaxSpeakersPerTurn { get; init; }

    /// <summary>交互模式（阶段 9）：说书人的提问与提议都走同一条待决机制（批 4 起用）。</summary>
    public InteractionOptions Interaction { get; init; } = new();

    /// <summary>
    /// 枭熊只读地图（《枭熊地图接入实现方案》V1）。<c>null</c> 或 <c>enabled = false</c> 时
    /// 不拉取、不注入——没有地图的会话行为与以前完全一致。
    /// </summary>
    public VttOptions Vtt { get; init; } = new();

    /// <summary>
    /// 地图文档提供者（M3）：Host 注入"当前地图"（已标定的那张），渲染成说书人可见的空间段。
    /// <c>null</c> 或返回 <c>null</c> = 没有地图 → 那一段完全不出（不输出空壳或"未登记"垃圾）。
    /// </summary>
    public Func<Maps.MapDocument?>? MapDocumentProvider { get; init; }

    /// <summary>
    /// 迷雾写入（M6b）：给一批格索引，把它们并进 `explored`，返回**实际新增**的格数。
    /// Host 注入（它才拿得到 <c>MapStore</c>）；返回 <c>null</c> = 没有可写的地图。
    /// </summary>
    public Func<IReadOnlyList<Maps.GridCell>, int?>? MapFogRevealer { get; init; }

    /// <summary>
    /// 角色文字段的感知半径（格，M9a）：只描述这个范围内的东西。默认 12 格（与多模态裁剪同一个数）。
    /// </summary>
    public double CharacterViewRadiusCells { get; init; } = 12;

    /// <summary>
    /// 地图视觉（M8b）：<c>playerView=true</c> 取玩家视角、<c>false</c> 取 DM 视角，返回 PNG；
    /// <c>null</c> = 不出图（未标定 / 没有已探索格）。Host 注入——它才拿得到 MapStore。
    /// </summary>
    public Func<bool, byte[]?>? MapViewImageProvider { get; init; }

    /// <summary>地图视觉配置（默认关闭）。</summary>
    public VisionOptions Vision { get; init; } = new();

    /// <summary>
    /// 规则数据包（V3/C2）：攻击、法术与技能表都从这里查。<c>null</c> = 未加载，
    /// 战斗/检定工具会回一句"规则包未加载"，其余功能不受影响。
    /// </summary>
    public RulesData? Rules { get; init; }

    /// <summary>
    /// 每一轮末尾说书人复盘后，最多再执行几拍（点名 / 描写）——<b>有界</b>，防止它一轮里把场面跑飞。
    /// <para>
    /// <b>0 = 只写不推进</b>（§12）：复盘照常跑（说书人仍可提议世界状态 / 提问 / 建议收束），
    /// 但 <c>nudge_character</c> / <c>idle_beat</c> 会被<b>直接拒绝</b>且不入队——
    /// 导演还没介绍完场景时，说书人不能自顾自点名推进。
    /// </para>
    /// </summary>
    public int NarratorBeatsPerTurn { get; init; } = 1;

    /// <summary>
    /// 说书人待执行意图的队列上限（§11）。<b>0 = 自动</b>：`max(5, 在场角色数 + 1)`——
    /// 多角色场里"每个角色各被点名一次"必须都进得去，所以默认值跟着角色数走。
    /// </summary>
    public int NarratorIntentQueueLimit { get; init; }

    /// <summary>按角色视角检索长程记忆的字符预算（§11.4-7）。</summary>
    public int RecallMaxChars { get; init; } = 1200;

    /// <summary>剧情时间线的落盘路径（<c>data/stories/&lt;id&gt;/story.jsonl</c>）。</summary>
    public string? StoryPath { get; init; }

    /// <summary>
    /// 前台三层上下文的参数（《叙事模式后台与上下文设计》§1.3）：
    /// L2 上限、压缩比例、单条截断。默认值就是设计文档里定的那一组。
    /// </summary>
    public RoleplayContextOptions Context { get; init; } = new();

    /// <summary>共享 system prompt；同一模式下所有会话字节一致（硬约束 6）。</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    public const string DefaultSystemPrompt =
        "你在一场多角色角色扮演中扮演其中一个角色（本场由用户担任导演，他会下元指令）。用简体中文写正文。"
        + "只写你这个角色的言行，以及你此刻能感知到的环境与其他人——不要替其他角色或导演决定行动。"
        + "你看不见地图上的数字：说距离用「几步」「不远」「隔着一条走廊」这类能感觉到的说法，"
        + "不要报具体尺数或格数。"
        + "不要写元话语（不出现「作为 AI」「根据设定」「本次发言」这类词），不要复述别人刚说过的话，"
        + "不要输出标题、列表或格式标记。正文控制在三到六句：把空间留给别人接话，而不是一次写完整场。";
}

/// <summary>
/// <c>POST /api/roleplay/dm-note</c> 的结果（§3.3）：成功时带 <see cref="Id"/>（就是第五列那张卡片的 id），
/// 校验失败时带 <see cref="Error"/>。
/// </summary>
public sealed record DmNoteResult(bool Ok, string? Id, string? Error);

/// <summary>
/// 前台三层上下文的参数（§1.3）。四项都是"L2 多大才压缩"的旋钮：
/// <see cref="HistoryLimit"/> 与 <see cref="HistoryTokenBudget"/> 先到先生效。
/// </summary>
public sealed record RoleplayContextOptions
{
    /// <summary>L2 最多留多少条；超过就把前半段压进 L3。</summary>
    public int HistoryLimit { get; init; } = 80;

    /// <summary>L2 的 token 估算上限（先到先生效）。</summary>
    public int HistoryTokenBudget { get; init; } = 8000;

    /// <summary>压缩时压掉的比例（0.5 = 前一半进摘要）。</summary>
    public double CompressRatio { get; init; } = 0.5;

    /// <summary>单条剧情进上下文时截断到多少字符。</summary>
    public int LineChars { get; init; } = 200;
}

/// <summary>
/// 角色扮演（叙事）会话的编排外壳（设计文档 §11.2 / §11.4）：
/// 与会议模式<b>共用内核</b>（<c>AgentSession</c>、唯一那条 SSE、与会者注册表、角色卡、可中断运行时），
/// 差异只收在 <see cref="RoleplayStrategy"/> 与这一层里。
/// <para>
/// 本层负责一条完整链路：导演写开场 → 导演指令（元指令分流）→ 在场角色<b>时序</b>接话
/// → 提交进 <see cref="StoryTimeline"/>。正文一旦产生就成立：后续的状态结算失败不得撤销它（§11.3-4）。
/// </para>
/// </summary>
public sealed class RoleplaySession : SessionCore, INarratorActions, ITableActions, ICharacterActions, IStoryRecall
{
    private readonly RoleplayOptions _options;
    private readonly IEventBus _bus;
    private readonly RoleplayStrategy _strategy;
    private readonly ParticipantRegistry _registry;
    private readonly ITurnScheduler _scheduler;
    private readonly StoryTimeline _timeline;
    private readonly StoryMemory _memory;
    private readonly WorldStateStore _world;
    private readonly NarratorToolDispatcher _toolDispatcher;
    private readonly IVttClient? _vtt;
    private readonly VttOptions _vttOptions;
    private readonly IReadOnlyDictionary<string, string> _vttTokenIds;
    /// <summary>
    /// L2 剧情历史（三层上下文 §1）：**只追加**，压缩时才裁掉前半段。
    /// 上一版是 6 条滚动窗口（`RemoveAt(0)`）——每加一条就把第一行挤掉，共享节逐字变化
    /// → 前缀缓存每轮 miss。这是本批要修掉的真问题。
    /// </summary>
    private readonly List<HistoryLine> _history = [];

    /// <summary>L3 摘要段（压缩产物）：留在前缀里，只有压缩时才变。</summary>
    private readonly StringBuilder _summary = new();

    /// <summary>L3 已覆盖到第几幕（落盘后重启仍能续上）。</summary>
    private long _summaryUpTo;

    /// <summary>本场发生过几次压缩（诊断 / 探针用）。</summary>
    private int _historyCompressions;

    /// <summary>最近一次装配的前缀指纹（探针据它验"不压缩的轮次指纹不变"）。</summary>
    private string? _lastPrefixFingerprint;

    /// <summary>L3 摘要的落盘路径（与 <c>story.jsonl</c> 同级）；空 = 不落盘。</summary>
    private readonly string? _contextPath;

    /// <summary>
    /// 后台观察记录（§3.1）：思考与工具调用在产生点落盘，刷新/重启都不丢。
    /// 与 <c>story.jsonl</c> 分开——它是观察用的旁路，不参与剧情重放。
    /// </summary>
    private readonly BackstageLog _backstage;
    /// <summary>
    /// 说书人登记的待执行意图（点名 / 描写一拍）。<b>用 List 不用 Queue</b>：
    /// 同一轮里重复点名要"就地改内容、保留首次位置"，Queue 的入队语义做不到这件事。
    /// </summary>
    private readonly List<NarratorIntent> _narratorIntents = [];

    /// <summary>
    /// 本轮对每个去重键的尝试次数（`nudge:{角色id}` / `beat`）：回执里要说"第 N 次已合并"，
    /// 模型才知道自己排多了。每轮（一次导演指令）清零。
    /// </summary>
    private readonly Dictionary<string, int> _intentAttempts = new(StringComparer.Ordinal);

    /// <summary>
    /// 本轮内说书人是否<b>自己</b>把会话停住了（`declare_scene_end`，§13-B）。
    /// 与"外部暂停（导演点暂停）"是两件事：前者本轮应立即收尾，后者仍要走等待门。
    /// </summary>
    private bool _pausedByNarratorThisTurn;
    private string? _narratorId;

    /// <summary>
    /// 本轮导演输入原文（r9）：`dm_end` 必须能证明"导演这一轮说过话"。
    /// 在 <see cref="SayAsync"/> 开头写入、在收场成功后清空；开场轮与"导演沉默"期间为 <c>null</c>。
    /// </summary>
    private string? _directorInputThisRound;

    /// <summary>
    /// 本轮已经提过几次判定（r11）：<b>同一轮只允许一个</b>。
    /// 提示词管不住（实测 1.68 秒内连提两个不同技能，现有"同一件事"防刷拦不到），所以这里硬挡。
    /// 被动 / 暗骰一律算在同一个额度里，不留缝。
    /// </summary>
    private int _checksThisRound;

    /// <summary>本轮第一个判定的名字（拒绝时点名，帮模型理解"哪个已经用掉了"）。</summary>
    private string? _firstCheckThisRound;

    /// <summary>
    /// 本轮已提交的角色请求数（M9b 修补，码 <c>one-request-per-round</c>）：
    /// <c>request_judgement</c> 与 <c>request_combat_action</c> <b>共享</b>这一份额度。
    /// <para>
    /// 为什么必须限：r17 实测模型在 10 秒里连发 12 次 <c>request_judgement</c>（几乎一秒一次、
    /// reason 几乎一样），剧情流刷屏 13 行、却只落成 2 条 check——它把"没立刻反应"当成了"重试"。
    /// 说书人的 <c>request_check</c> 从 r11 起就有这道守卫，角色这条当时漏了。
    /// </para>
    /// </summary>
    private int _requestsThisRound;

    /// <summary>本轮第一个请求的摘要（拒绝时点名，让模型知道哪个已经用掉了）。</summary>
    private string? _firstRequestThisRound;

    /// <summary>上一轮地图图的版本与字节（<c>vision.onlyOnChange</c> 用；文档没变就不重取）。</summary>
    private long _lastMapImageRevision = -1;
    private byte[]? _lastMapImageDm;
    private byte[]? _lastMapImagePlayer;
    private VttSnapshot _vttSnapshot = VttSnapshot.Unavailable("还没有拉取过地图");
    private Encounter? _encounter;
    private int _encounterSequence;
    private readonly RulesData? _rules;
    private readonly SheetStore? _sheets;
    private readonly IReadOnlyList<string> _startupWarnings;
    private readonly CheckHistory _checkHistory = new();
    private int _checkSequence;

    protected override IEventBus Bus => _bus;

    protected override string StateEventType => "roleplay.state";

    protected override InteractionOptions Interaction => _options.Interaction;

    public RoleplaySession(
        RoleplayOptions options,
        IChatClient chatClient,
        IEventBus bus,
        IReadOnlyList<RoleCard> cards,
        IVttClient? vtt = null,
        RulesData? rules = null,
        SheetStore? sheets = null,
        IReadOnlyList<string>? startupWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cards);

        _options = options;
        _bus = bus;
        _vtt = vtt;
        _vttOptions = options.Vtt;
        _rules = rules ?? options.Rules;
        _sheets = sheets;
        _startupWarnings = startupWarnings ?? [];

        // L3 摘要与 story.jsonl 同级落盘：重启后不能丢（§1.2）。
        _contextPath = options.StoryPath is { Length: > 0 } storyPath
            ? Path.Combine(Path.GetDirectoryName(storyPath) ?? ".", "context-summary.json")
            : null;
        LoadContextSummary();

        // 后台记录与 story.jsonl 同级（data/stories/<id>/backstage.jsonl）。
        _backstage = new BackstageLog(
            options.StoryPath is { Length: > 0 } backstagePath
                ? Path.Combine(Path.GetDirectoryName(backstagePath) ?? ".", "backstage.jsonl")
                : null);

        // 角色卡 → 枭熊 token 的稳定映射（《枭熊地图接入实现方案》7.4-4）：
        // 没配的角色不进字典，锚点解析会退回名字匹配——老数据行为不变。
        var tokenIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var card in cards)
        {
            if (!string.IsNullOrWhiteSpace(card.VttTokenId))
            {
                tokenIds[card.Id] = card.VttTokenId.Trim();
            }
        }

        _vttTokenIds = tokenIds;

        // 时间线自带 JSONL 重建：崩溃后按行恢复，不必另建落盘通道（§11.4-2）。
        _timeline = new StoryTimeline(options.StoryPath);
        // 记忆与剧情同目录：重启后"他记得什么"也能回来（§11.4-7）。
        _memory = new StoryMemory(path: MemoryPath(options.StoryPath));
        // 说书人的判定：先认显式指定的 id，其次认角色卡里 role: host 的那张——否则用户把卡名从
        // host 改成别的（比如"说书人"），说书人就会凭空消失、开场没人写。
        // 必须在卡司之前定下来：谁在场也按它判定（见下）。
        _narratorId = cards
            .FirstOrDefault(card => string.Equals(card.Id, options.NarratorId, StringComparison.Ordinal))?.Id
            ?? cards.FirstOrDefault(card => card.Role == ParticipantRole.Host)?.Id;

        var actors = cards
            .Select(card => new RoleplayActor(card.Id, card.Name)
            {
                // 说书人始终在场（它控场）；卡司为空视为全员在场。
                // "谁说书人"按 id 判定，不看 role: host —— 运行期 data/roles/ 里那张 host 卡是
                // **会议主持人**，按 role 判定会让它凭空出现在叙事场景里、还作为 NPC 参与接话。
                Present = options.Cast.Count == 0
                    || string.Equals(card.Id, _narratorId, StringComparison.Ordinal)
                    || options.Cast.Contains(card.Id, StringComparer.Ordinal),
            })
            .ToArray();

        _strategy = new RoleplayStrategy(actors) { NarratorId = _narratorId ?? options.NarratorId };

        // 世界状态与卡司同源：初始在场集合由开局卡司决定；后续变更由说书人按导演的意图提交（§11.4 第 6b 项）。
        _world = new WorldStateStore(
            actors.Select(actor => new CharacterState(actor.Id, actor.Name) { Present = actor.Present }),
            place: options.Situation);

        // 上限为 0（默认）时取"在场角色数"：一次导演指令让所有在场角色都表态。
        // 说书人不计入——它控场，不算发言角色（§11.4-5）。
        var speakerLimit = options.MaxSpeakersPerTurn > 0
            ? options.MaxSpeakersPerTurn
            : actors.Count(actor =>
                actor.Present && !string.Equals(actor.Id, _narratorId, StringComparison.Ordinal));

        _scheduler = new SequentialTurnScheduler(speakerLimit);

        // 说书人的工具面（§11.4-5）：控场工具 + 地图 / 骰子 + 规则查询（§27），权限在工具内部判定。
        // 世界状态只有"提议"一条入口——说书人不能自己落盘剧情决策（§11.4 第 6b 项）。
        // 规则包顺手传进去：没加载时 lookup_* 明确回"规则包未加载"，不假装成功。
        _toolDispatcher = new NarratorToolDispatcher(this, IsNarrator, this, this, _rules, this);

        _registry = new ParticipantRegistry(
            new ParticipantRegistryOptions
            {
                MaxConcurrentTurns = Math.Max(1, options.MaxConcurrentTurns),
                MaxStepsPerTurn = options.MaxStepsPerTurn,
                ThoughtSummaryMaxChars = options.ThoughtSummaryMaxChars,
                MaxSpeechChars = options.MaxNarrationChars,
                SystemPrompt = options.SystemPrompt,
                Bus = bus,
                ToolDispatcher = _toolDispatcher,
                // 叙事模式下 NPC 一个工具都不带（§11.5-2）：工具调用会产生元话语、破坏沉浸。
                // 说书人单独一份控制面工具；"谁说书人"按 id 判定，不依赖 role: host。
                Tools = _toolDispatcher.Tools,
                MemberTools = _toolDispatcher.CharacterTools,
                // 后台落盘（§3.1）：思考与工具调用在这里被送进 backstage.jsonl。
                // rev 用"即将提交的那一幕"，与 story.jsonl 的 revision 对齐。
                Backstage = signal => _backstage.Record(signal, _timeline.Revision + 1),
                CarriesHostTools = card => string.Equals(card.Id, _narratorId, StringComparison.Ordinal),
                // 版本兜底改看世界状态版本：叙事里的"权威状态"是世界状态，不是资料版本（§11.2）。
                CurrentMaterialVersion = () => (int)_world.Version,
            },
            chatClient);

        foreach (var card in cards)
        {
            _registry.Add(card);
        }

        // 世界状态按 JSONL 里 kind=world 的行重建：重启后"此刻的时间 / 地点 / 在场"与剧情一致（§11.4-6）。
        ReplayWorldState();
    }

    public string StoryId => _options.StoryId;

    public long Revision => _timeline.Revision;


    public StoryTimeline Timeline => _timeline;

    public WorldStateStore World => _world;

    public IReadOnlyList<Participant> Participants => _registry.Participants;

    /// <summary>
    /// 开始这一场：<b>立即返回</b>，开场在后台由说书人写成正文。
    /// 与会议对称——会议是"用户给议题 → 主持人开局"，这里是"用户给设定 → 导演开场"，
    /// 都不做预览与重掷（§11.4-8）；也不让 HTTP 请求挂在整场戏上（§5.1 的长跑语义）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        MarkRunning("这场戏");

        PublishState();
        foreach (var warning in _startupWarnings)
        {
            _timeline.AppendWarning("sheet-invalid", warning, "sheet-loader");
            _bus.Publish("roleplay.warning", new { code = "sheet-invalid", message = warning });
        }

        _bus.Publish("roleplay.started", new
        {
            storyId = _options.StoryId,
            worldview = _options.World,
            situation = _options.Situation,
            cast = _strategy.PresentActors.Select(actor => new { id = actor.Id, name = actor.Name }).ToArray(),
        });

        StartBackground(RunOpeningAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 导演指令：旁白与元指令分流——括号里的元指令不进时间线，也不该被角色当成台词（§11.4-9）。
    /// </summary>
    public async Task SayAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        // 真机 A（2026-10-29）：`paused` 与 `awaitingInput` 是**两种不同的东西**——
        // 前者是"说书人建议收束 / 用户手动暂停"后的休眠，后者是"正等你拍板"。
        // 以前两者共用同一道等待门，于是 paused 时发一条指令会一直挂到用户点「继续」为止（实测 146 秒）。
        // 导演发指令的直觉就是"继续"，所以这里自动恢复，并推一条事件说明发生了什么。
        if (Status == MeetingStatus.Paused)
        {
            await ResumeAsync(cancellationToken).ConfigureAwait(false);

            _bus.Publish("roleplay.auto-resume", new
            {
                storyId = _options.StoryId,
                reason = "director-say",
                message = "会话此前处于暂停（建议收束 / 手动暂停）；收到导演指令后已自动继续。",
            });
        }

        var parsed = OocChannel.Parse(input);

        if (parsed.Directives.Count > 0)
        {
            _bus.Publish("roleplay.ooc", new
            {
                storyId = _options.StoryId,
                directives = parsed.Directives,
                text = parsed.Text,
            });
        }

        // r9：记下"这一轮导演说了什么"。dm_end 要拿它当依据（导演沉默时不允许收场）。
        _directorInputThisRound = string.IsNullOrWhiteSpace(parsed.Text) ? input : parsed.Text;

        // r11：每轮重置判定额度——一次导演 say = 一轮 = 一个 request_check。
        _checksThisRound = 0;
        _firstCheckThisRound = null;
        // 请求额度同理：一次导演 say = 一轮 = 一个请求（judgement 与 combat_action 合计）。
        _requestsThisRound = 0;
        _firstRequestThisRound = null;
        // 规则查询额度（§27）：一次导演 say = 一轮 = 最多 3 次查资料（不占请求额度）。
        _toolDispatcher.ResetRuleQuota();
        // 回忆额度（§2）：一轮一次——否则模型会拿它代替记忆。
        _toolDispatcher.ResetStoryQuota();

        // 意图去重（§11）：一轮 = 一次导演指令。下一轮对同一角色的再次点名算"首次"，不是合并。
        lock (Gate)
        {
            _intentAttempts.Clear();
            // §13-B：新一轮开始，重置"本轮内自己暂停过"的标记。
            _pausedByNarratorThisTurn = false;
        }

        await WaitWhileBlockedAsync(cancellationToken).ConfigureAwait(false);

        if (Status == MeetingStatus.Ended)
        {
            return;
        }

        BeginInteractionRound();

        // 每轮开头拉一次地图：本轮所有角色看到同一份快照，桥挂了也只是"这一轮没有地图段落"
        // （不抛、不阻塞——超时上限由 vtt.timeoutSeconds 兜住）。
        await RefreshVttAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<StoryLine> lines;

        try
        {
            lines = await RunSequentialAsync(BuildDirectorTask(parsed), cancellationToken).ConfigureAwait(false);

            // §13-B：本轮内说书人已经自己建议收束 → 这一轮到此为止：
            // 不再跑复盘（会再调一次模型、还可能再建议一次），也不再进交互收口
            //（那会挂在一个已暂停的会话上）。外部暂停时这个标记是 false，流程照旧。
            if (!_pausedByNarratorThisTurn)
            {
                // 一轮演完，说书人复盘：提议世界状态 / 点名 / 描写一拍 / 建议收束（§11.4-5）。
                // 它登记的意图在这里执行——有界（narratorBeatsPerTurn），防止一轮里把场面跑飞。
                var closing = await RunNarratorReviewAsync(parsed.Text, lines, cancellationToken).ConfigureAwait(false);

                if (closing.Count > 0)
                {
                    lines = [.. lines, .. closing];
                }

                // 这一幕收幕之前收口交互（§14.5.1）：该问就问，导演的答复影响下一轮怎么走。
                await EnforceRoundInteractionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 结束 / 进程退出：正常路径。
            return;
        }
        catch (Exception ex)
        {
            // provider 或工具出问题时，导演这一句没被处理——必须让用户看见，
            // 否则界面完全没反馈，用户只会以为是自己没按对（前端把 notice 显示在顶栏）。
            //
            // r15：这里曾经把**整回合**吞掉（正文与 turn 都不落），而且日志里只有一句 "say"。
            // 现在至少带上 turnId 与明确的 abandoned 标记，并把"这一轮作废"写进消息——
            // 真正的根因（工具参数异常）已在 dispatcher 层被拦下（见 ToolArguments / DispatchAsync）。
            _bus.Publish("roleplay.error", new
            {
                code = "say",
                message = ex.Message,
                turnId = ResolveNarrator()?.Session.CurrentTurnId,
                abandoned = true,
            });

            return;
        }

        // 一轮结束（含下面那条早退路径）：把攒着的思考落一条。
        // 后台观察的价值就在于"刷新/重启都还在"，留一半在内存里等于没做。
        _backstage.FlushThoughts(_timeline.Revision + 1);

        // 纯出戏指令且没人接话：只留痕，不推进剧情时间线。
        if (lines.Count == 0 && parsed.Text.Length == 0)
        {
            return;
        }

        var turn = _timeline.Append(parsed.Text, lines);

        // 长程记忆与时间线同源：每条回合都记进去，按角色视角检索时才知道"谁在不在场"（§11.4-7）。
        _memory.Record(turn);

        _bus.Publish("roleplay.turn", new
        {
            storyId = _options.StoryId,
            revision = turn.Revision,
            directorInput = turn.DirectorInput,
            lines = Project(turn.Lines),
        });

        PublishState();
    }

    // ——— INarratorActions：说书人的控场动作（§11.4-5） ———

    /// <summary>
    /// 提议一次世界状态变更：先问导演（用户），确认后才落盘。
    /// <para>
    /// <b>超时按"采用"收口</b>（用户拍板）：说书人提的变更本来就是从当前剧情推出来的一步，
    /// 导演不在场时宁可让剧情连续，也不要停在半截——想逐条看住的用户把 <c>interaction.timeoutSeconds</c> 调大。
    /// 交互模式整个关掉（<c>enabled = false</c>）时退回"全自动自主推进"：提议即生效。
    /// </para>
    /// </summary>
    public async Task<NarratorActionOutcome> ProposeWorldStateAsync(
        WorldStateMutation mutation,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        var question = new InteractionQuestion(
            WorldStateQuestionId,
            $"说书人提议改变世界状态（{reason}）：" + Environment.NewLine + DescribeChange(mutation),
            [
                new InteractionOption(ApplyOptionId, "同意，按提议生效", "按上面写着的时间 / 地点 / 在场变更来"),
                new InteractionOption(RejectOptionId, "不改，保持现状", "这次变更作废，世界状态保持原样"),
            ],
            DefaultOptionId: ApplyOptionId);

        InteractionOutcome? outcome;

        try
        {
            outcome = await RequestDecisionAsync(
                PendingDecisionKind.WorldState,
                [question],
                turnId: ResolveNarrator()?.Session.CurrentTurnId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 本场结束 / 进程退出：收口干净，不把取消带到工具循环之外。
            return new NarratorActionOutcome(false, "这次提议没有提交（本场已经结束）。");
        }

        if (outcome is null)
        {
            // 交互模式关掉：退回全自动推进，提议直接生效。
            return ApplyWorldState(mutation, reason, confirmed: false, timedOut: false);
        }

        var applied = outcome.Answers
            .SelectMany(answer => answer.Selected)
            .Contains(ApplyOptionId, StringComparer.Ordinal);

        if (!applied)
        {
            _bus.Publish("roleplay.world", new
            {
                storyId = _options.StoryId,
                revision = _timeline.Revision,
                version = _world.Version,
                applied = false,
                reason,
                timedOut = outcome.TimedOut,
            });

            return new NarratorActionOutcome(
                true,
                $"导演没有采纳这次变更（{reason}），世界状态保持原样；正文照旧，继续往下写。");
        }

        return ApplyWorldState(mutation, reason, confirmed: true, timedOut: outcome.TimedOut);
    }

    /// <summary>
    /// 提议揭示迷雾（M6b）：走与 <see cref="ProposeWorldStateAsync"/> 同一套确认通道，
    /// 但**默认选项是「不揭示」**——超时不能替 DM 揭开信息。
    /// </summary>
    public async Task<NarratorActionOutcome> ProposeRevealFogAsync(
        IReadOnlyList<Maps.GridCell>? cells,
        Maps.FogShape? shape,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_options.MapFogRevealer is null)
        {
            return new NarratorActionOutcome(false, "这张地图还不支持迷雾提议（没有可写的文档）。");
        }

        var resolved = ResolveFogCells(cells, shape);

        if (resolved.Count == 0)
        {
            return new NarratorActionOutcome(false, "这次提议没有落到任何格子上：给 cells 或 shape（圆形），并确认地图已标定。");
        }

        cells = resolved;

        var preview = string.Join("、", cells.Take(8).Select(cell => $"[{cell.X},{cell.Y}]"));
        var more = cells.Count > 8 ? "…" : string.Empty;

        var question = new InteractionQuestion(
            FogRevealQuestionId,
            $"说书人提议揭示 {cells.Count} 格迷雾（{reason}）：{preview}{more}",
            [
                new InteractionOption(ApplyOptionId, "揭示这些格子", "把它们标成「已探索」（半透明）；当前视野不变"),
                new InteractionOption(RejectOptionId, "不揭示", "保持全黑，玩家看不见"),
            ],
            // 关键：超时 = 不揭示。
            DefaultOptionId: RejectOptionId);

        InteractionOutcome? outcome;

        try
        {
            outcome = await RequestDecisionAsync(
                // P1：独立成 `fog`，前端据此区分展示（默认选项也与世界状态相反：超时 = 不揭示）。
                PendingDecisionKind.Fog,
                [question],
                turnId: ResolveNarrator()?.Session.CurrentTurnId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new NarratorActionOutcome(false, "这次迷雾提议没有提交（本场已经结束）。");
        }

        if (outcome is null)
        {
            // 交互模式关掉：**不做全自动揭示**——迷雾是信息闸门，没有导演就保持原样。
            return new NarratorActionOutcome(true, "交互模式已关闭：迷雾提议没有执行，玩家仍看不到这些格子。");
        }

        var applied = outcome.Answers
            .SelectMany(answer => answer.Selected)
            .Contains(ApplyOptionId, StringComparer.Ordinal);

        if (!applied)
        {
            return new NarratorActionOutcome(
                true,
                outcome.TimedOut
                    ? $"等导演确认超时，按约定不揭示：{cells.Count} 格保持全黑。"
                    : $"导演没有采纳这次揭示（{reason}）；{cells.Count} 格保持全黑，照旧往下写。");
        }

        var revealed = _options.MapFogRevealer(cells) ?? 0;

        return new NarratorActionOutcome(
            true,
            revealed > 0
                ? $"已揭示 {revealed} 格（其余本来就已探索）；当前视野不变。"
                : "这些格子本来就已探索，这次提议没有改变任何东西。");
    }

    /// <summary>
    /// 把"格子列表 或 圆形区域"统一成格索引（M6b）。圆形需要标定（dpi/offset），
    /// 没有标定就没有格。超出画布范围的格子在这里被裁掉，不交给校验。
    /// </summary>
    private IReadOnlyList<Maps.GridCell> ResolveFogCells(IReadOnlyList<Maps.GridCell>? cells, Maps.FogShape? shape)
    {
        if (cells is { Count: > 0 })
        {
            return cells;
        }

        if (shape is not { Type: "circle", Center: { Count: >= 2 } center } ||
            _options.MapDocumentProvider?.Invoke() is not { } document ||
            document.Grid is not { Dpi: > 0 } grid)
        {
            return [];
        }

        var radius = Math.Max(1, Math.Min(12, shape.RadiusCells));
        var centerX = (center[0] - grid.OffsetX) / grid.Dpi;
        var centerY = (center[1] - grid.OffsetY) / grid.Dpi;
        var span = (int)Math.Ceiling(radius);
        var width = document.Image?.Width ?? Maps.MapStore.BlankWidth;
        var height = document.Image?.Height ?? Maps.MapStore.BlankHeight;
        var columns = (int)Math.Ceiling(Math.Max(0, width - grid.OffsetX) / grid.Dpi);
        var rows = (int)Math.Ceiling(Math.Max(0, height - grid.OffsetY) / grid.Dpi);

        var result = new List<Maps.GridCell>();

        for (var dy = -span; dy <= span; dy += 1)
        {
            for (var dx = -span; dx <= span; dx += 1)
            {
                var gx = (int)Math.Floor(centerX) + dx;
                var gy = (int)Math.Floor(centerY) + dy;

                if (gx < 0 || gy < 0 || gx >= columns || gy >= rows)
                {
                    continue;
                }

                // 格心到圆心的距离 ≤ 半径（格）才算在内。
                var distance = Math.Sqrt(Math.Pow(gx + 0.5 - centerX, 2) + Math.Pow(gy + 0.5 - centerY, 2));

                if (distance <= radius)
                {
                    result.Add(new Maps.GridCell(gx, gy));
                }
            }
        }

        return result;
    }

    public async Task<NarratorActionOutcome> AskUserAsync(
        IReadOnlyList<InteractionQuestion> questions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var problems = AskUserSpec.Validate(questions, Interaction);

        if (problems.Count > 0)
        {
            return new NarratorActionOutcome(false, $"这次提问没有发出去：{string.Join("；", problems)}");
        }

        var normalized = AskUserSpec.Normalize(questions);
        InteractionOutcome? outcome;

        try
        {
            outcome = await RequestDecisionAsync(
                PendingDecisionKind.AskUser,
                normalized,
                turnId: ResolveNarrator()?.Session.CurrentTurnId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new NarratorActionOutcome(false, "这次提问没有发出去（本场已经结束）。");
        }

        if (outcome is null)
        {
            return new NarratorActionOutcome(true, "当前不允许提问（interaction.enabled = false），按你自己的判断继续推进。");
        }

        MarkAskedThisRound();
        return new NarratorActionOutcome(true, AskUserSpec.DescribeAnswers(outcome, normalized));
    }

    public NarratorActionOutcome NudgeCharacter(string characterId, string task)
    {
        var actor = _strategy.Actor(characterId);

        if (actor is null)
        {
            return new NarratorActionOutcome(false, $"没有这个角色：{characterId}");
        }

        if (IsNarrator(characterId))
        {
            return new NarratorActionOutcome(false, "说书人不需要给自己点名。");
        }

        // §12：只写不推进——拒绝且**不入队**（否则队列会永远堆着不执行的点名）。
        if (_options.NarratorBeatsPerTurn <= 0)
        {
            return new NarratorActionOutcome(false, NoAutoBeatMessage);
        }

        var outcome = EnqueueDeduplicated(new NarratorIntent("nudge", characterId, task), $"对 {actor.Name} 的点名");

        _bus.Publish("host.decision", new
        {
            action = "点名发言",
            participantId = _narratorId,
            target = characterId,
            reason = task,
        });

        return outcome;
    }

    public NarratorActionOutcome IdleBeat(string? hint)
    {
        // §12：只写不推进——描写一拍同样被拒（且不入队）。
        if (_options.NarratorBeatsPerTurn <= 0)
        {
            return new NarratorActionOutcome(false, NoAutoBeatMessage);
        }

        var outcome = EnqueueDeduplicated(new NarratorIntent("beat", null, hint ?? string.Empty), "描写一拍");

        _bus.Publish("host.decision", new
        {
            action = "自主描写",
            participantId = _narratorId,
            target = (string?)null,
            reason = string.IsNullOrWhiteSpace(hint) ? "导演没有新指令，说书人自己描写一拍。" : hint,
        });

        return outcome;
    }

    public NarratorActionOutcome DeclareSceneEnd(string reason)
    {
        // 战斗还在打时**机制拒绝**（§29）：会话被意外 Paused 后导演得手动 resume，
        // 比一次被拒调用贵得多，这类状态转换不能只靠模型自律。
        if (_encounter is { Phase: not EncounterPhase.Ended } running)
        {
            var blocked = $"战斗还在进行（{running.Id}，第 {running.Round} 轮），不能收束；先把这场战斗打完（end_combat）或交回导演。";
            _timeline.AppendWarning("combat-active", blocked, "declare_scene_end");
            _bus.Publish("roleplay.warning", new { code = "combat-active", message = blocked });
            return new NarratorActionOutcome(false, blocked);
        }

        // §13-A：幂等。已经 Paused（刚才已经建议过收束 / 导演手动暂停）时不再重复：
        // 不 pause、不推 host.decision、不入队——否则模型会在同一轮里连建议 4 次。
        if (Status == MeetingStatus.Paused)
        {
            return new NarratorActionOutcome(
                false,
                "已处于暂停状态（刚才已经建议过收束），无需再次建议；等导演决定下一步。");
        }

        Enqueue(new NarratorIntent("end", null, reason));

        // §13-B：标记"本轮内自己暂停"——本轮后续角色回合与复盘都不再执行，
        // 否则这一步 pause 之后，后面的步骤会卡在 WaitWhileBlockedAsync 上等它恢复
        //（真机实测：say 挂了 219 秒）。
        _pausedByNarratorThisTurn = true;

        // 只建议、不结束：把场面停在 Paused 等导演决定下一步（正在跑的 turn 不被打断）。
        _ = PauseAsync();

        _bus.Publish("host.decision", new
        {
            action = "建议收束",
            participantId = _narratorId,
            target = (string?)null,
            reason,
        });

        return new NarratorActionOutcome(true, "已建议收束：本场停在这里，等导演决定下一步。");
    }

    // ——— ITableActions：地图与骰子（《枭熊地图接入实现方案》V1 / V2） ———

    /// <summary>
    /// 说书人的主动查询：某个角色周围有谁、各在多远。
    /// 地图不可用时回一句"这一轮没有地图"，<b>不给任何估算</b>——编出来的距离比没有更糟。
    /// </summary>
    public Task<NarratorActionOutcome> LookAroundAsync(
        string? characterId,
        double? radiusFeet,
        CancellationToken cancellationToken)
    {
        if (!_vttOptions.Enabled || _vtt is null)
        {
            return Task.FromResult(new NarratorActionOutcome(false, "地图还没接入（vtt.enabled = false）。"));
        }

        var snapshot = _vttSnapshot;

        if (!snapshot.Available)
        {
            return Task.FromResult(new NarratorActionOutcome(
                false,
                $"地图不可用：{snapshot.UnavailableReason ?? "未知原因"}。这一轮按没有地图处理，不要自己估算距离。"));
        }

        var context = new VttContext(snapshot, _world.Current, _vttOptions, _vttTokenIds);

        // 预检查与锚点解析同源：模型的输入是角色名（"Ella"），内部按 id 存——
        // 各写一套判定就会出现"预检查说没有、真正解析却有"（真机上踩过）。
        if (!string.IsNullOrWhiteSpace(characterId) && !context.HasAnchor(characterId))
        {
            return Task.FromResult(new NarratorActionOutcome(false, $"地图上没有「{characterId}」这个棋子。"));
        }

        var text = context.LookAround(characterId, radiusFeet);

        return Task.FromResult(new NarratorActionOutcome(true, text));
    }

    /// <summary>
    /// 掷骰：结果写进 <c>story.jsonl</c> 的 <c>kind=roll</c> 记录，回执里带明细。
    /// <b>数值只能由工具产生</b>：说书人照着回执写正文，不能自己改。
    /// </summary>
    public Task<NarratorActionOutcome> RollDiceAsync(
        string who,
        string expression,
        string? mode,
        string reason,
        int? dc,
        CancellationToken cancellationToken)
    {
        var result = Dice.Roll(expression, mode, dc);

        if (!result.Ok)
        {
            // 表达式看不懂：回灌原因让它改，不落盘、不抛。
            return Task.FromResult(new NarratorActionOutcome(false, result.Describe()));
        }

        var record = _timeline.AppendRoll(who, result, reason);

        _bus.Publish("roleplay.roll", new
        {
            storyId = _options.StoryId,
            who = record.Who,
            expression = record.Expression,
            mode = record.Mode,
            attacks = record.Attacks.Select(attack => new
            {
                label = attack.Label,
                rolled = attack.Rolled,
                kept = attack.Kept,
                total = attack.Total,
                success = attack.Success,
            }).ToArray(),
            total = record.Total,
            dc = record.Dc,
            success = record.Success,
            reason = record.Reason,
        });

        return Task.FromResult(new NarratorActionOutcome(true, result.Describe()));
    }

    // ——— 战斗托管（C1：开战 / 换手 / 收场） ———

    /// <summary>当前战斗（C1）。未开战或已结束时仍保留最后一份快照，用 <c>Phase</c> 判断。</summary>
    public Encounter? CurrentEncounter => _encounter;

    /// <summary>
    /// 开战（C1）：掷先攻 → 排序 → 第一个回合立即开始。同时只允许一场；
    /// 拒绝要落 <c>combat-rejected</c>（拒绝也是正史，说书人照着它叙述原因）。
    /// </summary>
    public Task<NarratorActionOutcome> StartCombatAsync(
        IReadOnlyList<CombatantSetup> combatants,
        int? seed,
        string? encounterId,
        CancellationToken cancellationToken)
    {
        if (_encounter is { Phase: not EncounterPhase.Ended })
        {
            return Task.FromResult(RejectCombat(
                _encounter.Id,
                EncounterEngine.CodeAlreadyActive,
                $"已经有一场战斗在进行（{_encounter.Id}，第 {_encounter.Round} 轮）；先 end_combat 结束它。"));
        }

        if (combatants is null || combatants.Count == 0)
        {
            return Task.FromResult(RejectCombat(string.Empty, EncounterEngine.CodeInvalidSetup, "start_combat 至少要给一个参战者。"));
        }

        var id = string.IsNullOrWhiteSpace(encounterId) ? $"enc-{_encounterSequence + 1}" : encounterId.Trim();
        var actualSeed = seed ?? Random.Shared.Next(1, int.MaxValue);
        // 第 2 层来源（§26.2）：start_combat 的 per-combatant sheet 覆盖角色卡旁路表。
        if (_sheets is not null)
        {
            foreach (var setup in combatants)
            {
                if (setup.Sheet is not { } inline)
                {
                    continue;
                }

                var errors = MeetingRoom.Core.Rules.SheetValidator.Validate(inline);

                if (errors.Count > 0)
                {
                    return Task.FromResult(RejectCombat(
                        id,
                        "invalid-sheet",
                        $"「{setup.Id}」的数值表不完整：{string.Join("；", errors)}——先补齐再开战。"));
                }

                _sheets.Put(inline with { CharacterId = string.IsNullOrWhiteSpace(inline.CharacterId) ? setup.Id : inline.CharacterId });
            }
        }

        // r10：没有角色卡的敌人靠 inline hp 开战——先按它们建一张最小表，
        // 之后的伤害 / 胜负校验才有依据（否则模型根本没有满足守卫的手段）。
        if (_sheets is not null)
        {
            foreach (var setup in combatants)
            {
                if (_sheets.Get(setup.Id) is not null || setup.Hp is not { } inlineHp)
                {
                    continue;
                }

                var created = _sheets.CreateMinimal(
                    setup.Id,
                    inlineHp,
                    setup.MaxHp ?? inlineHp,
                    setup.Ac ?? 10);

                _sheets.Put(created);
            }
        }

        // r9：开战必须带 HP——否则整场战斗都在"胜负未校验"的盲区里跑。
        // 来源可以是 roles/<id>.sheet.json、本次请求的 inline Sheet，或此前 dm_override 建的最小表。
        if (_sheets is null)
        {
            return Task.FromResult(RejectCombat(
                id,
                "combatants-need-hp",
                "没有数值表（SheetStore），开不了战：在 start_combat 里给每个参战者的 hp，或先建表。"));
        }

        var missingHp = combatants
            .Where(setup => _sheets.Get(setup.Id)?.CurrentHp is null)
            .Select(setup => setup.Name ?? setup.Id)
            .ToArray();

        if (missingHp.Length > 0)
        {
            return Task.FromResult(RejectCombat(
                id,
                "combatants-need-hp",
                $"这些参战者既没有角色卡也没给 hp：{string.Join("、", missingHp)}。"
                + "在 combatants 里给它们写 hp / maxHp / ac"
                + "（例：{\"id\":\"goblin-1\",\"side\":\"foe\",\"hp\":7,\"maxHp\":7,\"ac\":15}）。"));
        }

        var result = EncounterEngine.Start(id, combatants, actualSeed);

        if (!result.Ok || result.Encounter is null)
        {
            return Task.FromResult(RejectCombat(id, result.Code ?? EncounterEngine.CodeInvalidSetup, result.Message ?? "开战失败。"));
        }

        _encounter = result.Encounter;
        _encounterSequence = Math.Max(_encounterSequence, EncounterIndex(id));

        _timeline.AppendCombatStart(new CombatStartEvent(
            _encounter.Id,
            _encounter.Round,
            actualSeed,
            EncounterEngine.OrderOf(_encounter),
            _encounter.Combatants));

        PublishCombatTurn();

        return Task.FromResult(new NarratorActionOutcome(
            true,
            $"战斗开始（{_encounter.Id}，seed {actualSeed}）：{DescribeOrder(_encounter)}。现在轮到 {ActorName(_encounter)}。"));
    }

    /// <summary>结束当前回合，把场面交给下一位（C1）；索引回到 0 时轮数 +1。</summary>
    public Task<NarratorActionOutcome> EndTurnAsync(CancellationToken cancellationToken)
    {
        if (_encounter is null)
        {
            return Task.FromResult(RejectCombat(string.Empty, EncounterEngine.CodeNotActive, "还没有战斗，先 start_combat。"));
        }

        var result = EncounterEngine.Advance(_encounter);

        if (!result.Ok || result.Encounter is null)
        {
            return Task.FromResult(RejectCombat(_encounter.Id, result.Code ?? EncounterEngine.CodeNotActive, result.Message ?? "换手失败。"));
        }

        _encounter = result.Encounter;
        PublishCombatTurn();

        var turnText = $"第 {_encounter.Round} 轮，轮到 {ActorName(_encounter)}（{_encounter.CurrentIndex + 1}/{_encounter.Order.Count}）。";

        // 回合开始：状态到期已在 BeginTurn 里做，这里紧接着处理死亡豁免（顺序固定）。
        if (ResolveDeathSaveAtTurnStart() is { } deathSave)
        {
            turnText += Environment.NewLine + deathSave;
        }

        return Task.FromResult(new NarratorActionOutcome(true, turnText));
    }

    /// <summary>
    /// DM 覆盖（D3）：九个白名单字段；<c>from</c> 由引擎填、调用方只给 <c>to</c>。
    /// <para>
    /// 成功落 <c>kind=combat-override</c>（重放直接应用、不重新校验）；<c>field=dc</c> 还会改指定
    /// <c>checkId</c> 的记录。改 <c>hp</c> 走 D1 的迁移规则；改 <c>status</c> 不动 HP 也不动死亡豁免。
    /// </para>
    /// </summary>
    public Task<NarratorActionOutcome> DmOverrideAsync(
        DmOverrideRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = DmOverrideEngine.Apply(_encounter, _sheets, request);

        if (!result.Ok)
        {
            return Task.FromResult(RejectCombat(
                _encounter?.Id ?? string.Empty,
                result.Code ?? "override-rejected",
                result.Message ?? "这次覆盖没有生效。"));
        }

        if (result.Encounter is not null)
        {
            _encounter = result.Encounter;
        }

        _timeline.AppendCombatOverride(
            _encounter?.Id ?? string.Empty,
            request.Target,
            result.Field,
            result.From,
            result.To,
            request.Reason,
            result.CreatedSheet);

        if (string.Equals(result.Field, "dc", StringComparison.OrdinalIgnoreCase)
            && request.CheckId is { Length: > 0 } checkId
            && int.TryParse(result.To, out var dc))
        {
            _timeline.ApplyCheckDcOverride(checkId, dc);
        }

        _bus.Publish("roleplay.combat", new
        {
            action = "override",
            target = request.Target,
            field = result.Field,
            from = result.From,
            to = result.To,
            reason = request.Reason,
        });

        return Task.FromResult(new NarratorActionOutcome(true, result.Message ?? "覆盖已生效。"));
    }

    /// <summary>结束整场战斗（C1）：outcome 取 victory / defeat / retreat / dm_end。</summary>
    public Task<NarratorActionOutcome> EndCombatAsync(string outcome, CancellationToken cancellationToken)
    {
        if (_encounter is null)
        {
            return Task.FromResult(RejectCombat(string.Empty, EncounterEngine.CodeNotActive, "还没有战斗。"));
        }

        var parsed = ParseOutcome(outcome);

        if (parsed is null)
        {
            return Task.FromResult(RejectCombat(
                _encounter.Id,
                "invalid-outcome",
                $"outcome 只能是 victory / defeat / retreat / dm_end（当前「{outcome}」）。"));
        }

        // 胜负校验（r8）：`EncounterEngine.End` 只判断"是否已结束"，传什么就落什么——
        // 所以"敌人还站着就报 victory"能通过。这里补上事实校验。
        // `retreat` / `dm_end` 不校验：它们是导演侧的决定，不是战果（用户明确选的轻方案）。
        // r9：`dm_end` 是"导演叫停"，必须能在本轮导演输入里找到依据。
        // 这是弱守卫——只能拦住"导演完全没说话"，拦不住"导演说了别的话"（见 §40 已知边界）。
        if (parsed.Value == CombatOutcome.DmEnded && string.IsNullOrWhiteSpace(_directorInputThisRound))
        {
            return Task.FromResult(RejectCombat(
                _encounter.Id,
                "dm-end-needs-director",
                "导演这一轮没有发话，收场等他开口。用 next_turn 继续推进。"));
        }

        if (parsed.Value is CombatOutcome.Victory or CombatOutcome.Defeat)
        {
            var guard = CheckOutcomeEarned(_encounter, parsed.Value);

            if (!guard.Ok)
            {
                return Task.FromResult(RejectCombat(_encounter.Id, guard.Code!, guard.Message!));
            }

            if (guard.Unchecked && guard.Message is { } note)
            {
                // 没有数值表时不卡死，但必须留下痕迹——否则"未校验"会静默过去。
                _timeline.AppendWarning("combat-outcome-unchecked", note, "end_combat");
                _bus.Publish("roleplay.warning", new { code = "combat-outcome-unchecked", message = note });
            }
        }

        var rounds = _encounter.Round;
        var result = EncounterEngine.End(_encounter, parsed.Value);

        if (!result.Ok || result.Encounter is null)
        {
            return Task.FromResult(RejectCombat(_encounter.Id, result.Code ?? EncounterEngine.CodeAlreadyEnded, result.Message ?? "收场失败。"));
        }

        _encounter = result.Encounter;
        var wire = WireOutcome(parsed.Value);

        // 收场成功：本轮导演输入已用掉，清空——避免"上一轮说过话"被下一轮借用。
        _directorInputThisRound = null;

        _timeline.AppendCombatEnd(new CombatEndEvent(_encounter.Id, wire, rounds));
        _bus.Publish("roleplay.combat", new { encounterId = _encounter.Id, ended = wire, rounds });

        return Task.FromResult(new NarratorActionOutcome(true, $"战斗结束（{_encounter.Id}）：{wire}，共 {rounds} 轮。"));
    }

    /// <summary>
    /// 战斗内的一次攻击 / 施法（C2）：命中与伤害由引擎算，事件落 <c>kind=attack/damage</c>；
    /// <b>不扣 HP</b>（C2 不发明 HP 存储）。
    /// </summary>
    public Task<NarratorActionOutcome> SubmitIntentAsync(
        CombatActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_rules is null)
        {
            return Task.FromResult(new NarratorActionOutcome(false, "规则包未加载（rules-data.json）：这次行动没有执行。"));
        }

        if (_encounter is not { Phase: EncounterPhase.Active } encounter)
        {
            return Task.FromResult(RejectCombat(_encounter?.Id ?? string.Empty, EncounterEngine.CodeNotActive, "现在不在战斗中，先用 start_combat 开战。"));
        }

        if (encounter.Find(request.ActorId) is null)
        {
            return Task.FromResult(RejectCombat(encounter.Id, "unknown-combatant", $"战斗名单里没有「{request.ActorId}」。"));
        }

        // 先消耗行动经济（C3b）：失败直接拒绝，不掷骰。
        var before = _encounter;
        var economy = EncounterEngine.SubmitAction(
            encounter,
            request.ActorId,
            request.Kind,
            request.UseBonusAction,
            request.DistanceFeet,
            request.HelpTargetId,
            request.ProvokesOpportunityFrom,
            request.ReadyTrigger);

        if (!economy.Ok || economy.Encounter is null)
        {
            return Task.FromResult(RejectCombat(
                encounter.Id,
                economy.Code ?? "rejected",
                economy.Message ?? "这次行动没有成立。"));
        }

        _encounter = economy.Encounter;
        encounter = _encounter;

        // 非攻击动作：消耗完就落一条 combat-action，不掷骰。
        if (request.Kind is not (CombatActionKind.Attack or CombatActionKind.CastSpell))
        {
            var action = WireAction(request.Kind);

            _timeline.AppendCombatAction(
                encounter.Id,
                request.ActorId,
                action,
                request.HelpTargetId ?? request.TargetId,
                request.DistanceFeet,
                request.Reason);

            _bus.Publish("roleplay.combat", new
            {
                encounterId = encounter.Id,
                action,
                actor = request.ActorId,
                target = request.HelpTargetId ?? request.TargetId,
                distanceFeet = request.DistanceFeet,
                remainingMovementFeet = RemainingMovement(encounter),
                reason = request.Reason,
            });

            var actionText = DescribeAction(encounter, request);

            foreach (var opportunity in economy.Opportunities)
            {
                _timeline.AppendCombatAction(
                    encounter.Id,
                    opportunity.AttackerId,
                    "opportunity-attack",
                    opportunity.TargetId,
                    null,
                    "离开触及范围触发");

                _bus.Publish("roleplay.combat", new
                {
                    encounterId = encounter.Id,
                    action = "opportunity-attack",
                    actor = opportunity.AttackerId,
                    target = opportunity.TargetId,
                });

                actionText += Environment.NewLine
                    + $"（{opportunity.AttackerId} 可以对他做一次借机攻击——reaction 已消耗。）";
            }

            return Task.FromResult(new NarratorActionOutcome(true, actionText));
        }

        // 施法校验（D2）：法术存在 → 环级够 → 槽位可用；顺序固定，失败回滚行动经济、不掷骰。
        int? spendSlotLevel = null;
        string? slotWarning = null;

        if (request.Kind == CombatActionKind.CastSpell)
        {
            var (spell, spellCode, spellMessage) = CombatEngine.ValidateSpell(_rules, request);

            if (spell is null)
            {
                _encounter = before;
                return Task.FromResult(RejectCombat(encounter.Id, spellCode ?? "unknown-spell", spellMessage ?? "这个法术不能施放。"));
            }

            if (spell.Level > 0)
            {
                var castAtLevel = request.CastAtLevel ?? spell.Level;
                var (slotOk, slotCode, slotMessage) = _sheets is null
                    ? (true, string.Empty, string.Empty)
                    : _sheets.CanSpendSlot(request.ActorId, castAtLevel);

                if (!slotOk)
                {
                    _encounter = before;
                    return Task.FromResult(RejectCombat(encounter.Id, slotCode, slotMessage));
                }

                spendSlotLevel = castAtLevel;
            }
        }

        // 目标在 Dodge → 攻击它有劣势。
        var dodging = encounter.Find(request.TargetId)?.Dodging == true;

        // 距离：调用方给了就用；没给但有地图时用 VttContext 补（一个入口，不搞两套）。
        var distance = request.DistanceFeet;

        if (distance is null && _vttSnapshot.Available)
        {
            var context = new VttContext(_vttSnapshot, _world.Current, _vttOptions, _vttTokenIds);

            if (context.TryDistanceFeet(request.ActorId, request.TargetId, out var measured))
            {
                distance = measured;
            }
        }

        // Help：被帮助者在下一次攻击时获得优势，用一次就消失（C4）。
        var (withHelp, helpConsumed) = EncounterEngine.ConsumeHelp(encounter, request.ActorId);

        if (helpConsumed)
        {
            _encounter = withHelp;
            encounter = withHelp;
        }

        IReadOnlyList<AdvantageMode>? modes = helpConsumed ? [AdvantageMode.Advantage] : request.Modes;

        var result = CombatEngine.Resolve(
            _rules,
            request with { TargetIsDodging = dodging, DistanceFeet = distance, Modes = modes });

        if (!result.Ok)
        {
            // 参数问题不消耗行动经济：回到提交前的状态。
            _encounter = before;
            return Task.FromResult(RejectCombat(encounter.Id, result.Code ?? "rejected", result.Message ?? "这次行动没有成立。"));
        }

        // 施放成功才扣槽（前面的分支已经挡掉 no-slot）。
        // 这里再失败说明是竞态或 Sheet 被中途改写——法术已经结算了，绝不能静默：
        // 记一条警告事件并在回执里点名，否则就成了"法术放出来了但槽没扣"。
        if (spendSlotLevel is { } slotLevel && _sheets is not null)
        {
            var (slotOk, slotCode, slotMessage, _) = _sheets.SpendSlot(request.ActorId, slotLevel);

            if (!slotOk)
            {
                _timeline.AppendWarning(slotCode, slotMessage, "combat_action");
                _bus.Publish("roleplay.warning", new
                {
                    code = slotCode,
                    message = slotMessage,
                    actor = request.ActorId,
                    field = "spell-slot",
                    level = slotLevel,
                });

                slotWarning = $"（注意：{slotLevel} 环法术位没能扣掉——{slotMessage}已于日志记录。）";
            }
        }

        if (result.Attack is { } attack)
        {
            _timeline.AppendAttack(
                request.ActorId,
                result.WeaponId ?? result.SpellId ?? string.Empty,
                request.TargetArmorClass,
                attack);
        }

        if (result.Save is { } save)
        {
            _timeline.AppendCheck(new StoryCheckRecord(
                request.ActorId,
                "save",
                request.Reason ?? string.Empty,
                request.SaveDc ?? 0,
                save,
                DateTimeOffset.UtcNow)
            {
                KindType = "save",
                Visibility = "public",
            });
        }

        DamageApplication? application = null;
        string? concentrationNote = null;

        if (result.Damage is { } damage)
        {
            if (result.DamageApplied > 0 && _sheets is not null)
            {
                application = _sheets.ApplyDamage(request.TargetId, result.DamageApplied);
            }

            _timeline.AppendDamage(request.ActorId, request.TargetId, damage, application);
        }

        // 专注检定（C4）：受伤时 CON 豁免，DC = max(10, 伤害 / 2)；失败则专注中断。
        if (application is { Ok: true } hit && result.DamageApplied > 0 && hit.Sheet is { } damaged)
        {
            var concentration = ConcentrationCheck.Check(damaged, result.DamageApplied);

            if (concentration.Required)
            {
                if (!concentration.Maintained)
                {
                    _sheets?.Put(concentration.Sheet);
                }

                _timeline.AppendCheck(new StoryCheckRecord(
                    request.TargetId,
                    "save",
                    "专注检定",
                    concentration.Dc,
                    concentration.Roll,
                    DateTimeOffset.UtcNow)
                {
                    KindType = "save",
                    Ability = "con",
                    DcSource = "rules",
                    Visibility = "public",
                });

                _bus.Publish("roleplay.check", new
                {
                    actor = request.TargetId,
                    kindType = "save",
                    ability = "con",
                    dc = concentration.Dc,
                    dcSource = "rules",
                    total = concentration.Roll?.Total,
                    success = concentration.Maintained,
                    focus = true,
                });

                concentrationNote = concentration.Maintained
                    ? $"专注检定 DC {concentration.Dc}：维持住了（{concentration.Roll?.Total}）。"
                    : $"专注检定 DC {concentration.Dc}：失败（{concentration.Roll?.Total}），专注被打断。";
            }
        }

        _bus.Publish("roleplay.combat", new
        {
            encounterId = encounter.Id,
            action = request.Kind.ToString().ToLowerInvariant(),
            actor = request.ActorId,
            target = request.TargetId,
            distanceFeet = distance,
            weaponId = result.WeaponId,
            spellId = result.SpellId,
            attack = result.Attack is { } attackRoll
                ? new { natural = attackRoll.Natural, total = attackRoll.Total, success = attackRoll.Success, crit = attackRoll.Crit }
                : null,
            save = result.Save is { } saveRoll
                ? new { natural = saveRoll.Natural, total = saveRoll.Total, success = saveRoll.Success }
                : null,
            damage = result.Damage is { } damageResult
                ? new
                {
                    raw = damageResult.RawTotal,
                    applied = result.DamageApplied,
                    crit = damageResult.Crit,
                    hpBefore = application?.HpBefore,
                    hpAfter = application?.HpAfter,
                    tempHpAfter = application?.TempHpAfter,
                    status = application?.Status is { } appliedStatus ? appliedStatus.ToString().ToLowerInvariant() : null,
                    hpNote = application is null && result.DamageApplied > 0 ? "目标没有数值表，伤害未扣" : null,
                }
                : null,
            masteries = result.Masteries.Select(mastery => new
            {
                name = mastery.Requested,
                modelled = mastery.Modelled,
            }).ToArray(),
        });

        var text = result.Describe();

        if (distance is { } measuredDistance)
        {
            text += Environment.NewLine + $"距离 {measuredDistance:0.#} 尺。";
        }

        if (slotWarning is { } warning)
        {
            text += Environment.NewLine + warning;
        }

        if (concentrationNote is { } focus)
        {
            text += Environment.NewLine + focus;
        }

        if (result.Damage is not null)
        {
            if (application is { Ok: true } applied)
            {
                text += Environment.NewLine + DescribeHp(applied);
            }
            else if (application is { Ok: false })
            {
                text += Environment.NewLine + "（伤害没有扣：" + application.Message + "）";
            }
            else if (result.DamageApplied > 0)
            {
                text += Environment.NewLine + "（没有数值表，伤害未扣；DM 自行记 HP。）";
            }
        }

        return Task.FromResult(new NarratorActionOutcome(true, text));
    }

    private static string WireAction(CombatActionKind kind) => kind switch
    {
        CombatActionKind.CastSpell => "cast_spell",
        CombatActionKind.Attack => "attack",
        CombatActionKind.Move => "move",
        CombatActionKind.Dash => "dash",
        CombatActionKind.Dodge => "dodge",
        CombatActionKind.Disengage => "disengage",
        CombatActionKind.Hide => "hide",
        CombatActionKind.Help => "help",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static int RemainingMovement(Encounter encounter) =>
        encounter.Turn is { } turn ? Math.Max(0, turn.MovementMaxFeet - turn.MovementUsedFeet) : 0;

    private static string DescribeAction(Encounter encounter, CombatActionRequest request) => request.Kind switch
    {
        CombatActionKind.Move => $"移动 {request.DistanceFeet:0.#} 尺（还剩 {RemainingMovement(encounter)} 尺）。",
        CombatActionKind.Dash => $"Dash：本回合移动上限提高了（还剩 {RemainingMovement(encounter)} 尺）。",
        CombatActionKind.Dodge => "已进入 Dodge：直到你的下一回合开始，攻击你的人都处于劣势。",
        CombatActionKind.Disengage => "已 Disengage：本回合移动不再引发借机攻击。",
        CombatActionKind.Hide => "已 Hide（DM 可随时撤销）。",
        CombatActionKind.Help => $"已 Help：{request.HelpTargetId} 的下一次检定有优势。",
        _ => "已记录这次行动。",
    };

    /// <summary>
    /// 回合开始的死亡豁免（D1）：只有当前行动者处于 <see cref="VitalStatus.Dying"/> 才掷。
    /// <para>
    /// 顺序固定：<c>BeginTurn</c> 先做状态到期递减，再走这里掷豁免——避免"到期的状态还影响豁免"。
    /// 结果落 <c>kind=check</c>（<c>kindType=death-save</c>）与 <c>kind=combat-status</c>。
    /// </para>
    /// </summary>
    private string? ResolveDeathSaveAtTurnStart()
    {
        if (_encounter is not { Turn: { } turn } encounter)
        {
            return null;
        }

        if (_sheets?.Get(turn.ActorId) is not { } sheet || sheet.Status != VitalStatus.Dying)
        {
            return null;
        }

        var result = DeathSaveEngine.Roll(sheet);
        _sheets.Put(result.Sheet);

        _timeline.AppendCheck(new StoryCheckRecord(
            turn.ActorId,
            "death-save",
            "回合开始死亡豁免",
            DeathSaveEngine.SavesToResolve,
            result.Roll,
            DateTimeOffset.UtcNow)
        {
            KindType = "death-save",
            DcSource = "rules",
            Visibility = "public",
            Successes = result.Saves.Successes,
            Failures = result.Saves.Failures,
            StatusAfter = result.Status,
        });

        _timeline.AppendCombatStatus(
            encounter.Id,
            turn.ActorId,
            sheet.Status.ToString().ToLowerInvariant(),
            result.Status.ToString().ToLowerInvariant(),
            "death-saves",
            encounter.Round);

        _bus.Publish("roleplay.check", new
        {
            actor = turn.ActorId,
            kindType = "death-save",
            natural = result.Natural,
            successes = result.Saves.Successes,
            failures = result.Saves.Failures,
            statusAfter = result.Status.ToString().ToLowerInvariant(),
        });

        return result.Message;
    }

    private static string DescribeHp(DamageApplication application)
    {
        var text = $"目标 HP {application.HpBefore} → {application.HpAfter}";

        if (application.AbsorbedByTempHp > 0)
        {
            text += $"（临时 HP 吸收 {application.AbsorbedByTempHp}）";
        }

        var failures = application.Saves?.Failures ?? 0;

        return application.Status switch
        {
            VitalStatus.Dying => text + $"（昏迷，濒死：死亡豁免失败 {failures}/3）",
            VitalStatus.Stable => text + "（昏迷，已稳定：不再掷死亡豁免）",
            VitalStatus.Dead => text + "（死亡）",
            _ => text,
        };
    }

    /// <summary>
    /// 叙事检定（C2）：四种形态 + 暗骰 + Stakes + 防刷。
    /// <para>
    /// <b>不改世界状态、不进回合状态机、不消耗行动经济</b>——它只产出"结果 + 后果"。
    /// 暗骰的两个面在引擎里就分开了：<c>roleplay.check</c> 事件带 DM 面与角色面两份，
    /// 数值只进 DM 面与 <c>kind=check</c> 记录。
    /// </para>
    /// </summary>
    public Task<NarratorActionOutcome> RequestCheckAsync(
        CheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_rules is null)
        {
            return Task.FromResult(new NarratorActionOutcome(false, "规则包未加载（rules-data.json）：这次检定没有执行。"));
        }

        // r11：同一轮只允许一个判定（提示词那条保留，但降为辅助——实测它管不住）。
        // 计数在**入口**：被引擎拒掉的那次也算用掉，否则模型可以靠"换参数试错"绕开。
        _checksThisRound += 1;

        if (_checksThisRound > 1)
        {
            var used = _firstCheckThisRound ?? "上一个";
            return Task.FromResult(new NarratorActionOutcome(
                false,
                $"这一轮已经提过一个判定了（{used}），等这轮结算完、剧情往前走一步再提下一个。"));
        }

        var checkId = $"chk-{++_checkSequence}";
        // F7-①：请求没给加值时从数值表回落——表在这里注入，引擎保持纯函数。
        var result = NarrativeCheckEngine.Resolve(
            _rules,
            request with { Sheet = _sheets?.Get(request.ActorId) },
            _checkHistory,
            checkId);

        // 记下这个名字：第二个判定被拒时要能说清"already used 是哪个"。
        _firstCheckThisRound = result.Skill ?? result.Ability ?? request.Skill ?? request.Ability ?? request.ActorId;

        // F7-②：被拒的检定（防刷 / 参数非法）**不进检定流**——不落 check 记录、不 publish，
        // 只在 warning 里留一条可查的痕迹。否则连调 11 次会在页面上堆 7 张空卡。
        if (!result.Ok)
        {
            var rejected = result.Message ?? "这次检定没有执行。";
            _timeline.AppendWarning(result.Code ?? "check-rejected", rejected, "request_check");

            return Task.FromResult(new NarratorActionOutcome(false, rejected));
        }

        _timeline.AppendCheck(new StoryCheckRecord(
            result.ActorId,
            "check",
            result.Reason ?? string.Empty,
            result.Dc,
            result.Roll,
            DateTimeOffset.UtcNow)
        {
            CheckId = result.CheckId,
            KindType = result.Kind.ToString().ToLowerInvariant(),
            Skill = result.Skill,
            Ability = result.Ability,
            DcSource = result.DcSource,
            Visibility = result.Visibility.ToString().ToLowerInvariant(),
            Stakes = result.Stakes is { } stakes
                ? new[] { stakes.OnSuccess, stakes.OnFailure, stakes.OnPartial }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray()
                : [],
            Applied = result.Applied,
            PassiveValue = result.PassiveValue,
            ModifierSource = result.ModifierSource,
        });

        _bus.Publish("roleplay.check", new
        {
            checkId = result.CheckId,
            actor = result.ActorId,
            kindType = result.Kind.ToString().ToLowerInvariant(),
            skill = result.Skill,
            ability = result.Ability,
            dc = result.Dc,
            dcSource = result.DcSource,
            dcAdjusted = result.DcAdjusted,
            // 枚举要显式转字符串（否则事件里是 0/1，前端没法判断暗骰）。
            visibility = result.Visibility.ToString().ToLowerInvariant(),
            natural = result.Roll?.Natural,
            total = result.Roll?.Total ?? result.PassiveValue,
            success = result.Success,
            applied = result.Applied,
            stakes = result.Stakes,
            modifierSource = result.ModifierSource,
            dmFacing = result.DmFacingSummary(),
            characterFacing = result.CharacterFacingSummary(),
        });

        return Task.FromResult(new NarratorActionOutcome(
            result.Ok,
            result.Ok ? result.DmFacingSummary() : result.Message ?? "这次检定没有执行。"));
    }

    /// <summary>拒绝：落盘 + 广播，回执写成能直接叙述的一句话。</summary>
    private NarratorActionOutcome RejectCombat(string encounterId, string code, string message)
    {
        _timeline.AppendCombatRejected(new CombatRejectedEvent(encounterId, code, message));
        _bus.Publish("roleplay.combat", new { encounterId, rejected = new { code, message } });
        return new NarratorActionOutcome(false, message);
    }

    /// <summary>回合开始：落 <c>combat-turn</c> 并广播当前行动者与剩余预算。</summary>
    private void PublishCombatTurn()
    {
        if (_encounter?.Turn is not { } turn)
        {
            return;
        }

        _timeline.AppendCombatTurn(new CombatTurnEvent(
            _encounter.Id,
            _encounter.Round,
            turn.ActorId,
            CombatBudget.Of(turn)));

        _bus.Publish("roleplay.combat", new
        {
            encounterId = _encounter.Id,
            round = _encounter.Round,
            phase = _encounter.Phase.ToString().ToLowerInvariant(),
            actor = turn.ActorId,
            budget = new
            {
                action = !turn.ActionUsed,
                bonus = !turn.BonusUsed,
                reaction = !turn.ReactionUsed,
                movementFeet = Math.Max(0, turn.MovementMaxFeet - turn.MovementUsedFeet),
            },
        });
    }

    private static string ActorName(Encounter encounter) =>
        encounter.Current is { } actor
            ? string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Id : actor.DisplayName
            : "（无人）";

    private static string DescribeOrder(Encounter encounter) =>
        string.Join(" → ", EncounterEngine.OrderOf(encounter).Select(entry => $"{entry.Actor}({entry.Initiative})"));

    /// <summary>从 <c>enc-N</c> 里取 N（不是这个形状就返回 0，不影响开下一场）。</summary>
    private static int EncounterIndex(string id) =>
        id.StartsWith("enc-", StringComparison.Ordinal) && int.TryParse(id[4..], out var value) ? value : 0;

    /// <summary>
    /// 胜负是否"挣到了"（r8）：victory 要求敌人全部 HP ≤ 0，defeat 要求自己这边全部 HP ≤ 0。
    /// <para>
    /// HP 的唯一真相在 <see cref="SheetStore"/>（与 <c>ProjectCombat()</c> 同一个取法，不在 <see cref="Encounter"/> 上加字段）。
    /// 没有数值表 / 某个参战者查不到 HP 时**不卡死**：放行并返回 <c>Unchecked = true</c>，
    /// 由调用方落一条 warning——probe 与未配表的场景非常常见，硬拒会让它们全跑不起来。
    /// </para>
    /// </summary>
    private (bool Ok, string? Code, string? Message, bool Unchecked) CheckOutcomeEarned(
        Encounter encounter,
        CombatOutcome outcome)
    {
        if (_sheets is null)
        {
            return (true, null, "没有数值表（SheetStore 未接入），胜负未校验。", true);
        }

        var side = outcome == CombatOutcome.Victory ? CombatSide.Foe : CombatSide.Party;
        var standing = new List<string>();
        var unknown = new List<string>();

        foreach (var combatant in encounter.Combatants.Where(candidate => candidate.Side == side))
        {
            var sheet = _sheets.Get(combatant.Id);

            if (sheet is null)
            {
                unknown.Add(combatant.DisplayName);
                continue;
            }

            if (sheet.CurrentHp > 0)
            {
                standing.Add($"{combatant.DisplayName} HP {sheet.CurrentHp}");
            }
        }

        if (unknown.Count > 0)
        {
            return (true, null, $"这些参战者没有数值表，胜负未校验：{string.Join("、", unknown)}。", true);
        }

        if (standing.Count == 0)
        {
            return (true, null, null, false);
        }

        var who = string.Join("、", standing);
        var message = outcome == CombatOutcome.Victory
            ? $"场上还有 {standing.Count} 个敌人能行动（{who}），不算胜利。用 next_turn 继续打，或用 dm_end 并引用导演原话收场。"
            : $"自己这边还有 {standing.Count} 个人能行动（{who}），不算失败。用 next_turn 继续打，或用 dm_end 并引用导演原话收场。";

        return (false, "outcome-not-earned", message, false);
    }

    private static CombatOutcome? ParseOutcome(string? outcome) =>
        outcome?.Trim().ToLowerInvariant() switch
        {
            "victory" => CombatOutcome.Victory,
            "defeat" => CombatOutcome.Defeat,
            "retreat" => CombatOutcome.Retreat,
            "dm_end" or "dm-ended" or "dm" => CombatOutcome.DmEnded,
            _ => null,
        };

    private static string WireOutcome(CombatOutcome outcome) => outcome switch
    {
        CombatOutcome.Victory => "victory",
        CombatOutcome.Defeat => "defeat",
        CombatOutcome.Retreat => "retreat",
        _ => "dm_end",
    };

    /// <summary>只读端点用：最近一次快照的对外形状（没有地图时 <c>available = false</c> 与原因）。</summary>
    public object VttStatus() =>
        VttStatusView.Project(
            _vttSnapshot,
            enabled: _vttOptions.Enabled && _vtt is not null,
            includeHidden: _vttOptions.IncludeHidden);

    /// <summary>
    /// 拉一次地图快照。<b>不抛异常</b>：取消、超时、桥没启动都只留下"不可用"的快照——
    /// 回合要照常往下走（《枭熊地图接入实现方案》§5-4）。
    /// </summary>
    public async Task<VttSnapshot> RefreshVttAsync(CancellationToken cancellationToken = default)
    {
        if (_vtt is null || !_vttOptions.Enabled)
        {
            return _vttSnapshot;
        }

        VttSnapshot snapshot;

        try
        {
            snapshot = await _vtt.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException or OperationCanceledException or InvalidOperationException)
        {
            snapshot = VttSnapshot.Unavailable($"地图桥不可用：{ex.Message}");
        }

        _vttSnapshot = snapshot;
        return snapshot;
    }

    /// <summary>说书人登记的控制面意图（点名 / 描写一拍），由编排循环执行（批 4 第 5 步）。</summary>
    public int PendingNarratorIntents
    {
        get
        {
            lock (Gate)
            {
                return _narratorIntents.Count;
            }
        }
    }

    /// <summary>落盘一次世界状态变更（确认 / 超时 / 未提问三条来路都汇到这里）。</summary>
    private NarratorActionOutcome ApplyWorldState(
        WorldStateMutation mutation,
        string reason,
        bool confirmed,
        bool timedOut)
    {
        var result = _world.Apply(mutation);

        if (!result.Applied)
        {
            // 结算失败不撤销正文（§11.3-4）：把原因回灌给模型，剧情照旧往下走。
            // 但导演也该看得见"这次提议没生效、为什么"——只在工具回执里等于只有模型知道。
            _bus.Publish("roleplay.world", new
            {
                storyId = _options.StoryId,
                revision = _timeline.Revision,
                version = _world.Version,
                applied = false,
                reason,
                timedOut,
                errors = result.Errors,
            });

            return new NarratorActionOutcome(
                false,
                $"世界状态没有改变：{string.Join("；", result.Errors)}。正文照旧，继续往下写。");
        }

        // 先落盘再广播：落盘失败的日志与事件都留不住，但"重开一场能回到同一状态"靠的是它。
        _timeline.AppendWorld(mutation, result.State.Version, reason);

        _bus.Publish("roleplay.world", new
        {
            storyId = _options.StoryId,
            revision = _timeline.Revision,
            version = result.State.Version,
            time = result.State.Time,
            place = result.State.Place,
            present = result.State.Present,
            applied = true,
            reason,
            confirmed,
            timedOut,
        });

        // 第 1 列读快照：刷一次，导演立刻看到新现场。
        PublishState();

        var how = timedOut ? "（超时未答复，按采用收口）" : confirmed ? string.Empty : "（未提问，直接生效）";
        return new NarratorActionOutcome(true, $"世界状态已更新到 v{result.State.Version}{how}。");
    }

    /// <summary>
    /// 按时间线里 <c>kind=world</c> 的行把世界状态重建回来（§11.4-2 的"崩溃后按行重建"）。
    /// 重放失败的行（例如角色卡被删了、变更里出现"未知角色"）跳过并报一条事件——
    /// 恢复不需要完美，但**不静默**。
    /// </summary>
    private void ReplayWorldState()
    {
        foreach (var record in _timeline.WorldRecords)
        {
            var result = _world.Apply(record.Mutation);

            if (!result.Applied)
            {
                _bus.Publish("roleplay.error", new
                {
                    code = "world_replay",
                    message = $"重放第 {record.Version} 次世界状态变更失败：{string.Join("；", result.Errors)}",
                });
            }
        }
    }

    /// <summary>把一次变更写成给导演看的人话——他只能凭这句话拍板。</summary>
    private static string DescribeChange(WorldStateMutation mutation)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(mutation.Time))
        {
            parts.Add($"时间 → {mutation.Time}");
        }

        if (!string.IsNullOrWhiteSpace(mutation.Place))
        {
            parts.Add($"地点 → {mutation.Place}");
        }

        if (mutation.Enter.Count > 0)
        {
            parts.Add($"进场：{string.Join('、', mutation.Enter)}");
        }

        if (mutation.Leave.Count > 0)
        {
            parts.Add($"离场：{string.Join('、', mutation.Leave)}");
        }

        if (mutation.Attributes.Count > 0)
        {
            parts.Add("属性：" + string.Join(
                '、',
                mutation.Attributes.Select(delta => $"{delta.Character} 的 {delta.Key} {delta.Delta:+#;-#;0}")));
        }

        if (mutation.Items.Count > 0)
        {
            parts.Add("持有物：" + string.Join(
                '、',
                mutation.Items.Select(delta => $"{delta.Character} {(delta.Remove ? "失去" : "得到")}「{delta.Item}」")));
        }

        if (mutation.Knowledge.Count > 0)
        {
            parts.Add("已知：" + string.Join(
                '、',
                mutation.Knowledge.Select(delta => $"{delta.Character} 得知「{delta.Fact}」")));
        }

        return parts.Count == 0 ? "（没有具体变化）" : string.Join(Environment.NewLine, parts);
    }

    /// <summary>普通入队（不去重）：只给"建议收束"用——它进队后本来就只是留痕，执行阶段不处理。</summary>
    private void Enqueue(NarratorIntent intent)
    {
        lock (Gate)
        {
            _narratorIntents.Add(intent);
        }
    }

    /// <summary>
    /// 入队并**去重**（§11）：同一轮里同一角色的点名 / 同一轮的描写只保留一条，
    /// <b>保留首次入队的位置、内容取最后一次</b>——否则"最新一次点名插队"会让执行顺序不稳定。
    /// <para>
    /// 回执必须把结果说给模型听：它看不见队列，只会一直排；说清"已合并第 N 次 / 当前待执行 M 个"，
    /// 它才会停下来。队列满时明确拒绝，不让它以为排进去了。
    /// </para>
    /// </summary>
    private NarratorActionOutcome EnqueueDeduplicated(NarratorIntent intent, string label)
    {
        var key = intent.Kind == "nudge" ? $"nudge:{intent.Target}" : "beat";

        lock (Gate)
        {
            var attempts = _intentAttempts.TryGetValue(key, out var count) ? count + 1 : 1;
            _intentAttempts[key] = attempts;

            var index = _narratorIntents.FindIndex(item => SameKey(item, key));

            if (index >= 0)
            {
                // 合并：只换内容，位置不动。
                _narratorIntents[index] = intent;

                var position = index + 1;
                return new NarratorActionOutcome(
                    true,
                    $"已合并：本轮{label}的第 {attempts} 次合并为 1 次（内容取最新，仍在排队第 {position} 位）。"
                    + $"当前待执行 {_narratorIntents.Count} 个。");
            }

            var limit = IntentQueueLimitLocked();

            if (_narratorIntents.Count >= limit)
            {
                return new NarratorActionOutcome(
                    false,
                    $"队列已有 {_narratorIntents.Count} 个待执行动作（上限 {limit}），本次未加入。"
                    + "请先让现有动作执行，不要继续排队。");
            }

            _narratorIntents.Add(intent);

            return new NarratorActionOutcome(
                true,
                $"已安排{label}（排队第 {_narratorIntents.Count} 位）。当前待执行 {_narratorIntents.Count} 个。");
        }
    }

    private static bool SameKey(NarratorIntent intent, string key) =>
        key == "beat" ? intent.Kind == "beat" : intent.Kind == "nudge" && key == $"nudge:{intent.Target}";

    /// <summary>
    /// 队列上限：<b>至少 5，且不小于在场角色数 + 1</b>。
    /// 多角色场里"每个角色各被点名一次"是完全合理的，上限比角色数小就会误伤——
    /// 那正是这批改动最不该碰到的场景。
    /// </summary>
    private int IntentQueueLimitLocked()
    {
        if (_options.NarratorIntentQueueLimit > 0)
        {
            return _options.NarratorIntentQueueLimit;
        }

        var actors = _registry.Participants.Count(participant => !IsNarrator(participant.Id));
        return Math.Max(5, actors + 1);
    }

    /// <summary>待执行意图的快照（探针 / 诊断）：顺序即执行顺序。</summary>
    internal IReadOnlyList<(string Kind, string? Target, string Text)> PendingIntentSnapshot()
    {
        lock (Gate)
        {
            return _narratorIntents.Select(item => (item.Kind, item.Target, item.Text)).ToArray();
        }
    }

    /// <summary>问答进剧情日志（`kind=interaction`）：重启后仍能回答"当时他选的是什么"（§14.6）。</summary>
    protected override Task OnInteractionResolvedAsync(PendingDecision decision, InteractionOutcome outcome)
    {
        _timeline.AppendInteraction(
            decision.Kind.ToString(),
            [.. decision.Questions.Select(question => question.Prompt)],
            outcome);

        return Task.CompletedTask;
    }

    /// <summary>本场结束时的收尾（状态与快照由内核统一处理）。</summary>
    protected override Task OnEndedAsync(CancellationToken cancellationToken)
    {
        _bus.Publish("roleplay.ended", new
        {
            storyId = _options.StoryId,
            revision = _timeline.Revision,
        });

        return Task.CompletedTask;
    }

    /// <summary>全量快照（重连校准用，§8.4）。</summary>
    public object Snapshot() => SnapshotCore();

    /// <summary>
    /// 当前在场角色的 id（M1 地图编辑器用它校验 token：token.id 必须是已知角色）。
    /// 只读投影，不暴露注册表本身。
    /// </summary>
    public IReadOnlyList<string> ActorIds =>
        _registry.Participants.Select(participant => participant.Id).ToArray();

    /// <summary>
    /// 地图投影（M4）：快照与 <c>GET /api/maps/{id}/document</c> 用**同一份数据**，
    /// 只是字段名按前端习惯（<c>grid.feetPerCell</c>）。没有地图时返回 <c>null</c>。
    /// </summary>
    private object? MapProjection()
    {
        if (_options.MapDocumentProvider?.Invoke() is not { } document)
        {
            return null;
        }

        return new
        {
            id = document.Id,
            name = document.Name,
            grid = new
            {
                dpi = document.Grid.Dpi,
                offsetX = document.Grid.OffsetX,
                offsetY = document.Grid.OffsetY,
                feetPerCell = document.Grid.Scale,
            },
            tokens = document.Tokens
                .Select(token => new { id = token.Id, name = token.Name, x = token.X, y = token.Y, visible = token.Visible })
                .ToArray(),
            rooms = document.Rooms
                .Select(room => new
                {
                    id = room.Id,
                    name = room.Name,
                    polygon = room.Polygon.Select(point => new[] { point.X, point.Y }).ToArray(),
                    light = room.Light,
                })
                .ToArray(),
            walls = document.Walls
                .Select(wall => new
                {
                    id = wall.Id,
                    points = wall.Points.Select(point => new[] { point.X, point.Y }).ToArray(),
                    kind = wall.Kind,
                    open = wall.Open,
                })
                .ToArray(),
            // 区域（M7）：与 GET document 同源，玩家视角只渲染 visible=true 的。
            zones = (document.Zones ?? []).Select(zone => new
            {
                id = zone.Id,
                name = zone.Name,
                kind = zone.Kind,
                shape = zone.Shape!.Type == "polygon"
                    ? new
                    {
                        type = "polygon",
                        points = (zone.Shape.Points ?? []).Select(point => new[] { point.X, point.Y }).ToArray(),
                    }
                    : (object)new
                    {
                        type = "circle",
                        center = new[] { zone.Shape.Center?.X ?? 0, zone.Shape.Center?.Y ?? 0 },
                        radiusCells = zone.Shape.RadiusCells,
                    },
                color = zone.Color,
                note = zone.Note,
                visible = zone.Visible,
            }).ToArray(),
            // 迷雾（M6a）：与 GET document 同一份数据，玩家视角的遮罩就靠它。
            fog = new
            {
                enabled = (document.Fog ?? Maps.MapFog.Disabled).Enabled,
                explored = (document.Fog ?? Maps.MapFog.Disabled).ExploredCells
                    .Select(cell => new[] { cell.X, cell.Y }).ToArray(),
                visible = (document.Fog ?? Maps.MapFog.Disabled).VisibleCells
                    .Select(cell => new[] { cell.X, cell.Y }).ToArray(),
            },
        };
    }

    /// <summary>
    /// 地图被编辑器改过（M4）：立刻重发一次快照，叙事侧的战术板不必等下一轮。
    /// 只做"推一把"，真正的数据仍从 <see cref="MapProjection"/> 重新读。
    /// </summary>
    public void NotifyMapChanged() => PublishState();

    protected override object SnapshotCore()
    {
        lock (Gate)
        {
            return new
            {
                status = Status.ToWire(),
                storyId = _options.StoryId,
                worldview = _options.World,
                situation = _options.Situation,
                userRole = _options.UserRole,
                revision = _timeline.Revision,
                worldState = _world.Snapshot(),
                // M4：地图节点进快照（与 GET /api/maps/{id}/document 同源同形状），
                // 前端只吃这一份——不再"快照来自 A、document 来自 B"（F6 的教训）。
                map = MapProjection(),
                cast = _registry.Participants
                    .Select(participant => new
                    {
                        id = participant.Id,
                        name = participant.Name,
                        role = participant.Role == ParticipantRole.Host ? "narrator" : "actor",
                        present = _world.Current.Present.Contains(participant.Id, StringComparer.Ordinal),
                        status = participant.Status.ToWire(),
                    })
                    .ToArray(),
                // 只回最近若干幕：快照是全量重建用的，但它不该随剧情增长而无限变大。
                turns = _timeline.Turns
                    .TakeLast(20)
                    .Select(turn => new
                    {
                        revision = turn.Revision,
                        // `at` 是 F6 内联排序用的：检定卡 / 骰子卡要按时间插进剧情流。
                        at = turn.At,
                        directorInput = turn.DirectorInput,
                        lines = Project(turn.Lines),
                    })
                    .ToArray(),
                // 检定与骰子（F6）：与 turns 同口径截断，字段**直接照记录类序列化**，不另造形状。
                // 没有它们，刷新后检定卡会全部消失（和 F1 的 combat 节点同一个道理）。
                checks = _timeline.CheckRecords.TakeLast(20).ToArray(),
                rolls = _timeline.RollRecords.TakeLast(20).ToArray(),
                // 角色请求（M9b）：刷新后请求行要还在，所以快照是权威源（与 checks / rolls 同一套）。
                requests = _timeline.RequestRecords.TakeLast(20).ToArray(),
                // 还挂着没答复的待决事项（说书人的提议 / 提问）：前端重连后据此重建浮层。
                pendingDecisions = PendingDecisionsSnapshot(),
                // 地图（V1）：前端第 1 列据它显示"地图已连接 / 最后更新 / 标尺"。
                vtt = VttStatus(),
                // 战斗投影（F1，形状照 §5.2）：前端刷新 / 重连后靠它重建战斗条，
                // 数据源只用 Encounter + SheetStore，**不缓存 HP**（HP 的唯一真相仍是 Sheet）。
                combat = ProjectCombat(),
                // 上下文指标（E，2026-10-29）：验证时只能间接估 token，这几项直接摆到接口上。
                context = ContextMetrics(),
            };
        }
    }

    /// <summary>
    /// 上下文指标（E）：L2 条数 / token 粗估 / 压缩次数 / 摘要长度 / 前缀指纹。
    /// <para>
    /// 只读投影，<b>不参与任何判定</b>；token 与压缩阈值用的是同一个估算（字符数 / 2），
    /// 所以这里的 <c>estimatedTokens</c> 就是"什么时候会压缩"那把尺子。
    /// </para>
    /// </summary>
    private object ContextMetrics()
    {
        int historyCount;
        int summaryChars;

        lock (Gate)
        {
            historyCount = _history.Count;
            summaryChars = _summary.Length;
        }

        // 摘要也在上下文里：一起算进去才接近真实占用。
        var chars = summaryChars;

        foreach (var line in SnapshotHistory())
        {
            chars += line.Text.Length + 12;
        }

        return new
        {
            historyCount,
            estimatedTokens = chars / 2,
            compressionCount = _historyCompressions,
            summaryChars,
            prefixFingerprint = _lastPrefixFingerprint,
        };
    }

    /// <summary>
    /// `roleplay.state.combat`（§5.2）：没有战斗时返回 <c>null</c>。
    /// 行动经济用 <see cref="CombatBudget"/>（与 <c>combat-turn</c> 事件同一口径）。
    /// </summary>
    private object? ProjectCombat()
    {
        if (_encounter is not { } encounter)
        {
            return null;
        }

        return new
        {
            encounterId = encounter.Id,
            round = encounter.Round,
            phase = encounter.Phase.ToString().ToLowerInvariant(),
            order = encounter.Order
                .Select(id =>
                {
                    var combatant = encounter.Find(id);
                    return new { actor = id, initiative = combatant?.Initiative ?? 0 };
                })
                .ToArray(),
            currentActor = encounter.Turn?.ActorId,
            budget = encounter.Turn is { } turn ? CombatBudget.Of(turn) : null,
            combatants = encounter.Combatants
                .Select(combatant =>
                {
                    var sheet = _sheets?.Get(combatant.Id);

                    return new
                    {
                        id = combatant.Id,
                        name = combatant.DisplayName,
                        cnName = combatant.CnName,
                        hp = sheet?.CurrentHp,
                        maxHp = sheet?.MaxHp,
                        status = sheet?.Status.ToString().ToLowerInvariant(),
                        conditions = combatant.ConditionRounds.Keys.ToArray(),
                    };
                })
                .ToArray(),
        };
    }

    /// <summary>开场：由说书人写第一拍（环境 + 在场角色的反应），不让角色抢跑（§11.4-5、§11.4-8）。</summary>
    private async Task RunOpeningAsync()
    {
        try
        {
            var opener = ResolveNarrator() ?? ResolvePresentActors().FirstOrDefault();
            if (opener is null)
            {
                _bus.Publish("roleplay.error", new { code = "no_cast", message = "没有可用角色，无法写开场。" });
                return;
            }

            // 开场也要看见地图：不然"我推门进去"那一轮里，说书人描述的房间与角色后来看到的对不上。
            await RefreshVttAsync(LifetimeToken).ConfigureAwait(false);

            var task = Narrator.BuildOpeningTask(
                _options.World,
                _options.Situation,
                _options.UserRole,
                _options.Cast);

            var line = await RunActorTurnAsync(opener, task, LifetimeToken).ConfigureAwait(false);

            var lines = line is null ? [] : new[] { line };
            var turn = _timeline.Append(null, lines);
            _memory.Record(turn);

            _bus.Publish("roleplay.turn", new
            {
                storyId = _options.StoryId,
                revision = turn.Revision,
                directorInput = string.Empty,
                lines = Project(turn.Lines),
            });

            PublishState();
        }
        catch (OperationCanceledException)
        {
            // 结束或进程退出：正常路径。
        }
        catch (Exception ex)
        {
            _bus.Publish("roleplay.error", new { code = "opening", message = ex.Message });
        }
    }

    /// <summary>时序接话：一次一个，后者能看到前者刚说的话（全员并行会让角色各说各的，§11.4-1）。</summary>
    private async Task<IReadOnlyList<StoryLine>> RunSequentialAsync(string task, CancellationToken cancellationToken)
    {
        var members = ResolvePresentActors();
        var spoken = new List<string>();
        var lines = new List<StoryLine>();

        while (members.Count > 0)
        {
            var plan = _scheduler.Plan(new SchedulingContext
            {
                Candidates = members.Select(member => member.Id).ToArray(),
                Spoken = spoken,
            });

            if (plan.Count == 0)
            {
                break;
            }

            var speaker = members.FirstOrDefault(member =>
                string.Equals(member.Id, plan[0].ParticipantId, StringComparison.Ordinal));

            if (speaker is null)
            {
                break;
            }

            spoken.Add(speaker.Id);

            var line = await RunActorTurnAsync(speaker, task, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                continue;
            }

            lines.Add(line);
            RememberLine(speaker.Name, line.Text);

            // 逐条推给前端：正文是流式落地的，界面按幕累积而不是等整轮结束（§8.3）。
            _bus.Publish("roleplay.line", new
            {
                storyId = _options.StoryId,
                author = line.Author,
                name = speaker.Name,
                text = line.Text,
            });
        }

        return lines;
    }

    private async Task<StoryLine?> RunActorTurnAsync(
        Participant participant,
        string task,
        CancellationToken cancellationToken)
    {
        // §13-B：本轮内说书人自己建议了收束 → 后面的角色回合不再跑。
        // **必须放在 WaitWhileBlockedAsync 之前**：那一行会一直等暂停恢复（真机 219 秒的来源）。
        if (_pausedByNarratorThisTurn)
        {
            return null;
        }

        await WaitWhileBlockedAsync(cancellationToken).ConfigureAwait(false);

        if (Status == MeetingStatus.Ended)
        {
            return null;
        }

        var composer = CreateComposer(participant);
        var request = new TurnRequest(participant.Id, task, (int)_world.Version, composer);

        var outcome = await _registry.RunTurnAsync(participant.Id, request, cancellationToken).ConfigureAwait(false);
        if (!outcome.Produced || string.IsNullOrWhiteSpace(outcome.Text))
        {
            return null;
        }

        // 输出净化（§11.4-10）：剥掉元话语与残留格式标记，整条都是元话语时视为没有产出。
        var text = NarrativeOutputSanitizer.Sanitize(outcome.Text, participant.Name);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new StoryLine(participant.Id, text) { Witnesses = SceneWitnesses() };
    }

    private ContextComposer CreateComposer(Participant participant)
    {
        var roleSection = participant.Card.Body;

        if (IsNarrator(participant.Id))
        {
            // 说书人的职责提示与工具名出自同一处（Narrator），避免"提示词与工具集对不上"。
            roleSection = Join(
                roleSection,
                Narrator.BuildSystemPrompt(_options.World, _options.Situation, _world.Snapshot()));
        }

        // 私有节：他自己的状态 / 持有物 / 已知信息（别人的一概不给，§11.4-3）。
        // 只进角色节，绝不进共享节——共享节必须字节稳定，否则前缀缓存全 miss。
        roleSection = Join(roleSection, PrivateSectionFor(participant.Id));

        // 地图段落（《枭熊地图接入实现方案》V1）：也进角色节——它逐轮变化，
        // 放进共享节会打破第 1 轮之后的缓存前缀。说书人拿全量视图，角色只拿注入半径以内。
        roleSection = Join(roleSection, MapSectionFor(participant.Id));

        // 战斗投影（C1–C4）：**必须逐轮刷新**，所以同样进角色节，绝不进系统提示/共享节——
        // 否则先攻序与当前行动者会被钉死在第 1 轮，说书人无从知道该用 combat_action / next_turn。
        roleSection = Join(roleSection, CombatSectionFor(participant.Id));

        // 现场与进度（§1.1）：它们**每轮都变**，所以从共享节（前缀）搬到了角色节（尾部）。
        // 留在前缀里等于每轮重建前缀，缓存一次都命中不了。
        roleSection = Join(roleSection, SceneSection());

        var composer = new ContextComposer(
            participant.Id,
            _options.SystemPrompt,
            IsNarrator(participant.Id) ? _toolDispatcher.Tools : _toolDispatcher.CharacterTools,
            _strategy,
            new ComposeRequest
            {
                SharedSection = BuildSharedSection(),
                // L2：摘要点之后到现在的全部发言，只追加、不滚动。
                History = SnapshotHistory(),
                RoleSection = roleSection,
                Materials = [],
                // M8b：地图图**只进角色节**。共享节与系统提示一个字节都没动（前缀指纹不受影响）。
                Images = BuildMapImages(participant.Id),
            });

        // 探针靠它验"不压缩的轮次指纹不变"；运行时它也是缓存诊断的现成抓手。
        _lastPrefixFingerprint = composer.PrefixFingerprint;
        return composer;
    }

    /// <summary>
    /// 战斗投影（进角色节，逐轮刷新）：先攻序、轮次、当前行动者与剩余行动经济。
    /// <para>
    /// 没有这段，说书人只知道"开过一次战"（start_combat 的回执），看不到回合推进——
    /// 实战里它就会用 <c>dice_roll</c> 手搓攻击、并且永不调用 <c>next_turn</c>。
    /// </para>
    /// </summary>
    internal string CombatSectionFor(string participantId)
    {
        if (_encounter is not { Phase: EncounterPhase.Active } encounter)
        {
            return string.Empty;
        }

        var order = string.Join(
            " → ",
            encounter.Order.Select(id =>
            {
                var combatant = encounter.Find(id);
                return combatant is null ? id : $"{id}({combatant.Initiative})";
            }));

        var builder = new StringBuilder();
        builder.Append("当前战斗：").Append(encounter.Id)
            .Append("，第 ").Append(encounter.Round).Append(" 轮；先攻 ").Append(order).Append('。');

        if (encounter.Turn is { } turn)
        {
            builder.Append(" 现在轮到 ").Append(turn.ActorId).Append("（Action ")
                .Append(turn.ActionUsed ? "已用" : "可用").Append("、Bonus ")
                .Append(turn.BonusUsed ? "已用" : "可用").Append("、反应 ")
                .Append(turn.ReactionUsed ? "已用" : "可用").Append("、移动剩余 ")
                .Append(Math.Max(0, turn.MovementMaxFeet - turn.MovementUsedFeet)).Append(" 尺）。");
        }

        if (IsNarrator(participantId))
        {
            builder.Append(" 战斗内出手必须用 combat_action 结算（命中、伤害、扣血都由它算，")
                .Append("不要用 dice_roll 手搓）；一个人行动完就用 next_turn 交棒。");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 地图段落（进角色节）：说书人拿全量快照，角色拿"以自己为中心、注入半径以内"的那一份。
    /// 没有地图时返回空串——<b>回合里不会出现"地图已断开"这类元话语</b>（降级是静默的）。
    /// </summary>
    internal string MapSectionFor(string participantId)
    {
        // 旧 Vtt 通道（vtt.enabled=true）：保留，但**与本地地图互斥**——两条路同时写地图信息
        // 会让模型看到两份互相矛盾的坐标（设计文档 §8.5）。
        if (_vttOptions.Enabled && _vtt is not null && _vttSnapshot.Available)
        {
            var context = new VttContext(_vttSnapshot, _world.Current, _vttOptions, _vttTokenIds);
            return IsNarrator(participantId) ? context.ForNarrator() : context.ForCharacter(participantId);
        }

        if (_options.MapDocumentProvider?.Invoke() is not { } document)
        {
            return string.Empty;
        }

        // M9a：角色拿到的是"**自己视角**"的那一份（位置 / 附近的人 / 身边的区域 / 迷雾状态），
        // 只覆盖已探索格、不提隐藏的 token 与区域——信息闸门在 `MapCharacterView` 里。
        if (!IsNarrator(participantId))
        {
            return Maps.MapCharacterView.Build(document, participantId, _options.CharacterViewRadiusCells);
        }

        // 说书人：全量段（M3 起就是这一份，本批没动它）。
        return Maps.MapContextBuilder.Build(document, _encounter?.Turn?.ActorId, UserActorId());
    }

    /// <summary>
    /// 玩家（导演）在剧情里的那个角色：拿 <see cref="RoleplayOptions.UserRole"/> 去匹配在场角色的名字 / id。
    /// 地图段的参照物用它，这样同一张图每轮生成的段落基准固定（M3 收尾）。
    /// </summary>
    private string? UserActorId()
    {
        var userRole = _options.UserRole;

        if (string.IsNullOrWhiteSpace(userRole))
        {
            return null;
        }

        foreach (var participant in _registry.Participants)
        {
            if ((!string.IsNullOrEmpty(participant.Name)
                    && userRole.Contains(participant.Name, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(participant.Id)
                    && userRole.Contains(participant.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return participant.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// 地图图（M8b）：说书人拿 DM 视角、角色拿玩家视角；<c>onlyOnChange</c> 时按文档版本复用。
    /// 取不到图（204 / 未启用）就返回空——**不发图也不报错**。
    /// </summary>
    internal IReadOnlyList<ImageAttachment> BuildMapImages(string participantId)
    {
        var vision = _options.Vision;

        if (!vision.Enabled || _options.MapViewImageProvider is null)
        {
            return [];
        }

        var revision = _options.MapDocumentProvider?.Invoke()?.UpdatedAt ?? -1;

        if (vision.OnlyOnChange && revision == _lastMapImageRevision)
        {
            // 同一版文档：复用上一轮的图（省一次合成，也让请求体里的图字节稳定）。
            var reused = IsNarrator(participantId) ? _lastMapImageDm : _lastMapImagePlayer;

            if (reused is null)
            {
                return [];
            }

            return [new ImageAttachment(reused, "image/png", IsNarrator(participantId) ? "【地图·全图】" : "【地图·你看到的部分】")];
        }

        var playerView = !IsNarrator(participantId);
        var png = _options.MapViewImageProvider(playerView);

        if (revision != _lastMapImageRevision)
        {
            _lastMapImageRevision = revision;
            _lastMapImageDm = null;
            _lastMapImagePlayer = null;
        }

        if (playerView)
        {
            _lastMapImagePlayer = png;
        }
        else
        {
            _lastMapImageDm = png;
        }

        if (png is null)
        {
            return [];
        }

        return [new ImageAttachment(png, "image/png", playerView ? "【地图·你看到的部分】" : "【地图·全图】")];
    }

    // ——— ICharacterActions：角色能用的只读 / 只请求工具（M9b） ———

    public Task<NarratorActionOutcome> LookAroundForCharacterAsync(
        string actorId,
        double? radiusFeet,
        CancellationToken cancellationToken)
    {
        if (_options.MapDocumentProvider?.Invoke() is not { } document)
        {
            return Task.FromResult(new NarratorActionOutcome(false, "这一场还没有地图；你只能凭记忆判断周围。"));
        }

        var radiusCells = radiusFeet is { } feet && document.Grid is { Dpi: > 0 } grid
            ? feet / (grid.Scale > 0 ? grid.Scale : 5)
            : _options.CharacterViewRadiusCells;

        // 与角色节同一套闸门（`MapCharacterView`）：未探索格与 visible=false 的东西不出现。
        var view = Maps.MapCharacterView.Build(document, actorId, radiusCells);

        return Task.FromResult(view.Length > 0
            ? new NarratorActionOutcome(true, view)
            : new NarratorActionOutcome(true, "你此刻看不出周围有什么。"));
    }

    public Task<NarratorActionOutcome> CheckTerrainAsync(string actorId, CancellationToken cancellationToken)
    {
        if (_options.MapDocumentProvider?.Invoke() is not { } document)
        {
            return Task.FromResult(new NarratorActionOutcome(false, "这一场还没有地图。"));
        }

        var self = (document.Tokens ?? []).FirstOrDefault(token =>
            string.Equals(token.Id, actorId, StringComparison.OrdinalIgnoreCase));

        if (self is null)
        {
            return Task.FromResult(new NarratorActionOutcome(false, "地图上没有你的位置。"));
        }

        var parts = new List<string>();
        var grid = document.Grid;

        if (grid is { Dpi: > 0 } calibration)
        {
            var cell = new Maps.GridCell(
                (int)Math.Floor((self.X - calibration.OffsetX) / calibration.Dpi),
                (int)Math.Floor((self.Y - calibration.OffsetY) / calibration.Dpi));

            var fog = document.Fog is { Enabled: true } enabled ? enabled : null;
            var explored = fog is null
                ? null
                : new HashSet<Maps.GridCell>(fog.ExploredCells.Concat(fog.VisibleCells));
            var visibleCells = fog is null ? null : new HashSet<Maps.GridCell>(fog.VisibleCells);

            if (fog is not null)
            {
                parts.Add(visibleCells!.Contains(cell) ? "你脚下在视野里。" : "你脚下在视野外，只能凭记忆。");
            }

            foreach (var zone in (document.Zones ?? []).Where(zone => zone.Visible))
            {
                if (zone.Shape is null)
                {
                    continue;
                }

                var mine = Maps.MapCharacterView.ZoneCellsOf(document, zone).Contains(cell);
                var beside = Maps.MapCharacterView.ZoneCellsOf(document, zone)
                    .Any(other => Math.Abs(other.X - cell.X) <= 1 && Math.Abs(other.Y - cell.Y) <= 1);

                if (!mine && !beside)
                {
                    continue;
                }

                var note = string.IsNullOrWhiteSpace(zone.Note) ? string.Empty : $"（{zone.Note.Trim()}）";
                parts.Add($"{zone.Name}：{Maps.MapZoneKinds.Describe(zone.Kind)}{note}{(mine ? "，你正站在里面" : "，就在旁边")}。");
            }
        }

        if (parts.Count == 0)
        {
            parts.Add("脚下没有特别的地形效果。");
        }

        return Task.FromResult(new NarratorActionOutcome(true, string.Join(" ", parts)));
    }

    public Task<NarratorActionOutcome> RequestJudgementAsync(
        string actorId,
        string? skill,
        string? ability,
        string? kind,
        int? dcHint,
        string reason,
        CancellationToken cancellationToken)
    {
        var what = skill ?? ability ?? kind ?? "一次判定";
        var hint = dcHint is { } dc ? $"，建议 DC {dc}" : string.Empty;

        return SubmitRequestAsync(
            actorId,
            "check",
            $"对 {what} 做一次判定{hint}",
            reason,
            new Dictionary<string, string>
            {
                ["skill"] = skill ?? string.Empty,
                ["ability"] = ability ?? string.Empty,
                ["kind"] = kind ?? string.Empty,
                ["dcHint"] = dcHint?.ToString() ?? string.Empty,
            },
            cancellationToken);
    }

    public Task<NarratorActionOutcome> RequestCombatActionAsync(
        string actorId,
        string kind,
        string targetId,
        string? weaponId,
        string? spellId,
        string intent,
        string? reason,
        CancellationToken cancellationToken) =>
        SubmitRequestAsync(
            actorId,
            "combat_action",
            $"{kind} → {targetId}（{intent}）",
            string.IsNullOrWhiteSpace(reason) ? intent : reason,
            new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["targetId"] = targetId,
                ["weaponId"] = weaponId ?? string.Empty,
                ["spellId"] = spellId ?? string.Empty,
                ["intent"] = intent,
            },
            cancellationToken);

    /// <summary>
    /// 落一条 <c>kind=request</c> 并告诉角色"等说书人处理"。**不产生任何结算**（M9b 的硬约束）。
    /// </summary>
    private Task<NarratorActionOutcome> SubmitRequestAsync(
        string actorId,
        string requestKind,
        string summary,
        string reason,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        // one-request-per-round：两种请求共享额度。**被拒的不占新额度、也不落盘**——
        // 否则连发 12 次就会在 story.jsonl 里堆 12 条无意义的记录（r17 的现场）。
        if (_requestsThisRound >= 1)
        {
            var used = _firstRequestThisRound ?? "上一个";
            return Task.FromResult(new NarratorActionOutcome(
                false,
                $"这一轮已经提交过一个请求了（{used}），等说书人处理后再提下一个。不要重试。"));
        }

        _requestsThisRound = 1;
        _firstRequestThisRound = summary;
        var name = _registry.Participants
            .FirstOrDefault(participant => string.Equals(participant.Id, actorId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? actorId;

        var record = _timeline.AppendRequest(new StoryRequestRecord(
            actorId,
            requestKind,
            summary,
            string.IsNullOrWhiteSpace(reason) ? summary : reason,
            arguments,
            DateTimeOffset.UtcNow));

        _bus.Publish("roleplay.request", new
        {
            actorId,
            requestKind,
            summary,
            reason = record.Reason,
            at = record.At,
        });

        return Task.FromResult(new NarratorActionOutcome(
            true,
            $"已提交请求：{summary}。等说书人处理——它可能照你说的掷骰 / 结算，也可能用叙事化解；"
            + "你的正文可以写“我试着……”，但不要写结果。"));
    }

    /// <summary>
    /// 这个角色能看到的"自己的那份"世界状态（进角色节）。
    /// 说书人返回空：它的全量视图已经写在 <see cref="Narrator.BuildSystemPrompt"/> 里，不必来两遍。
    /// </summary>
    internal string PrivateSectionFor(string participantId)
    {
        if (IsNarrator(participantId))
        {
            // 说书人返回空：它的全量视图已经写在 Narrator.BuildSystemPrompt 里了，不必来两遍。
            return string.Empty;
        }

        var builder = new StringBuilder();
        var state = _world.SnapshotFor(participantId);

        if (state.Length > 0)
        {
            builder.Append(state);
        }

        // 长程记忆：他经历过的、以及被告知的（§11.4-7）。预算由 recallMaxChars 兜住。
        var recalled = _memory.RecallFor(participantId, _options.RecallMaxChars);

        if (recalled.Count > 0)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append("[你记得的事]");

            foreach (var item in recalled)
            {
                builder.Append('\n').Append("- ").Append(item);
            }
        }

        return builder.ToString();
    }

    internal string BuildSharedSection()
    {
        var builder = new StringBuilder();
        builder.Append("世界观：").Append(_options.World);
        builder.Append('\n').Append("当前情境：").Append(_options.Situation);

        if (!string.IsNullOrWhiteSpace(_options.UserRole))
        {
            builder.Append('\n').Append("导演（用户）身份：").Append(_options.UserRole);
        }

        // L3 摘要（§1）：**只有压缩时才变**，所以留在前缀里不会破坏缓存。
        // 摘要在 RememberLine 的锁里被追加，这里同样在锁内取快照，避免读到半段。
        lock (Gate)
        {
            if (_summary.Length > 0)
            {
                builder.Append('\n').Append('\n').Append("[早期剧情摘要]").Append('\n').Append(_summary.ToString().Trim());
            }
        }

        return builder.ToString();
    }

    private void RememberLine(string speaker, string text)
    {
        var limit = _options.Context.LineChars;
        var condensed = text.Length > limit ? text[..limit] + "…" : text;

        // 幕号用"即将提交的那一幕"：本 turn 结束时会 Append 成这个 revision。
        var revision = _timeline.Revision + 1;

        lock (Gate)
        {
            _history.Add(new HistoryLine(revision, speaker, condensed));

            // 两道阈值先到先生效（§1.1）：条数上限与 token 估算。
            if (_history.Count > _options.Context.HistoryLimit
                || EstimateHistoryTokens(_history) > _options.Context.HistoryTokenBudget)
            {
                CompressHistory();
            }
        }
    }

    /// <summary>
    /// 压缩（§1.2）：把 L2 的**前半段**压成一段规则摘要追加进 L3，L2 只剩后半段。
    /// 低频事件——每次只造成一次 cache miss，而不是每轮一次。
    /// </summary>
    private void CompressHistory()
    {
        var ratio = _options.Context.CompressRatio;
        ratio = ratio is > 0 and < 1 ? ratio : 0.5;

        var take = Math.Max(1, (int)Math.Floor(_history.Count * ratio));
        take = Math.Min(take, _history.Count - 1); // 至少留一条，别把 L2 清空

        if (take <= 0)
        {
            return;
        }

        var chunk = _history.GetRange(0, take);
        var summary = BuildRuleSummary(chunk);

        if (summary.Length > 0)
        {
            _summary.Append('\n').Append(summary);
        }

        _summaryUpTo = chunk[^1].Revision;
        _history.RemoveRange(0, take);
        _historyCompressions++;

        PersistContextSummary();
    }

    /// <summary>
    /// 规则摘要（第一版不调模型）：每条取前 60 字符，按角色归并成
    /// 「Ella：发现了地窖；拿到钥匙」这样一行。零额外 token。
    /// </summary>
    private static string BuildRuleSummary(IReadOnlyList<HistoryLine> chunk)
    {
        if (chunk.Count == 0)
        {
            return string.Empty;
        }

        const int snippetChars = 60;

        var builder = new StringBuilder();
        builder.Append("- 第 ").Append(chunk[0].Revision).Append('–').Append(chunk[^1].Revision).Append(" 幕：");

        foreach (var group in chunk.GroupBy(line => line.Author, StringComparer.Ordinal))
        {
            builder.Append('\n').Append("  · ").Append(group.Key).Append('：');

            var snippets = group
                .Select(line => line.Text.ReplaceLineEndings(" ").Trim())
                .Select(text => text.Length > snippetChars ? text[..snippetChars] + "…" : text)
                .Where(text => text.Length > 0)
                .ToList();

            builder.Append(string.Join("；", snippets));
        }

        return builder.ToString();
    }

    /// <summary>粗略 token 估算：中文约 1 token ≈ 1.5 字符，取 chars/2 偏保守（宁早压不晚压）。</summary>
    private static int EstimateHistoryTokens(IReadOnlyList<HistoryLine> history)
    {
        var chars = 0;

        foreach (var line in history)
        {
            chars += line.Text.Length + 12;
        }

        return chars / 2;
    }

    /// <summary>L2 的快照（装配时用；只在锁内做一次浅拷贝，避免边改边读）。</summary>
    private IReadOnlyList<HistoryLine> SnapshotHistory()
    {
        lock (Gate)
        {
            return _history.ToArray();
        }
    }

    /// <summary>
    /// 现场与进度（进角色节，逐轮刷新）：第几幕 + 时间、地点、在场名单。
    /// 之前它在共享节里——每轮都变，等于每轮重建前缀（§0）。
    /// </summary>
    internal string SceneSection()
    {
        var builder = new StringBuilder();
        builder.Append("剧情进度：第 ").Append(_timeline.Revision).Append(" 幕");

        var scene = _world.SnapshotPublic();

        if (scene.Length > 0)
        {
            builder.Append('\n').Append('\n').Append("[此刻的现场]").Append('\n').Append(scene);
        }

        return builder.ToString();
    }

    /// <summary>读回 L3 摘要（重启后不能丢，§1.2）。坏文件不拖垮会话。</summary>
    private void LoadContextSummary()
    {
        if (_contextPath is null || !File.Exists(_contextPath))
        {
            return;
        }

        try
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<ContextSummaryFile>(
                File.ReadAllText(_contextPath),
                ContextJsonOptions);

            if (loaded is { Text.Length: > 0 })
            {
                _summary.Append(loaded.Text);
                _summaryUpTo = loaded.UpToRevision;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // 摘要只是"省 token 的缓存"：读不回来就从空开始，历史本身还在 story.jsonl 里。
        }
    }

    private void PersistContextSummary()
    {
        if (_contextPath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_contextPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _contextPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new ContextSummaryFile(_summaryUpTo, _summary.ToString(), DateTimeOffset.UtcNow),
                    ContextJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不影响本场运行：摘要仍在内存里，只是重启后会重来。
        }
    }

    /// <summary>L3 摘要的落盘形状（<c>context-summary.json</c>）。</summary>
    private sealed record ContextSummaryFile(long UpToRevision, string Text, DateTimeOffset At);

    private static readonly System.Text.Json.JsonSerializerOptions ContextJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string BuildDirectorTask(DirectorInput input)
    {
        var task = new StringBuilder();

        if (input.Text.Length > 0)
        {
            task.Append("导演的旁白 / 指令：").Append(input.Text);
        }
        else
        {
            task.Append("导演只给了元指令，没有写旁白。");
        }

        if (input.Directives.Count > 0)
        {
            // 出戏指令是给全场的元指令，不进剧情时间线（§11.4-9）。
            task.Append('\n').Append("[导演的元指令，照做但不要写进正文] ")
                .Append(string.Join("；", input.Directives));
        }

        task.Append('\n').Append("接着往下写：只写你这个角色的言行，写到该别人接话为止。");
        return task.ToString();
    }

    /// <summary>
    /// 此刻在场、且能接话的角色。
    /// 权威来源是<b>世界状态</b>而不是构造时那份卡司——进场 / 离场由说书人提议、经导演确认后改的正是它；
    /// 两边各留一份的话，就会出现"进了场的人不接话、离场的人还在说话"。
    /// </summary>
    private IReadOnlyList<Participant> ResolvePresentActors() =>
        _world.Current.Present
            .Where(id => !string.Equals(id, _narratorId, StringComparison.Ordinal))
            .Select(_registry.Find)
            .Where(participant => participant is not null)
            .Select(participant => participant!)
            .ToArray();

    private Participant? ResolveNarrator() =>
        _narratorId is null ? null : _registry.Find(_narratorId);

    /// <summary>探针要复现同一套判定（<c>--probe-character-tools</c>）。</summary>
    internal bool IsNarrator(string participantId) =>
        string.Equals(participantId, _narratorId, StringComparison.Ordinal);

    /// <summary>
    /// 本场的工具面实例（探针用）：规则查询的"一轮 3 次"额度挂在 dispatcher 上，
    /// 由 <see cref="SayAsync"/> 每轮清零——探针必须拿同一个实例才能验到复位。
    /// </summary>
    internal NarratorToolDispatcher ToolDispatcher => _toolDispatcher;

    /// <summary>最近一次装配的前缀指纹（探针验"不压缩的轮次不变"）。</summary>
    internal string? LastPrefixFingerprint => _lastPrefixFingerprint;

    /// <summary>L2 当前条数（探针验压缩前后的规模）。</summary>
    internal int HistoryCount
    {
        get
        {
            lock (Gate)
            {
                return _history.Count;
            }
        }
    }

    /// <summary>本场已压缩几次。</summary>
    internal int HistoryCompressions => _historyCompressions;

    /// <summary>
    /// <c>recall_story</c> 的实现（§2）：从 <c>StoryTimeline</c> 取记录，按**见证者**过滤后拼成纯文本。
    /// <para>
    /// 角色只回忆得到自己见证过的发言（<see cref="StoryLine.Witnesses"/> 为空 = 公开）；
    /// 说书人看全量。查询<b>不改任何状态</b>——它只是读。
    /// </para>
    /// </summary>
    public string RecallStory(string participantId, StoryRecallQuery query)
    {
        var isNarrator = IsNarrator(participantId);
        var lines = _timeline.Recall(query);

        var visible = new List<StoryRecallLine>(lines.Count);

        foreach (var line in lines)
        {
            if (isNarrator || !string.Equals(line.Kind, "speech", StringComparison.Ordinal))
            {
                visible.Add(line);
                continue;
            }

            // 发言按见证者过滤：Witnesses 空 = 公开，否则必须在名单里。
            if (line.Witnesses.Count == 0 || line.Witnesses.Contains(participantId, StringComparer.Ordinal))
            {
                visible.Add(line);
            }
        }

        if (visible.Count == 0)
        {
            return "没有符合条件的记录（换个关键词，或放宽 since / kinds / author 试试）。"
                + "记录只包含本场已经发生的事；要查规则原文请用 lookup_rule。";
        }

        var builder = new StringBuilder();

        foreach (var line in visible)
        {
            builder.Append('\n').Append("- ").Append(DisplayLine(line));
        }

        var text = builder.ToString().TrimStart('\n');

        // 总长上限：回忆是"接不上时看一眼"，不是把整本记录塞回上下文。
        const int maxChars = 2000;

        if (text.Length > maxChars)
        {
            text = text[..maxChars] + "\n[truncated]";
        }

        return $"（只读回忆，共 {visible.Count} 条；它不改变任何状态）\n" + text;
    }

    /// <summary>把作者 id 换回名字（回忆给人看，不该出现 id）。</summary>
    private string DisplayLine(StoryRecallLine line)
    {
        var author = line.Author;

        if (_registry.Find(author) is { } participant)
        {
            author = participant.Name;
        }

        return line.Revision > 0
            ? $"第 {line.Revision} 幕 · {author}：{line.Text}"
            : $"{author}：{line.Text}";
    }

    /// <summary>
    /// 「只写不推进」时给模型的固定回执（§12）：<b>说清为什么被拒、该做什么</b>，
    /// 否则它会以为参数写错了，换个说法反复调。
    /// </summary>
    public const string NoAutoBeatMessage =
        "当前不允许说书人自主推进（narratorBeatsPerTurn = 0）：不要点名角色，也不要自主描写；"
        + "只写你自己的叙述，等导演指示。";

    /// <summary>DM 提醒的正文上限（§3.3：提醒是旁注，不该塞进来一屏设定）。</summary>
    public const int DmNoteMaxChars = 500;

    /// <summary>允许的严重度。</summary>
    private static readonly string[] DmNoteSeverities = ["info", "warn"];

    /// <summary>
    /// 导演投一条提醒到观察面（§3.3，B5）。<b>只登记、不等待、不改剧情</b>：
    /// 不进 awaiting（剧情照跑）、不进说书人上下文（否则它会照着自我修正）、不动 revision。
    /// <para>
    /// 三种回应：「知道了」只 ack；「记进设定」把这条追加进 L3 摘要（长期生效，前缀会因此变一次——
    /// 这是用户明确选择的语义）；「忽略」什么都不做。超时同「知道了」，<b>绝不自己改世界</b>。
    /// </para>
    /// </summary>
    public DmNoteResult AddDmNote(string? severity, string? text)
    {
        var level = string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim().ToLowerInvariant();
        var body = (text ?? string.Empty).Trim();

        if (!DmNoteSeverities.Contains(level, StringComparer.Ordinal))
        {
            return new DmNoteResult(false, null, $"severity 只能是 {string.Join(" / ", DmNoteSeverities)}。");
        }

        if (body.Length == 0)
        {
            return new DmNoteResult(false, null, "text 不能为空。");
        }

        if (body.Length > DmNoteMaxChars)
        {
            return new DmNoteResult(false, null, $"text 最多 {DmNoteMaxChars} 字符（当前 {body.Length}）。");
        }

        const string questionId = "dm-note";

        var questions = new[]
        {
            new InteractionQuestion(
                questionId,
                $"[{(level == "warn" ? "警告" : "提示")}] {body}",
                [
                    new InteractionOption("ack", "知道了"),
                    new InteractionOption("apply", "记进设定"),
                    new InteractionOption("ignore", "忽略"),
                ],
                DefaultOptionId: "ack"),
        };

        // 默认 = 知道了（无副作用）：超时只能是"没人看到"，绝不能自己改世界（§3.3）。
        // 收口回调由 registry **同步**调用：答复返回时「记进设定」已经写进摘要，
        // 不会出现"点了按钮但状态还没落地"的窗口。
        var note = RegisterSideDecision(
            PendingDecisionKind.DmNote,
            questions,
            [new InteractionAnswer(questionId, ["ack"])],
            _options.Interaction.TimeoutSeconds,
            (decision, outcome) => OnDmNoteResolved(decision, outcome, body, level));

        // 让第五列 / 观察页立刻看到这张卡片（不等下一轮 state 推送）。
        PublishState();

        return new DmNoteResult(true, note.Id, null);
    }

    /// <summary>DM 提醒的收口：只有「记进设定」会改东西（L3 摘要），其余一律什么都不做。</summary>
    private void OnDmNoteResolved(PendingDecision decision, InteractionOutcome outcome, string text, string severity)
    {
        var selected = outcome.Answers
            .FirstOrDefault(answer => string.Equals(answer.QuestionId, "dm-note", StringComparison.Ordinal))
            ?.Selected.FirstOrDefault();

        if (!outcome.TimedOut && string.Equals(selected, "apply", StringComparison.Ordinal))
        {
            var line = $"- 导演{(severity == "warn" ? "警告" : "提醒")}（第 {_timeline.Revision + 1} 幕）：{text}";

            lock (Gate)
            {
                _summary.Append('\n').Append(line);
            }

            PersistContextSummary();
        }

        // 卡片消失：OpenDecisions 不再包含它，重推一次 state 让第五列与观察页同步。
        PublishState();
    }

    /// <summary>
    /// 后台观察视图（§3.1）：只读，按幕升序。给 <c>GET /api/roleplay/backstage</c> 与页面用。
    /// </summary>
    public object BackstageView(long since, int limit) => new
    {
        storyId = _options.StoryId,
        revision = _timeline.Revision,
        events = _backstage.View(since, limit).Select(entry => new
        {
            revision = entry.Rev,
            at = entry.At,
            kind = entry.Kind,
            who = entry.Who,
            name = entry.Name,
            args = entry.Args,
            result = entry.Result,
            text = entry.Text,
        }).ToArray(),
    };

    /// <summary>后台记录的条数（探针 / 诊断）。</summary>
    internal int BackstageCount => _backstage.Count;

    /// <summary>后台记录的落盘路径（探针 / 诊断）。</summary>
    internal string? BackstagePath => _backstage.Path;

    /// <summary>L2 的快照（探针验"只追加、不滚动"）。</summary>
    internal IReadOnlyList<HistoryLine> HistorySnapshot() => SnapshotHistory();

    /// <summary>L3 摘要全文（探针验"摘要出现且内容合理"）。</summary>
    internal string SummaryText
    {
        get
        {
            lock (Gate)
            {
                return _summary.ToString();
            }
        }
    }

    /// <summary>
    /// 这一幕的见证者：此刻在场的角色（含说书人）。不在场的人不该记得这一幕——
    /// 长程记忆的"按角色视角"就是靠它（§11.4-3）。
    /// </summary>
    /// <summary>
    /// 说书人复盘这一轮：给它一次判断"现在最该做什么"的机会（用工具表达），再执行它登记的意图。
    /// <c>narratorBeatsPerTurn = 0</c> 时整个跳过——退回"只有导演指令才推进"。
    /// </summary>
    private async Task<IReadOnlyList<StoryLine>> RunNarratorReviewAsync(
        string directorInput,
        IReadOnlyList<StoryLine> spoken,
        CancellationToken cancellationToken)
    {
        // §12：复盘**始终执行**（说书人仍要看一眼本轮、仍可提议 / 提问 / 建议收束）；
        // 是否"自己点名 / 自己描写"由 RunPendingIntentsAsync 的预算决定（beats = 0 时它不执行）。
        if (ResolveNarrator() is not { } narrator)
        {
            return [];
        }

        var lines = new List<StoryLine>();
        var task = Narrator.BuildReviewTask(directorInput, SpokenContext(spoken), _timeline.Revision + 1);

        if (await RunActorTurnAsync(narrator, task, cancellationToken).ConfigureAwait(false) is { } closing)
        {
            lines.Add(closing);
        }

        lines.AddRange(await RunPendingIntentsAsync(cancellationToken).ConfigureAwait(false));
        return lines;
    }

    /// <summary>
    /// 执行说书人登记的意图（点名 / 描写），最多 <c>narratorBeatsPerTurn</c> 个。
    /// "建议收束"不进这里：它在工具内已经把自己停进 <c>Paused</c> 了，这一轮剩下的意图不该再跑。
    /// </summary>
    private async Task<IReadOnlyList<StoryLine>> RunPendingIntentsAsync(CancellationToken cancellationToken)
    {
        var lines = new List<StoryLine>();
        var budget = Math.Max(0, _options.NarratorBeatsPerTurn);

        while (budget-- > 0 && Status == MeetingStatus.Running)
        {
            NarratorIntent? intent;

            lock (Gate)
            {
                if (_narratorIntents.Count > 0)
                {
                    intent = _narratorIntents[0];
                    _narratorIntents.RemoveAt(0);
                }
                else
                {
                    intent = null;
                }
            }

            if (intent is null)
            {
                break;
            }

            switch (intent.Kind)
            {
                case "nudge" when intent.Target is { } target && _registry.Find(target) is { } member:
                    if (await RunActorTurnAsync(member, intent.Text, cancellationToken).ConfigureAwait(false) is { } nudged)
                    {
                        lines.Add(nudged);
                    }

                    break;

                case "beat" when ResolveNarrator() is { } narrator:
                    var beatTask = Narrator.BuildBeatTask(intent.Text);
                    if (await RunActorTurnAsync(narrator, beatTask, cancellationToken).ConfigureAwait(false) is { } beat)
                    {
                        lines.Add(beat);
                    }

                    break;
            }
        }

        return lines;
    }

    /// <summary>这一轮说了什么（给复盘看）：显示名 + 正文首段。</summary>
    private IReadOnlyList<string> SpokenContext(IReadOnlyList<StoryLine> spoken) =>
        spoken.Select(line =>
        {
            var condensed = line.Text.Length > 200 ? line.Text[..200] + "…" : line.Text;
            return $"{_registry.Find(line.Author)?.Name ?? line.Author}：{condensed}";
        }).ToArray();

    private IReadOnlyList<string> SceneWitnesses() => _world.Current.Present;

    /// <summary>记忆落盘路径：与剧情同目录的 memory.jsonl（没配 storyPath 就只留在内存里）。</summary>
    private static string? MemoryPath(string? storyPath) =>
        storyPath is null
            ? null
            : Path.Combine(Path.GetDirectoryName(storyPath) ?? ".", "memory.jsonl");

    private static IReadOnlyList<object> Project(IReadOnlyList<StoryLine> lines) =>
        lines.Select(line => (object)new { author = line.Author, text = line.Text }).ToArray();

    /// <summary>世界状态提议的问题 id（答复按它取值）。</summary>
    public const string WorldStateQuestionId = "world_state";

    /// <summary>提议的两个选项：同意即落盘，不改则保持原样。</summary>
    public const string ApplyOptionId = "apply";

    /// <summary>迷雾提议的问题 id（与 world-state 分开：语义不同，超时口径也不同）。</summary>
    public const string FogRevealQuestionId = "fog-reveal";

    public const string RejectOptionId = "reject";

    private static string Join(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second
        : string.IsNullOrWhiteSpace(second) ? first
        : first + Environment.NewLine + Environment.NewLine + second;

    /// <summary>说书人登记的控制面意图（点名 / 描写一拍 / 建议收束）。</summary>
    private sealed record NarratorIntent(string Kind, string? Target, string Text);
}
