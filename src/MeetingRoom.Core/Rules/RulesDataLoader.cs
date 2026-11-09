using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Rules;

/// <summary>
/// 规则数据包的读取（schema <c>meetingroom-rules/3</c> 与 <c>/4</c>）。
/// <para>
/// V4 在 V3 基础上补了怪物的数值与特性（hp / ac / abilities / traits / actions…），
/// 并新增 <c>classes / subclasses / feats / optionalFeatures</c> 四节；V3 的包仍然能读
/// （那些字段为空），所以这里<b>按版本白名单</b>校验，而不是钉死一个字符串。
/// </para>
/// <para>
/// <b>不猜、不补</b>：schema 不对、id 重复、id 为空都直接抛 <see cref="InvalidDataException"/>——
/// 一份半对的规则包比没有更危险（伤害类型读错一个字，抗性就全错）。
/// </para>
/// </summary>
public static class RulesDataLoader
{
    /// <summary>当前优先使用的 schema（V4）。</summary>
    public const string SchemaId = "meetingroom-rules/4";

    /// <summary>仍然接受的旧版本（V3 的包没有职业 / 专长等四节，读进来是空列表）。</summary>
    public const string LegacySchemaId = "meetingroom-rules/3";

    /// <summary>接受的全部 schema 版本。</summary>
    public static IReadOnlyList<string> AcceptedSchemas { get; } = [SchemaId, LegacySchemaId];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 未知键直接报错：拼错一个字段名（例如 "dex" 写成 dexterityModifier 之外的名字）
        // 默认会被静默忽略——这条在 C1 已经踩过一次，别让它再静默。
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>解析 JSON 文本。格式/契约不对时抛 <see cref="InvalidDataException"/>。</summary>
    public static RulesData Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            // 与"schema 不对 / id 重复"同类：都是数据包不可用，调用方按一种异常处理。
            throw new InvalidDataException("规则数据包是空的。");
        }

        RulesData? data;

        try
        {
            data = JsonSerializer.Deserialize<RulesData>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"规则数据包不是合法 JSON 或字段类型不符：{ex.Message}", ex);
        }

        if (data is null)
        {
            throw new InvalidDataException("规则数据包是空的。");
        }

        if (!AcceptedSchemas.Contains(data.Schema, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"规则数据包 schema 不对：期望 {string.Join(" 或 ", AcceptedSchemas)}，实际「{data.Schema}」。");
        }

        RequireUniqueIds(data.MonsterTemplates.Select(item => item.Id), "monsterTemplates");
        RequireUniqueIds(data.Classes.Select(item => item.Id), "classes");
        RequireUniqueIds(data.Subclasses.Select(item => item.Id), "subclasses");
        RequireUniqueIds(data.Feats.Select(item => item.Id), "feats");
        RequireUniqueIds(data.OptionalFeatures.Select(item => item.Id), "optionalFeatures");
        RequireUniqueIds(data.Spells.Select(item => item.Id), "spells");
        RequireUniqueIds(data.Items.Select(item => item.Id), "items");
        RequireUniqueIds(data.Conditions.Select(item => item.Id), "conditions");
        RequireUniqueIds(data.Actions.Select(item => item.Id), "actions");
        RequireUniqueIds(data.Skills.Select(item => item.Id), "skills");

        return data;
    }

    /// <summary>从文件加载（真实包到位后由调用方给出路径；测试只读仓库内的 fixture）。</summary>
    public static RulesData Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Parse(File.ReadAllText(path));
    }

    private static void RequireUniqueIds(IEnumerable<string> ids, string section)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidDataException($"{section} 里有条目缺少 id。");
            }

            if (!seen.Add(id))
            {
                throw new InvalidDataException($"{section} 里的 id 重复：{id}");
            }
        }
    }
}
