using MeetingRoom.Core.Interaction;

namespace MeetingRoom.Core.Config;

/// <summary>
/// exe 同级 meetingroom.json 的完整结构（设计文档 §10.5）。
/// 所有属性都带内置默认值：配置文件缺项即用默认值补齐，缺整个文件也能跑。
/// </summary>
public sealed record MeetingRoomOptions
{
    public ServerOptions Server { get; init; } = new();

    public ProviderOptions Provider { get; init; } = new();

    public LimitsOptions Limits { get; init; } = new();

    public SpeechOptions Speech { get; init; } = new();

    public SummariesOptions Summaries { get; init; } = new();

    public ContextOptions Context { get; init; } = new();

    /// <summary>
    /// 交互模式（阶段 9）：主持人提问与说书人提议共用同一份配置（设计文档 §14.5）。
    /// <b>批 4 与阶段 9 是同一种机制</b>，所以配置也只有一份。
    /// </summary>
    public InteractionOptions Interaction { get; init; } = new();

    /// <summary>
    /// 图片生成模块（独立附加功能，设计方案 §八）：独立窗口、用户手动触发、结果另存。
    /// 它不进任何与会者的上下文，也不经过编排层 —— 现有功能一行行为都不变。
    /// </summary>
    public ImageOptions Images { get; init; } = new();

    /// <summary>
    /// 枭熊（Owlbear Rodeo）只读地图接入（《枭熊地图接入实现方案》V1）：
    /// 由本机的 obr_bridge 提供纯 HTTP 快照，编排层每轮拉一次、注入一段"你看到什么"。
    /// 默认关闭——没有桥的时候一行行为都不变。
    /// </summary>
    public VttOptions Vtt { get; init; } = new();

    /// <summary>
    /// 地图视觉（M8b，《地图视野与特殊区域设计》§5.3）：把合成视图作为图像输入发给模型。
    /// 默认关闭——关掉时不发图、不改上下文结构、前缀指纹一个字节都不变。
    /// </summary>
    public VisionOptions Vision { get; init; } = new();

    /// <summary>
    /// 叙事模式的配置段（《叙事模式后台与上下文设计》§1.3）：目前只有前台上下文那一组。
    /// </summary>
    public RoleplaySectionOptions Roleplay { get; init; } = new();

    /// <summary>
    /// 规则数据包（战斗 / 检定 / 法术位 / 规则查询）：路径指向 <c>rules-data.json</c>。
    /// 默认 <c>data/rules/rules-data.json</c>（相对 exe，构建时由 Content 拷到 exe 同级）。
    /// 文件不在 = 不加载——此时战斗、检定与规则查询都会明确回"规则包未加载"，而不是假装成功。
    /// 覆盖顺序：配置 &gt; <c>--rules</c> / 环境变量 <c>MEETINGROOM_RULES_DATA</c>。
    /// </summary>
    public RulesOptions Rules { get; init; } = new();
}

/// <summary>
/// 叙事模式配置段：<c>roleplay.context.*</c> 那一层。
/// </summary>
public sealed record RoleplaySectionOptions
{
    /// <summary>前台三层上下文的参数（默认值与设计文档 §1.3 一致）。</summary>
    public MeetingRoom.Core.Roleplay.RoleplayContextOptions Context { get; init; } = new();

    /// <summary>
    /// 说书人每轮复盘后最多再执行几拍自主动作（点名 / 描写），默认 1。
    /// <b>0 = 只写不推进</b>（§12）：复盘照常，但 <c>nudge_character</c> / <c>idle_beat</c> 会被拒。
    /// </summary>
    public int NarratorBeatsPerTurn { get; init; } = 1;
}

/// <summary>
/// 规则数据包配置（战斗托管 C1–C4 / D1–D3 / 规则查询 §27）：<b>一个入口</b>——配置优先，环境变量只做兜底。
/// </summary>
public sealed record RulesOptions
{
    /// <summary>
    /// <c>rules-data.json</c> 的路径。默认 <c>data/rules/rules-data.json</c>；
    /// <b>相对路径一律按安装目录（<c>PathLayout.InstallDir</c>）解析</b>，不按当前工作目录——
    /// 从别处启动 Host 时 cwd 不是 exe 目录，按 cwd 找会"静默找不到包"。空 = 不加载。
    /// </summary>
    public string DataPath { get; init; } = "data/rules/rules-data.json";
}

/// <summary>
/// 地图接入配置（《枭熊地图接入实现方案》§3 V1）。桥挂了 / 超时 / JSON 异常时一律降级为
/// "本轮没有地图段落"，绝不中断回合——所以这里只约束"想用地图时"的参数合法性。
/// </summary>
public sealed record VttOptions
{
    /// <summary>关掉时既不拉取也不注入（默认）。</summary>
    public bool Enabled { get; init; }

    /// <summary>桥的纯 HTTP 快照地址（本机回环，不走 HTTPS，不碰自签证书）。</summary>
    public string StateUrl { get; init; } = "http://127.0.0.1:9877/state";

    /// <summary>单次拉取的超时（秒）——它是回合里唯一肯等的上限，不能大。</summary>
    public int TimeoutSeconds { get; init; } = 2;

    /// <summary>快照新鲜期（秒）：这段时间内复用上一次的成功结果，不再打桥。</summary>
    public int StaleSeconds { get; init; } = 10;

    /// <summary>角色视角注入的半径（尺）：只给这个范围内的目标，避免"一眼看全图"。</summary>
    public int InjectRadiusFeet { get; init; } = 60;

    /// <summary>
    /// 是否把 DM 隐藏的 token 也交给模型。<b>默认 false</b>：枭熊的显示开关是唯一的信息闸门
    /// （扩展读不到迷雾），打开它等于允许剧透，只应在明确不需要保密的场合用。
    /// </summary>
    public bool IncludeHidden { get; init; }
}

/// <summary>
/// 图片生成配置。密钥**只存变量名**（值放进程环境，且必须在宿主启动前就存在 —— 《实施进度与待办》风险 2）。
/// </summary>
public sealed record ImageOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>图像端点的站点根。</summary>
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    /// <summary>图像端点路径：OpenRouter 是 <c>/images</c>，OpenAI 官方端点写 <c>/images/generations</c>。</summary>
    public string EndpointPath { get; init; } = "/images";

    /// <summary>image-capable 的模型 slug（以 <c>GET /api/v1/images/models</c> 的列为准）。</summary>
    public string Model { get; init; } = "google/gemini-3-pro-image";

    /// <summary>密钥所在的环境变量名（值不在配置里）。</summary>
    public string ApiKeyEnv { get; init; } = "OPENROUTER_API_KEY";

    /// <summary>0 = 不设客户端超时（仍受取消控制）。</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// 出网代理：<c>""</c> = 默认（环境变量 → WinINET/PAC，见设计方案 §七）；
    /// <c>off</c> = 直连；其余为显式代理 URL（如 <c>http://127.0.0.1:10828</c>）。
    /// </summary>
    public string Proxy { get; init; } = "";

    /// <summary>单次生成最多几张参考图。</summary>
    public int MaxReferenceImages { get; init; } = 3;

    /// <summary>单张参考图的字节上限。</summary>
    public long MaxReferenceBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>单张生成结果的字节上限。</summary>
    public long MaxOutputBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// 下面四个出图参数的约定：<b>空串 = 不发送这个参数</b>，由上游用它自己的默认值。
    /// 为什么这么定：不同模型支持的参数集不一样 —— FLUX.3 就明确拒绝 <c>output_format</c> 与 <c>quality</c>
    /// （上游原话：<c>does not support: output_format, quality</c>），一律发过去会让本来能用的模型也报 400。
    /// </summary>
    public string DefaultAspectRatio { get; init; } = "1:1";

    /// <summary>512 / 768 / 1K / 1.5K / 2K / 4K —— 各模型接受的范围不同，空 = 不发。</summary>
    public string DefaultResolution { get; init; } = "1K";

    /// <summary>auto / low / medium / high；空 = 不发（FLUX 系不支持）。</summary>
    public string DefaultQuality { get; init; } = "";

    /// <summary>png / jpeg / webp；空 = 不发（FLUX 系不支持，由上游定）。</summary>
    public string OutputFormat { get; init; } = "";

    /// <summary><c>n</c> 的上限（单图 provider 会拒绝 <c>n &gt; 1</c>）。</summary>
    public int MaxCount { get; init; } = 1;
}

public sealed record ServerOptions
{
    /// <summary>监听端口；0 表示动态分配（实现步骤 2.5）。</summary>
    public int Port { get; init; } = 8787;

    /// <summary>监听地址，必须是回环地址（设计文档 §10.4）。</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>SSE 心跳间隔（秒），见设计文档 §8.4。</summary>
    public int HeartbeatSeconds { get; init; } = 15;

    /// <summary>
    /// 启动后是否用系统默认浏览器打开界面。默认 true（一条命令就能用）；
    /// 用 WebView2 等外部浏览器调试时置 false 或传 <c>--no-browser</c>，避免多弹一个窗口。
    /// </summary>
    public bool OpenBrowser { get; init; } = true;

    /// <summary>
    /// 启动窗口形态（F5）：<c>app</c> = 用 Edge / Chrome 的 <c>--app</c> 模式无地址栏、直接全屏；
    /// <c>browser</c> = 退回系统默认浏览器（旧行为）。<b>找不到 Edge/Chrome 时一律默默回退</b>，
    /// 不因为"想要全屏"让程序起不来。
    /// </summary>
    public string LaunchMode { get; init; } = "app";
}

public sealed record ProviderOptions
{
    public string Name { get; init; } = "deepseek";

    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    public string Model { get; init; } = "deepseek-chat";

    /// <summary>密钥只放环境变量，这里存变量名（设计文档 §10.5 规则 4）。</summary>
    public string ApiKeyEnv { get; init; } = "DEEPSEEK_API_KEY";
}

public sealed record LimitsOptions
{
    public int MaxStepsPerTurn { get; init; } = 12;

    public int MaxConcurrentTurns { get; init; } = 4;

    public int SteerMergeWindowMs { get; init; } = 2000;

    /// <summary>单场会议的轮次上限：防止"推进轮次 → 再评估 → 再推进"无限循环。</summary>
    public int MaxRounds { get; init; } = 5;
}

/// <summary>发言输出的约束（设计文档 §9.1 的讨论区：一次发言就是讨论区的一条消息）。</summary>
public sealed record SpeechOptions
{
    /// <summary>
    /// 单次发言的字数上限。<b>0 表示不限制</b>。
    /// 会议的价值在于多轮轮流交换观点，而不是一条超长发言——所以默认压到 300 字。
    /// </summary>
    public int MaxCharsPerSpeech { get; init; } = 300;
}

public sealed record SummariesOptions
{
    public int MaterialSummaryFallbackChars { get; init; } = 300;

    public int ThoughtSummaryMaxChars { get; init; } = 500;

    public string ThoughtSummaryStyle { get; init; } = "paper-abstract";
}

public sealed record ContextOptions
{
    public int AlwaysMaterialMaxCount { get; init; } = 12;

    public int TriggeredTokenBudget { get; init; } = 2000;

    public int TriggeredCooldownRounds { get; init; } = 10;
}

/// <summary>
/// 地图视觉配置（M8b）：<c>enabled=false</c>（默认）时行为与引入多模态前完全一致。
/// </summary>
public sealed record VisionOptions
{
    /// <summary>关掉时既不取图也不附进上下文。</summary>
    public bool Enabled { get; init; }

    /// <summary>图的长边像素（256–2048）；默认 768，与 <c>MapViewRenderer</c> 的默认一致。</summary>
    public int MaxPx { get; init; } = 768;

    /// <summary>文档没变时复用上一轮的图引用，不重复取（默认 true）。</summary>
    public bool OnlyOnChange { get; init; } = true;
}
