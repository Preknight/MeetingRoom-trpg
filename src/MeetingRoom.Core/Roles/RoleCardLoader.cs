using MeetingRoom.Core.Participants;

namespace MeetingRoom.Core.Roles;

/// <summary>
/// 角色卡解析（设计文档 §5.4）：<c>---</c> 包裹的极简 frontmatter + Markdown 正文。
/// 只支持 <c>key: value</c> 与 <c>key: [a, b]</c> 两种形态——够本项目用，不引 YAML 库。
/// </summary>
public static class RoleCardLoader
{
    private const string Fence = "---";

    /// <summary>解析一张角色卡；<paramref name="fallbackId"/> 用于没写 <c>id</c> 时兜底（如文件名）。</summary>
    public static RoleCard Parse(string markdown, string? fallbackId = null)
    {
        var normalized = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');

        var index = 0;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (lines.Length > 0 && lines[0].Trim() == Fence)
        {
            index = 1;
            while (index < lines.Length && lines[index].Trim() != Fence)
            {
                var line = lines[index].Trim();
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim().Trim('"');
                    fields[key] = value;
                }

                index++;
            }

            if (index < lines.Length && lines[index].Trim() == Fence)
            {
                index++; // 跳过结束围栏
            }
        }

        var body = string.Join('\n', lines[index..]).Trim();
        var id = Get(fields, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = fallbackId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException("角色卡缺少 id，且没有可用的文件名兜底。");
        }

        return new RoleCard
        {
            Id = id,
            Name = Fallback(Get(fields, "name"), id),
            Title = Get(fields, "title"),
            Summary = Get(fields, "summary"),
            Role = IsHost(Get(fields, "role")) ? ParticipantRole.Host : ParticipantRole.Member,
            Model = Get(fields, "model"),
            Effort = Get(fields, "effort"),
            AllowedTools = ParseList(Get(fields, "allowedTools")),
            VttTokenId = Get(fields, "vttTokenId"),
            ReadOnly = bool.TryParse(Get(fields, "readOnly"), out var readOnly) && readOnly,
            Body = body,
        };
    }

    /// <summary>从文件加载（<c>data/roles/&lt;id&gt;.md</c>）。</summary>
    public static RoleCard Load(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>加载一个目录下的全部角色卡（按文件名排序，结果稳定）。</summary>
    public static IReadOnlyList<RoleCard> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToList();
    }

    private static bool IsHost(string value) =>
        string.Equals(value, "host", StringComparison.OrdinalIgnoreCase);

    private static string Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>解析 <c>[a, b, c]</c> 形态的列表；空值返回空列表。</summary>
    private static IReadOnlyList<string> ParseList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Trim()
            .Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
