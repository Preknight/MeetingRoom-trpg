using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Roles;

/// <summary>
/// 角色卡旁路数值表（§26.2 第 1 层）：<c>roles/&lt;id&gt;.sheet.json</c>。
/// <para>
/// 会话启动时预加载：文件名的 <c>&lt;id&gt;</c> 就是角色 id，<c>characterId</c> 必须与它一致（不猜、不覆盖）。
/// 校验不过 / JSON 坏掉一律进 <see cref="RoleSheetLoadResult.Problems"/>，由调用方写进启动诊断——
/// <b>不允许"能扣血但检定算不了"的半残表</b>进战斗（§26.4）。
/// </para>
/// </summary>
public sealed record RoleSheetLoadResult(IReadOnlyList<Sheet> Sheets, IReadOnlyList<string> Problems);

/// <summary>加载 <c>roles/*.sheet.json</c> 并校验。</summary>
public static class RoleSheetLoader
{
    public const string Suffix = ".sheet.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 与规则包 / 战斗脚本同一条规矩：未知键必须报错，否则"字段名写错"会静默失效。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static RoleSheetLoadResult Load(string rolesDirectory)
    {
        if (string.IsNullOrWhiteSpace(rolesDirectory) || !Directory.Exists(rolesDirectory))
        {
            return new RoleSheetLoadResult([], []);
        }

        var sheets = new List<Sheet>();
        var problems = new List<string>();

        foreach (var path in Directory.EnumerateFiles(rolesDirectory, "*" + Suffix, SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            var characterId = fileName[..^Suffix.Length];

            if (string.IsNullOrWhiteSpace(characterId))
            {
                problems.Add($"{fileName}：文件名里没有角色 id，已跳过。");
                continue;
            }

            Sheet? sheet;

            try
            {
                var text = File.ReadAllText(path);
                var root = JsonNode.Parse(text) as JsonObject
                    ?? throw new JsonException("顶层必须是一个 JSON 对象");

                var aliasProblem = NormalizeAliases(root);

                if (aliasProblem is not null)
                {
                    problems.Add($"{fileName}：{aliasProblem}——该角色按「没有数值表」处理。");
                    continue;
                }

                sheet = root.Deserialize<Sheet>(ReadOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 坏 JSON 不崩会话：记警告、视为无表（§26.4）。
                problems.Add($"{fileName} 读不出来（{ex.Message}）；该角色按「没有数值表」处理。");
                continue;
            }

            if (sheet is null)
            {
                problems.Add($"{fileName} 是空的；该角色按「没有数值表」处理。");
                continue;
            }

            if (!string.Equals(sheet.CharacterId, characterId, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{fileName}：characterId 是「{sheet.CharacterId}」，与文件名「{characterId}」不一致；已跳过（不猜用哪个）。");
                continue;
            }

            var errors = SheetValidator.Validate(sheet);

            if (errors.Count > 0)
            {
                problems.Add($"{fileName} 校验不过：{string.Join("；", errors)}——该角色按「没有数值表」处理。");
                continue;
            }

            sheets.Add(sheet);
        }

        return new RoleSheetLoadResult(sheets, problems);
    }

    /// <summary>顶层标量简写 → <see cref="Sheet"/> 的规范名（§26.3：简写与规范名都接受，规范名优先）。</summary>
    private static readonly Dictionary<string, string> ScalarAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ac"] = "armorClass",
        ["profBonus"] = "proficiencyBonus",
    };

    /// <summary><c>abilities</c> 里的属性简写 → <see cref="Ability"/> 全名（手写卡更自然）。</summary>
    private static readonly Dictionary<string, string> AbilityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["str"] = "strength",
        ["dex"] = "dexterity",
        ["con"] = "constitution",
        ["int"] = "intelligence",
        ["wis"] = "wisdom",
        ["cha"] = "charisma",
    };

    /// <summary>
    /// 把简写键改写成规范名（§26.3）。规则：
    /// <list type="bullet">
    /// <item>只有简写 → 直接改名；</item>
    /// <item>只有规范名 → 不动（规范名优先）；</item>
    /// <item>两名同现且值相同 → 删掉简写、不报错；值不同 → <b>拒绝整张表</b>（不静默取其一）；</item>
    /// <item><c>level</c> 在 <see cref="Sheet"/> 里不存在 → 填了就拒绝，并说明该用什么代替。</item>
    /// </list>
    /// </summary>
    private static string? NormalizeAliases(JsonObject root)
    {
        if (root.Any(pair => string.Equals(pair.Key, "level", StringComparison.OrdinalIgnoreCase)))
        {
            return "Sheet 没有 level 字段（戏法档位请在施法时用 combat_action 的 characterLevel 给）";
        }

        foreach (var (alias, canonical) in ScalarAliases)
        {
            var conflict = Merge(root, alias, canonical);

            if (conflict is not null)
            {
                return conflict;
            }
        }

        if (root.TryGetPropertyValue("abilities", out var abilitiesNode) && abilitiesNode is JsonObject abilities)
        {
            foreach (var (alias, canonical) in AbilityAliases)
            {
                var conflict = Merge(abilities, alias, canonical);

                if (conflict is not null)
                {
                    return conflict;
                }
            }
        }

        return null;
    }

    /// <summary>合并一个别名；返回冲突说明（null = 没问题）。</summary>
    private static string? Merge(JsonObject target, string alias, string canonical)
    {
        var keys = target.Select(pair => pair.Key).ToArray();
        var aliasKey = keys.FirstOrDefault(key => string.Equals(key, alias, StringComparison.OrdinalIgnoreCase));
        var canonicalKey = keys.FirstOrDefault(key => string.Equals(key, canonical, StringComparison.OrdinalIgnoreCase));

        if (aliasKey is null)
        {
            return null;
        }

        if (canonicalKey is not null)
        {
            if (!JsonNode.DeepEquals(target[aliasKey], target[canonicalKey]))
            {
                return $"「{aliasKey}」与「{canonicalKey}」同时出现且值不同（{target[aliasKey]?.ToJsonString()} vs {target[canonicalKey]?.ToJsonString()}）——不猜用哪个";
            }

            target.Remove(aliasKey);
            return null;
        }

        var value = target[aliasKey]?.DeepClone();
        target.Remove(aliasKey);
        target[canonical] = value;
        return null;
    }
}
