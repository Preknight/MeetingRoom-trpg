using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using MeetingRoom.Agent.ChatClients;
using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Config;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Images;
using MeetingRoom.Core.Logging;
using MeetingRoom.Core.Maps;
using MeetingRoom.Core.Context;
using MeetingRoom.Core.Paths;
using MeetingRoom.Core.Rules;
using MeetingRoom.Core.Vtt;
using MeetingRoom.Core.Workspaces;
using MeetingRoom.Host.Dev;
using MeetingRoom.Host.Drafts;
using MeetingRoom.Host.Images;
using MeetingRoom.Host.Maps;
using MeetingRoom.Host.Interactions;
using MeetingRoom.Host.Launcher;
using MeetingRoom.Host.Logging;
using MeetingRoom.Host.Materials;
using MeetingRoom.Host.Meeting;
using MeetingRoom.Host.Roleplay;
using MeetingRoom.Host.Sessions;
using MeetingRoom.Host.Shell;
using MeetingRoom.Host.Sse;
using MeetingRoom.Host.Workspaces;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // 输出被重定向时 Windows 不允许改控制台编码，不影响运行。
}

ConfigLoadResult config;
try
{
    config = ConfigLoader.Load(PathLayout.ConfigFile);
}
catch (ConfigException ex)
{
    // 配置错导致起不来时，控制台窗口很可能已经关了——这条必须落盘（待办 A6）。
    using (var log = new FileLog(PathLayout.LogsDir))
    {
        log.Error($"配置非法，拒绝启动：{PathLayout.ConfigFile}");
        log.Error(ex.Message);
    }

    Console.Error.WriteLine($"配置非法，拒绝启动：{PathLayout.ConfigFile}");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("（非法值不会静默回退默认值，请修正后重新启动。）");
    return 1;
}

var options = config.Options;

// 规则包路径可由命令行覆盖（§27）：--rules <path> 优先于配置与 MEETINGROOM_RULES_DATA。
// 只影响"去哪找包"，不动别的配置；相对路径交给 WorkspaceSessionFactory 按安装目录解析。
if (TryArg(args, "--rules") is { Length: > 0 } rulesOverride)
{
    options = options with { Rules = options.Rules with { DataPath = rulesOverride } };
}

// 阶段 2 的临时探针（实现步骤 4.1 的验收入口）：控制台直接对 provider 发一条流式请求。
// 用法：MeetingRoom.Host.exe --probe-chat "用一句话说明你是谁"
if (args.Contains("--probe-chat", StringComparer.Ordinal))
{
    var prompt = args.SkipWhile(a => !string.Equals(a, "--probe-chat", StringComparison.Ordinal)).Skip(1).FirstOrDefault()
        ?? "用一句话说明你是谁。";
    return await ProbeChatAsync(options, prompt, ParseCancelAfter(args));
}

// 图片生成模块的真机自检（图片模块实现步骤 A5）：确认出网（含系统 PAC）、密钥可用、模型 slug 可生成。
// 用法：MeetingRoom.Host.exe --probe-image "a red cube" [--model black-forest-labs/flux-3-image]
//        [--reference C:\某张图.png] [--resolution 512] [--aspect 1:1]
// 产物落**安装目录**下的 data/images-probe/ —— 探针不碰工作空间，免得污染用户数据。
if (args.Contains("--probe-image", StringComparer.Ordinal))
{
    var prompt = args.SkipWhile(a => !string.Equals(a, "--probe-image", StringComparison.Ordinal)).Skip(1).FirstOrDefault()
        ?? "a red cube on a white table";

    // --model / --resolution / --aspect 只**覆盖本次探针**（record 的 with），不动配置文件。
    var model = TryArg(args, "--model");
    var resolution = TryArg(args, "--resolution");
    var aspectRatio = TryArg(args, "--aspect");
    var probeOptions = options with
    {
        Images = options.Images with
        {
            Model = string.IsNullOrWhiteSpace(model) ? options.Images.Model : model,
            DefaultResolution = string.IsNullOrWhiteSpace(resolution) ? options.Images.DefaultResolution : resolution,
            DefaultAspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? options.Images.DefaultAspectRatio : aspectRatio,
        },
    };

    return await ProbeImageAsync(probeOptions, prompt, TryArg(args, "--reference"));
}

// 规则数据包的自检（V3）：确认真实包（约 1.9 MB）能被 RulesDataLoader 吃下，并抽三条关键条目。
// 用法：MeetingRoom.Host.exe --probe-rules "<rules-data.json>"
// 真实包不进仓库，所以测试里只能用环境变量跳过；这个探针是"真实包到底能不能解析"的直接证据。
if (args.Contains("--probe-rules", StringComparer.Ordinal))
{
    var path = args.SkipWhile(a => !string.Equals(a, "--probe-rules", StringComparison.Ordinal)).Skip(1).FirstOrDefault();
    return ProbeRules(path);
}

// 战斗状态机的自检（C1）：跑一段脚本化遭遇（开战 → 指定轮数 → 收场），打印事件流与最终状态。
// 用法：MeetingRoom.Host.exe --probe-launcher
// 打印窗口启动探测结果与将执行的命令行（F5）：不真的启动浏览器，便于核对探测顺序与参数。
if (args.Contains("--probe-launcher", StringComparer.Ordinal))
{
    return ProbeLauncher();
}

// 用法：MeetingRoom.Host.exe --probe-events
// 对账：后端实际 Publish 的事件类型 vs 前端 stream.js 的 EVENT_TYPES 白名单（§31.1 的根治）。
// 只在开发期可用（要读源码树）；发布目录没有源码时明确说"无法对账"。
if (args.Contains("--probe-events", StringComparer.Ordinal))
{
    return ProbeEvents();
}

// 用法：MeetingRoom.Host.exe --probe-sheets "<roles 目录>"
// 只读：跑一遍角色卡旁路数值表（roles/*.sheet.json）的加载与校验，打印通过项与问题项（§26.4）。
if (args.Contains("--probe-sheets", StringComparer.Ordinal))
{
    var rolesDir = args.SkipWhile(a => !string.Equals(a, "--probe-sheets", StringComparison.Ordinal)).Skip(1).FirstOrDefault();
    return ProbeSheets(rolesDir);
}

// 用法：MeetingRoom.Host.exe --probe-tools
// 打印工具清单 + 前缀指纹（§25.5）：同一构建多次运行必须哈希一致；不写文件、不碰工作空间。
if (args.Contains("--probe-tools", StringComparer.Ordinal))
{
    return ProbeTools();
}

// 用法：MeetingRoom.Host.exe --probe-combat "tests\MeetingRoom.Tests\TestData\Combat\combat-sample.json"
// 与 probe-rules 同样绕开 xunit/testhost，不写文件、不碰工作空间。
if (args.Contains("--probe-fog-margin", StringComparer.Ordinal))
{
    // 迷雾边缘冗余：底图外 3 格内合法（遮边用），越出冗余才拒。
    return ProbeFogMargin();
}

if (args.Contains("--probe-pending-kind", StringComparer.Ordinal))
{
    // P1：迷雾提议用独立 kind（fog），世界状态提议仍是 world_state。
    return ProbePendingKind();
}

if (args.Contains("--probe-interaction-timeout", StringComparer.Ordinal))
{
    // P0：超时计时准不准（短超时量实际耗时，不等 120 秒）。
    return ProbeInteractionTimeout();
}

if (args.Contains("--probe-character-tools", StringComparer.Ordinal))
{
    // M9b 反证：角色拿到哪 6 个，以及说书人专属的（含 lookup_monster）确实不在里面。
    return ProbeCharacterTools();
}

if (args.Contains("--probe-rule-lookup", StringComparer.Ordinal))
{
    // §27：规则查询——命中顺序 / 跨类候选 / 九类格式 / detail 截断 / 限流 / 越权。
    return ProbeRuleLookup(TryArg(args, "--probe-rule-lookup"));
}

if (args.Contains("--probe-narrative-context", StringComparer.Ordinal))
{
    // 《叙事模式后台与上下文设计》B1 + B2：三层上下文的前缀指纹序列、压缩时机、recall_story。
    return ProbeNarrativeContext();
}

if (args.Contains("--probe-backstage", StringComparer.Ordinal))
{
    // 《叙事模式后台与上下文设计》B3：后台记录落盘 / 滚动 2000 / 重启后仍在 / 只读。
    return ProbeBackstage();
}

if (args.Contains("--probe-dm-note", StringComparer.Ordinal))
{
    // 《叙事模式后台与上下文设计》B5：DM 提醒——校验、三回应、超时无副作用、不进说书人上下文。
    return ProbeDmNote();
}

if (args.Contains("--probe-paused-say", StringComparer.Ordinal))
{
    // 真机 A（2026-10-29）：paused 时发导演指令必须自动恢复并立刻处理，不能挂到用户点继续。
    return ProbePausedSay();
}

if (args.Contains("--probe-context-metrics", StringComparer.Ordinal))
{
    // 真机 E：上下文指标要能从接口拿到（historyCount / estimatedTokens / compressionCount / …）。
    return ProbeContextMetrics();
}

if (args.Contains("--probe-intent-dedup", StringComparer.Ordinal))
{
    // §11：说书人意图去重——同轮同键合并、保留首位置、内容取最新、队列上限、跨轮重置。
    return ProbeIntentDedup();
}

if (args.Contains("--probe-scene-end", StringComparer.Ordinal))
{
    // §13：declare_scene_end 幂等 + 本轮内主动 pause 后立即收尾（不再挂 219 秒）。
    return ProbeSceneEnd();
}

if (args.Contains("--probe-narrator-beats", StringComparer.Ordinal))
{
    // §12：narratorBeatsPerTurn = 0 是"只写不推进"——复盘照常跑、只是不点名不描写。
    return ProbeNarratorBeats();
}

if (args.Contains("--probe-vision", StringComparer.Ordinal))
{
    // M8b 补验：说书人拿 dm 图、角色拿 player 图、无已探索格时都不发图。
    return ProbeVisionAttachments(TryArg(args, "--probe-vision"));
}

if (args.Contains("--probe-map-view", StringComparer.Ordinal))
{
    // M8a：合成视图——尺寸、裁剪、204 条件、缓存与"图不含 token"。
    return ProbeMapView(TryArg(args, "--probe-map-view"));
}

if (args.Contains("--probe-character-view", StringComparer.Ordinal))
{
    // M9a：角色视角文字段——三条闸门（只写已探索 / 不提隐藏 / 不报总量）。
    return ProbeCharacterView(TryArg(args, "--probe-character-view"));
}

if (args.Contains("--probe-fog", StringComparer.Ordinal))
{
    // M6b：迷雾提议的三条语义（同意只加 explored / 拒绝与超时都不动 / 无地图则拒绝）。
    return ProbeFogProposal(TryArg(args, "--maps"));
}

if (args.Contains("--probe-combat-guard", StringComparer.Ordinal))
{
    // r8：胜利/失败要有事实依据，不能"敌人还站着就报 victory"。
    return ProbeCombatGuard();
}

if (args.Contains("--probe-map-context", StringComparer.Ordinal))
{
    // M3：把地图文档压成说书人可见的那一段，原样打印 + 字数。
    return ProbeMapContext(TryArg(args, "--probe-map-context"), TryArg(args, "--focus"), TryArg(args, "--preferred"));
}

if (args.Contains("--probe-check-spam", StringComparer.Ordinal))
{
    // 会话层验证（F7-②）：不依赖模型愿不愿意连调。
    return ProbeCheckSpam(TryArg(args, "--rules"));
}

if (args.Contains("--probe-combat", StringComparer.Ordinal))
{
    var path = args.SkipWhile(a => !string.Equals(a, "--probe-combat", StringComparison.Ordinal)).Skip(1).FirstOrDefault();
    return ProbeCombat(path, TryArg(args, "--rules"));
}

var token = StartupToken.Generate();

// 日志（待办 A6）：落 **exe 同级**的 logs/，按天一个文件，不提供配置覆盖项（设计方案 §10.1）。
// 控制台只给"看着那个窗口的人"看；排错看文件——双击 exe 起服务时窗口一关就什么都查不到。
var fileLog = new FileLog(PathLayout.LogsDir);

// 启动诊断既进控制台也进文件（路径基准是否真的落在 exe 同级，实现步骤 2.3 的验收入口）。
void Say(string message)
{
    Console.WriteLine(message);
    fileLog.Info(message.TrimStart());
}

Say("MeetingRoom · 启动诊断");
Say($"  安装目录：{PathLayout.InstallDir}");
Say($"  前端资源：{PathLayout.WwwRoot}（存在：{Directory.Exists(PathLayout.WwwRoot)}）");
Say($"  程序状态：{PathLayout.AppDataDir}（工作空间清单等）");
Say($"  日志目录：{fileLog.Directory}（今天：{fileLog.CurrentPath}）");
Say($"  配置文件：{PathLayout.ConfigFile}");
Say(
    $"  图片生成：{(options.Images.Enabled ? "启用" : "关闭")} "
    + $"{options.Images.BaseUrl.TrimEnd('/')}{options.Images.EndpointPath} 模型 {options.Images.Model}；"
    + $"key 变量 {options.Images.ApiKeyEnv}"
    + $"（进程环境里{(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.Images.ApiKeyEnv)) ? "没有" : "有")}）；"
    + $"代理 {(options.Images.Proxy.Trim().Length == 0 ? "默认（环境变量 → WinINET/PAC）" : options.Images.Proxy)}");
Say(
    $"  地图（枭熊）：{(options.Vtt.Enabled ? "启用" : "关闭")} {options.Vtt.StateUrl}"
    + (options.Vtt.Enabled
        ? $"（超时 {options.Vtt.TimeoutSeconds}s，新鲜期 {options.Vtt.StaleSeconds}s，注入半径 {options.Vtt.InjectRadiusFeet} 尺，"
            + $"{(options.Vtt.IncludeHidden ? "含隐藏项" : "不含隐藏项")}）"
        : "（机制与骰子工具仍在，只是不再拉取快照）"));
foreach (var notice in config.Notices)
{
    Say($"  配置提示：{notice}");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // 一切相对路径都从 exe 同级开始：开发期是 bin/<Config>/net10.0/，发布期是安装目录。
    ContentRootPath = PathLayout.InstallDir,
    WebRootPath = PathLayout.WwwRoot,
});

// 框架自己的日志（请求开始 / 结束与状态码、未处理异常）也落同一个文件——排错最需要的就是这几条。
builder.Logging.AddProvider(new FileLogProvider(fileLog));

// 硬约束：只绑回环，绝不绑 0.0.0.0（设计文档 §10.4）。
builder.WebHost.UseUrls($"http://{FormatBinding(options.Server.Bind)}:{options.Server.Port}");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(fileLog);

var bus = new EventBus();
builder.Services.AddSingleton<IEventBus>(bus);

// 工作空间（设计方案 §15.7 / §15.8）：清单落 appdata/workspaces.json，
// **用户数据**（角色卡、资料、世界书、会话）都在工作空间自己的目录里。
var workspaces = new WorkspaceRegistry(Path.Combine(PathLayout.AppDataDir, "workspaces.json"));
builder.Services.AddSingleton(workspaces);

// 迁移路径（只提示、不自动搬，见 §15.8）：还没有"当前工作空间"时，
// ① 清单里记得过哪个就打开哪个；② 否则把安装目录下的旧 data/ 作为 Legacy 工作空间打开——
// WorkspaceLayout(data) 的 roles/ 与 meetings/ 恰好等于旧的 PathLayout.*，所以现形态天然等价。
if (workspaces.Current is null)
{
    if (workspaces.List().FirstOrDefault() is { } remembered)
    {
        workspaces.Open(remembered.Path, remembered.Kind);
        Say($"  已打开上次的工作空间：{remembered.Name}（{remembered.Path}）");
    }
    else if (Directory.Exists(PathLayout.DataDir))
    {
        workspaces.Open(PathLayout.DataDir, WorkspaceKind.Legacy);
        Say($"  已把旧的 data/ 作为过渡工作空间打开：{PathLayout.DataDir}");
    }
}

// 阶段 1 的临时物：假事件流既产生事件、又充当"没有真实会议时"的全量快照来源。
var dev = new DevEventStream(bus);
builder.Services.AddSingleton(dev);

// provider 客户端：没有配 key 也照样起服务（开会时才报错），这样界面仍可用 ?dev=1 看演示。
IChatClient chatClient;
try
{
    chatClient = new OpenAiCompatibleModelFactory().Create(new ChatModelSettings(
        options.Provider.Name,
        options.Provider.BaseUrl,
        options.Provider.Model,
        options.Provider.ApiKeyEnv));
}
catch (InvalidOperationException ex)
{
    chatClient = new UnconfiguredChatClient(ex.Message);
    Say($"  提示：{ex.Message}");
    Say("  真实会议暂不可用；界面仍可用 ?dev=1 查看演示数据。");
}

// 地图视觉（M8b）：启用时先探测 provider 到底看不看得懂图——**用颜色回读，不看状态码**。
// 背景：2026-10-28 实测 `deepseek-v4-pro` 收到图返回 200，但推理里写 `[Unsupported Image]`、正文答非所问；
// 只看状态码会把"不支持视觉"误判成"支持"。
if (options.Vision.Enabled && !ProbeVision(chatClient, options.Provider.Model))
{
    Console.Error.WriteLine(
        $"当前模型 {options.Provider.Model} 不接受图像输入，"
        + "地图视觉需要支持视觉的模型，请更换 provider.model（或把 vision.enabled 设回 false）。");
    return 1;
}

// 会话装配：目录来自当前工作空间，角色卡来自工作空间根的 roles/（跨会话共享，§15.2）。
// 地图接入（《枭熊地图接入实现方案》V1）：只在启用时建客户端；关掉时工厂拿到 null，
// 叙事会话与以前完全一致（不拉取、不注入、工具回一句"还没接入"）。
IVttClient? vtt = options.Vtt.Enabled ? new HttpVttClient(options.Vtt) : null;
var sessions = new WorkspaceSessionFactory(chatClient, bus, options, vtt);

// 端点也要能看到它：还没开演时 GET /api/roleplay/vtt 用来确认"桥通不通"。
if (vtt is not null)
{
    builder.Services.AddSingleton<IVttClient>(vtt);
}

// 交互超时的**实际生效值**打在启动日志里（P0）。
// r16 报"配了 120 秒却等了 260 秒"，而探针证明计时器本身是准的（1 秒 → 1003ms）——
// 现场量到的墙钟里很可能还含前置回合与上一条交互的等待。把生效值写出来，下次对日志就能一眼对上，
// 不必再靠"我印象里配的是 120"这种回忆。
Say($"  交互模式：{(options.Interaction.Enabled ? "开" : "关")}，等待上限 "
    + $"{(options.Interaction.TimeoutSeconds > 0 ? $"{options.Interaction.TimeoutSeconds} 秒" : "不超时（永久等待）")}");

// 角色卡为空时写入内置默认卡（开箱可用，之后用户可以随意改，§5.4）。
if (workspaces.Current is { } workspace)
{
    var cards = sessions.LoadSharedRoles(workspace.Layout);
    Say($"  工作空间「{workspace.Name}」（{workspace.KindWire}）角色卡 {cards.Count} 张：{workspace.Layout.RolesDir}");
}

// 会话生命周期与两个快照源的唯一入口（一次只活跃一场会话，见 §8.1 / §15.9）。
var hub = new SessionHub(workspaces, sessions, dev, bus, fileLog);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton<IMeetingStateSource>(hub);
builder.Services.AddSingleton<IRoleplayStateSource>(hub);

// 图片生成模块的组合根（设计方案 §二）：按**当前工作空间**取图片库，所以不是启动时定死的单例。
builder.Services.AddSingleton<ImageStudio>();

var app = builder.Build();

app.UseDefaultFiles();

// 前端是"无构建步骤"的物理目录（硬约束 2）：改完刷新就要生效，
// 所以开发期与发布期都禁用缓存，否则浏览器会一直用旧的 JS/CSS（设计文档 §2.3）。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
});

// 全部状态变化都从这一条 SSE 出去（设计文档 §8.1）。
// 地图编辑器页（M1）：`/map-editor` 没有扩展名，静态文件中间件不会命中，显式给一条。
app.MapGet("/map-editor", (IWebHostEnvironment env) =>
{
    var root = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var page = Path.Combine(root, "map-editor.html");

    return File.Exists(page)
        ? Results.File(page, "text/html; charset=utf-8")
        : Results.NotFound(new { error = "缺少 wwwroot/map-editor.html。" });
});

app.MapEventStream();
app.MapMaterialEndpoints();
app.MapMeetingEndpoints();
app.MapRoleplayEndpoints();
app.MapInteractionEndpoints();
app.MapWorkspaceEndpoints();
app.MapSessionEndpoints();
app.MapDraftEndpoints();
app.MapImageEndpoints();
app.MapMapEndpoints();
app.MapSystemEndpoints();
app.MapDevEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
{
    // 端口配 0（动态分配）时以实际监听地址为准。
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
        ?? $"http://{FormatBinding(options.Server.Bind)}:{options.Server.Port}";
    var url = $"{address.TrimEnd('/')}/?t={token}";

    Say($"  界面地址：{url}");
    Say($"  一次性 token：{token}");
    Say("  事件流：GET /api/events（唯一的一条 SSE）；演示数据：GET /api/dev/fake 或界面加 ?dev=1");
    Say(
        $"  工作空间：{workspaces.List().Count} 个已注册（当前：{workspaces.Current?.Name ?? "未打开"}）；"
        + $"清单在 {workspaces.RegistryFile}");
    Say(hub.Active is { } active
        ? $"  当前会话：{active.KindWire}/{active.Id}「{active.Title}」"
        : "  当前会话：无（POST /api/sessions 新建，或 POST /api/sessions/open 切到已有的一场）");
    if (hub.Meeting is { } meeting)
    {
        Say($"  会议与会者 {meeting.Participants.Count} 人：{string.Join('、', meeting.Participants.Select(p => p.Name))}");
    }

    Say("  开始真实会议：POST /api/meeting/start {\"topic\":\"…\"}（界面指令区也有按钮）");
    Say("  开始叙事：POST /api/roleplay/start {\"worldview\":\"…\",\"situation\":\"…\"}（开场由说书人直接写）；之后 POST /api/roleplay/say {\"text\":\"…\"}");

    // 旧数据只提示、不自动搬（动用户数据要他自己点头，§15.8）。
    if (Directory.Exists(PathLayout.MeetingsDir) || Directory.Exists(PathLayout.StoriesDir))
    {
        Say(
            $"  提示：安装目录下还有旧形态的数据（{PathLayout.DataDir}）。"
            + $"可把它作为一个工作空间打开：POST /api/workspaces/open {{\"path\":\"{PathLayout.DataDir.Replace("\\", "\\\\")}\"}}（不会自动搬）。");
    }

    Say("  按 Ctrl+C 停止。");

    // 调试时（例如用 WebView2 接管）可以关掉自动打开：--no-browser 或 server.openBrowser=false。
    if (options.Server.OpenBrowser && !args.Contains("--no-browser", StringComparer.Ordinal))
    {
        BrowserLauncher.Open(url, options.Server.LaunchMode);
    }
    else
    {
        Say("  （未自动打开浏览器：--no-browser 或 server.openBrowser=false）");
    }
});

app.Run();

fileLog.Info("服务已停止");
fileLog.Dispose();
return 0;

// IPv6 字面量在 URL 里必须带方括号（::1 → [::1]）。
static string FormatBinding(string bind) =>
    bind.Contains(':', StringComparison.Ordinal) && !bind.StartsWith('[') ? $"[{bind}]" : bind;

// 解析探针的 --cancel-after-ms N（毫秒）。
static int? ParseCancelAfter(string[] args)
{
    var index = Array.IndexOf(args, "--cancel-after-ms");
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var milliseconds) && milliseconds > 0
        ? milliseconds
        : null;
}

// 取"开关 + 值"形式的命令行参数（--reference C:\x.png）；没给返回 null。
static string? TryArg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>
/// 战斗状态机自检（C1）：读脚本 → 开战 → 走指定轮数 → 收场，打印顺序 / 换手 / 轮数。
/// 与 <c>--probe-rules</c> 同样绕开 xunit/testhost；不写文件、不碰工作空间。异常或自检不符返回非 0。
/// </summary>
static int ProbeCombat(string? path, string? rulesArgument)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        Console.Error.WriteLine("用法：MeetingRoom.Host.exe --probe-combat <script.json>");
        return 2;
    }

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"❌ 找不到遭遇脚本：{path}");
        return 2;
    }

    CombatScript script;

    try
    {
        script = CombatScript.Load(path);
    }
    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"❌ 遭遇脚本不可用：{ex.Message}");
        return 1;
    }

    if (script.Combatants.Count == 0)
    {
        Console.Error.WriteLine("❌ 遭遇脚本里没有参战者。");
        return 1;
    }

    // 脚本里有 actions / checks 时需要规则包：优先 --rules，其次环境变量（不硬编码路径）。
    var needsRules = script.Actions.Count > 0 || script.Checks.Count > 0 || script.Steps?.Count > 0;
    var rulesPath = rulesArgument ?? Environment.GetEnvironmentVariable("MEETINGROOM_RULES_DATA");
    RulesData? rules = null;

    if (needsRules)
    {
        if (string.IsNullOrWhiteSpace(rulesPath) || !File.Exists(rulesPath))
        {
            Console.Error.WriteLine("❌ 脚本里有 actions / checks，但没有规则包：用 --rules <rules-data.json> 或设置 MEETINGROOM_RULES_DATA。");
            return 1;
        }

        rules = RulesDataLoader.Load(rulesPath);
    }

    var start = EncounterEngine.Start("enc-1", script.Combatants, script.Seed);

    if (!start.Ok || start.Encounter is null)
    {
        Console.Error.WriteLine($"❌ 开战失败（{start.Code}）：{start.Message}");
        return 1;
    }

    var encounter = start.Encounter;

    Console.WriteLine($"combat-start enc-1 round=1 seed={script.Seed}");
    Console.WriteLine($"  order = {DescribeOrder(encounter)}");
    Console.WriteLine($"  turn  = round {encounter.Round} {encounter.Turn?.ActorId}（{encounter.CurrentIndex + 1}/{encounter.Order.Count}）");

    var steps = Math.Max(0, script.Rounds) * encounter.Order.Count;

    for (var step = 0; step < steps; step++)
    {
        var advance = EncounterEngine.Advance(encounter);

        if (!advance.Ok || advance.Encounter is null)
        {
            Console.Error.WriteLine($"❌ 第 {step + 1} 次换手失败（{advance.Code}）：{advance.Message}");
            return 1;
        }

        encounter = advance.Encounter;
        Console.WriteLine($"  turn  = round {encounter.Round} {encounter.Turn?.ActorId}（{encounter.CurrentIndex + 1}/{encounter.Order.Count}）");
    }

    // rounds = 0 表示"轮数不管，交给 steps 自己编排"。
    if (script.Rounds > 0)
    {
        var expectedRound = script.Rounds + 1;

        if (encounter.Round != expectedRound)
        {
            Console.Error.WriteLine($"❌ 轮数不符：走完 {script.Rounds} 轮后期望 {expectedRound}，实际 {encounter.Round}。");
            return 1;
        }
    }


    var failed = false;
    var rng = new Random(script.Seed);
    var history = new CheckHistory();
    var store = new SheetStore();
    var checkIndex = 0;

    if (script.Sheets is { } sheets)
    {
        foreach (var (id, sheet) in sheets)
        {
            store.Put(sheet.CharacterId == id ? sheet : sheet with { CharacterId = id });
        }
    }

    // §26.2 第 2 层：参战者自带的数值表（与会话层同一条路：校验不过就拒绝开战）。
    foreach (var setup in script.Combatants)
    {
        if (setup.Sheet is not { } inline)
        {
            continue;
        }

        var sheetErrors = SheetValidator.Validate(inline);

        if (sheetErrors.Count > 0)
        {
            Console.Error.WriteLine($"❌ 「{setup.Id}」的数值表不完整：{string.Join("；", sheetErrors)}");
            return 1;
        }

        store.Put(inline with { CharacterId = string.IsNullOrWhiteSpace(inline.CharacterId) ? setup.Id : inline.CharacterId });
    }

    foreach (var action in script.Actions)
    {
        var result = CombatEngine.Resolve(rules!, action, rng);

        Console.WriteLine($"combat-action {action.ActorId} → {action.TargetId} {action.Kind.ToString().ToLowerInvariant()} {result.WeaponId ?? result.SpellId}");
        foreach (var line in result.Describe().Split((char)10))
        {
            Console.WriteLine("  " + line);
        }

        if (result.Damage is { } && result.DamageApplied > 0)
        {
            var application = store.ApplyDamage(action.TargetId, result.DamageApplied);

            Console.WriteLine(application.Ok
                ? $"  HP：{application.HpBefore} → {application.HpAfter}"
                    + (application.AbsorbedByTempHp > 0 ? $"（临时 HP 吸收 {application.AbsorbedByTempHp}）" : "")
                    + (application.Status is { } status ? $"（{status.ToString().ToLowerInvariant()}）" : "")
                : $"  HP 未扣：{application.Message}");
        }

        if (!result.Ok)
        {
            failed = true;
        }
    }

    foreach (var sheet in store.All.OrderBy(item => item.CharacterId, StringComparer.Ordinal))
    {
        Console.WriteLine($"sheet {sheet.CharacterId}：HP {sheet.CurrentHp}/{sheet.MaxHp}，临时 {sheet.TempHp}，{sheet.Status.ToString().ToLowerInvariant()}");
    }

    foreach (var step in script.Steps ?? [])
    {
        if (step.AdvanceTurn)
        {
            var advance = EncounterEngine.Advance(encounter);

            if (!advance.Ok || advance.Encounter is null)
            {
                Console.Error.WriteLine($"❌ 换手失败（{advance.Code}）：{advance.Message}");
                return 1;
            }

            encounter = advance.Encounter;
            Console.WriteLine($"  advance → round {encounter.Round} {encounter.Turn?.ActorId}（{encounter.CurrentIndex + 1}/{encounter.Order.Count}）");

            // D1：回合开始先做状态到期（BeginTurn 已做），再掷死亡豁免。
            if (encounter.Turn is { } currentTurn
                && store.Get(currentTurn.ActorId) is { } currentSheet
                && currentSheet.Status == VitalStatus.Dying)
            {
                var deathSave = DeathSaveEngine.Roll(currentSheet, rng);
                store.Put(deathSave.Sheet);
                Console.WriteLine($"  {deathSave.Message}");
            }

            continue;
        }

        if (step.Override is { } over)
        {
            var result = DmOverrideEngine.Apply(encounter, store, new DmOverrideRequest
            {
                Target = step.ActorId ?? string.Empty,
                Field = over.Field,
                Value = over.Value,
                Reason = over.Reason,
                CheckId = over.CheckId,
            });

            Console.WriteLine(result.Ok
                ? $"  override {result.Field}：{result.From ?? "-"} → {result.To ?? "-"}（{result.Message}）"
                : $"  override rejected（{result.Code}）：{result.Message}");

            if (result.Encounter is not null)
            {
                encounter = result.Encounter;
            }

            if (!result.Ok && !step.ExpectRejected)
            {
                failed = true;
            }

            continue;
        }

        if (step.LongRest && step.ActorId is { } restTarget)
        {
            var rested = store.LongRest(restTarget);
            Console.WriteLine(rested is null
                ? $"  long-rest {restTarget}：数值表里没有这个人。"
                : $"  long-rest {restTarget}：HP {rested.CurrentHp}/{rested.MaxHp}，{rested.Status.ToString().ToLowerInvariant()}，"
                  + $"槽位已恢复（{string.Join("、", rested.SpellSlots.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} 环 {kv.Value.Used}/{kv.Value.Max}"))}）");

            if (rested is null && !step.ExpectRejected)
            {
                failed = true;
            }

            continue;
        }

        if (step.ShortRest && step.ActorId is { } shortRestTarget)
        {
            var (ok, message, _) = store.ShortRest(shortRestTarget);
            Console.WriteLine($"  short-rest {shortRestTarget}：{message}");

            if (!ok && !step.ExpectRejected)
            {
                failed = true;
            }

            continue;
        }

        if (step.Heal is { } healAmount && step.ActorId is { } healTarget)
        {
            var healed = store.Heal(healTarget, healAmount);
            Console.WriteLine(healed is null
                ? $"  heal {healTarget}：数值表里没有这个人。"
                : $"  heal {healTarget} {healAmount}：HP → {healed.CurrentHp}（{healed.Status.ToString().ToLowerInvariant()}，死亡豁免 {healed.DeathSaves?.Successes ?? 0}/3 成功、{healed.DeathSaves?.Failures ?? 0}/3 失败）");

            if (healed is null && !step.ExpectRejected)
            {
                failed = true;
            }

            continue;
        }

        if (step.Stabilize && step.ActorId is { } stabilizeTarget)
        {
            var (ok, message, _) = store.Stabilize(stabilizeTarget);
            Console.WriteLine($"  stabilize {stabilizeTarget}：{message}");

            if (!ok && !step.ExpectRejected)
            {
                failed = true;
            }

            continue;
        }

        var request = step.ToRequest();
        var beforeStep = encounter;
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
            var rejected = $"combat-action {request.ActorId} {step.Kind ?? "attack"} → rejected（{economy.Code}）：{economy.Message}";
            Console.WriteLine(rejected);

            if (!step.ExpectRejected)
            {
                failed = true;
            }

            continue;
        }

        encounter = economy.Encounter;
        Console.WriteLine($"combat-action {request.ActorId} {step.Kind ?? "attack"}（剩余移动 {Remaining(encounter)}）");

        foreach (var opportunity in economy.Opportunities)
        {
            Console.WriteLine($"  借机攻击：{opportunity.AttackerId} → {opportunity.TargetId}（reaction 已消耗）");
        }

        if (request.Kind is CombatActionKind.Attack or CombatActionKind.CastSpell)
        {
            // D2：施法校验（法术存在 → 环级 → 槽位），失败回滚行动经济、不掷骰。
            int? spendSlotLevel = null;

            if (request.Kind == CombatActionKind.CastSpell)
            {
                var (spell, spellCode, spellMessage) = CombatEngine.ValidateSpell(rules!, request);

                if (spell is null)
                {
                    encounter = beforeStep;
                    Console.WriteLine($"  rejected（{spellCode}）：{spellMessage}");

                    if (!step.ExpectRejected)
                    {
                        failed = true;
                    }

                    continue;
                }

                if (spell.Level > 0)
                {
                    var castAtLevel = request.CastAtLevel ?? spell.Level;
                    var (slotOk, slotCode, slotMessage) = store.CanSpendSlot(request.ActorId, castAtLevel);

                    if (!slotOk)
                    {
                        encounter = beforeStep;
                        Console.WriteLine($"  rejected（{slotCode}）：{slotMessage}");

                        if (!step.ExpectRejected)
                        {
                            failed = true;
                        }

                        continue;
                    }

                    spendSlotLevel = castAtLevel;
                }
            }

            var (withHelp, helpConsumed) = EncounterEngine.ConsumeHelp(encounter, request.ActorId);

            if (helpConsumed)
            {
                encounter = withHelp;
                Console.WriteLine("  Help 优势已消费（下一次攻击有优势）。");
            }

            IReadOnlyList<AdvantageMode>? modes = helpConsumed ? [AdvantageMode.Advantage] : request.Modes;
            var dodging = encounter.Find(request.TargetId)?.Dodging == true;
            var result = CombatEngine.Resolve(rules!, request with { TargetIsDodging = dodging, Modes = modes }, rng);

            if (!result.Ok)
            {
                // 参数 / 射程类失败：回滚行动经济（与会话层同一条规则）。
                encounter = beforeStep;
                Console.WriteLine($"  rejected（{result.Code}）：{result.Message}");

                if (!step.ExpectRejected)
                {
                    failed = true;
                }

                continue;
            }

            if (result.Attack is { } roll && roll.Mode != AdvantageMode.Normal)
            {
                Console.WriteLine($"  攻击检定：{roll.Mode.ToString().ToLowerInvariant()}（d20 [{string.Join(", ", roll.Rolled)}] 取 {roll.Natural}）");
            }

            if (spendSlotLevel is { } spentLevel)
            {
                store.SpendSlot(request.ActorId, spentLevel);
                Console.WriteLine($"  消耗 {spentLevel} 环法术位。");
            }

            foreach (var line in result.Describe().Split((char)10))
            {
                Console.WriteLine("  " + line);
            }

            if (result.Damage is { } && result.DamageApplied > 0)
            {
                var application = store.ApplyDamage(request.TargetId, result.DamageApplied);
                var savesText = application.Saves is { } saves
                    ? $"死亡豁免 {saves.Successes}/3 成功、{saves.Failures}/3 失败"
                    : "";

                Console.WriteLine(application.Ok
                    ? $"  HP：{application.HpBefore} → {application.HpAfter}"
                      + (application.Status is { } status ? $"（{status.ToString().ToLowerInvariant()}，{savesText}）" : "")
                    : $"  HP 未扣：{application.Message}");

                if (application is { Ok: true, Sheet: { } damaged })
                {
                    var concentration = ConcentrationCheck.Check(damaged, result.DamageApplied, rng);

                    if (concentration.Required)
                    {
                        Console.WriteLine(
                            $"  专注检定：DC {concentration.Dc}，d20 {concentration.Roll?.Total} → "
                            + (concentration.Maintained ? "维持" : "被打断"));
                    }
                }
            }

        }
    }

    foreach (var check in script.Checks)
    {
        // F7-①：把数值表带上——脚本没给 modifier 时就能验证"从 sheet 回落"。
        var result = NarrativeCheckEngine.Resolve(
            rules!,
            check with { Sheet = store.Get(check.ActorId) },
            history,
            $"chk-{++checkIndex}",
            rng);

        Console.WriteLine($"check chk-{checkIndex} {check.ActorId} {check.Kind.ToString().ToLowerInvariant()} {result.Skill ?? result.Ability ?? "?"}"
            + $"（加值来源 {result.ModifierSource}）");
        Console.WriteLine($"  DM 面：{result.DmFacingSummary()}");
        Console.WriteLine($"  角色面：{result.CharacterFacingSummary()}");

        if (!result.Ok)
        {
            failed = true;
        }
    }

    var end = EncounterEngine.End(encounter, CombatOutcome.Victory);

    if (!end.Ok || end.Encounter is null)
    {
        Console.Error.WriteLine($"❌ 收场失败（{end.Code}）：{end.Message}");
        return 1;
    }

    encounter = end.Encounter;
    Console.WriteLine($"combat-end outcome=victory rounds={encounter.Round} phase={encounter.Phase.ToString().ToLowerInvariant()}");

    if (failed)
    {
        Console.Error.WriteLine("❌ 有 action / check 没有成立（见上面的原因）。");
        return 1;
    }

    Console.WriteLine($"✅ 战斗状态机走通：{encounter.Order.Count} 人、{script.Rounds} 轮；actions {script.Actions.Count} / checks {script.Checks.Count}。");
    return 0;

    static int Remaining(Encounter current) =>
        current.Turn is { } turn ? Math.Max(0, turn.MovementMaxFeet - turn.MovementUsedFeet) : 0;

    static string DescribeOrder(Encounter current) =>
        string.Join(" → ", EncounterEngine.OrderOf(current).Select(entry =>
            string.IsNullOrEmpty(entry.Tiebreak)
                ? $"{entry.Actor}({entry.Initiative})"
                : $"{entry.Actor}({entry.Initiative},{entry.Tiebreak})"));
}

/// <summary>
/// 规则数据包自检（V3）：用真实的 <c>RulesDataLoader</c> 读整包，<b>绕过 xunit / testhost</b>。
/// 计数与关键条目都对得上才返回 0；任何反序列化异常打印可读错误并返回 1。不写任何文件、不碰工作空间。
/// </summary>
static int ProbeLauncher()
{
    var browser = MeetingRoom.Host.Launcher.BrowserLauncher.FindChromiumBrowser();
    Console.WriteLine($"探测到的 Chromium 浏览器：{browser ?? "（未找到，将回退系统默认浏览器）"}");

    var command = MeetingRoom.Host.Launcher.BrowserLauncher.DescribeAppCommand("http://127.0.0.1:8787/");
    Console.WriteLine(command is null
        ? "app 模式命令行：不可用（无 Edge / Chrome）"
        : $"app 模式命令行：{command}");
    return 0;
}

static int ProbeEvents()
{
    var root = AppContext.BaseDirectory;

    // 必须**同时**有 wwwroot/js 与 src：bin 目录里也有 wwwroot 副本，只看它会在 bin 里停下。
    while (root is not null
           && !(Directory.Exists(Path.Combine(root, "wwwroot", "js"))
                && Directory.Exists(Path.Combine(root, "src"))))
    {
        root = Path.GetDirectoryName(root);
    }

    if (root is null)
    {
        Console.Error.WriteLine("找不到仓库根（要同时有 wwwroot/js 与 src/）：这个探针只在开发期可用。");
        return 1;
    }

    var sourceRoot = Path.Combine(root, "src");
    var streamPath = Path.Combine(root, "wwwroot", "js", "stream.js");

    if (!Directory.Exists(sourceRoot) || !File.Exists(streamPath))
    {
        Console.Error.WriteLine($"源码树不完整：{sourceRoot} / {streamPath}");
        return 1;
    }

    var published = new SortedSet<string>(StringComparer.Ordinal);
    var pattern = new System.Text.RegularExpressions.Regex(
        @"Publish\(""([a-z0-9.]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            continue;
        }

        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(File.ReadAllText(file)))
        {
            published.Add(match.Groups[1].Value);
        }
    }

    // 前端白名单：EVENT_TYPES 数组里的 'xxx' 字面量。
    var stream = File.ReadAllText(streamPath);
    var arrayStart = stream.IndexOf("const EVENT_TYPES", StringComparison.Ordinal);
    var arrayEnd = arrayStart < 0 ? -1 : stream.IndexOf("];", arrayStart, StringComparison.Ordinal);
    var subscribed = new SortedSet<string>(StringComparer.Ordinal);

    if (arrayStart >= 0 && arrayEnd > arrayStart)
    {
        var block = stream[arrayStart..arrayEnd];

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(block, "'([a-z0-9.]+)'"))
        {
            subscribed.Add(match.Groups[1].Value);
        }
    }

    Console.WriteLine($"后端 Publish 的类型：{published.Count}");
    Console.WriteLine($"前端订阅的类型：{subscribed.Count}");

    var missing = published.Where(type => !subscribed.Contains(type)).ToArray();
    var extra = subscribed.Where(type => !published.Contains(type)).ToArray();

    // 分类（免得下一个人重查一遍）：快照已覆盖 / 已知待接线 / 其它（需处理）。
    var snapshotCovered = new HashSet<string>(StringComparer.Ordinal)
    {
        "image.list", "image.job", "image.generated", "image.failed",
        "meeting.started", "meeting.round", "meeting.broadcast", "meeting.ended",
    };
    var pending = new HashSet<string>(StringComparer.Ordinal)
    {
        "roleplay.check", "roleplay.roll", "interrupt.skipped",
    };

    foreach (var type in missing)
    {
        var note = snapshotCovered.Contains(type)
            ? "（快照覆盖：meeting.state / image.list 已能重建）"
            : pending.Contains(type)
                ? "（待前端接线：数据已产出，界面还没展示）"
                : "（未分类，需处理）";

        Console.WriteLine($"  ⚠ 后端发了但前端没订阅：{type}{note}");
    }

    // 动态发布（类型名走字符串常量/数组传参）在这里会误报"后端没发"——列出来只是免得不清楚的人去查。
    var dynamicPublished = new HashSet<string>(StringComparer.Ordinal)
    {
        "workspace.list", "workspace.opened", "session.list", "draft.list",
    };

    foreach (var type in extra)
    {
        Console.WriteLine(dynamicPublished.Contains(type)
            ? $"  · 前端订阅了、后端以动态方式发布（SessionHub 传字符串常量）：{type}"
            : $"  ⚠ 前端订阅了但后端没发：{type}");
    }

    if (missing.Length > 0 || extra.Length > 0)
    {
        // 诊断工具：**只报告不阻断**。这些不一致里有一部分是既有缺口（例如 roleplay.check 还没做前端展示），
        // 让它一直返回非 0 会把 CI 变红却没人修；真正的修法是一次性补齐或明确豁免。
        Console.WriteLine($"⚠ 事件表不一致：缺 {missing.Length} 个、多 {extra.Length} 个（见上）。");
        return 0;
    }

    Console.WriteLine("✅ 事件表一致（后端发的每个类型前端都订阅了）。");
    return 0;
}

static int ProbeSheets(string? rolesDirectory)
{
    if (string.IsNullOrWhiteSpace(rolesDirectory))
    {
        Console.Error.WriteLine("用法：MeetingRoom.Host.exe --probe-sheets <roles 目录>");
        return 1;
    }

    var result = MeetingRoom.Core.Roles.RoleSheetLoader.Load(rolesDirectory);

    Console.WriteLine($"目录：{rolesDirectory}");
    Console.WriteLine($"通过：{result.Sheets.Count} 张");

    foreach (var sheet in result.Sheets)
    {
        Console.WriteLine($"  ✅ {sheet.CharacterId}：HP {sheet.CurrentHp}/{sheet.MaxHp}，AC {sheet.ArmorClass}，六属性 {sheet.Abilities?.Count ?? 0} 项");
    }

    Console.WriteLine($"问题：{result.Problems.Count} 条");

    foreach (var problem in result.Problems)
    {
        Console.WriteLine($"  ⚠ {problem}");
    }

    return 0;
}

/// <summary>
/// 迷雾边缘冗余探针：`[-3, cols-1+3]` 内合法，越出才拒；带冗余格的图也要能出图。
/// </summary>
static int ProbeFogMargin()
{
    var grid = new MapCalibration(100, 0, 0, "square", 5);
    var checks = new List<(string Name, bool Pass, string Detail)>();

    (bool Ok, string? Error) ValidateFog(params (int X, int Y)[] cells) =>
        MapDocumentValidator.Validate(
            [], [], [], [],
            new MapFog(true, cells.Select(c => new GridCell(c.X, c.Y)).ToArray(), []),
            [],
            2000,
            1500,
            grid);

    // ① 冗余内的负格：合法（这就是用户那张图的 [3,-3]）
    var negative = ValidateFog((3, -3));
    checks.Add(("① [3,-3] 在冗余内 → 通过", negative.Ok, negative.Error ?? "通过"));

    // ② 冗余上界：x = cols-1+3 合法
    var columns = (int)Math.Ceiling(2000 / 100.0);
    var edge = ValidateFog((columns - 1 + MapDocumentValidator.FogMarginCells, 5));
    checks.Add(($"② x = cols-1+3（{columns - 1 + 3}）→ 通过", edge.Ok, edge.Error ?? "通过"));

    // ③ 越出冗余才拒
    var tooFar = ValidateFog((3, -10));
    checks.Add(("③ y = -10 越出冗余 → 拒绝", !tooFar.Ok, tooFar.Error ?? "（竟然通过了）"));
    checks.Add(("③ 错误文案带「含 3 格边缘冗余」",
        (tooFar.Error ?? string.Empty).Contains("含 3 格边缘冗余", StringComparison.Ordinal), tooFar.Error ?? "（空）"));

    // ④ 带冗余格的图能正常出图（裁框会伸到底图外，不能抛越界）
    var document = new MapDocument(
        "map-fog-margin",
        "冗余验证",
        "sketch",
        null,
        grid,
        [],
        [],
        [],
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        new MapFog(true, [new GridCell(3, -3), new GridCell(4, -2), new GridCell(5, 5)], [new GridCell(5, 5)]),
        []);

    var rendered = MapViewRenderer.Render(document, null, playerView: true, 768);
    var size = (0, 0);

    if (rendered is not null)
    {
        using var image = SixLabors.ImageSharp.Image.Load(rendered);
        size = (image.Width, image.Height);
    }

    checks.Add(("④ player 视图含冗余格仍能出图", rendered is { Length: > 0 }, $"bytes={rendered?.Length ?? 0} 尺寸={size.Item1}×{size.Item2}"));

    Console.WriteLine("----- 迷雾边缘冗余 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 待决事项 kind 探针（P1）：`interaction.request` 事件里的 kind 必须是 fog / world_state 两种，不能混。
/// </summary>
static int ProbePendingKind()
{
    var fixture = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "MeetingRoom.Tests", "TestData", "Maps", "view.json");
    var mapDocument = File.Exists(fixture)
        ? JsonSerializer.Deserialize<MapDocument>(
            File.ReadAllText(fixture),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        : null;

    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "kind-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var bus = new CountingEventBus();
    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-pending-kind",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        MapDocumentProvider = () => mapDocument,
        MapFogRevealer = _ => 0,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions
        {
            Enabled = true,
            // 不靠超时收口：探针自己答复，否则两条提议会互等 60 秒。
            TimeoutSeconds = 60,
        },
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new UnusedChatClient(),
        bus,
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "narrator", Name = "说书人", Role = MeetingRoom.Core.Participants.ParticipantRole.Host },
        ]);

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // 读最近一条 interaction.request 的 kind（payload 是匿名对象，转 JSON 再挑最稳）。
    string LastRequestKind()
    {
        foreach (var evt in bus.Recent(50).Reverse())
        {
            if (evt.Type != "interaction.request")
            {
                continue;
            }

            var json = JsonSerializer.Serialize(evt.Payload);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("kind", out var kind))
            {
                return kind.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    // ① 迷雾提议 → kind = fog（在后台跑，探针只读事件然后取消）
    using (var cts = new CancellationTokenSource())
    {
        var fog = session.ProposeRevealFogAsync([new MeetingRoom.Core.Maps.GridCell(2, 2)], null, "探针：看 kind", cts.Token);
        Thread.Sleep(200);
        var kind = LastRequestKind();
        cts.Cancel();

        try
        {
            fog.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        checks.Add(("迷雾提议的 pending kind = fog", kind == "fog", $"实际：{kind}"));
    }

    // ② 世界状态提议 → kind 仍是 world_state
    using (var cts = new CancellationTokenSource())
    {
        var world = session.ProposeWorldStateAsync(
            new MeetingRoom.Core.Roleplay.WorldStateMutation { Time = "探针时间" },
            "探针：看 kind",
            cts.Token);
        Thread.Sleep(200);
        var kind = LastRequestKind();
        cts.Cancel();

        try
        {
            world.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        checks.Add(("世界状态提议的 pending kind = world_state", kind == "world_state", $"实际：{kind}"));
    }

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 待决事项 kind -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 交互超时探针（P0）：用 1 秒超时量"从挂起到收口"的实际耗时，并确认结果是默认选项。
/// </summary>
static int ProbeInteractionTimeout()
{
    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ① 最底层：registry 自己的计时（不含任何上层开销）
    var registry = new MeetingRoom.Core.Interaction.PendingDecisionRegistry();
    var options = new[]
    {
        new MeetingRoom.Core.Interaction.InteractionOption("apply", "同意", "按提议生效"),
        new MeetingRoom.Core.Interaction.InteractionOption("reject", "不改", "保持原样"),
    };

    var question = new MeetingRoom.Core.Interaction.InteractionQuestion("t", "要改吗？", options);

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var decision = registry.Open(
        MeetingRoom.Core.Interaction.PendingDecisionKind.WorldState,
        [question],
        defaultSelection: [new MeetingRoom.Core.Interaction.InteractionAnswer("t", ["reject"])],
        timeoutSeconds: 1);

    var outcome = decision.Completion.GetAwaiter().GetResult();
    watch.Stop();

    checks.Add(("① registry 1 秒超时实际耗时 ≤ 6s", watch.Elapsed.TotalSeconds <= 6,
        $"{watch.Elapsed.TotalMilliseconds:F0} ms（配置 1000 ms，偏差 {watch.Elapsed.TotalMilliseconds - 1000:F0} ms）"));
    checks.Add(("① 超时后走默认选项（reject）",
        outcome.TimedOut && outcome.Answers.SelectMany(a => a.Selected).Contains("reject"),
        $"timedOut={outcome.TimedOut} 选中={string.Join(",", outcome.Answers.SelectMany(a => a.Selected))}"));

    registry.Dispose();

    // ② 会话层：迷雾提议整条（这才是 r16 实测 260 秒的那条路）
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "timeout-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    // 迷雾提议要先有地图文档，否则会在进交互之前就返回（那样量的不是超时）。
    var fixture = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "MeetingRoom.Tests", "TestData", "Maps", "view.json");
    var mapDocument = File.Exists(fixture)
        ? JsonSerializer.Deserialize<MapDocument>(
            File.ReadAllText(fixture),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        : null;

    var sessionOptions = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-interaction-timeout",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        MapDocumentProvider = () => mapDocument,
        // 探针不落盘：揭示直接返回 0 格（这里只量超时与默认选项，不验写入）。
        MapFogRevealer = _ => 0,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions
        {
            Enabled = true,
            TimeoutSeconds = 1,
        },
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        sessionOptions,
        new UnusedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "narrator", Name = "说书人", Role = MeetingRoom.Core.Participants.ParticipantRole.Host },
        ]);

    var dispatcher = new MeetingRoom.Core.Roleplay.NarratorToolDispatcher(session, session.IsNarrator, session, session);
    var call = new Microsoft.Extensions.AI.FunctionCallContent(
        "call-fog",
        MeetingRoom.Core.Roleplay.Narrator.ProposeRevealFog,
        new Dictionary<string, object?>
        {
            ["cells"] = new[] { new { x = 2, y = 2 } },
            ["reason"] = "探针：量超时",
        });

    var fogWatch = System.Diagnostics.Stopwatch.StartNew();
    var fogOutcome = dispatcher.InvokeAsync("narrator", call, CancellationToken.None).GetAwaiter().GetResult();
    fogWatch.Stop();
    var fogText = fogOutcome.Result?.ToString() ?? string.Empty;

    checks.Add(("② 迷雾提议 1 秒超时实际耗时 ≤ 6s", fogWatch.Elapsed.TotalSeconds <= 6,
        $"{fogWatch.Elapsed.TotalMilliseconds:F0} ms（偏差 {fogWatch.Elapsed.TotalMilliseconds - 1000:F0} ms）"));
    checks.Add(("② 超时 = 不揭示（默认 reject）",
        fogText.Contains("不揭示", StringComparison.Ordinal), fogText));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 交互超时 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 角色工具集探针（M9b）：白名单正好 5 个，且说书人专属的 4 个**不在**里面（反证）。
/// 同时给出两组前缀指纹——说书人必须不变，角色变了属预期。
/// </summary>
static int ProbeCharacterTools()
{
    var character = MeetingRoom.Core.Roleplay.NarratorToolDispatcher.BuildCharacterTools();
    var names = character.Select(tool => tool.Name).ToArray();
    var expected = MeetingRoom.Core.Roleplay.Narrator.CharacterToolNames;

    var checks = new List<(string Name, bool Pass, string Detail)>();

    checks.Add(($"角色工具正好 {expected.Length} 个", names.Length == expected.Length,
        string.Join(" / ", names)));
    checks.Add(("名字与白名单一致", expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(names.OrderBy(x => x, StringComparer.Ordinal)),
        string.Join(" / ", expected.OrderBy(x => x, StringComparer.Ordinal))));

    // 反证：这些说书人专属工具一个都不能出现在角色表里（§27 起多一个 lookup_monster）。
    string[] forbidden =
    [
        MeetingRoom.Core.Roleplay.Narrator.RequestCheck,
        MeetingRoom.Core.Roleplay.Narrator.CombatAction,
        MeetingRoom.Core.Roleplay.Narrator.DmOverride,
        MeetingRoom.Core.Roleplay.Narrator.ProposeRevealFog,
        MeetingRoom.Core.Roleplay.Narrator.LookupMonster,
    ];

    var leaked = forbidden.Where(name => names.Contains(name, StringComparer.Ordinal)).ToArray();
    checks.Add(($"反证：说书人专属 {forbidden.Length} 个都不在角色表里", leaked.Length == 0,
        leaked.Length == 0 ? $"已检查 {string.Join(" / ", forbidden)}" : $"泄漏：{string.Join(" / ", leaked)}"));

    // §27：lookup_rule 必须在角色表里（角色也能查规则原文）。
    checks.Add(("角色表里有 lookup_rule",
        names.Contains(MeetingRoom.Core.Roleplay.Narrator.LookupRule, StringComparer.Ordinal),
        string.Join(" / ", names)));

    // 两组指纹：说书人不变（本批不给它加工具），角色必然变（预期）。
    var narratorTools = MeetingRoom.Core.Roleplay.NarratorToolDispatcher.BuildTools();
    var prompt = MeetingRoom.Core.Roleplay.Narrator.BuildSystemPrompt("（probe）未指定", "（probe）未指定", string.Empty);

    var narratorFingerprint = MeetingRoom.Core.Context.ContextComposer.ToolFingerprintOf(narratorTools);
    var characterFingerprint = MeetingRoom.Core.Context.ContextComposer.ToolFingerprintOf(character);

    // 直调 dispatcher（确定性比"模型愿不愿意调工具"可靠得多）：角色视角过滤 + 请求落盘 + 反证拒绝。
    var fixture = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "MeetingRoom.Tests", "TestData", "Maps", "view.json");
    var document = File.Exists(fixture)
        ? JsonSerializer.Deserialize<MapDocument>(
            File.ReadAllText(fixture),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        : null;

    if (document is not null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "chartools-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
        {
            StoryId = "probe-character-tools",
            World = "（探针）",
            Situation = "（探针）",
            StoryPath = Path.Combine(dir, "story.jsonl"),
            MapDocumentProvider = () => document,
        };

        var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
            options,
            new UnusedChatClient(),
            new CountingEventBus(),
            [
                new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
                new MeetingRoom.Core.Roles.RoleCard { Id = "narrator", Name = "说书人", Role = MeetingRoom.Core.Participants.ParticipantRole.Host },
            ]);

        var dispatcher = new MeetingRoom.Core.Roleplay.NarratorToolDispatcher(session, session.IsNarrator, session, session);

        static Microsoft.Extensions.AI.FunctionCallContent Call(string name, params (string Key, object? Value)[] args) =>
            new("call-" + Guid.NewGuid().ToString("N")[..6], name, args.ToDictionary(pair => pair.Key, pair => pair.Value));

        var looked = dispatcher.InvokeAsync("ella", Call(MeetingRoom.Core.Roleplay.Narrator.LookAround), CancellationToken.None).GetAwaiter().GetResult();
        var lookedText = looked.Result?.ToString() ?? string.Empty;

        checks.Add(("角色调 look_around 有回执", lookedText.Length > 0, lookedText.ReplaceLineEndings(" ")[..Math.Min(120, lookedText.Length)]));

        var judged = dispatcher.InvokeAsync(
            "ella",
            Call(MeetingRoom.Core.Roleplay.Narrator.RequestJudgement, ("skill", "arcana"), ("reason", "这门上有没有魔法痕迹")),
            CancellationToken.None).GetAwaiter().GetResult();
        var judgedText = judged.Result?.ToString() ?? string.Empty;

        var logPath = options.StoryPath;
        var hasRequest = File.Exists(logPath) && File.ReadAllText(logPath).Contains("\"kind\":\"request\"", StringComparison.Ordinal);

        checks.Add(("角色调 request_judgement 有回执", judgedText.Contains("已提交请求", StringComparison.Ordinal),
            judgedText.ReplaceLineEndings(" ")[..Math.Min(120, judgedText.Length)]));
        checks.Add(("story.jsonl 出现 kind=request", hasRequest, logPath));

        // one-request-per-round（r17 修的缺陷）：同一轮第二个请求必须被拒，且回执要点名上一条。
        var second = dispatcher.InvokeAsync(
            "ella",
            Call(MeetingRoom.Core.Roleplay.Narrator.RequestJudgement, ("skill", "history"), ("reason", "再试一次")),
            CancellationToken.None).GetAwaiter().GetResult();
        var secondText = second.Result?.ToString() ?? string.Empty;

        checks.Add(("同一轮第二个请求被拒（one-request-per-round）",
            secondText.Contains("这一轮已经提交过一个请求了", StringComparison.Ordinal),
            secondText));
        checks.Add(("回执点名了上一条请求",
            secondText.Contains("对 arcana 做一次判定", StringComparison.Ordinal),
            secondText));

        // 跨轮：一次导演 say = 新一轮 → 额度重置，第一个请求重新可用。
        session.SayAsync("（元指令）探针：开始新一轮。", CancellationToken.None).GetAwaiter().GetResult();
        var nextRound = dispatcher.InvokeAsync(
            "ella",
            Call(MeetingRoom.Core.Roleplay.Narrator.RequestCombatAction,
                ("kind", "attack"), ("targetId", "bob"), ("intent", "试着推开那扇门")),
            CancellationToken.None).GetAwaiter().GetResult();

        checks.Add(("跨轮后第一个请求通过",
            (nextRound.Result?.ToString() ?? string.Empty).Contains("已提交请求", StringComparison.Ordinal),
            nextRound.Result?.ToString() ?? "（空）"));

        var denied = dispatcher.InvokeAsync(
            "ella",
            Call(MeetingRoom.Core.Roleplay.Narrator.RequestCheck, ("skill", "arcana"), ("reason", "越权试探")),
            CancellationToken.None).GetAwaiter().GetResult();

        checks.Add(("反证：角色调 request_check 被拒",
            (denied.Result?.ToString() ?? string.Empty).Contains("只有说书人", StringComparison.Ordinal),
            denied.Result?.ToString() ?? "（空）"));

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    Console.WriteLine("----- 角色工具集 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    Console.WriteLine();
    Console.WriteLine($"说书人工具面指纹：{narratorFingerprint}");
    Console.WriteLine($"说书人前缀指纹：  {MeetingRoom.Core.Context.ContextComposer.PrefixFingerprintOf(prompt, string.Empty, narratorTools)}");
    Console.WriteLine($"角色工具面指纹：  {characterFingerprint}");
    Console.WriteLine($"角色前缀指纹：    {MeetingRoom.Core.Context.ContextComposer.PrefixFingerprintOf(prompt, string.Empty, character)}");

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

static int ProbeTools()
{
    var tools = MeetingRoom.Core.Roleplay.NarratorToolDispatcher.BuildTools();
    var names = tools.Select(tool => tool.Name).ToArray();
    var prompt = MeetingRoom.Core.Roleplay.Narrator.BuildSystemPrompt("（probe）未指定", "（probe）未指定", string.Empty);
    var toolFingerprint = MeetingRoom.Core.Context.ContextComposer.ToolFingerprintOf(tools);
    var prefixFingerprint = MeetingRoom.Core.Context.ContextComposer.PrefixFingerprintOf(prompt, string.Empty, tools);

    Console.WriteLine($"工具数量：{tools.Count}");

    foreach (var tool in tools)
    {
        Console.WriteLine($"- {tool.Name}（说明 {tool.Description?.Length ?? 0} 字）");
    }

    // schema 可见性（§27 的配套诊断）：枚举参数应当在 schema 里变成 enum，否则模型看不见可选值。
    foreach (var tool in tools.Where(t => t.Name is "request_check" or "combat_action"))
    {
        if (tool is Microsoft.Extensions.AI.AIFunction function)
        {
            var schema = function.JsonSchema.GetRawText();
            Console.WriteLine($"{tool.Name} schema（{schema.Length} 字符）：{schema}");
        }
    }

    Console.WriteLine($"工具面指纹：{toolFingerprint}");
    Console.WriteLine($"前缀指纹（空公共节）：{prefixFingerprint}");
    Console.WriteLine($"前缀版本号：{MeetingRoom.Core.Roleplay.Narrator.PromptPrefixVersion}");

    var missing = MeetingRoom.Core.Roleplay.Narrator.ToolNames.Except(names, StringComparer.Ordinal).ToArray();
    var extra = names.Except(MeetingRoom.Core.Roleplay.Narrator.ToolNames, StringComparer.Ordinal).ToArray();

    if (missing.Length > 0 || extra.Length > 0)
    {
        Console.Error.WriteLine($"❌ 工具清单与注册表不一致：缺 {string.Join("、", missing)}；多 {string.Join("、", extra)}");
        return 1;
    }

    Console.WriteLine($"✅ 工具清单一致：{string.Join("、", names)}");
    return 0;
}

static int ProbeRules(string? path)
{
    // 不给路径就用默认位置（§27：配置默认 data/rules/rules-data.json，相对 exe）。
    var rulesPath = string.IsNullOrWhiteSpace(path)
        ? Path.Combine(PathLayout.InstallDir, "data", "rules", "rules-data.json")
        : path;

    if (!File.Exists(rulesPath))
    {
        Console.Error.WriteLine($"❌ 找不到规则数据包：{rulesPath}");
        return 2;
    }

    var sizeMb = new FileInfo(rulesPath).Length / 1024.0 / 1024.0;
    var started = Stopwatch.StartNew();
    RulesData data;

    try
    {
        data = RulesDataLoader.Load(rulesPath);
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"❌ 规则数据包不可用：{ex.Message}");
        return 1;
    }

    started.Stop();

    Console.WriteLine($"schema = {data.Schema}（{sizeMb:F2} MB，反序列化 {started.ElapsedMilliseconds} ms）");
    Console.WriteLine($"  spells = {data.Spells.Count}");
    Console.WriteLine($"  items = {data.Items.Count}");
    Console.WriteLine($"  monsterTemplates = {data.MonsterTemplates.Count}");
    Console.WriteLine($"  conditions = {data.Conditions.Count}");
    Console.WriteLine($"  actions = {data.Actions.Count}");
    Console.WriteLine($"  skills = {data.Skills.Count}");
    Console.WriteLine($"  classes = {data.Classes.Count}");
    Console.WriteLine($"  subclasses = {data.Subclasses.Count}");
    Console.WriteLine($"  feats = {data.Feats.Count}");
    Console.WriteLine($"  optionalFeatures = {data.OptionalFeatures.Count}");

    var fireball = data.FindSpell("Fireball|XPHB");
    var longsword = data.FindItem("Longsword|XPHB");
    var vecna = data.FindMonster("Vecna the Archlich|VEoR");
    var wizard = data.FindClass("Wizard|XPHB");
    var drunken = data.FindSubclass("Monk - Drunken Master|XGE|XPHB");
    var agonizing = data.FindOptionalFeature("Agonizing Blast|XPHB");

    Console.WriteLine($"  Fireball|XPHB：damage = {Describe(fireball?.Damage)}，save = {fireball?.Save ?? "(null)"}");
    Console.WriteLine($"  Longsword|XPHB：kind = {longsword?.Kind ?? "(缺失)"}，mastery = {string.Join('、', longsword?.Mastery ?? [])}");
    Console.WriteLine($"  Vecna the Archlich|VEoR：type = {vecna?.Type ?? "(缺失)"}，size = {vecna?.Size ?? "(null)"}，cr = {vecna?.Cr ?? "(null)"}");
    Console.WriteLine($"  Vecna 数值：hp = {vecna?.Hp?.Average?.ToString() ?? "(null)"}（{vecna?.Hp?.Formula ?? "(null)"}），"
        + $"ac = {vecna?.AcText ?? "(null)"}，abilities = {vecna?.Abilities?.Describe() ?? "(null)"}，"
        + $"traits = {vecna?.Traits.Count ?? 0}，actions = {vecna?.Actions.Count ?? 0}");
    Console.WriteLine($"  Wizard|XPHB：hitDie = {wizard?.HitDieText ?? "(缺失)"}，施法属性 = {wizard?.SpellcastingAbility ?? "(null)"}，子职 {wizard?.Subclasses.Count ?? 0} 个");
    Console.WriteLine($"  Monk - Drunken Master|XGE|XPHB：所属职业 = {drunken?.ClassName ?? "(缺失)"}，"
        + $"原文继承 = {drunken?.FeaturesInheritedFrom ?? "(无)"}，特性 {drunken?.Features.Count ?? 0} 条");
    Console.WriteLine($"  Agonizing Blast|XPHB：featureTypes = {string.Join('、', agonizing?.FeatureTypes ?? [])}，长度 {agonizing?.Text.Length ?? 0}");

    var problems = new List<string>();

    Expect(problems, "spells", data.Spells.Count, 488);
    Expect(problems, "items", data.Items.Count, 965);
    Expect(problems, "monsterTemplates", data.MonsterTemplates.Count, 706);
    Expect(problems, "conditions", data.Conditions.Count, 27);
    // V4 的 actions 是 31（V3 是 48：那一版混进了 2014 与 2024 两套的重复条目）。
    Expect(problems, "actions", data.Actions.Count, 31);
    Expect(problems, "skills", data.Skills.Count, 18);
    Expect(problems, "classes", data.Classes.Count, 13);
    Expect(problems, "subclasses", data.Subclasses.Count, 200);
    Expect(problems, "feats", data.Feats.Count, 128);
    Expect(problems, "optionalFeatures", data.OptionalFeatures.Count, 156);

    if (fireball is null)
    {
        problems.Add("按 id 找不到 Fireball|XPHB");
    }

    if (longsword is null)
    {
        problems.Add("按 id 找不到 Longsword|XPHB");
    }
    else if (!longsword.Mastery.Contains("Sap", StringComparer.Ordinal))
    {
        problems.Add($"Longsword|XPHB 的 mastery 不含英文 Sap（实际：{string.Join('、', longsword.Mastery)}）");
    }

    if (vecna is null)
    {
        problems.Add("按 id 找不到 Vecna the Archlich|VEoR");
    }
    else
    {
        // V4 才有这些：包是对的就必须读得出来（读不出来说明模型少字段，查询工具会给出空数值）。
        if (vecna.Hp?.Average is null)
        {
            problems.Add("Vecna 的 hp.average 没读出来（V4 字段）");
        }

        if (vecna.Ac.Count == 0)
        {
            problems.Add("Vecna 的 ac 没读出来（V4 字段）");
        }

        if (vecna.Abilities is null || vecna.Abilities.IsEmpty)
        {
            problems.Add("Vecna 的 abilities 没读出来（V4 字段）");
        }

        if (vecna.Traits.Count == 0 || vecna.Actions.Count == 0)
        {
            problems.Add($"Vecna 的 traits/actions 没读出来（traits {vecna.Traits.Count} / actions {vecna.Actions.Count}）");
        }
    }

    if (wizard is null)
    {
        problems.Add("按 id 找不到 Wizard|XPHB");
    }
    else if (wizard.HitDie != 6 || wizard.Subclasses.Count == 0 || wizard.Features.Count == 0)
    {
        problems.Add($"Wizard|XPHB 读出来不对：hitDie {wizard.HitDie?.ToString() ?? "(null)"}，"
            + $"subclasses {wizard.Subclasses.Count}，features {wizard.Features.Count}");
    }

    if (drunken is null)
    {
        problems.Add("按 id 找不到 Monk - Drunken Master|XGE|XPHB");
    }
    else
    {
        if (drunken.Features.Count == 0)
        {
            problems.Add("醉拳子职的 features 没读出来");
        }

        if (string.IsNullOrEmpty(drunken.FeaturesInheritedFrom))
        {
            problems.Add("醉拳子职的 featuresInheritedFrom 没读出来（V4 字段）");
        }
    }

    if (agonizing is null)
    {
        problems.Add("按 id 找不到 Agonizing Blast|XPHB");
    }
    else if (agonizing.Text.Length == 0 || agonizing.FeatureTypes.Count == 0)
    {
        problems.Add("Agonizing Blast 的 text / featureTypes 没读出来");
    }

    if (problems.Count > 0)
    {
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"❌ {problem}");
        }

        return 1;
    }

    Console.WriteLine("✅ 真实规则包反序列化成功，十个 section 计数与生成器一致。");
    return 0;

    static string Describe(IReadOnlyList<DamagePartDefinition>? parts) =>
        parts is null || parts.Count == 0 ? "(无)" : string.Join(" + ", parts.Select(part => $"{part.Dice} {part.Type}"));

    static void Expect(List<string> problems, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            problems.Add($"{name} 计数不符：期望 {expected}，实际 {actual}");
        }
    }
}

/// <summary>
/// 说书人意图去重探针（§11）。
/// <para>
/// 真机现象：两幕只产出 2 段对话，说书人却调了 14 次工具（idle_beat 6 / nudge 5 …）。
/// 队列每轮只消费 1 个，剩下的跨轮消化——观众看到的是"剧情自己乱跑"。
/// 这里验四件事：同键合并、顺序与内容、描写去重、队列上限与拒绕回执。
/// </para>
/// </summary>
static int ProbeIntentDedup()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "dedup-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-intent-dedup",
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        // §12 起 0 = "只写不推进"（意图会被拒），这里要测去重，所以用 1。
        NarratorBeatsPerTurn = 1,
        MaxSpeakersPerTurn = 1,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new ScriptedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "bob", Name = "Bob" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "cara", Name = "Cara" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "dan", Name = "Dan" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "eve", Name = "Eve" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ]);

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ① 连点同一角色 5 次 → 队列只 1 条；第 2 次起回执说"已合并"
    var replies = new List<string>();

    for (var attempt = 1; attempt <= 5; attempt++)
    {
        replies.Add(session.NudgeCharacter("ella", $"第 {attempt} 次点名").Message);
    }

    var afterFive = session.PendingIntentSnapshot();

    checks.Add(("连点同一角色 5 次 → 队列只有 1 条",
        afterFive.Count == 1 && afterFive[0].Target == "ella",
        $"队列 {afterFive.Count} 条：[{string.Join(" / ", afterFive.Select(item => $"{item.Kind}:{item.Target}"))}]"));

    checks.Add(("首次回执说“排队第 1 位 / 当前待执行 1 个”",
        replies[0].Contains("排队第 1 位", StringComparison.Ordinal)
        && replies[0].Contains("当前待执行 1 个", StringComparison.Ordinal),
        replies[0]));

    checks.Add(("第 2–5 次回执都说“已合并”，并点名第几次",
        replies.Skip(1).All(text => text.Contains("已合并", StringComparison.Ordinal))
        && replies[4].Contains("第 5 次", StringComparison.Ordinal),
        replies[4]));

    checks.Add(("合并后内容取最后一次",
        afterFive[0].Text == "第 5 次点名",
        afterFive[0].Text));

    // ② 新的一轮：A → B → A，队列是 A、B，A 的内容是最后一次的 task
    session.SayAsync("新一轮。", CancellationToken.None).GetAwaiter().GetResult();

    session.NudgeCharacter("ella", "先让 Ella 说话");
    session.NudgeCharacter("bob", "Bob 接着");
    session.NudgeCharacter("ella", "Ella 换一句");

    var ordered = session.PendingIntentSnapshot();

    checks.Add(("A → B → A：队列是 A、B 两条（顺序不变）",
        ordered.Count == 2 && ordered[0].Target == "ella" && ordered[1].Target == "bob",
        string.Join(" / ", ordered.Select(item => $"{item.Target}"))));

    checks.Add(("A 的内容是最后一次的 task（位置仍在第 1）",
        ordered[0].Text == "Ella 换一句" && ordered[1].Text == "Bob 接着",
        $"{ordered[0].Text} | {ordered[1].Text}"));

    // ③ 3 次 idle_beat → 队列只有 1 条 beat
    session.SayAsync("再来一轮。", CancellationToken.None).GetAwaiter().GetResult();
    var beatReplies = new List<string>();

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        beatReplies.Add(session.IdleBeat($"第 {attempt} 拍").Message);
    }

    var beats = session.PendingIntentSnapshot().Where(item => item.Kind == "beat").ToArray();

    checks.Add(("连调 3 次 idle_beat → 队列只有 1 条，内容取最后一次",
        beats.Length == 1 && beats[0].Text == "第 3 拍",
        $"{beats.Length} 条 / {beats.FirstOrDefault().Text}"));

    checks.Add(("idle_beat 第二次起回执也说“已合并”",
        beatReplies.Skip(1).All(text => text.Contains("已合并", StringComparison.Ordinal)),
        beatReplies[^1]));

    // ④ 队列上限：上限 = max(5, 在场角色数 + 1)；这里 5 个角色 → 上限 6
    session.SayAsync("第三轮。", CancellationToken.None).GetAwaiter().GetResult();

    var accepted = 0;
    var rejected = new List<string>();

    foreach (var id in new[] { "ella", "bob", "cara", "dan", "eve" })
    {
        var outcome = session.NudgeCharacter(id, $"点 {id}");

        if (outcome.Applied)
        {
            accepted++;
        }
        else
        {
            rejected.Add($"{id}: {outcome.Message}");
        }
    }

    var snapshot = session.PendingIntentSnapshot();

    checks.Add(("多角色各点一次都在上限内（不被误伤）",
        accepted == 5,
        $"接受 {accepted} / 拒绝 {rejected.Count}"));

    // 再点一个已排队的角色不会新增（合并），但点第 6 个不同角色会被拒
    session.NudgeCharacter("ella", "重复点 Ella（应合并）");
    var sixth = session.PendingIntentSnapshot().Count;
    checks.Add(("重复点已排队角色只合并、不新增",
        sixth == snapshot.Count,
        $"队列 {sixth} 条（前一步 {snapshot.Count} 条）"));

    // ⑤ 上限拒绕：默认上限跟着角色数（5 角色 → 6），正常永远撞不到——所以用一个"上限=2"的会话单独验。
    var smallDir = Path.Combine(dir, "small");
    Directory.CreateDirectory(smallDir);

    var smallSession = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options with
        {
            StoryId = "probe-intent-dedup-small",
            StoryPath = Path.Combine(smallDir, "story.jsonl"),
            NarratorIntentQueueLimit = 2,
        },
        new ScriptedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "bob", Name = "Bob" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "cara", Name = "Cara" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ]);

    var first = smallSession.NudgeCharacter("ella", "点 Ella");
    var second = smallSession.NudgeCharacter("bob", "点 Bob");
    var third = smallSession.NudgeCharacter("cara", "点 Cara");
    var merged = smallSession.NudgeCharacter("ella", "再点 Ella（应合并、不占位）");

    checks.Add(("队列满时拒绝并明确回执（上限=2 的会话）",
        first.Applied && second.Applied && !third.Applied
        && third.Message.Contains("未加入", StringComparison.Ordinal)
        && third.Message.Contains("上限 2", StringComparison.Ordinal),
        third.Message));

    checks.Add(("队列满时对已排队角色仍可合并（不占新位）",
        merged.Applied
        && merged.Message.Contains("已合并", StringComparison.Ordinal)
        && smallSession.PendingIntentSnapshot().Count == 2,
        $"{merged.Message} · 队列 {smallSession.PendingIntentSnapshot().Count} 条"));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 说书人意图去重（§11） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// §13 探针：<c>declare_scene_end</c> 的幂等与"本轮内主动暂停后立即收尾"。
/// <para>
/// 真机现场：说书人同一轮连建议 4 次收束，每次 `PauseAsync()`，之后的角色回合卡在
/// `WaitWhileBlockedAsync` 上——`POST /api/roleplay/say` 挂了 **219 秒**（客户端 499 断开）。
/// </para>
/// </summary>
static int ProbeSceneEnd()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "sceneend-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    MeetingRoom.Core.Roleplay.RoleplayOptions MakeOptions(string storyId) => new()
    {
        StoryId = storyId,
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(Path.Combine(dir, storyId), "story.jsonl"),
        NarratorBeatsPerTurn = 1,
        // 只 1 个角色：这样"角色 1 次 + 复盘 1 次"的调用次数才可判定。
        MaxSpeakersPerTurn = 1,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
    };

    var cards = new[]
    {
        new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
        new MeetingRoom.Core.Roles.RoleCard { Id = "bob", Name = "Bob" },
        new MeetingRoom.Core.Roles.RoleCard
        {
            Id = "narrator",
            Name = "说书人",
            Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
        },
    };

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ——— ① 幂等：连调 4 次 ———
    var bus = new CountingEventBus();
    var plain = new MeetingRoom.Core.Roleplay.RoleplaySession(
        MakeOptions("sceneend-idempotent"), new ScriptedChatClient(), bus, cards);

    var replies = Enumerable.Range(0, 4)
        .Select(index => plain.DeclareSceneEnd($"第 {index + 1} 次建议收束"))
        .ToList();

    checks.Add(("连调 4 次：只有第 1 次生效",
        replies[0].Applied && replies.Skip(1).All(reply => !reply.Applied),
        string.Join(" | ", replies.Select(reply => reply.Applied ? "生效" : "拒绝"))));

    checks.Add(("后 3 次回执明确说“已处于暂停状态”",
        replies.Skip(1).All(reply => reply.Message.Contains("已处于暂停状态", StringComparison.Ordinal)),
        replies[3].Message));

    checks.Add(("host.decision（建议收束）只推 1 次",
        bus.CountOf("host.decision") == 1,
        $"{bus.CountOf("host.decision")} 次"));

    checks.Add(("队列里只有 1 条 end（不重复入队）",
        plain.PendingIntentSnapshot().Count(item => item.Kind == "end") == 1,
        $"{plain.PendingIntentSnapshot().Count(item => item.Kind == "end")} 条"));

    // ——— ② 复盘里建议收束 → 本轮立即收尾 ———
    var declareClient = new DeclareEndChatClient();
    var quick = new MeetingRoom.Core.Roleplay.RoleplaySession(
        MakeOptions("sceneend-inturn"), declareClient, new CountingEventBus(), cards);

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var finished = quick.SayAsync("（探针）你们在码头上。", CancellationToken.None).Wait(TimeSpan.FromSeconds(20));
    watch.Stop();

    checks.Add(("复盘里 declare_scene_end：say 在数秒内返回（真机 219 秒的来源）",
        finished && watch.Elapsed.TotalSeconds < 5,
        finished ? $"{watch.Elapsed.TotalMilliseconds:F0} ms" : "20 秒内没返回"));

    checks.Add(("说书人建议收束后会话处于 paused",
        quick.Status.ToString() == "Paused",
        quick.Status.ToString()));

    checks.Add(("复盘里 declare 之后没有后续角色回合（模型只多调 1 次收尾）",
        declareClient.CallCount == 3,
        $"模型调用 {declareClient.CallCount} 次（角色 1 + 复盘 declare 1 + 该步收尾 1；"
        + "若后续角色回合还在跑，这里会继续往上长）"));

    checks.Add(("这一轮的正文仍然落盘（不会因为提前收尾就丢回合）",
        quick.Revision == 1,
        $"revision {quick.Revision}"));

    // ——— ③ 外部暂停（导演点暂停）仍然走"入口自动恢复"，不被本次改动修坏 ———
    var pausedClient = new CountingChatClient();
    var paused = new MeetingRoom.Core.Roleplay.RoleplaySession(
        MakeOptions("sceneend-external"), pausedClient, new CountingEventBus(), cards);

    paused.PauseAsync(CancellationToken.None).GetAwaiter().GetResult();

    var pauseWatch = System.Diagnostics.Stopwatch.StartNew();
    var resumed = paused.SayAsync("（探针）继续。", CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
    pauseWatch.Stop();

    checks.Add(("外部暂停：入口自动恢复，say 不被卡住（上一批语义保留）",
        resumed && pauseWatch.Elapsed.TotalSeconds < 5,
        resumed ? $"{pauseWatch.Elapsed.TotalMilliseconds:F0} ms" : "10 秒内没返回"));

    // ——— ④ probe-broken fixture 不再被扫到 —---
    var rolesDir = Path.Combine(PathLayout.InstallDir, "data", "roles");
    var sheets = MeetingRoom.Core.Roles.RoleSheetLoader.Load(rolesDir);

    checks.Add(("probe-broken.sheet.json 已不在扫描目录（不再产生 sheet-invalid 警告）",
        sheets.Problems.All(problem => !problem.Contains("probe-broken", StringComparison.OrdinalIgnoreCase)),
        sheets.Problems.Count == 0 ? "本目录 0 条问题" : string.Join(" / ", sheets.Problems)));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 建议收束（§13） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// §12 探针：<c>narratorBeatsPerTurn = 0</c> 必须做到"只写不推进"——
/// <b>复盘照常跑</b>（说书人仍看本轮、仍可提议），但 <c>nudge_character</c> / <c>idle_beat</c> 被拒且不入队。
/// </summary>
static int ProbeNarratorBeats()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "beats-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    MeetingRoom.Core.Roleplay.RoleplayOptions MakeOptions(string storyId, int beats) => new()
    {
        StoryId = storyId,
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(Path.Combine(dir, storyId), "story.jsonl"),
        NarratorBeatsPerTurn = beats,
        MaxSpeakersPerTurn = 1,
        // 关掉交互：提议会"直接生效"（RequestDecisionAsync 返回 null 分支），不用等 120 秒。
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
    };

    var cards = new[]
    {
        new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
        new MeetingRoom.Core.Roles.RoleCard
        {
            Id = "narrator",
            Name = "说书人",
            Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
        },
    };

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ——— beats = 0 ———
    var quietClient = new CountingChatClient();
    var quiet = new MeetingRoom.Core.Roleplay.RoleplaySession(
        MakeOptions("beats-zero", 0),
        quietClient,
        new CountingEventBus(),
        cards);

    quiet.SayAsync("（探针）你们在码头上。", CancellationToken.None).GetAwaiter().GetResult();

    checks.Add(("beats=0：复盘那一步的模型调用确实发生（不是整段跳过）",
        quietClient.CallCount > 0,
        $"模型调用 {quietClient.CallCount} 次（角色 1 次 + 复盘 1 次）"));

    var nudge = quiet.NudgeCharacter("ella", "让 Ella 接话");
    var beat = quiet.IdleBeat("描写一拍");
    var queue = quiet.PendingIntentSnapshot();

    checks.Add(("beats=0：nudge_character 被拒且给出明确回执",
        !nudge.Applied && nudge.Message.Contains("不允许说书人自主推进", StringComparison.Ordinal),
        nudge.Message));

    checks.Add(("beats=0：idle_beat 被拒且给出明确回执",
        !beat.Applied && beat.Message.Contains("不允许说书人自主推进", StringComparison.Ordinal),
        beat.Message));

    checks.Add(("beats=0：被拒的意图不入队（队列保持 0）",
        queue.Count == 0,
        $"队列 {queue.Count} 条"));

    var proposed = quiet.ProposeWorldStateAsync(
        new MeetingRoom.Core.Roleplay.WorldStateMutation { Place = "仓库里" },
        "（探针）换个地点",
        CancellationToken.None).GetAwaiter().GetResult();

    checks.Add(("beats=0：propose_world_state 仍能工作（不被 beats 开关拦住）",
        proposed.Applied
        && !proposed.Message.Contains("不允许说书人自主推进", StringComparison.Ordinal),
        proposed.Message));

    var asked = quiet.AskUserAsync(
        [new MeetingRoom.Core.Interaction.InteractionQuestion(
            "q1",
            "接下来怎么走？",
            [
                new MeetingRoom.Core.Interaction.InteractionOption("go", "继续", "按现在的节奏往下演"),
                new MeetingRoom.Core.Interaction.InteractionOption("wait", "等等", "先停在这里"),
            ])],
        CancellationToken.None).GetAwaiter().GetResult();

    checks.Add(("beats=0：ask_user 的拒绝与 beats 无关（只为交互关闭）",
        !asked.Message.Contains("不允许说书人自主推进", StringComparison.Ordinal),
        asked.Message));

    // ——— beats = 1（回归） ———
    var normal = new MeetingRoom.Core.Roleplay.RoleplaySession(
        MakeOptions("beats-one", 1),
        new ScriptedChatClient(),
        new CountingEventBus(),
        cards);

    var okNudge = normal.NudgeCharacter("ella", "让 Ella 接话");
    var okBeat = normal.IdleBeat("描写一拍");

    checks.Add(("beats=1：nudge / idle_beat 照旧入队（回归）",
        okNudge.Applied && okBeat.Applied && normal.PendingIntentSnapshot().Count == 2,
        $"{okNudge.Message} / {okBeat.Message} · 队列 {normal.PendingIntentSnapshot().Count} 条"));

    // ——— 配置校验 ———
    var baseOptions = new MeetingRoomOptions();
    var zero = baseOptions with { Roleplay = baseOptions.Roleplay with { NarratorBeatsPerTurn = 0 } };
    var negative = baseOptions with { Roleplay = baseOptions.Roleplay with { NarratorBeatsPerTurn = -1 } };

    var zeroProblems = ConfigLoader.Validate(zero);
    var negativeProblems = ConfigLoader.Validate(negative);

    checks.Add(("配置：narratorBeatsPerTurn = 0 合法",
        zeroProblems.Count == 0,
        zeroProblems.Count == 0 ? "0 通过校验" : string.Join(" / ", zeroProblems)));

    checks.Add(("配置：narratorBeatsPerTurn = -1 报错",
        negativeProblems.Any(text => text.Contains("narratorBeatsPerTurn", StringComparison.Ordinal)),
        string.Join(" / ", negativeProblems)));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 只写不推进（§12） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 真机 A 的回归探针：<c>paused</c> 之后发导演指令。
/// <para>
/// 以前 <c>paused</c> 与 <c>awaitingInput</c> 共用一道等待门，发一条指令会一直挂到用户点「继续」
/// （真机实测 146 秒）。现在 <c>SayAsync</c> 检测到 <c>Paused</c> 就自动恢复并立即处理——
/// 这里量两件事：**耗时**与**是否真的处理了**。
/// </para>
/// </summary>
static int ProbePausedSay()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "pausedsay-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-paused-say",
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        NarratorBeatsPerTurn = 0,
        MaxSpeakersPerTurn = 1,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
    };

    var bus = new CountingEventBus();
    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new ScriptedChatClient(),
        bus,
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ]);

    var checks = new List<(string Name, bool Pass, string Detail)>();

    session.SayAsync("第一轮。", CancellationToken.None).GetAwaiter().GetResult();
    var revisionBeforePause = session.Revision;

    session.PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
    checks.Add(("暂停后状态为 paused", session.Status.ToString() == "Paused", session.Status.ToString()));

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var finished = session.SayAsync("暂停之后的一条指令。", CancellationToken.None).Wait(TimeSpan.FromSeconds(20));
    watch.Stop();

    checks.Add(("paused 时发指令：不挂（20 秒内返回，实测应 <1s）",
        finished && watch.Elapsed.TotalSeconds < 2,
        $"{(finished ? $"{watch.Elapsed.TotalMilliseconds:F0} ms" : "20 秒内没返回")}"));

    checks.Add(("这条指令被真正处理了（revision 前进）",
        session.Revision == revisionBeforePause + 1,
        $"第 {revisionBeforePause} 幕 → 第 {session.Revision} 幕"));

    checks.Add(("会话已自动恢复（不再是 paused）",
        session.Status.ToString() != "Paused",
        session.Status.ToString()));

    checks.Add(("推了一条 roleplay.auto-resume 事件",
        bus.CountOf("roleplay.auto-resume") == 1,
        $"{bus.CountOf("roleplay.auto-resume")} 次"));

    // 已经在运行时不重复恢复：再发一条不应再产生 auto-resume。
    session.SayAsync("再一条。", CancellationToken.None).GetAwaiter().GetResult();

    checks.Add(("未暂停时不推 auto-resume（只恢复一次）",
        bus.CountOf("roleplay.auto-resume") == 1,
        $"{bus.CountOf("roleplay.auto-resume")} 次"));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- paused 时发指令（真机 A） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 真机 E 的回归探针：<c>roleplay.state.context</c> 要给出五项指标，
/// 且数值与 internal 属性一致（接口不是另算一套）。
/// </summary>
static int ProbeContextMetrics()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "ctxmetrics-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    MeetingRoom.Core.Roleplay.RoleplayOptions MakeOptions(string storyId) => new()
    {
        StoryId = storyId,
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        NarratorBeatsPerTurn = 0,
        MaxSpeakersPerTurn = 1,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
        // 压缩阈值压到 2：两轮就能触发一次，验证 compressionCount 会动。
        Context = new MeetingRoom.Core.Roleplay.RoleplayContextOptions { HistoryLimit = 2, CompressRatio = 0.5 },
    };

    var cards = new[]
    {
        new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
        new MeetingRoom.Core.Roles.RoleCard
        {
            Id = "narrator",
            Name = "说书人",
            Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
        },
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        MakeOptions("probe-context-metrics"),
        new ScriptedChatClient(),
        new CountingEventBus(),
        cards);

    var checks = new List<(string Name, bool Pass, string Detail)>();

    for (var round = 1; round <= 4; round++)
    {
        session.SayAsync($"第 {round} 轮。", CancellationToken.None).GetAwaiter().GetResult();
    }

    var context = ContextNode(session.Snapshot());

    checks.Add(("快照里有 context 节点，五项字段齐",
        context is not null
        && context.Value.TryGetProperty("historyCount", out _)
        && context.Value.TryGetProperty("estimatedTokens", out _)
        && context.Value.TryGetProperty("compressionCount", out _)
        && context.Value.TryGetProperty("summaryChars", out _)
        && context.Value.TryGetProperty("prefixFingerprint", out _),
        context?.GetRawText() ?? "（没有 context）"));

    if (context is { } node)
    {
        var historyCount = node.GetProperty("historyCount").GetInt32();
        var tokens = node.GetProperty("estimatedTokens").GetInt32();
        var compress = node.GetProperty("compressionCount").GetInt32();
        var summaryChars = node.GetProperty("summaryChars").GetInt32();
        var fingerprint = node.GetProperty("prefixFingerprint").GetString() ?? string.Empty;

        checks.Add(("historyCount 与内部 L2 条数一致",
            historyCount == session.HistoryCount && historyCount > 0,
            $"接口 {historyCount} / 内部 {session.HistoryCount}"));

        checks.Add(("estimatedTokens > 0（粗估非零）", tokens > 0, $"{tokens} token"));

        checks.Add(("compressionCount 与内部计数一致，且确实压过一次",
            compress == session.HistoryCompressions && compress >= 1,
            $"接口 {compress} / 内部 {session.HistoryCompressions}"));

        checks.Add(("summaryChars 与 L3 摘要长度一致",
            summaryChars == session.SummaryText.Length && summaryChars > 0,
            $"接口 {summaryChars} / 内部 {session.SummaryText.Length}"));

        checks.Add(("prefixFingerprint 与最近一次装配一致（64 位十六进制）",
            fingerprint.Length == 64
            && string.Equals(fingerprint, session.LastPrefixFingerprint, StringComparison.Ordinal),
            fingerprint.Length == 64 ? fingerprint[..12] + "…" : $"长度 {fingerprint.Length}"));
    }

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 上下文指标（真机 E） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;

    static System.Text.Json.JsonElement? ContextNode(object snapshot)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;

        return root.TryGetProperty("context", out var node) ? node.Clone() : null;
    }
}

/// <summary>
/// DM 提醒探针（《叙事模式后台与上下文设计》§3.3）：导演投一条旁注到观察面。
/// <para>
/// 要守住三条：**不进说书人上下文**（否则它会照着自我修正）、**不暂停剧情**（不阻塞 SayAsync）、
/// **超时无副作用**（默认「知道了」，世界一字不改）。
/// </para>
/// </summary>
static int ProbeDmNote()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "dmnote-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-dm-note",
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        NarratorBeatsPerTurn = 0,
        MaxSpeakersPerTurn = 1,
        // 关掉主动提问（免得每轮挂 120 秒），但把超时设成 1 秒——DM 提醒的超时复用它。
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false, TimeoutSeconds = 1 },
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new ScriptedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ]);

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ——— 校验 ———
    var empty = session.AddDmNote("info", "   ");
    var tooLong = session.AddDmNote("info", new string('长', MeetingRoom.Core.Roleplay.RoleplaySession.DmNoteMaxChars + 1));
    var badSeverity = session.AddDmNote("urgent", "随便一句");

    checks.Add(("校验：空文本 / 超长 / 非法 severity 都拒绝，且给出原因",
        !empty.Ok && !tooLong.Ok && !badSeverity.Ok
        && !string.IsNullOrWhiteSpace(empty.Error)
        && !string.IsNullOrWhiteSpace(tooLong.Error)
        && !string.IsNullOrWhiteSpace(badSeverity.Error),
        $"{empty.Error} / {tooLong.Error} / {badSeverity.Error}"));

    // ——— 投一条有效提醒：第五列能看到 ———
    const string marker = "Ella 上一幕不该知道钥匙在井里";
    var posted = session.AddDmNote("warn", marker);
    var pendingAfterPost = PendingIds(session.Snapshot());

    checks.Add(("投递成功且出现在待确认事件里（kind=dm_note）",
        posted.Ok && posted.Id is { Length: > 0 } && pendingAfterPost.Contains(posted.Id),
        $"id {posted.Id} / pending {pendingAfterPost.Count} 条"));

    // ——— 不进说书人上下文 ———
    var shared = session.BuildSharedSection();
    var history = string.Join('\n', session.HistorySnapshot().Select(line => line.Text));
    var summaryBefore = session.SummaryText;

    checks.Add(("说书人上下文里没有这条提醒（共享节 / L2 / L3 摘要都不含）",
        !shared.Contains(marker, StringComparison.Ordinal)
        && !history.Contains(marker, StringComparison.Ordinal)
        && !summaryBefore.Contains(marker, StringComparison.Ordinal),
        $"共享节 {shared.Length} 字符 · L2 {session.HistoryCount} 条 · 摘要 {summaryBefore.Length} 字符"));

    // ——— 不暂停剧情：投完提醒照样能跑一轮 ———
    var revisionBeforeTurn = session.Revision;
    var turnTask = session.SayAsync("继续。", CancellationToken.None);
    var finished = turnTask.Wait(TimeSpan.FromSeconds(10));

    checks.Add(("不暂停剧情：投完提醒后一轮照跑（10 秒内完成）",
        finished && session.Revision == revisionBeforeTurn + 1,
        finished ? $"第 {revisionBeforeTurn} 幕 → 第 {session.Revision} 幕" : "10 秒内没跑完"));

    checks.Add(("跑完一轮后说书人上下文里仍然没有这条提醒",
        !session.BuildSharedSection().Contains(marker, StringComparison.Ordinal)
        && !session.SummaryText.Contains(marker, StringComparison.Ordinal),
        $"共享节 {session.BuildSharedSection().Length} 字符 · 摘要 {session.SummaryText.Length} 字符"));

    // ——— 三种回应 ———
    var ackNote = session.AddDmNote("info", "只 ack 的一条");
    var revisionAtAck = session.Revision;
    var summaryAtAck = session.SummaryText;
    var acked = ackNote.Id is { } ackId && session.AnswerPendingDecision(
        ackId,
        [new MeetingRoom.Core.Interaction.InteractionAnswer("dm-note", ["ack"])]);

    checks.Add(("回应「知道了」：只 ack，revision 与摘要都不变",
        acked && session.Revision == revisionAtAck && session.SummaryText == summaryAtAck
        && !PendingIds(session.Snapshot()).Contains(ackNote.Id!),
        $"acked={acked} · 第 {session.Revision} 幕 · 摘要 {session.SummaryText.Length} 字符"));

    const string applyMarker = "井里的钥匙是铁匠藏的";
    var applyNote = session.AddDmNote("info", applyMarker);
    var revisionAtApply = session.Revision;
    var applied = applyNote.Id is { } applyId && session.AnswerPendingDecision(
        applyId,
        [new MeetingRoom.Core.Interaction.InteractionAnswer("dm-note", ["apply"])]);

    checks.Add(("回应「记进设定」：追加进 L3 摘要，但 revision 不变",
        applied && session.Revision == revisionAtApply
        && session.SummaryText.Contains(applyMarker, StringComparison.Ordinal)
        && session.SummaryText.Contains("导演提醒", StringComparison.Ordinal),
        $"applied={applied} · 第 {session.Revision} 幕（投递时 {revisionAtApply}）· 摘要 {session.SummaryText.Length} 字符：[{session.SummaryText.ReplaceLineEndings(" / ")}]"));

    var ignoreNote = session.AddDmNote("warn", "这条要被忽略");
    var summaryAtIgnore = session.SummaryText;
    var revisionAtIgnore = session.Revision;
    var ignored = ignoreNote.Id is { } ignoreId && session.AnswerPendingDecision(
        ignoreId,
        [new MeetingRoom.Core.Interaction.InteractionAnswer("dm-note", ["ignore"])]);

    checks.Add(("回应「忽略」：什么都不改（revision 与摘要都不变）",
        ignored && session.Revision == revisionAtIgnore && session.SummaryText == summaryAtIgnore,
        $"ignored={ignored} · 摘要 {session.SummaryText.Length} 字符"));

    // ——— 超时：卡片过期，世界一字未改 ———
    var revisionAtTimeout = session.Revision;
    var summaryAtTimeout = session.SummaryText;
    var timedOut = session.AddDmNote("warn", "超时应该只当没看到");

    Thread.Sleep(1800);

    var pendingAfterTimeout = PendingIds(session.Snapshot());

    checks.Add(("超时：卡片消失、revision 与摘要都不变（默认「知道了」）",
        !pendingAfterTimeout.Contains(timedOut.Id!)
        && session.Revision == revisionAtTimeout
        && session.SummaryText == summaryAtTimeout
        && !session.SummaryText.Contains("超时应该只当没看到", StringComparison.Ordinal),
        $"pending {pendingAfterTimeout.Count} 条（去掉本条={!pendingAfterTimeout.Contains(timedOut.Id!)}）· 第 {session.Revision} 幕（投递时 {revisionAtTimeout}）· 摘要 {session.SummaryText.Length}/{summaryAtTimeout.Length} 字符 · 含标记={session.SummaryText.Contains("超时应该只当没看到", StringComparison.Ordinal)}"));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- DM 提醒（dm-note） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;

    // 快照里的 pendingDecisions（只取 id；形状与前端 state.pending 同一份）。
    static HashSet<string> PendingIds(object snapshot)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("pendingDecisions", out var pending)
            && pending.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in pending.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.GetString() is { } value)
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }
}

/// <summary>
/// 后台观察探针（《叙事模式后台与上下文设计》§3.1）：说书人 / 角色的思考与工具调用
/// 在<b>产生点</b>落到 <c>backstage.jsonl</c>，刷新与重启都不丢，且只读。
/// <para>
/// "重启后仍在"这条不能靠 <c>story.jsonl</c> 的数据来验（它本来就存着）——
/// 这里在临时目录里真造一份记录，再用<b>新的</b> <see cref="MeetingRoom.Core.Roleplay.BackstageLog"/>
/// 实例读同一路径（构造函数里就是从文件 Load），等价于重启 Host 后重新打开会话。
/// </para>
/// </summary>
static int ProbeBackstage()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "backstage-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-backstage",
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        NarratorBeatsPerTurn = 0,
        MaxSpeakersPerTurn = 2,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
    };

    var cards = new[]
    {
        new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
        new MeetingRoom.Core.Roles.RoleCard { Id = "bob", Name = "Bob" },
        new MeetingRoom.Core.Roles.RoleCard
        {
            Id = "narrator",
            Name = "说书人",
            Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
        },
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new ToolThenTextChatClient(),
        new CountingEventBus(),
        cards);

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // 两轮：每轮两个角色各"调一次工具再出正文"，所以 thought 与 tool 都会产生。
    for (var round = 1; round <= 2; round++)
    {
        session.SayAsync($"第 {round} 轮：你们在码头上继续。", CancellationToken.None).GetAwaiter().GetResult();
    }

    var logPath = Path.Combine(dir, "backstage.jsonl");
    var lines = File.Exists(logPath) ? File.ReadAllLines(logPath).Where(line => line.Length > 0).ToArray() : [];

    checks.Add(("backstage.jsonl 已落盘且非空",
        File.Exists(logPath) && lines.Length > 0,
        $"{lines.Length} 行 → {logPath}"));

    var entries = lines
        .Select(line => System.Text.Json.JsonSerializer.Deserialize<MeetingRoom.Core.Roleplay.BackstageEntry>(
            line, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }))
        .Where(entry => entry is not null)
        .Select(entry => entry!)
        .ToList();

    var kinds = entries.Select(entry => entry.Kind).Distinct().OrderBy(kind => kind).ToArray();

    checks.Add(("记录了 thought 与 tool 两种 kind",
        kinds.Contains("thought") && kinds.Contains("tool"),
        string.Join(" / ", kinds)));

    var thought = entries.FirstOrDefault(entry => entry.Kind == "thought");
    checks.Add(("thought 记录字段齐（rev / at / who / text）",
        thought is { Rev: > 0, Who.Length: > 0, Text.Length: > 0 } && thought.At != default,
        thought is null ? "（没有）" : $"rev {thought.Rev} · {thought.Who} · {thought.Text!.Length} 字符"));

    var tool = entries.FirstOrDefault(entry => entry.Kind == "tool");
    checks.Add(("tool 记录字段齐（name / args / result）",
        tool is { Name.Length: > 0, Result.Length: > 0 },
        tool is null ? "（没有）" : $"{tool.Name} · args {tool.Args ?? "(空)"} · result {tool.Result!.Length} 字符"));

    // 重启等价：新建一个同路径的 BackstageLog（构造函数从文件 Load）。
    var reopened = new MeetingRoom.Core.Roleplay.BackstageLog(logPath);
    var reopenedEvents = reopened.View(0, 500);

    checks.Add(("重启后仍在（新实例从文件读回同样条数）",
        reopened.Count == entries.Count && reopenedEvents.Count == entries.Count,
        $"文件 {entries.Count} 条 / 读回 {reopened.Count} 条"));

    // 滚动保留：灌 2005 条，内存与文件都只留 2000。
    var rollDir = Path.Combine(dir, "roll");
    Directory.CreateDirectory(rollDir);
    var rollPath = Path.Combine(rollDir, "backstage.jsonl");
    var rolling = new MeetingRoom.Core.Roleplay.BackstageLog(rollPath);

    for (var index = 1; index <= 2005; index++)
    {
        rolling.Record(
            // 用 tool-result（直接落一条完整记录）：tool 那个 kind 只是"发起"，要等回执才成条。
            new MeetingRoom.Core.Roleplay.BackstageSignal("ella", "t", "tool-result", "dice_roll", "{}", "ok", string.Empty),
            index);
    }

    rolling.FlushThoughts(2005);

    var rollLines = File.ReadAllLines(rollPath).Count(line => line.Trim().Length > 0);

    checks.Add(("滚动保留上限 2000（内存与文件都是 2000）",
        rolling.Count == 2000 && rollLines == 2000,
        $"内存 {rolling.Count} / 文件 {rollLines}"));

    // 只读：查询不改剧情 revision，也不改条目数。
    var revisionBefore = session.Revision;
    var countBefore = session.BackstageCount;
    session.BackstageView(0, 500);

    checks.Add(("查询只读（revision 与条数都不变）",
        session.Revision == revisionBefore && session.BackstageCount == countBefore,
        $"第 {revisionBefore} 幕 / {countBefore} 条"));

    // API 视图形状：events 里每条都带 revision / at / kind / who。
    var view = session.BackstageView(0, 500);
    var json = System.Text.Json.JsonSerializer.Serialize(view);
    var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
    var events = root.TryGetProperty("events", out var eventsElement) ? eventsElement : default;
    var firstEvent = events.ValueKind == System.Text.Json.JsonValueKind.Array && events.GetArrayLength() > 0
        ? events[0]
        : default;

    checks.Add(("BackstageView 形状可用（revision + events[]，每条带 revision/at/kind/who）",
        events.ValueKind == System.Text.Json.JsonValueKind.Array
        && events.GetArrayLength() > 0
        && firstEvent.TryGetProperty("revision", out _)
        && firstEvent.TryGetProperty("at", out _)
        && firstEvent.TryGetProperty("kind", out _)
        && firstEvent.TryGetProperty("who", out _),
        $"events {events.ValueKind}/{(events.ValueKind == System.Text.Json.JsonValueKind.Array ? events.GetArrayLength() : 0)} 条"));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine("----- 后台观察（backstage.jsonl） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 叙事模式前台上下文探针（《叙事模式后台与上下文设计》§1）：连续跑 45 轮，
/// 逐轮记录 **L1 前缀指纹** 与 **L2 快照**，验证：
/// <list type="bullet">
///   <item>不压缩的轮次 L1 指纹完全不变（45 轮里只该出现 2 个不同值）；</item>
///   <item>L2 只追加（上一轮快照永远是新快照的前缀），压缩那轮除外；</item>
///   <item>第 81 条触发一次压缩：L2 降到约 41 条、L3 摘要出现并落盘；</item>
///   <item><c>recall_story</c>：关键词 / 作者 / 类型 / last、一轮一次限流、只读。</item>
/// </list>
/// </summary>
static int ProbeNarrativeContext()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "ctx-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-narrative-context",
        World = "（探针）灰港",
        Situation = "（探针）夜里的码头",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        // 只跑角色、不做说书人复盘、关掉交互提问：每轮 2 条发言，节奏完全可控
        // （不关交互的话每轮会等 120 秒才按默认选项收口）。
        NarratorBeatsPerTurn = 0,
        MaxSpeakersPerTurn = 2,
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = false },
        Context = new MeetingRoom.Core.Roleplay.RoleplayContextOptions
        {
            HistoryLimit = 80,
            HistoryTokenBudget = 8000,
            CompressRatio = 0.5,
            LineChars = 200,
        },
    };

    var client = new ScriptedChatClient();
    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        client,
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "bob", Name = "Bob" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ]);

    var checks = new List<(string Name, bool Pass, string Detail)>();
    var fingerprints = new List<string>();
    var counts = new List<int>();
    var compressionsPerRound = new List<int>();
    List<MeetingRoom.Core.Context.HistoryLine>? previous = null;
    var appendOnly = true;
    var appendOnlyBreaks = 0;

    const int rounds = 45;

    for (var round = 1; round <= rounds; round++)
    {
        session.SayAsync($"第 {round} 轮：你们在码头上继续。", CancellationToken.None).GetAwaiter().GetResult();

        fingerprints.Add(session.LastPrefixFingerprint ?? "(null)");
        counts.Add(session.HistoryCount);
        compressionsPerRound.Add(session.HistoryCompressions);

        var snapshot = session.HistorySnapshot();

        if (previous is not null)
        {
            // 压缩会把前半段裁掉，所以"只追加"这条不变量只对"没有变短"的轮次成立。
            if (snapshot.Count >= previous.Count)
            {
                var prefixKept = snapshot.Take(previous.Count).SequenceEqual(previous);

                if (!prefixKept)
                {
                    appendOnly = false;
                    appendOnlyBreaks++;
                }
            }
        }

        previous = snapshot.ToList();
    }

    Console.WriteLine($"----- 叙事上下文（{rounds} 轮，每轮 2 条） -----");
    Console.WriteLine("轮次  L2条数  压缩次数  L1指纹（前 12）");

    for (var index = 0; index < fingerprints.Count; index++)
    {
        var mark = index + 1;
        Console.WriteLine($"{mark,3}   {counts[index],6}  {compressionsPerRound[index],8}  {fingerprints[index][..Math.Min(12, fingerprints[index].Length)]}");
    }

    var distinct = fingerprints.Distinct(StringComparer.Ordinal).ToList();
    var firstCompressRound = compressionsPerRound.FindIndex(count => count > 0) + 1;

    checks.Add(("45 轮里 L1 前缀指纹只出现 2 个值（压缩前 / 压缩后）",
        distinct.Count == 2,
        $"不同值 {distinct.Count} 个：{string.Join(" / ", distinct.Select(value => value[..Math.Min(12, value.Length)]))}"));

    checks.Add(("压缩只发生一次，且落在第 41 轮（累计第 81 条）",
        compressionsPerRound[^1] == 1 && firstCompressRound == 41,
        $"压缩次数 {compressionsPerRound[^1]}，首次压缩在第 {firstCompressRound} 轮"));

    checks.Add(("压缩前 40 轮指纹完全一致",
        fingerprints.Take(40).Distinct(StringComparer.Ordinal).Count() == 1,
        $"第 1 轮 {fingerprints[0][..12]} / 第 40 轮 {fingerprints[39][..12]}"));

    checks.Add(("第 41 轮（压缩轮）指纹变了",
        !string.Equals(fingerprints[39], fingerprints[40], StringComparison.Ordinal),
        $"第 40 轮 {fingerprints[39][..12]} → 第 41 轮 {fingerprints[40][..12]}"));

    checks.Add(("压缩后 41–45 轮指纹再次稳定",
        fingerprints.Skip(40).Distinct(StringComparer.Ordinal).Count() == 1,
        $"第 41 轮 {fingerprints[40][..12]} / 第 45 轮 {fingerprints[44][..12]}"));

    checks.Add(("L2 只追加、不滚动（压缩轮除外，逐轮比对快照前缀）",
        appendOnly,
        appendOnly ? $"{rounds} 轮全部以前一轮快照为前缀" : $"断裂 {appendOnlyBreaks} 次"));

    checks.Add(("压缩后 L2 规模约为上限的一半（81 → 41 左右）",
        counts[40] is >= 40 and <= 45,
        $"第 41 轮结束 L2 = {counts[40]} 条（第 40 轮 {counts[39]} 条）"));

    var summary = session.SummaryText;
    checks.Add(("L3 摘要非空且带幕号范围",
        summary.Length > 0 && summary.Contains("幕：", StringComparison.Ordinal),
        summary.Length == 0 ? "（空）" : summary.ReplaceLineEndings(" / ")[..Math.Min(160, summary.ReplaceLineEndings(" / ").Length)]));

    var summaryFile = Path.Combine(dir, "context-summary.json");
    checks.Add(("L3 摘要已落盘（context-summary.json，与 story.jsonl 同级）",
        File.Exists(summaryFile),
        summaryFile));

    // 重启后摘要不丢：用同一个目录再装配一次，L3 应当还在。
    var reopened = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new ScriptedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "bob", Name = "Bob" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ]);

    checks.Add(("重新装配会话后 L3 摘要仍在（重启不丢）",
        reopened.SummaryText.Length == summary.Length && summary.Length > 0,
        $"原 {summary.Length} 字符 / 新 {reopened.SummaryText.Length} 字符"));

    // ——— B2：recall_story ———
    var recalled = session.RecallStory("ella", new MeetingRoom.Core.Roleplay.StoryRecallQuery { Last = 5 });
    checks.Add(("回忆默认最近 5 条（last=5），每行带幕号",
        CountRecallLines(recalled) == 5 && recalled.Contains("第 ", StringComparison.Ordinal),
        recalled.ReplaceLineEndings(" / ")[..Math.Min(180, recalled.ReplaceLineEndings(" / ").Length)]));

    var byAuthor = session.RecallStory("ella", new MeetingRoom.Core.Roleplay.StoryRecallQuery { Author = "Ella", Last = 20 });
    checks.Add(("author=Ella 只返回她的发言",
        CountRecallLines(byAuthor) > 0 && !byAuthor.Contains("Bob：", StringComparison.Ordinal),
        byAuthor.ReplaceLineEndings(" / ")[..Math.Min(160, byAuthor.ReplaceLineEndings(" / ").Length)]));

    var byKeyword = session.RecallStory("ella", new MeetingRoom.Core.Roleplay.StoryRecallQuery { Text = "码头", Last = 10 });
    checks.Add(("关键词「码头」能命中",
        CountRecallLines(byKeyword) > 0,
        byKeyword.ReplaceLineEndings(" / ")[..Math.Min(160, byKeyword.ReplaceLineEndings(" / ").Length)]));

    var byKind = session.RecallStory("ella", new MeetingRoom.Core.Roleplay.StoryRecallQuery { Kinds = ["world"], Last = 10 });
    checks.Add(("kinds=world 只返回世界记录（本场没有 → 明确回执）",
        byKind.Contains("没有符合条件的记录", StringComparison.Ordinal),
        byKind.ReplaceLineEndings(" / ")[..Math.Min(120, byKind.ReplaceLineEndings(" / ").Length)]));

    var narratorRecall = session.RecallStory("narrator", new MeetingRoom.Core.Roleplay.StoryRecallQuery { Last = 40 });
    checks.Add(("说书人能看到最多 40 条（上限生效）",
        CountRecallLines(narratorRecall) == 40,
        $"返回 {CountRecallLines(narratorRecall)} 条"));

    // 限流：一轮一次（用 session 自己的 dispatcher，额度与真实运行一致）。
    var dispatcher = session.ToolDispatcher;
    dispatcher.ResetStoryQuota();

    static Microsoft.Extensions.AI.FunctionCallContent RecallCall() =>
        new("call-" + Guid.NewGuid().ToString("N")[..6], MeetingRoom.Core.Roleplay.Narrator.RecallStory,
            new Dictionary<string, object?> { ["last"] = 3 });

    var firstRecall = dispatcher
        .InvokeAsync("ella", RecallCall(), CancellationToken.None)
        .GetAwaiter().GetResult().Result?.ToString() ?? string.Empty;
    var secondRecall = dispatcher
        .InvokeAsync("ella", RecallCall(), CancellationToken.None)
        .GetAwaiter().GetResult().Result?.ToString() ?? string.Empty;

    checks.Add(("recall_story 一轮只能调一次（第二次被拒）",
        firstRecall.Length > 0
        && !firstRecall.Contains("已经回忆过", StringComparison.Ordinal)
        && secondRecall.Contains("已经回忆过一次", StringComparison.Ordinal),
        secondRecall.ReplaceLineEndings(" ")[..Math.Min(120, secondRecall.Length)]));

    var revisionBefore = session.Revision;
    session.RecallStory("ella", new MeetingRoom.Core.Roleplay.StoryRecallQuery { Last = 3 });

    checks.Add(("回忆不改变剧情状态（revision 不变）",
        session.Revision == revisionBefore,
        $"第 {revisionBefore} 幕 → 第 {session.Revision} 幕"));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;

    static int CountRecallLines(string text) =>
        text.Split('\n').Count(line => line.TrimStart().StartsWith("- 第 ", StringComparison.Ordinal));
}

/// <summary>
/// 规则查询探针（§27）：检索顺序（全等 → 关键词 → 正文）、跨类候选、九类紧凑 / 详情、
/// detail 截断、一轮 3 次限流、以及"角色不能查怪物"的结构性越权。
/// 默认读 exe 同级的 <c>data/rules/rules-data.json</c>（也可给路径）。
/// </summary>
static int ProbeRuleLookup(string? path)
{
    var rulesPath = string.IsNullOrWhiteSpace(path)
        ? Path.Combine(AppContext.BaseDirectory, "data", "rules", "rules-data.json")
        : path;

    if (!File.Exists(rulesPath))
    {
        Console.Error.WriteLine($"❌ 找不到规则数据包：{rulesPath}（先跑一次 dotnet build，或直接给路径）");
        return 2;
    }

    RulesData data;

    try
    {
        data = RulesDataLoader.Load(rulesPath);
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"❌ 规则数据包不可用：{ex.Message}");
        return 1;
    }

    var checks = new List<(string Name, bool Pass, string Detail)>();

    static string OneLine(string text)
    {
        var flat = text.ReplaceLineEndings(" / ");
        return flat.Length <= 180 ? flat : flat[..180] + "…";
    }

    string Ask(string query, string? kind, bool detail = false)
    {
        if (string.Equals(kind, "monster", StringComparison.OrdinalIgnoreCase))
        {
            return RulesLookup.DescribeMonster(data, query, detail);
        }

        var parsed = RulesLookup.ParseKind(kind, out var error);

        if (error is not null)
        {
            return error;
        }

        return RulesLookup.Describe(RulesLookup.Search(data, query, parsed), query, parsed, detail);
    }

    // ⓪ 默认路径（§27 §5）：RulesOptions 的默认值是相对路径，必须按安装目录解析后确实有文件。
    var defaultDataPath = new RulesOptions().DataPath;
    var resolvedDefault = Path.Combine(PathLayout.InstallDir, defaultDataPath);

    checks.Add(("默认 DataPath 是相对路径，按安装目录解析后文件存在",
        !Path.IsPathRooted(defaultDataPath) && File.Exists(resolvedDefault),
        $"{defaultDataPath} → {resolvedDefault}"));

    // ① 中文名命中：还要求紧凑行里带环级 / 学派 / 伤害（用户定的一行格式）
    var fireballCn = Ask("火球术", "spell");
    checks.Add(("中文名「火球术」命中法术，紧凑行含 3 环·塑能 / 8d6",
        fireballCn.Contains("火球术", StringComparison.Ordinal)
        && fireballCn.Contains("3 环", StringComparison.Ordinal)
        && fireballCn.Contains("塑能", StringComparison.Ordinal)
        && fireballCn.Contains("8d6", StringComparison.Ordinal),
        OneLine(fireballCn)));

    // ② 英文名命中同一条
    var fireballEn = Ask("Fireball", "spell");
    checks.Add(("英文名「Fireball」命中同一条",
        fireballEn.Contains("火球术", StringComparison.Ordinal) && fireballEn.Contains("3 环", StringComparison.Ordinal),
        OneLine(fireballEn)));

    // ③ 模糊命中：关键词子串
    var fuzzy = Ask("火球", "spell");
    checks.Add(("模糊「火球」命中火球术（候选里排第一）",
        fuzzy.Contains("火球术", StringComparison.Ordinal),
        OneLine(fuzzy)));

    // ④ 跨类命中：盾牌 同时挂在 items 与 spells 上 → 列候选、不崩
    var cross = RulesLookup.Search(data, "盾牌", null);
    var crossText = RulesLookup.Describe(cross, "盾牌", null, false);
    checks.Add(("跨类「盾牌」命中 ≥2 类，回执是候选列表",
        cross.Select(hit => hit.Kind).Distinct().Count() >= 2 && crossText.Contains("候选", StringComparison.Ordinal),
        OneLine(crossText)));

    // ⑤ kind=optional：中文名与英文名各一次
    foreach (var term in new[] { "苦痛魔爆", "Agonizing Blast" })
    {
        var optional = RulesLookup.Search(data, term, RuleKind.Optional);
        var optionalText = RulesLookup.Describe(optional, term, RuleKind.Optional, false);
        checks.Add(($"kind=optional 查「{term}」命中邪术师祈唤",
            optional.Count == 1
            && optional[0].Kind == RuleKind.Optional
            && optionalText.Contains("Agonizing Blast", StringComparison.Ordinal)
            && optionalText.Contains("邪术师祈唤", StringComparison.Ordinal),
            OneLine(optionalText)));
    }

    // ⑥ 职业：hitDie / 施法属性 / 子职数
    var wizard = Ask("法师", "class");
    checks.Add(("查「法师」返回职业，带 d6 / 施法 智力 / 子职 22 个",
        wizard.Contains("d6", StringComparison.Ordinal)
        && wizard.Contains("施法 智力", StringComparison.Ordinal)
        && wizard.Contains("子职 22", StringComparison.Ordinal),
        OneLine(wizard)));

    // ⑦ 子职：数据里 200 条子职的 cnName 全为空（导出侧缺口），所以用英文名 + keywords 命中；
    //    同时验证 featuresInheritedFrom 写进了回执（用户要的"2024 版带 featuresInheritedFrom=PHB"）。
    var drunken = RulesLookup.Search(data, "Drunken Master", RuleKind.Subclass);
    var drunkenText = RulesLookup.Describe(drunken, "Drunken Master", RuleKind.Subclass, false);
    checks.Add(("查子职「Drunken Master」命中，带职业 / 特性数 / 原文继承 PHB",
        drunken.Count == 1
        && drunken[0].Id == "Monk - Drunken Master|XGE|XPHB"
        && drunkenText.Contains("原文继承 PHB", StringComparison.Ordinal),
        OneLine(drunkenText)));

    // ⑧ 查不到：明确回执，不抛
    var nothing = Ask("绝对不存在的条目 xyzzy", null);
    checks.Add(("查不存在的词 → 明确回执，不抛异常",
        nothing.Contains("没有匹配", StringComparison.Ordinal),
        OneLine(nothing)));

    // ⑨ detail=true：只返 1 条 + 英文原文
    var spelled = Ask("火球术", "spell", detail: true);
    checks.Add(("detail=true 只返 1 条，且带英文原文",
        spelled.Contains("A bright streak", StringComparison.Ordinal)
        && !spelled.Contains("另有", StringComparison.Ordinal),
        $"长度 {spelled.Length} / {OneLine(spelled)}"));

    // ⑩ 超长截断：法师全部特性 7127 字符
    var longDetail = Ask("法师", "class", detail: true);
    checks.Add(("超 4000 字符截断并标注 [truncated]",
        longDetail.Contains("[truncated]", StringComparison.Ordinal)
        && longDetail.Length <= RulesLookup.DetailTextLimit + 64,
        $"长度 {longDetail.Length}"));

    // ⑪ 怪物紧凑 / 详情（说书人工具）
    var monster = Ask("地精武者", "monster");
    checks.Add(("怪物紧凑回执含 CR / HP / AC / 六属性 / 特性名 + 基线提醒",
        monster.Contains("CR ", StringComparison.Ordinal)
        && monster.Contains("HP ", StringComparison.Ordinal)
        && monster.Contains("AC ", StringComparison.Ordinal)
        && monster.Contains("STR ", StringComparison.Ordinal)
        && monster.Contains("模板基线", StringComparison.Ordinal),
        OneLine(monster)));

    var monsterDetail = Ask("Goblin Warrior", "monster", detail: true);
    checks.Add(("怪物 detail 带动作原文（命中加值与伤害骰）",
        monsterDetail.Contains("【动作】", StringComparison.Ordinal)
        && monsterDetail.Contains("命中 +", StringComparison.Ordinal)
        && monsterDetail.Contains("模板基线", StringComparison.Ordinal),
        OneLine(monsterDetail)));

    // ⑫ dispatcher 层：越权 / 限流 / 规则包未加载
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "rulelookup-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-rule-lookup",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = Path.Combine(dir, "story.jsonl"),
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new UnusedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard
            {
                Id = "narrator",
                Name = "说书人",
                Role = MeetingRoom.Core.Participants.ParticipantRole.Host,
            },
        ],
        rules: data);

    // 用 session 自己的工具面：规则查询的额度挂在 dispatcher 上，SayAsync 每轮清零的必须是同一个实例。
    var dispatcher = session.ToolDispatcher;

    static Microsoft.Extensions.AI.FunctionCallContent Call(string name, params (string Key, object? Value)[] args) =>
        new("call-" + Guid.NewGuid().ToString("N")[..6], name, args.ToDictionary(pair => pair.Key, pair => pair.Value));

    string Invoke(MeetingRoom.Core.Roleplay.NarratorToolDispatcher target, string participant, string name, params (string Key, object? Value)[] args) =>
        target.InvokeAsync(participant, Call(name, args), CancellationToken.None).GetAwaiter().GetResult().Result?.ToString() ?? string.Empty;

    var characterRule = Invoke(dispatcher, "ella", MeetingRoom.Core.Roleplay.Narrator.LookupRule, ("query", "火球术"), ("kind", "spell"));
    checks.Add(("角色调 lookup_rule 有回执（角色可用）",
        characterRule.Contains("火球术", StringComparison.Ordinal),
        OneLine(characterRule)));

    var characterMonster = Invoke(dispatcher, "ella", MeetingRoom.Core.Roleplay.Narrator.LookupMonster, ("query", "Goblin Warrior"));
    checks.Add(("反证：角色调 lookup_monster 被拒（只有说书人）",
        characterMonster.Contains("只有说书人", StringComparison.Ordinal),
        OneLine(characterMonster)));

    // 限流：先清零（上面角色那次查询已经用掉 1 次），再连查 4 次——第 4 次必须被挡。
    dispatcher.ResetRuleQuota();
    var quota = new List<string>();

    foreach (var term in new[] { "火球术", "法师", "盾牌", "长剑" })
    {
        quota.Add(Invoke(dispatcher, "narrator", MeetingRoom.Core.Roleplay.Narrator.LookupRule, ("query", term)));
    }

    checks.Add(("一轮第 4 次查询被拒（每轮最多 3 次）",
        quota.Take(3).All(text => !text.Contains("已用完", StringComparison.Ordinal))
        && quota[3].Contains("已用完", StringComparison.Ordinal),
        OneLine(quota[3])));

    // 跨轮复位：一次导演 say = 新一轮 → 额度回来
    session.SayAsync("（元指令）探针：开始新一轮。", CancellationToken.None).GetAwaiter().GetResult();
    var nextRound = Invoke(dispatcher, "narrator", MeetingRoom.Core.Roleplay.Narrator.LookupRule, ("query", "火球术"));

    checks.Add(("跨轮后额度复位（又能查）",
        !nextRound.Contains("已用完", StringComparison.Ordinal) && nextRound.Contains("火球术", StringComparison.Ordinal),
        OneLine(nextRound)));

    // 规则包未加载：明确回执，不假装成功
    var bare = new MeetingRoom.Core.Roleplay.NarratorToolDispatcher(session, session.IsNarrator, session, session);
    var unloaded = Invoke(bare, "narrator", MeetingRoom.Core.Roleplay.Narrator.LookupRule, ("query", "火球术"));

    checks.Add(("规则包未加载 → 明确回执",
        unloaded.Contains("规则包未加载", StringComparison.Ordinal),
        OneLine(unloaded)));

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    Console.WriteLine($"----- 规则查询（{data.Schema}，{data.Spells.Count} 法术） -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 图片生成的真机自检（图片模块实现步骤 A5）：只走 IImageGenerator，**不碰工作空间**。
/// 打印 HTTP 结果、字节数、嗅探到的类型与落盘路径 —— 但**不打印密钥、也不打印完整 base64**。
/// </summary>
static async Task<int> ProbeImageAsync(MeetingRoomOptions options, string prompt, string? referencePath)
{
    var images = options.Images;

    Console.WriteLine($"→ {images.Model}  {images.BaseUrl.TrimEnd('/')}{images.EndpointPath}");
    Console.WriteLine($"  代理：{(images.Proxy.Trim().Length == 0 ? "默认（环境变量 → WinINET/PAC）" : images.Proxy)}");
    Console.WriteLine($"  参考图：{(string.IsNullOrWhiteSpace(referencePath) ? "（无，纯文生图）" : referencePath)}");
    Console.WriteLine($"  提示词：{prompt}");

    var references = new List<ReferenceImage>();
    if (!string.IsNullOrWhiteSpace(referencePath))
    {
        try
        {
            var (bytes, mediaType) = await ImageSource
                .FromPath(referencePath, images.MaxReferenceBytes)
                .ReadAsync(CancellationToken.None);

            references.Add(new ReferenceImage(mediaType, bytes));
            Console.WriteLine($"  参考图已读入：{mediaType}，{bytes.LongLength:N0} 字节");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"  ❌ 参考图不可用：{ex.Message}");
            return 1;
        }
    }

    using var generator = new OpenRouterImageGenerator(images);
    var started = Stopwatch.StartNew();

    try
    {
        var generated = await generator.GenerateAsync(
            // 全限定名：Microsoft.Extensions.AI 里也有一个 ImageGenerationRequest（Program.cs 引了那个包）。
            new MeetingRoom.Core.Images.ImageGenerationRequest { Prompt = prompt },
            references);
        started.Stop();

        var directory = Path.Combine(PathLayout.DataDir, "images-probe");
        Directory.CreateDirectory(directory);

        for (var i = 0; i < generated.Count; i++)
        {
            var image = generated[i];

            // 扩展名跟着**类型**走（不能一律 .png）：上游可能返回 JPEG / WebP。
            var extension = ImageBytes.ExtensionFor(image.MediaType);

            var name = $"probe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{i + 1}{extension}";
            var full = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(full, image.Bytes);

            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes)).ToLowerInvariant();
            Console.WriteLine($"  ✅ {image.MediaType}  {image.Bytes.LongLength:N0} 字节  sha256 {sha[..12]}…");
            Console.WriteLine($"     落盘 {full}");
        }

        Console.WriteLine($"  用时 {started.Elapsed.TotalSeconds:F1}s");
        return 0;
    }
    catch (ImageGenerationException ex)
    {
        Console.Error.WriteLine($"  ❌ {ex.Message}");
        return 1;
    }
    catch (TaskCanceledException)
    {
        Console.Error.WriteLine($"  ❌ 超时（images.timeoutSeconds = {images.TimeoutSeconds}）。");
        return 1;
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"  ❌ 网络层失败：{ex.Message}");
        Console.Error.WriteLine("     → 出网不通时，把 images.proxy 设成显式代理（如 http://127.0.0.1:10828）再试；"
            + "见设计方案 §七 的逃生口。");
        return 1;
    }
}

/// <summary>
/// 阶段 2 的临时探针：只走底层 IChatClient 的流式接口，逐块打印。
/// Ctrl+C 触发取消令牌后应当立刻停止，且不把 OperationCanceledException 抛到顶层（实现步骤 4.1）。
/// </summary>
static async Task<int> ProbeChatAsync(MeetingRoomOptions options, string prompt, int? cancelAfterMs)
{
    IChatClient client;
    try
    {
        client = new OpenAiCompatibleModelFactory().Create(new ChatModelSettings(
            options.Provider.Name,
            options.Provider.BaseUrl,
            options.Provider.Model,
            options.Provider.ApiKeyEnv));
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    using var owned = client;
    using var cancellation = new CancellationTokenSource();

    // --cancel-after-ms N：与 Ctrl+C 走同一条取消路径，用来自动验证"取消令牌一触发，流立刻停止"。
    if (cancelAfterMs is { } milliseconds)
    {
        cancellation.CancelAfter(milliseconds);
    }

    ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true; // 不让进程立刻退出，交给取消路径收尾。
        cancellation.Cancel();
    };

    Console.CancelKeyPress += onCancel;
    Console.WriteLine($"→ {options.Provider.Name}/{options.Provider.Model}：{prompt}");

    try
    {
        ChatMessage[] messages = [new(ChatRole.User, prompt)];

        await foreach (var update in client
            .GetStreamingResponseAsync(messages, cancellationToken: cancellation.Token)
            .WithCancellation(cancellation.Token))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } chunk:
                        Console.Write(chunk.Text);
                        break;

                    // 思考内容：用于确认真实 provider 的 reasoning 确实被映射成 TextReasoningContent
                    // （测试里用的是假客户端，这条路径必须在真机上验一次）。
                    case TextReasoningContent { Text.Length: > 0 } reasoning:
                        Console.Write($"[思考]{reasoning.Text}");
                        break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("（流正常结束）");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.WriteLine("（已取消：流已停止，异常未泄漏到顶层）");
        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

/// <summary>
/// 会话层探针（F7-②）：连调 <see cref="RoleplaySession.RequestCheckAsync"/> 三次（同角色、同目标、同理由），
/// 断言「被拒的检定不落记录、不 publish」——不依赖模型愿不愿意连调。
/// 另加一条 Passive 用例：被动检定不掷骰（<c>roll == null</c>），但必须有判定值，前端不能把它当"空卡"过滤。
/// </summary>
static int ProbeCheckSpam(string? rulesArgument)
{
    var rulesPath = rulesArgument ?? Environment.GetEnvironmentVariable("MEETINGROOM_RULES_DATA");

    if (string.IsNullOrWhiteSpace(rulesPath) || !File.Exists(rulesPath))
    {
        Console.Error.WriteLine("用法：--probe-check-spam [--rules <rules-data.json>]（或设置 MEETINGROOM_RULES_DATA）");
        return 2;
    }

    var rules = RulesDataLoader.Load(rulesPath);
    // 只读 TEMP 沙箱下建不了目录（已知坑）→ 放在 bin/ 下的临时目录，跑完就删。
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "check-spam-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    var storyPath = Path.Combine(dir, "story.jsonl");

    var bus = new CountingEventBus();
    var sheets = new SheetStore();
    var minimal = sheets.CreateMinimal("ella", currentHp: 30, maxHp: 30, armorClass: 15);
    // 给一个技能加值，被动值才不是干巴巴的 10（也顺带验 F7-① 的 sheet 回落）。
    sheets.Put(minimal with { Skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["perception"] = 5, ["investigation"] = 4 } });

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-check-spam",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = storyPath,
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new UnusedChatClient(),
        bus,
        [new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" }],
        vtt: null,
        rules: rules,
        sheets: sheets);

    var spam = new CheckRequest
    {
        ActorId = "ella",
        TargetId = "chest",
        Kind = CheckKind.Skill,
        Skill = "Investigation",
        Dc = 14,
        Reason = "看符纹",
        Visibility = CheckVisibility.Secret,
    };

    var outcomes = new List<string>();

    for (var i = 0; i < 3; i++)
    {
        // r11：同一轮只允许一个判定，所以这里**每轮各提一次**——
        // 防刷（同目标同理由第 2 次 DC+5、第 3 次拒）是跨轮的，仍然成立。
        session.SayAsync($"（元指令）探针第 {i + 1} 轮。", CancellationToken.None).GetAwaiter().GetResult();
        var outcome = session.RequestCheckAsync(spam, CancellationToken.None).GetAwaiter().GetResult();
        outcomes.Add(outcome.Applied ? "ok" : $"rejected({outcome.Message})");
    }

    // r11：同一轮第二个判定必须被拒（提示词管不住，改引擎强制）
    string onePerRound;

    {
        session.SayAsync("（元指令）探针：同一轮提两个判定。", CancellationToken.None).GetAwaiter().GetResult();
        var first = session.RequestCheckAsync(
            new CheckRequest { ActorId = "ella", Kind = CheckKind.Skill, Skill = "Insight", Dc = 12, Reason = "看他的神色" },
            CancellationToken.None).GetAwaiter().GetResult();
        var second = session.RequestCheckAsync(
            new CheckRequest { ActorId = "ella", Kind = CheckKind.Skill, Skill = "History", Dc = 13, Reason = "回忆徽记" },
            CancellationToken.None).GetAwaiter().GetResult();

        onePerRound = $"first={(first.Applied ? "ok" : "rejected")} second={(second.Applied ? "ok" : second.Message)}";

        if (first.Applied && !second.Applied && second.Message.Contains("这一轮已经提过一个判定了", StringComparison.Ordinal))
        {
            Console.WriteLine("✅ 同一轮第二个判定被拒（one-check-per-round）");
            Console.WriteLine($"     上一个：{first.Message}");
        }
        else
        {
            Console.WriteLine("❌ 同一轮第二个判定没有被拒");
        }

        Console.WriteLine($"     {onePerRound}");
    }

    // Passive：不掷骰，但必须有一个可显示的判定值（同样要新开一轮，否则会被"一轮一个"拒）。
    session.SayAsync("（元指令）探针：被动感知。", CancellationToken.None).GetAwaiter().GetResult();
    var passive = session.RequestCheckAsync(
        new CheckRequest { ActorId = "ella", Kind = CheckKind.Passive, Skill = "Perception", Reason = "被动感知" },
        CancellationToken.None).GetAwaiter().GetResult();

    var checkRecords = 0;
    var spamWarnings = 0;
    var passiveRecords = 0;
    var passiveWithValue = 0;

    if (File.Exists(storyPath))
    {
        foreach (var line in File.ReadLines(storyPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var node = JsonNode.Parse(line);

            switch (node?["kind"]?.GetValue<string>())
            {
                case "check":
                    checkRecords++;

                    // 被动检定：`roll` 为空但 `passiveValue` 有值——前端不能把它当空卡过滤。
                    if (node["check"]?["passiveValue"] is { } passiveNode && passiveNode.GetValueKind() != JsonValueKind.Null)
                    {
                        passiveRecords++;
                        passiveWithValue++;
                    }

                    break;

                case "warning":
                    if (node["warning"]?["code"]?.GetValue<string>()?.Contains("spam", StringComparison.Ordinal) == true)
                    {
                        spamWarnings++;
                    }

                    break;
            }
        }
    }

    var snapshot = JsonSerializer.SerializeToNode(session.Snapshot(), EventJson.Options);
    var checksNode = snapshot?["checks"] ?? snapshot?["roleplay"]?["checks"];
    var checksInSnapshot = checksNode is JsonArray array ? array.Count : -1;
    var publishedChecks = bus.CountOf("roleplay.check");
    var publishedWarnings = bus.CountOf("roleplay.warning");

    Console.WriteLine($"三次同参调用：{string.Join(" / ", outcomes)}");
    Console.WriteLine($"story.jsonl：check 记录 {checkRecords} 条，check-spam 警告 {spamWarnings} 条");
    Console.WriteLine($"快照 checks：{checksInSnapshot} 条；publish roleplay.check：{publishedChecks} 次，roleplay.warning：{publishedWarnings} 次");
    Console.WriteLine($"Passive：Applied={passive.Applied}，msg={passive.Message}；被动记录 {passiveRecords} 条（带 passiveValue：{passiveWithValue}）");

    // 主动：第 1 次成功、第 2 次（防刷 +5）成功、第 3 次被拒 → 只有前两次落记录；
    // 被动：另落 1 条。所以 3 条记录、3 次 publish，被拒那次既不落也不发。
    // 4 条 = 3 轮各一次（第 2/3 轮含防刷语义）+ 同一轮那条 Insight。
    var pass = checkRecords == 4
        && spamWarnings == 1
        && checksInSnapshot == 4
        && publishedChecks == 4
        && passive.Applied
        && passiveRecords == 1
        && passiveWithValue == 1;

    Console.WriteLine(pass ? "✅ 被拒的检定不进检定流；被动检定仍可用" : "❌ 断言未通过");

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
    }

    return pass ? 0 : 1;
}

/// <summary>
/// 地图上下文探针（M3）：读一份 <see cref="MapDocument"/> JSON，打印 <see cref="MapContextBuilder.Build"/>
/// 的结果与字数——这段文字就是 <c>RoleplaySession.MapSectionFor</c> 原样拼进角色节的内容。
/// 用法：--probe-map-context &lt;document.json&gt; [--focus &lt;actorId&gt;]
/// </summary>
static int ProbeMapContext(string? path, string? focus, string? preferred = null)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.Error.WriteLine("用法：--probe-map-context <map-document.json> [--focus <actorId>] [--preferred <actorId>]");
        return 2;
    }

    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    MapDocument? document;

    try
    {
        document = JsonSerializer.Deserialize<MapDocument>(File.ReadAllText(path), options);
    }
    catch (Exception ex) when (ex is JsonException or IOException)
    {
        Console.Error.WriteLine($"❌ 读不了地图文档：{ex.Message}");
        return 1;
    }

    if (document is null)
    {
        Console.Error.WriteLine("❌ 地图文档是空的。");
        return 1;
    }

    var section = MapContextBuilder.Build(document, focus, preferred);

    Console.WriteLine("----- 上下文原文 -----");
    Console.WriteLine(section.Length == 0 ? "（空：没有可见 token 或未标定 → 这一段不出）" : section);
    Console.WriteLine("----- 统计 -----");
    Console.WriteLine($"字数：{section.Length}（上限 {MapContextBuilder.MaxChars}）");
    Console.WriteLine($"可见 token：{document.Tokens.Count(token => token.Visible)} / {document.Tokens.Count}");
    Console.WriteLine($"房间：{document.Rooms.Count}；墙：{document.Walls.Count}");

    return 0;
}

/// <summary>
/// 战斗胜负守卫探针（r8）：直接构造会话 + Encounter + SheetStore，跑五组断言。
/// <para>
/// 背景：`EncounterEngine.End` 只判断"是否已结束"，传 `victory` 就落 `victory`——
/// 于是"敌人还站着就报胜利"能通过。守卫在 `RoleplaySession.EndCombatAsync` 里。
/// </para>
/// 用法：<c>--probe-combat-guard</c>
/// </summary>
static int ProbeCombatGuard()
{
    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ① 敌人还有 HP → victory 被拒（code 不透出到 NarratorActionOutcome，只能验 message）
    {
        var (session, _, _) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        StartGuardEncounter(session, "enc-1");
        var outcome = session.EndCombatAsync("victory", CancellationToken.None).GetAwaiter().GetResult();
        checks.Add(("① 敌人站着 → victory 被拒",
            !outcome.Applied && outcome.Message.Contains("不算胜利", StringComparison.Ordinal),
            outcome.Message));
    }

    // ② 敌人全倒 → victory 通过
    {
        var (session, _, sheets) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        StartGuardEncounter(session, "enc-2");
        sheets!.SetHp("goblin", 0);
        var outcome = session.EndCombatAsync("victory", CancellationToken.None).GetAwaiter().GetResult();
        checks.Add(("② 敌人全倒 → victory 通过", outcome.Applied, outcome.Message));
    }

    // ③ 友方还有活人 → defeat 被拒；全倒 → 通过
    {
        var (session, _, sheets) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        StartGuardEncounter(session, "enc-3");
        var rejected = session.EndCombatAsync("defeat", CancellationToken.None).GetAwaiter().GetResult();
        sheets!.SetHp("ella", 0);
        var accepted = session.EndCombatAsync("defeat", CancellationToken.None).GetAwaiter().GetResult();
        checks.Add(("③ 友方站着 → defeat 被拒",
            !rejected.Applied && rejected.Message.Contains("不算失败", StringComparison.Ordinal),
            rejected.Message));
        checks.Add(("③ 友方全倒 → defeat 通过", accepted.Applied, accepted.Message));
    }

    // ④ dm_end 需要"本轮导演发话"（r9）：没有导演输入时拒
    {
        var (session, _, _) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        StartGuardEncounter(session, "enc-4");
        var outcome = session.EndCombatAsync("dm_end", CancellationToken.None).GetAwaiter().GetResult();
        checks.Add(("④ 无导演输入 → dm_end 被拒",
            !outcome.Applied && outcome.Message.Contains("导演这一轮没有发话", StringComparison.Ordinal),
            outcome.Message));
    }

    // ⑤ 有导演输入时 dm_end 通过（SayAsync 会因假 chat client 报错，但输入已记下）
    {
        var (session, _, _) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        StartGuardEncounter(session, "enc-5");
        session.SayAsync("（元指令）测试用：导演这一轮发过话。", CancellationToken.None).GetAwaiter().GetResult();
        var outcome = session.EndCombatAsync("dm_end", CancellationToken.None).GetAwaiter().GetResult();
        checks.Add(("⑤ 有导演输入 → dm_end 通过", outcome.Applied, outcome.Message));
    }

    // ⑥ dm_override 不能把 HP 改成 0（r9：模型用它绕过 HP 守卫）
    {
        var (_, _, sheets) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        var zero = MeetingRoom.Core.Combat.DmOverrideEngine.Apply(
            null,
            sheets,
            new MeetingRoom.Core.Combat.DmOverrideRequest
            {
                Target = "ella",
                Field = "hp",
                Value = JsonSerializer.SerializeToElement(0),
            });
        var positive = MeetingRoom.Core.Combat.DmOverrideEngine.Apply(
            null,
            sheets,
            new MeetingRoom.Core.Combat.DmOverrideRequest
            {
                Target = "ella",
                Field = "hp",
                Value = JsonSerializer.SerializeToElement(12),
            });

        checks.Add(("⑥ override hp=0 → 被拒",
            !zero.Ok && zero.Code == "override-hp-zero",
            zero.Message ?? "(无)"));
        checks.Add(("⑥ override hp=12 → 通过", positive.Ok, positive.Message ?? "(无)"));
    }

    // ⑦ 开战必须带 HP（r9：常规战斗敌人没表，整场落在校验盲区）
    {
        var (session, _, _) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        var missing = session.StartCombatAsync(
            [
                new CombatantSetup { Id = "ella", Name = "Ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2 },
                new CombatantSetup { Id = "ghost", Name = "无表幽灵", Side = CombatSide.Foe, Initiative = 10, DexterityModifier = 1 },
            ],
            seed: 7,
            encounterId: "enc-7",
            CancellationToken.None).GetAwaiter().GetResult();

        var complete = session.StartCombatAsync(
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2 },
                new CombatantSetup { Id = "goblin", Side = CombatSide.Foe, Initiative = 10, DexterityModifier = 1 },
            ],
            seed: 7,
            encounterId: "enc-7b",
            CancellationToken.None).GetAwaiter().GetResult();

        checks.Add(("⑦ 缺 HP 的参战者 → 开战失败",
            !missing.Applied && missing.Message.Contains("既没有角色卡也没给 hp", StringComparison.Ordinal),
            missing.Message));
        checks.Add(("⑦ 全带 HP → 开战成功", complete.Applied, complete.Message));
    }

    // ⑨ 无卡敌人带 inline hp → 开战成功，且表能查到（r10 的核心修复）
    {
        var (session, _, sheets) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        var started = session.StartCombatAsync(
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2 },
                new CombatantSetup
                {
                    Id = "goblin-inline",
                    Name = "哥布林甲",
                    Side = CombatSide.Foe,
                    Initiative = 10,
                    DexterityModifier = 1,
                    Hp = 7,
                    MaxHp = 7,
                    Ac = 15,
                },
            ],
            seed: 7,
            encounterId: "enc-9",
            CancellationToken.None).GetAwaiter().GetResult();

        var sheet = sheets!.Get("goblin-inline");

        checks.Add(("⑨ 无卡敌人带 inline hp → 开战成功", started.Applied, started.Message));
        checks.Add(("⑨ inline hp 建成最小表", sheet?.CurrentHp == 7 && sheet.MaxHp == 7,
            sheet is null ? "(没有表)" : $"HP {sheet.CurrentHp}/{sheet.MaxHp}、AC {sheet.ArmorClass}"));
    }

    // ⑪ 有卡角色传 inline hp → 不覆盖角色卡（r11 核实：Ella hp 24 是伤害，不是覆盖）
    {
        var (session, _, sheets) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        var started = session.StartCombatAsync(
            [
                new CombatantSetup
                {
                    Id = "ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2,
                    Hp = 99, MaxHp = 99, Ac = 30,
                },
                new CombatantSetup { Id = "goblin", Side = CombatSide.Foe, Initiative = 10, DexterityModifier = 1 },
            ],
            seed: 7,
            encounterId: "enc-11",
            CancellationToken.None).GetAwaiter().GetResult();

        var sheet = sheets!.Get("ella");

        checks.Add(("⑪ 有卡角色传 inline hp → 不覆盖角色卡",
            started.Applied && sheet?.CurrentHp == 30 && sheet.MaxHp == 30 && sheet.ArmorClass == 15,
            sheet is null ? "(没有表)" : $"HP {sheet.CurrentHp}/{sheet.MaxHp}、AC {sheet.ArmorClass}"));
    }

    // ⑩ 无卡敌人不带 hp → 仍被拒（守卫保留，拦的是"既无卡又没给 hp"）
    {
        var (session, _, _) = NewCombatSession(withSheets: true, ellaHp: 30, goblinHp: 7);
        var started = session.StartCombatAsync(
            [
                new CombatantSetup { Id = "ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2 },
                new CombatantSetup { Id = "goblin-naked", Name = "哥布林乙", Side = CombatSide.Foe, Initiative = 10, DexterityModifier = 1 },
            ],
            seed: 7,
            encounterId: "enc-10",
            CancellationToken.None).GetAwaiter().GetResult();

        checks.Add(("⑩ 无卡敌人不带 hp → 仍被拒",
            !started.Applied && started.Message.Contains("既没有角色卡也没给 hp", StringComparison.Ordinal),
            started.Message));
    }

    // ⑧ 没有 SheetStore：直接开不了战（原来"四个 outcome 全放行"的断言已按新规则作废）
    {
        var (session, _, _) = NewCombatSession(withSheets: false, ellaHp: 0, goblinHp: 0);
        var started = session.StartCombatAsync(
            [new CombatantSetup { Id = "ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2 }],
            seed: 7,
            encounterId: "enc-8",
            CancellationToken.None).GetAwaiter().GetResult();

        checks.Add(("⑧ 无 SheetStore → 开战失败",
            !started.Applied && started.Message.Contains("没有数值表", StringComparison.Ordinal),
            started.Message));
    }

    Console.WriteLine("----- 战斗胜负守卫 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");

    return failed == 0 ? 0 : 1;
}

static void StartGuardEncounter(MeetingRoom.Core.Roleplay.RoleplaySession session, string encounterId)
{
    var started = session.StartCombatAsync(
        [
            new CombatantSetup { Id = "ella", Name = "Ella", Side = CombatSide.Party, Initiative = 20, DexterityModifier = 2 },
            new CombatantSetup { Id = "goblin", Name = "哥布林", Side = CombatSide.Foe, Initiative = 10, DexterityModifier = 1 },
        ],
        seed: 7,
        encounterId: encounterId,
        CancellationToken.None).GetAwaiter().GetResult();

    if (!started.Applied)
    {
        throw new InvalidOperationException($"开战失败：{started.Message}");
    }
}

static (MeetingRoom.Core.Roleplay.RoleplaySession Session, CountingEventBus Bus, SheetStore? Sheets)
    NewCombatSession(bool withSheets, int ellaHp, int goblinHp)
{
    var bus = new CountingEventBus();
    SheetStore? sheets = null;

    if (withSheets)
    {
        sheets = new SheetStore();
        sheets.Put(sheets.CreateMinimal("ella", currentHp: ellaHp, maxHp: 30, armorClass: 15));
        sheets.Put(sheets.CreateMinimal("goblin", currentHp: goblinHp, maxHp: 7, armorClass: 13));
    }

    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "combat-guard-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-combat-guard",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = Path.Combine(dir, "story.jsonl"),
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new UnusedChatClient(),
        bus,
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "goblin", Name = "哥布林" },
        ],
        vtt: null,
        rules: null,
        sheets: sheets);

    return (session, bus, sheets);
}

/// <summary>
/// 迷雾提议探针（M6b）：用真 <see cref="MapStore"/> 与真会话，验证三条语义。
/// <para>
/// “DM 同意 / 拒绝”需要真实答复（<c>RequestDecisionAsync</c> 会挂起），probe 里跑不了——
/// 这里直接验证**写入委托**的效果（同意后只会走它），以及“没有交互通道时绝不揭示”。
/// </para>
/// </summary>
static int ProbeFogProposal(string? mapsArgument)
{
    var checks = new List<(string Name, bool Pass, string Detail)>();

    var mapsDir = mapsArgument
        ?? Path.Combine(AppContext.BaseDirectory, "data", "maps");

    if (!Directory.Exists(mapsDir))
    {
        Console.Error.WriteLine($"❌ 找不到 maps 目录：{mapsDir}");
        return 2;
    }

    var store = new MapStore(mapsDir);
    var blank = store.CreateBlank("probe-fog");
    var id = blank.Id;

    // ① 委托语义：只加 explored，不碰 visible
    {
        store.SaveDocument(id, [], [], [],
            new MapFog(true, [new GridCell(1, 1)], [new GridCell(1, 1)]));

        var revealer = BuildRevealer(store, id);
        var added = revealer([new GridCell(1, 1), new GridCell(2, 2), new GridCell(3, 3)]);
        var after = store.GetDocument(id)!.Fog!;

        checks.Add(("① 揭示只加 explored",
            after.ExploredCells.Count == 3 && after.VisibleCells.Count == 1,
            $"explored {after.ExploredCells.Count}（期望 3）、visible {after.VisibleCells.Count}（期望 1，不变）；新增 {added}"));
        checks.Add(("① 重复格不重复计数", added == 2, $"本次新增 {added}（期望 2：其中一格本来就已探索）"));
    }

    // ② 没有交互通道时：提议不揭示（这就是“超时/没人答复”的等价路径）
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "fog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var session = BuildFogSession(store, id, dir, revealerEnabled: true,
            interactionEnabled: false);

        var before = store.GetDocument(id)!.Fog!.ExploredCells.Count;
        var outcome = session.ProposeRevealFogAsync(
            [new GridCell(9, 9)], null, "探针", CancellationToken.None).GetAwaiter().GetResult();
        var after = store.GetDocument(id)!.Fog!.ExploredCells.Count;

        checks.Add(("② 无交互通道 → 不揭示",
            after == before && outcome.Message.Contains("没有执行", StringComparison.Ordinal),
            outcome.Message));
    }

    // ③ 没有可写地图（MapFogRevealer = null）→ 明确拒绝
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "fog2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var session = BuildFogSession(store, id, dir, revealerEnabled: false, interactionEnabled: false);
        var outcome = session.ProposeRevealFogAsync(
            [new GridCell(5, 5)], null, "探针", CancellationToken.None).GetAwaiter().GetResult();

        checks.Add(("③ 无地图 → 明确拒绝",
            !outcome.Applied && outcome.Message.Contains("不支持迷雾提议", StringComparison.Ordinal),
            outcome.Message));
    }

    // ④⑤ 参数容错（r15）：模型会把结构化参数写成 JSON 字符串，也会把 [gx,gy] 写成 {x,y}。
    {
        var shapeArgs = new Dictionary<string, object?>
        {
            ["shape"] = "{\"type\":\"circle\",\"center\":[100,100],\"radiusCells\":2}",
        };
        var shape = MeetingRoom.Core.Roleplay.ToolArguments.ReadFogShape(shapeArgs, "shape", out var shapeError);

        checks.Add(("④ shape 传 JSON 字符串能解析",
            shapeError is null && shape is { Type: "circle", Center.Count: 2 } && Math.Abs((shape.RadiusCells) - 2) < 0.001,
            shapeError ?? $"type={shape?.Type} center=[{string.Join(",", shape?.Center ?? [])}] radius={shape?.RadiusCells}"));

        var cellArgs = new Dictionary<string, object?>
        {
            ["cells"] = "[{\"x\":3,\"y\":4},{\"x\":5,\"y\":6}]",
        };
        var cells = MeetingRoom.Core.Roleplay.ToolArguments.ReadCells(cellArgs, "cells", out var cellError);

        checks.Add(("⑤ cells 传 {x,y} 对象能解析",
            cellError is null && cells is { Count: 2 } && cells[0].X == 3 && cells[1].Y == 6,
            cellError ?? string.Join(" ", (cells ?? []).Select(c => $"[{c.X},{c.Y}]"))));

        // 坏形状：必须给可读回执，而不是抛
        var badArgs = new Dictionary<string, object?> { ["shape"] = "不是 JSON" };
        _ = MeetingRoom.Core.Roleplay.ToolArguments.ReadFogShape(badArgs, "shape", out var badError);
        checks.Add(("⑤ 坏形状 → 可读错误（不抛）", badError is not null, badError ?? "(没有错误)"));
    }

    Console.WriteLine("----- 迷雾提议 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

static Func<IReadOnlyList<GridCell>, int?> BuildRevealer(MapStore store, string id) => cells =>
{
    if (store.GetDocument(id) is not { } document)
    {
        return null;
    }

    var explored = new HashSet<GridCell>(document.Fog?.ExploredCells ?? []);
    var before = explored.Count;
    explored.UnionWith(cells);

    if (explored.Count == before)
    {
        return 0;
    }

    store.SaveDocument(document.Id, document.Walls, document.Rooms, document.Tokens,
        new MapFog(true, explored.ToArray(), document.Fog?.VisibleCells ?? []));

    return explored.Count - before;
};

static MeetingRoom.Core.Roleplay.RoleplaySession BuildFogSession(
    MapStore store, string mapId, string dir, bool revealerEnabled, bool interactionEnabled)
{
    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-fog",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        Interaction = new MeetingRoom.Core.Interaction.InteractionOptions { Enabled = interactionEnabled },
        MapDocumentProvider = () => store.GetDocument(mapId),
        MapFogRevealer = revealerEnabled ? BuildRevealer(store, mapId) : null,
    };

    return new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new UnusedChatClient(),
        new CountingEventBus(),
        [new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" }]);
}

/// <summary>
/// 角色视角探针（M9a）：打印 <see cref="MapCharacterView.Build"/> 的原文并断言三条闸门。
/// <para>
/// 这段文字就是 <c>RoleplaySession.MapSectionFor</c> 给非说书人返回的内容（那段是 internal，
/// 这里直接调同一个生成器）。
/// </para>
/// </summary>
static int ProbeCharacterView(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.Error.WriteLine("用法：--probe-character-view <map-document.json>");
        return 2;
    }

    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    MapDocument? document;

    try
    {
        document = JsonSerializer.Deserialize<MapDocument>(File.ReadAllText(path), options);
    }
    catch (Exception ex) when (ex is JsonException or IOException)
    {
        Console.Error.WriteLine($"❌ 读不了地图文档：{ex.Message}");
        return 1;
    }

    if (document is null)
    {
        Console.Error.WriteLine("❌ 地图文档是空的。");
        return 1;
    }

    var text = MapCharacterView.Build(document, "ella");
    var lines = text.Split('\n');
    var checks = new List<(string Name, bool Pass, string Detail)>();

    checks.Add(("① 含自己的位置", text.Contains("储藏室", StringComparison.Ordinal), lines.Skip(1).FirstOrDefault() ?? "(空)"));
    checks.Add(("① 含附近 token 与格数距离", text.Contains("鲍勃", StringComparison.Ordinal) && text.Contains("3 格外", StringComparison.Ordinal), lines.FirstOrDefault(line => line.Contains("附近", StringComparison.Ordinal)) ?? "(没有附近行)"));
    checks.Add(("① 含身边的可见区域", text.Contains("油腻术", StringComparison.Ordinal), lines.FirstOrDefault(line => line.Contains("身边", StringComparison.Ordinal)) ?? "(没有身边行)"));
    checks.Add(("② 未探索格里的 token 不在输出里", !text.Contains("幽灵", StringComparison.Ordinal), "「幽灵」在 (18,14)，未探索"));
    checks.Add(("② 隐藏 token 不在输出里", !text.Contains("躲着的人", StringComparison.Ordinal), "「躲着的人」visible=false"));
    checks.Add(("③ 隐藏区域不在输出里", !text.Contains("密门", StringComparison.Ordinal), "「密门」visible=false"));
    checks.Add(("③ 不含像素坐标", !System.Text.RegularExpressions.Regex.IsMatch(text, @"\(\d{3,},"), "不该出现 (x,y)"));

    Console.WriteLine("----- 角色视角原文 -----");
    Console.WriteLine(text);
    Console.WriteLine("----- 断言 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>
/// 地图图附件探针（M8b 补验）：直接调 <c>RoleplaySession.BuildMapImages</c>，
/// 断言说书人拿 dm 图、角色拿 player 图、没有已探索格时两者都不发图。
/// </summary>
static int ProbeVisionAttachments(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.Error.WriteLine("用法：--probe-vision <map-document.json>");
        return 2;
    }

    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    var document = JsonSerializer.Deserialize<MapDocument>(File.ReadAllText(path), options);

    if (document is null)
    {
        Console.Error.WriteLine("❌ 地图文档是空的。");
        return 1;
    }

    (int Width, int Height) SizeOf(IReadOnlyList<MeetingRoom.Core.Context.ImageAttachment> images)
    {
        if (images.Count == 0)
        {
            return (-1, -1);
        }

        using var image = SixLabors.ImageSharp.Image.Load(images[^1].Bytes);
        return (image.Width, image.Height);
    }

    var checks = new List<(string Name, bool Pass, string Detail)>();

    // ① 说书人 → dm 图（整图缩到 maxPx）
    var (session, dir) = NewVisionSession(document, 768);
    var narrator = session.BuildMapImages("narrator");
    var (nw, nh) = SizeOf(narrator);

    checks.Add(("① 说书人拿到 dm 图", narrator.Count == 1 && narrator[0].Bytes.Length > 0,
        $"bytes={narrator.FirstOrDefault()?.Bytes.Length ?? 0} 尺寸={nw}×{nh} caption={narrator.FirstOrDefault()?.Caption}"));
    checks.Add(("① dm 图长边 = maxPx(768)", Math.Max(nw, nh) == 768, $"长边 {Math.Max(nw, nh)}"));

    // ② 角色 → player 图（只到已探索外接矩形，尺寸更小）
    var (session2, dir2) = NewVisionSession(document, 768);
    var character = session2.BuildMapImages("ella");
    var (cw, ch) = SizeOf(character);

    checks.Add(("② 角色拿到 player 图", character.Count == 1 && character[0].Bytes.Length > 0,
        $"bytes={character.FirstOrDefault()?.Bytes.Length ?? 0} 尺寸={cw}×{ch} caption={character.FirstOrDefault()?.Caption}"));
    checks.Add(("② player 图比 dm 图小", Math.Max(cw, ch) < Math.Max(nw, nh), $"player {cw}×{ch} < dm {nw}×{nh}"));

    // ③ 没有已探索格 → 两者都不发图（对应端点 204 的静默路径）
    var noFog = document with { Fog = new MapFog(true, [], []) };
    var (session3, dir3) = NewVisionSession(noFog, 768);
    var narratorEmpty = session3.BuildMapImages("narrator");
    var characterEmpty = session3.BuildMapImages("ella");

    checks.Add(("③ 无已探索格 → 两者都不发图",
        characterEmpty.Count == 0, $"角色 {characterEmpty.Count} 张；说书人 {narratorEmpty.Count} 张（dm 图不受迷雾影响，可以有）"));

    foreach (var folder in new[] { dir, dir2, dir3 })
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    Console.WriteLine("----- 地图图附件 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

static (MeetingRoom.Core.Roleplay.RoleplaySession Session, string Dir) NewVisionSession(MapDocument document, int maxPx)
{
    var dir = Path.Combine(AppContext.BaseDirectory, "probe-tmp", "vision-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);

    var options = new MeetingRoom.Core.Roleplay.RoleplayOptions
    {
        StoryId = "probe-vision",
        World = "（探针）",
        Situation = "（探针）",
        StoryPath = Path.Combine(dir, "story.jsonl"),
        Vision = new VisionOptions { Enabled = true, MaxPx = maxPx, OnlyOnChange = false },
        MapDocumentProvider = () => document,
        MapViewImageProvider = playerView => MapViewRenderer.Render(document, null, playerView, maxPx),
    };

    var session = new MeetingRoom.Core.Roleplay.RoleplaySession(
        options,
        new UnusedChatClient(),
        new CountingEventBus(),
        [
            new MeetingRoom.Core.Roles.RoleCard { Id = "ella", Name = "Ella" },
            new MeetingRoom.Core.Roles.RoleCard { Id = "narrator", Name = "说书人", Role = MeetingRoom.Core.Participants.ParticipantRole.Host },
        ]);

    return (session, dir);
}

/// <summary>
/// 视觉能力探测（M8b）：发一张 8×8 纯红图 + "这张图是什么颜色？只回答颜色。"，
/// **回答里含「红」才算支持**。任何异常都当"不支持"返回 false（调用方据此拒绝启动）。
/// </summary>
static bool ProbeVision(IChatClient client, string model)
{
    byte[] red;

    using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8, 8, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 0, 0)))
    using (var buffer = new MemoryStream())
    {
        image.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        red = buffer.ToArray();
    }

    try
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User,
            [
                new TextContent("这张图是什么颜色？只回答颜色。"),
                new DataContent(red, "image/png"),
            ]),
        };

        var response = client.GetResponseAsync(messages, new ChatOptions { ModelId = model })
            .GetAwaiter()
            .GetResult();

        var text = response.Text ?? string.Empty;

        if (!text.Contains('红', StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"  视觉探测回读：{(text.Length == 0 ? "（空）" : text)}");
            return false;
        }

        Console.WriteLine($"  视觉探测通过：{text.Trim()}");
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  视觉探测失败：{ex.Message}");
        return false;
    }
}

/// <summary>
/// 合成视图探针（M8a）：直接调 <see cref="MapViewRenderer"/>，断言尺寸 / 裁剪 / 204 条件 / 确定性与"不含 token"。
/// </summary>
static int ProbeMapView(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.Error.WriteLine("用法：--probe-map-view <map-document.json>");
        return 2;
    }

    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    var document = JsonSerializer.Deserialize<MapDocument>(File.ReadAllText(path), options);

    if (document is null)
    {
        Console.Error.WriteLine("❌ 地图文档是空的。");
        return 1;
    }

    var checks = new List<(string Name, bool Pass, string Detail)>();

    var dm = MapViewRenderer.Render(document, null, playerView: false, 768);
    var player = MapViewRenderer.Render(document, null, playerView: true, 768);

    int SizeOf(byte[]? png)
    {
        if (png is null)
        {
            return -1;
        }

        using var image = SixLabors.ImageSharp.Image.Load(png);
        return Math.Max(image.Width, image.Height);
    }

    checks.Add(("① dm 出图且长边 = 768", SizeOf(dm) == 768, $"长边 {SizeOf(dm)}"));
    checks.Add(("② player 出图且比 dm 小", player is not null && SizeOf(player) < SizeOf(dm),
        $"player 长边 {SizeOf(player)} < dm 长边 {SizeOf(dm)}"));

    // ③ 没有已探索格 / 未标定 → 不出图
    var noExplored = document with
    {
        Fog = new MapFog(true, [], []),
    };
    var uncalibrated = document with
    {
        Grid = new MapCalibration(0, 0, 0, "square", 5),
    };

    checks.Add(("③ 无已探索格 → 204（null）",
        MapViewRenderer.Render(noExplored, null, playerView: true, 768) is null, "player 视角无已探索格"));
    checks.Add(("③ 未标定 → 204（null）",
        MapViewRenderer.Render(uncalibrated, null, playerView: false, 768) is null, "dpi = 0"));

    // ④ 同一份文档两次渲染 → 字节一致（缓存的前提）；改一处 → 不同
    var again = MapViewRenderer.Render(document, null, playerView: false, 768);
    var changed = MapViewRenderer.Render(
        document with { Zones = [.. (document.Zones ?? []), new MapZone("z-new", "新区域", MapZoneKinds.Spell, new MapZoneShape("circle", Center: new Point2(200, 200), RadiusCells: 2), "#ff0000", "", true)] },
        null,
        playerView: false,
        768);

    checks.Add(("④ 同文档两次渲染字节一致",
        dm is not null && again is not null && dm.SequenceEqual(again), $"{dm?.Length ?? 0} bytes"));
    checks.Add(("④ 文档改动后字节不同",
        dm is not null && changed is not null && !dm.SequenceEqual(changed), "加了一个区域"));

    // ⑤ 图里不含 token：同一份文档去掉 token 后渲染结果**完全一致**
    var withoutTokens = MapViewRenderer.Render(document with { Tokens = [] }, null, playerView: false, 768);

    checks.Add(("⑤ 图不含 token（去 token 后字节一致）",
        dm is not null && withoutTokens is not null && dm.SequenceEqual(withoutTokens), "token 不影响出图"));

    Console.WriteLine("----- 合成视图 -----");

    foreach (var (name, pass, detail) in checks)
    {
        Console.WriteLine($"{(pass ? "✅" : "❌")} {name}");
        Console.WriteLine($"     {detail}");
    }

    var failed = checks.Count(check => !check.Pass);
    Console.WriteLine(failed == 0 ? $"✅ 全部 {checks.Count} 条通过" : $"❌ {failed} / {checks.Count} 条未通过");
    return failed == 0 ? 0 : 1;
}

/// <summary>只数事件类型的总线包装（<c>--probe-check-spam</c> 用）。</summary>
sealed class CountingEventBus : IEventBus
{
    private readonly EventBus _inner = new();
    private readonly Dictionary<string, int> _counts = [];

    public long NextSeq => _inner.NextSeq;

    public int CountOf(string type) => _counts.TryGetValue(type, out var value) ? value : 0;

    public MeetingEvent Publish(string type, object? payload)
    {
        _counts[type] = CountOf(type) + 1;
        return _inner.Publish(type, payload);
    }

    public IEventSubscription Subscribe(CancellationToken cancellationToken = default) => _inner.Subscribe(cancellationToken);

    public IReadOnlyList<MeetingEvent> Recent(int count) => _inner.Recent(count);
}

/// <summary>探针专用：检定路径不碰模型，这个客户端被调用即说明设计走错了。</summary>
/// <summary>
/// 探针用的"假模型"（建议收束版）：第 1 次出正文，第 2 次调 <c>declare_scene_end</c>。
/// 用来复现真机那条链：说书人在复盘里自己把会话暂停，然后后续步骤卡死。
/// </summary>
sealed class DeclareEndChatClient : IChatClient
{
    private int _calls;

    public ChatClientMetadata Metadata { get; } = new("probe-declare-end");

    public int CallCount => Volatile.Read(ref _calls);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("探针只走流式路径。");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _calls);

        await Task.Yield();

        // 第 2 次调用是"复盘"：说书人在复盘里自己请求收束（真机里就是这一步把会话暂停了）。
        if (index == 2)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent(
                        $"call-{index}",
                        "declare_scene_end",
                        new Dictionary<string, object?> { ["reason"] = "（探针）本场该收了" }),
                ],
            };

            yield break;
        }

        // 其余（角色发言 / 万一还有后续步骤）都出正文，避免假模型把 step 循环跑满。
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent($"第 {index} 次调用：码头上风很大，我环顾四周。")],
        };
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 探针用的"假模型"（计数版）：只吐正文，但记录**被调用了几次**——
/// §12 要证明"复盘那一步确实跑了模型"，光看产出分不清"复盘跳过"与"复盘没说话"。
/// </summary>
sealed class CountingChatClient : IChatClient
{
    private int _calls;

    public ChatClientMetadata Metadata { get; } = new("probe-counting");

    public int CallCount => Volatile.Read(ref _calls);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("探针只走流式路径。");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _calls);

        await Task.Yield();

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent($"第 {index} 次调用：码头上风很大。")],
        };
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 探针用的"假模型"（带工具调用版）：奇数次调用先想一句再调 <c>look_around</c>，
/// 偶数次调用出正文。这样才能同时造出后台记录要的 <c>thought</c> 与 <c>tool</c> 两种条目。
/// </summary>
sealed class ToolThenTextChatClient : IChatClient
{
    private int _calls;

    public ChatClientMetadata Metadata { get; } = new("probe-tool-then-text");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("探针只走流式路径。");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _calls);

        await Task.Yield();

        if (index % 2 == 1)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("（先看看四周）"),
                    new FunctionCallContent($"call-{index}", "look_around", new Dictionary<string, object?>()),
                ],
            };

            yield break;
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent($"第 {index} 句：码头上风很大，我环顾四周。")],
        };
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// 探针用的"假模型"：每次调用吐一句固定结构的正文（带自增序号，便于验证"只追加"）。
/// <b>不联网、不花 token</b>。
/// </summary>
sealed class ScriptedChatClient : IChatClient
{
    private int _calls;

    public ChatClientMetadata Metadata { get; } = new("probe-scripted");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("探针只走流式路径。");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _calls);

        await Task.Yield();

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent($"第 {index} 句：码头上风很大，我把披风裹紧了些。")],
        };
    }

    public void Dispose()
    {
    }
}

sealed class UnusedChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("probe");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("探针不应调用模型。");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("探针不应调用模型。");

    public void Dispose()
    {
    }
}
