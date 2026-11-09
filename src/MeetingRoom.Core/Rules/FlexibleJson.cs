using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Rules;

/// <summary>
/// 把"可能是字符串、数字、对象、数组"的 JSON 值统一读成一个字符串。
/// <para>
/// 真实规则包里这类字段很多：<c>cr</c> 可能是 <c>"1/4"</c> 或 <c>{"cr":"0","xp":0}</c>；
/// <c>type</c> 可能是 <c>"humanoid"</c> 或 <c>{"choose":["celestial","fiend"]}</c>；
/// 施法时间是 <c>[{"number":1,"unit":"action"}]</c> 也可能是 <c>["Varies"]</c>。
/// 与其为每种形状写一个类型，不如归一成文本——这些字段都只用于显示与匹配，不参与判定。
/// </para>
/// </summary>
public sealed class FlexibleTextConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ToText(document.RootElement) ?? string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    /// <summary>归一规则：null → null；字符串原样；数字 / 布尔 → 原文；数组 → 顿号连接；对象 → 按常见形状取值。</summary>
    internal static string? ToText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(
            '、',
            element.EnumerateArray()
                .Select(ToText)
                .Where(text => !string.IsNullOrEmpty(text))),
        JsonValueKind.Object => DescribeObject(element),
        _ => element.GetRawText(),
    };

    private static string DescribeObject(JsonElement element)
    {
        // {uid:"Sap", note:"…"}：武器的精通 / 属性条目（V4 里这两项的数组元素是对象）
        if (element.TryGetProperty("uid", out var uid))
        {
            return ToText(uid) ?? string.Empty;
        }

        // {choose:[...]}：二选一的怪物类型
        if (element.TryGetProperty("choose", out var choose))
        {
            return ToText(choose) ?? string.Empty;
        }

        // {cr:"0", xp:0}：挑战等级
        if (element.TryGetProperty("cr", out var cr))
        {
            return ToText(cr) ?? string.Empty;
        }

        // {text:"a melee weapon…", cost:10}：施法材料
        if (element.TryGetProperty("text", out var text))
        {
            return ToText(text) ?? string.Empty;
        }

        // {number:1, unit:"action"}：施法时间
        if (element.TryGetProperty("number", out var number) && element.TryGetProperty("unit", out var unit))
        {
            return $"{ToText(number)} {ToText(unit)}";
        }

        // {amount:150, type:"feet"}：距离
        if (element.TryGetProperty("amount", out var amount) && element.TryGetProperty("type", out var type))
        {
            return $"{ToText(amount)} {ToText(type)}";
        }

        if (element.TryGetProperty("type", out var onlyType))
        {
            return ToText(onlyType) ?? string.Empty;
        }

        return element.GetRawText();
    }
}

/// <summary>
/// 把"键 → 任意值"的对象统一读成 <c>IReadOnlyDictionary&lt;string,string&gt;</c>。
/// <para>
/// 真实包里的 <c>saves</c> / <c>skills</c> 就是这种形状：<c>{"dex":7,"perception":16}</c>，
/// 但个别条目的值会是字符串（<c>{"stealth":"+7"}</c>）——它们只用于显示与匹配，归一成文本最稳。
/// </para>
/// </summary>
public sealed class FlexibleTextMapConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    public override bool HandleNull => true;

    public override IReadOnlyDictionary<string, string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            var text = FlexibleTextConverter.ToText(property.Value);

            if (!string.IsNullOrEmpty(text))
            {
                map[property.Name] = text;
            }
        }

        return map;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var pair in value)
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// 把"字符串数组 / 单个字符串 / 对象数组 / null"统一读成 <c>IReadOnlyList&lt;string&gt;</c>。
/// 元素用 <see cref="FlexibleTextConverter"/> 的规则归一；null 读成空数组（不是 null，省掉调用方的判空）。
/// </summary>
public sealed class FlexibleTextListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override bool HandleNull => true;

    public override IReadOnlyList<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            var single = FlexibleTextConverter.ToText(root);
            return string.IsNullOrEmpty(single) ? [] : [single];
        }

        return root.EnumerateArray()
            .Select(FlexibleTextConverter.ToText)
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToArray();
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// 怪物速度（<c>speed</c>）的归一：真实包里形状太多——<c>{"walk":40}</c>、
/// <c>{"fly":{"number":60,"condition":"hover"}}</c>、<c>{"alternate":{"walk":[…]}}</c>、
/// <c>{"choose":{"amount":30,"from":[…]}}</c>、<c>{"walk":40,"canHover":true}</c>。
/// 统一成 <c>walk 40、fly 60（hover）</c> 这样的文本：这些值只用于显示，不参与移动力计算。
/// </summary>
public sealed class MonsterSpeedConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return Describe(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static string Describe(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return FlexibleTextConverter.ToText(element) ?? string.Empty;
        }

        var parts = new List<string>();

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "canHover" when property.Value.ValueKind == JsonValueKind.True:
                    parts.Add("可悬浮");
                    continue;
                case "alternate":
                    parts.Add("可替换：" + Describe(property.Value));
                    continue;
                case "choose":
                    parts.Add("可选：" + Describe(property.Value));
                    continue;
                default:
                    var text = DescribeValue(property.Value);

                    if (text.Length > 0)
                    {
                        parts.Add($"{property.Name} {text}");
                    }

                    continue;
            }
        }

        return string.Join('、', parts);
    }

    private static string DescribeValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.Object:
            {
                var number = value.TryGetProperty("number", out var amount) ? amount.GetRawText() : null;
                var condition = value.TryGetProperty("condition", out var conditionElement) && conditionElement.ValueKind == JsonValueKind.String
                    ? conditionElement.GetString()
                    : null;

                if (number is not null)
                {
                    return string.IsNullOrWhiteSpace(condition) ? number : $"{number}（{condition}）";
                }

                return Describe(value);
            }

            case JsonValueKind.Array:
                return string.Join(" / ", value.EnumerateArray().Select(DescribeValue));
            default:
                return FlexibleTextConverter.ToText(value) ?? string.Empty;
        }
    }
}
