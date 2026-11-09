namespace MeetingRoom.Core.Rules;

/// <summary>
/// 属性缩写的解析（数据包与规则文本里到处是 <c>str</c> / <c>dex</c> …）。
/// 只做"字符串 ↔ 枚举"这一件事，避免每个调用点各写一份 switch。
/// </summary>
public static class AbilityText
{
    /// <summary>解析缩写或全名（大小写不敏感）；认不出返回 <c>null</c>，不猜。</summary>
    public static Ability? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "str" or "strength" or "力量" => Ability.Strength,
        "dex" or "dexterity" or "敏捷" => Ability.Dexterity,
        "con" or "constitution" or "体质" => Ability.Constitution,
        "int" or "intelligence" or "智力" => Ability.Intelligence,
        "wis" or "wisdom" or "感知" => Ability.Wisdom,
        "cha" or "charisma" or "魅力" => Ability.Charisma,
        _ => null,
    };

    /// <summary>枚举 → 三字母缩写（与数据包一致）。</summary>
    public static string Wire(Ability ability) => ability switch
    {
        Ability.Strength => "str",
        Ability.Dexterity => "dex",
        Ability.Constitution => "con",
        Ability.Intelligence => "int",
        Ability.Wisdom => "wis",
        _ => "cha",
    };
}
