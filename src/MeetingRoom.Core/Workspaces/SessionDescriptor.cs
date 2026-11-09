using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Workspaces;

/// <summary>
/// 一场会话的元信息（设计方案 §15.2 / §15.8）：落 <c>&lt;会话目录&gt;/session.json</c>。
/// <para>
/// 为什么单独落一份：会话目录里原本只有"数据"（<c>transcript.jsonl</c> / <c>story.jsonl</c> /
/// <c>manifest.json</c>），装不下"这场叫什么、议题 / 世界观是什么"。而侧栏要显示它们、
/// 重新打开一场会话时也要靠它们复原设定（会议议题此前只活在内存里）。
/// </para>
/// </summary>
public sealed record SessionDescriptor
{
    /// <summary>会话 id——同时是目录名（<c>meetings/&lt;id&gt;/</c>），见 <see cref="SessionIds"/>。</summary>
    public required string Id { get; init; }

    /// <summary>会话类型：会议进 <c>meetings/</c>、叙事进 <c>stories/</c>（§15.2）。</summary>
    public required WorkspaceKind Kind { get; init; }

    /// <summary>给侧栏看的名字（会议默认取议题、叙事默认取世界观首句）。</summary>
    public required string Title { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>会议：议题。</summary>
    public string? Topic { get; init; }

    /// <summary>叙事：世界观 / 背景。</summary>
    public string? Worldview { get; init; }

    /// <summary>叙事：开场情境。</summary>
    public string? Situation { get; init; }

    /// <summary>叙事：导演在故事里的身份（空 = 由导演在开场里说明）。</summary>
    public string? UserRole { get; init; }

    /// <summary>叙事：开局在场角色 id；空 = 全员在场。</summary>
    public IReadOnlyList<string> Cast { get; init; } = [];

    /// <summary>叙事：说书人的角色卡 id。</summary>
    public string? NarratorId { get; init; }

    /// <summary>类型在界面与 JSON 里的写法（与 <c>workspace.json</c> 同一套）。</summary>
    [JsonIgnore]
    public string KindWire => WorkspaceDescriptor.WireOf(Kind);
}

/// <summary>
/// 会话 id 的生成与净化。id 直接当目录名用，所以必须挡掉路径分隔符、<c>.</c> 之类的字符
/// （否则 <c>../..</c> 就能把会话目录写到工作空间外面去）。
/// </summary>
public static class SessionIds
{
    private const int MaxLength = 48;

    /// <summary>把标题揉成一个可当目录名的 id；标题里没有可用字符时退回带时间戳的兜底名。</summary>
    public static string FromTitle(string? title)
    {
        var slug = Clean(title);
        return slug.Length > 0 ? slug : $"session-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
    }

    /// <summary>
    /// 净化一个候选 id：保留中文 / 字母 / 数字 / <c>-</c> / <c>_</c>，其余字符折成 <c>-</c>；
    /// 折叠连续分隔符、去掉首尾分隔符、限长。返回空串表示"没有可用字符"。
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(MaxLength);
        var lastWasSeparator = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                if (builder.Length == MaxLength)
                {
                    break;
                }

                builder.Append(ch);
                lastWasSeparator = false;
                continue;
            }

            // 空白、连字符与其它字符一律折成一个分隔符；连续的直接跳过，开头的也跳过。
            if (!lastWasSeparator && builder.Length > 0 && builder.Length < MaxLength)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>在 <paramref name="parent"/> 下找一个没被占用的目录名：撞了就依次试 <c>-2</c>、<c>-3</c>…</summary>
    public static string Unique(string parent, string candidate)
    {
        var name = candidate;

        for (var suffix = 2; Directory.Exists(Path.Combine(parent, name)) && suffix < 1000; suffix++)
        {
            name = $"{candidate}-{suffix}";
        }

        return name;
    }
}
