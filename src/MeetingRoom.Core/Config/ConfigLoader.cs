using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetingRoom.Core.Config;

/// <summary>配置加载结果：生效值 + 提示（自动生成默认文件、被忽略的未知键等）。</summary>
public sealed record ConfigLoadResult(MeetingRoomOptions Options, IReadOnlyList<string> Notices);

/// <summary>
/// meetingroom.json 的读取 / 校验 / 默认值（设计文档 §10.5）。
/// 规则：缺项用内置默认值补齐；未知键忽略并记一条提示；非法值抛 <see cref="ConfigException"/>。
/// 配置改后重启生效，不做热重载。
/// </summary>
public static class ConfigLoader
{
    /// <summary>已知配置键，用于"未知键忽略并记日志"。</summary>
    private static readonly Dictionary<string, string[]> KnownKeys = new(StringComparer.Ordinal)
    {
        ["server"] = ["port", "bind", "heartbeatSeconds", "openBrowser"],
        ["provider"] = ["name", "baseUrl", "model", "apiKeyEnv"],
        ["limits"] = ["maxStepsPerTurn", "maxConcurrentTurns", "steerMergeWindowMs", "maxRounds"],
        ["speech"] = ["maxCharsPerSpeech"],
        ["summaries"] = ["materialSummaryFallbackChars", "thoughtSummaryMaxChars", "thoughtSummaryStyle"],
        ["context"] = ["alwaysMaterialMaxCount", "triggeredTokenBudget", "triggeredCooldownRounds"],
        ["interaction"] = ["enabled", "everyRound", "allowSelfSkip", "maxConsecutiveSkips", "timeoutSeconds", "maxOptions"],
        ["images"] =
        [
            "enabled", "baseUrl", "endpointPath", "model", "apiKeyEnv", "timeoutSeconds", "proxy",
            "maxReferenceImages", "maxReferenceBytes", "maxOutputBytes",
            "defaultAspectRatio", "defaultResolution", "defaultQuality", "outputFormat", "maxCount",
        ],
        ["vtt"] = ["enabled", "stateUrl", "timeoutSeconds", "staleSeconds", "injectRadiusFeet", "includeHidden"],
        ["vision"] = ["enabled", "maxPx", "onlyOnChange"],
        ["roleplay"] = ["context", "narratorBeatsPerTurn"],
    };

    /// <summary>
    /// 三段键（<c>roleplay.context.*</c>）：<see cref="KnownKeys"/> 只查两层，
    /// 这一层单独列出来，写错键才有提示。
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownNestedKeys = new(StringComparer.Ordinal)
    {
        ["roleplay.context"] = ["historyLimit", "historyTokenBudget", "compressRatio", "lineChars"],
    };

    private static readonly string[] KnownSummaryStyles = ["paper-abstract"];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// 读取并校验配置。文件不存在时写出一份完整的默认配置（便于用户改），本次仍以默认值运行。
    /// </summary>
    /// <exception cref="ConfigException">文件不是合法 JSON、结构不符或存在非法值。</exception>
    public static ConfigLoadResult Load(string path)
    {
        var notices = new List<string>();

        if (!File.Exists(path))
        {
            var created = new MeetingRoomOptions();
            var defaultProblems = Validate(created);
            if (defaultProblems.Count > 0)
            {
                // 内置默认值自身非法属实现缺陷，仍然拒绝启动以免掩盖错误。
                throw new ConfigException(defaultProblems);
            }

            WriteDefaultFile(path, created, notices);
            return new ConfigLoadResult(created, notices);
        }

        var text = File.ReadAllText(path);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new ConfigException([$"配置文件不是合法 JSON：{ex.Message}"]);
        }

        if (root is not JsonObject rootObject)
        {
            throw new ConfigException(["配置文件的顶层必须是 JSON 对象。"]);
        }

        ReportUnknownKeys(rootObject, notices);

        MeetingRoomOptions options;
        try
        {
            options = rootObject.Deserialize<MeetingRoomOptions>(ReadOptions) ?? new MeetingRoomOptions();
        }
        catch (JsonException ex)
        {
            throw new ConfigException([$"配置项类型不匹配：{ex.Message}"]);
        }

        var problems = Validate(options);
        if (problems.Count > 0)
        {
            throw new ConfigException(problems);
        }

        return new ConfigLoadResult(options, notices);
    }

    /// <summary>逐条校验，一次性报出全部问题（便于用户一轮改完）。</summary>
    public static IReadOnlyList<string> Validate(MeetingRoomOptions options)
    {
        var problems = new List<string>();

        if (options.Server.Port is not 0 && options.Server.Port is (< 1 or > 65535))
        {
            problems.Add($"server.port 必须在 1..65535 之间（0 表示动态分配），当前为 {options.Server.Port}。");
        }

        if (!IsLoopbackBinding(options.Server.Bind))
        {
            problems.Add(
                $"server.bind 必须是回环地址（127.0.0.1 / ::1 / localhost），当前为“{options.Server.Bind}”"
                + "——只绑回环是硬约束（设计文档 §10.4）。");
        }

        RequirePositive(problems, "server.heartbeatSeconds", options.Server.HeartbeatSeconds);

        RequireText(problems, "provider.name", options.Provider.Name);
        RequireText(problems, "provider.model", options.Provider.Model);
        RequireText(problems, "provider.apiKeyEnv", options.Provider.ApiKeyEnv);
        if (!IsHttpUrl(options.Provider.BaseUrl))
        {
            problems.Add($"provider.baseUrl 必须是 http/https 绝对地址，当前为“{options.Provider.BaseUrl}”。");
        }

        RequirePositive(problems, "limits.maxStepsPerTurn", options.Limits.MaxStepsPerTurn);
        RequirePositive(problems, "limits.maxConcurrentTurns", options.Limits.MaxConcurrentTurns);
        RequirePositive(problems, "limits.steerMergeWindowMs", options.Limits.SteerMergeWindowMs);
        RequirePositive(problems, "limits.maxRounds", options.Limits.MaxRounds);

        if (options.Speech.MaxCharsPerSpeech < 0)
        {
            problems.Add($"speech.maxCharsPerSpeech 不能为负（0 表示不限制），当前为 {options.Speech.MaxCharsPerSpeech}。");
        }

        RequirePositive(problems, "summaries.materialSummaryFallbackChars", options.Summaries.MaterialSummaryFallbackChars);
        RequirePositive(problems, "summaries.thoughtSummaryMaxChars", options.Summaries.ThoughtSummaryMaxChars);
        if (!KnownSummaryStyles.Contains(options.Summaries.ThoughtSummaryStyle, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(
                $"summaries.thoughtSummaryStyle 只能是 {string.Join(" / ", KnownSummaryStyles)}，"
                + $"当前为“{options.Summaries.ThoughtSummaryStyle}”。");
        }

        RequirePositive(problems, "context.alwaysMaterialMaxCount", options.Context.AlwaysMaterialMaxCount);
        RequirePositive(problems, "context.triggeredTokenBudget", options.Context.TriggeredTokenBudget);
        RequirePositive(problems, "context.triggeredCooldownRounds", options.Context.TriggeredCooldownRounds);

        // 交互模式（阶段 9 / §14.5）：负数一律拒绝；两个"提问来源"同时关掉等于把交互模式关了。
        if (options.Interaction.MaxConsecutiveSkips < 0)
        {
            problems.Add(
                "interaction.maxConsecutiveSkips 不能为负（0 表示不限制），"
                + $"当前为 {options.Interaction.MaxConsecutiveSkips}。");
        }

        if (options.Interaction.TimeoutSeconds < 0)
        {
            problems.Add(
                "interaction.timeoutSeconds 不能为负（0 表示永久等待），"
                + $"当前为 {options.Interaction.TimeoutSeconds}。");
        }

        if (options.Interaction.MaxOptions < 2)
        {
            problems.Add(
                "interaction.maxOptions 至少为 2（只有一个选项的提问没有意义），"
                + $"当前为 {options.Interaction.MaxOptions}。");
        }

        if (options.Interaction.Enabled
            && !options.Interaction.EveryRound
            && !options.Interaction.AllowSelfSkip)
        {
            problems.Add(
                "interaction.everyRound 与 interaction.allowSelfSkip 不能同时为 false："
                + "那样主持人 / 说书人永远没法提问，交互模式形同关闭"
                + "（真要关请直接设 interaction.enabled = false）。");
        }

        if (options.Roleplay.Context.HistoryLimit < 1)
        {
            problems.Add($"roleplay.context.historyLimit 至少为 1，当前为 {options.Roleplay.Context.HistoryLimit}。");
        }

        RequirePositive(problems, "roleplay.context.historyTokenBudget", options.Roleplay.Context.HistoryTokenBudget);

        if (options.Roleplay.Context.CompressRatio is <= 0 or >= 1)
        {
            problems.Add(
                "roleplay.context.compressRatio 必须在 (0,1) 之间（0.5 = 压掉前一半），"
                + $"当前为 {options.Roleplay.Context.CompressRatio}。");
        }

        RequirePositive(problems, "roleplay.context.lineChars", options.Roleplay.Context.LineChars);

        if (options.Roleplay.NarratorBeatsPerTurn < 0)
        {
            problems.Add(
                "roleplay.narratorBeatsPerTurn 不能为负（0 = 只写不推进：复盘照常、但不点名不描写），"
                + $"当前为 {options.Roleplay.NarratorBeatsPerTurn}。");
        }

        ValidateImages(options.Images, problems);
        ValidateVtt(options.Vtt, problems);
        ValidateVision(options.Vision, problems);

        return problems;
    }

    /// <summary>
    /// 地图接入（《枭熊地图接入实现方案》V1）。与图片模块同一条规矩：<c>enabled = false</c> 时不校验其余字段，
    /// 关掉的功能不该因为它自己的参数非法而挡住整个服务。
    /// </summary>
    private static void ValidateVision(VisionOptions vision, List<string> problems)
    {
        if (!vision.Enabled)
        {
            return;
        }

        // 范围与 `MapViewRenderer` 的钳制一致：越界值说明写错了，静默钳掉会让人以为配置生效了。
        if (vision.MaxPx is < 256 or > 2048)
        {
            problems.Add($"vision.maxPx 要在 256–2048 之间，当前为 {vision.MaxPx}。");
        }
    }

    private static void ValidateVtt(VttOptions vtt, List<string> problems)
    {
        if (!vtt.Enabled)
        {
            return;
        }

        if (!IsHttpUrl(vtt.StateUrl))
        {
            problems.Add($"vtt.stateUrl 必须是 http/https 绝对地址，当前为“{vtt.StateUrl}”。");
        }

        RequirePositive(problems, "vtt.timeoutSeconds", vtt.TimeoutSeconds);
        RequirePositive(problems, "vtt.staleSeconds", vtt.StaleSeconds);
        RequirePositive(problems, "vtt.injectRadiusFeet", vtt.InjectRadiusFeet);
    }

    /// <summary>
    /// 图片生成模块（设计方案 §八）。<c>enabled = false</c> 时**不校验其余字段** ——
    /// 关掉的功能不该因为它自己的参数非法而挡住整个服务。
    /// </summary>
    private static void ValidateImages(ImageOptions images, List<string> problems)
    {
        if (!images.Enabled)
        {
            return;
        }

        if (!IsHttpUrl(images.BaseUrl))
        {
            problems.Add($"images.baseUrl 必须是 http/https 绝对地址，当前为“{images.BaseUrl}”。");
        }

        if (!string.IsNullOrWhiteSpace(images.EndpointPath) && !images.EndpointPath.StartsWith('/'))
        {
            problems.Add($"images.endpointPath 必须以 / 开头（它拼在 baseUrl 后面），当前为“{images.EndpointPath}”。");
        }

        RequireText(problems, "images.model", images.Model);
        RequireText(problems, "images.apiKeyEnv", images.ApiKeyEnv);

        if (images.TimeoutSeconds < 0)
        {
            problems.Add($"images.timeoutSeconds 不能为负（0 表示不限时），当前为 {images.TimeoutSeconds}。");
        }

        if (images.MaxCount is < 1 or > 10)
        {
            problems.Add($"images.maxCount 必须在 1..10 之间，当前为 {images.MaxCount}。");
        }

        if (images.MaxReferenceImages < 0)
        {
            problems.Add($"images.maxReferenceImages 不能为负（0 表示只做文生图），当前为 {images.MaxReferenceImages}。");
        }

        if (images.MaxReferenceBytes <= 0 || images.MaxOutputBytes <= 0)
        {
            problems.Add(
                "images.maxReferenceBytes 与 images.maxOutputBytes 必须大于 0，"
                + $"当前为 {images.MaxReferenceBytes} / {images.MaxOutputBytes}。");
        }

        var proxy = images.Proxy.Trim();
        if (proxy.Length > 0
            && !proxy.Equals("off", StringComparison.OrdinalIgnoreCase)
            && !Uri.TryCreate(proxy, UriKind.Absolute, out _))
        {
            problems.Add($"images.proxy 只接受空、off 或绝对 URL，当前为“{images.Proxy}”。");
        }
    }

    private static void WriteDefaultFile(string path, MeetingRoomOptions options, List<string> notices)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(options, WriteOptions) + Environment.NewLine);
            notices.Add($"未找到配置文件，已按内置默认值生成：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notices.Add($"未找到配置文件且无法写入 {path}：{ex.Message}（本次以内置默认值运行）");
        }
    }

    private static void ReportUnknownKeys(JsonObject root, List<string> notices)
    {
        foreach (var (key, value) in root)
        {
            if (!KnownKeys.TryGetValue(key, out var known))
            {
                notices.Add($"忽略未知键 {key}");
                continue;
            }

            if (value is not JsonObject section)
            {
                // 类型不符留给反序列化报错，这里不重复提示。
                continue;
            }

            foreach (var (childKey, childValue) in section)
            {
                if (!known.Contains(childKey, StringComparer.OrdinalIgnoreCase))
                {
                    notices.Add($"忽略未知键 {key}.{childKey}");
                    continue;
                }

                // 三段键（roleplay.context.*）：只查一层会把写错的子键吞掉。
                if (childValue is JsonObject nested
                    && KnownNestedKeys.TryGetValue($"{key}.{childKey}", out var knownNested))
                {
                    foreach (var (nestedKey, _) in nested)
                    {
                        if (!knownNested.Contains(nestedKey, StringComparer.OrdinalIgnoreCase))
                        {
                            notices.Add($"忽略未知键 {key}.{childKey}.{nestedKey}");
                        }
                    }
                }
            }
        }
    }

    private static bool IsLoopbackBinding(string bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return false;
        }

        var value = bind.Trim().Trim('[', ']');
        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void RequirePositive(List<string> problems, string key, int value)
    {
        if (value <= 0)
        {
            problems.Add($"{key} 必须大于 0，当前为 {value}。");
        }
    }

    private static void RequireText(List<string> problems, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{key} 不能为空。");
        }
    }
}
