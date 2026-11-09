using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Rules;

/// <summary>数据包里的一段伤害（<c>spells[].damage[]</c>）。</summary>
public sealed record DamagePartDefinition(string Dice, string Type);

/// <summary>施法时间的一段（<c>time[]</c>）：<c>{"number":1,"unit":"action"}</c>；反应法术还带 <c>condition</c>。</summary>
public sealed record SpellTime
{
    public int? Number { get; init; }

    public string Unit { get; init; } = string.Empty;

    /// <summary>反应触发的条件（只有反应法术有）。</summary>
    public string? Condition { get; init; }

    public string? Note { get; init; }

    public string Describe()
    {
        var text = $"{Number?.ToString() ?? string.Empty} {Unit}".Trim();
        return string.IsNullOrWhiteSpace(Condition) ? text : $"{text}（{Condition}）";
    }
}

/// <summary>距离 / 持续时间共用的 <c>{type, amount}</c> 形状（如 <c>{"type":"feet","amount":150}</c>）。</summary>
public sealed record SpellDistance
{
    public string Type { get; init; } = string.Empty;

    public int? Amount { get; init; }

    public string Describe() => Amount is { } amount ? $"{amount} {Type}" : Type;
}

/// <summary>施法距离（<c>range</c>）：<c>{"type":"point","distance":{...}}</c>。</summary>
public sealed record SpellRange
{
    public string Type { get; init; } = string.Empty;

    public SpellDistance? Distance { get; init; }

    public string Describe() => Distance is null ? Type : $"{Type} {Distance.Describe()}";
}

/// <summary>施法成分（<c>components</c>）：三个键都可能缺（不是每个法术都有 V/S/M）。</summary>
public sealed record SpellComponents
{
    public bool? V { get; init; }

    public bool? S { get; init; }

    /// <summary>材料成分；可能是字符串，也可能是 <c>{text, cost}</c>（有价材料）。</summary>
    [JsonConverter(typeof(FlexibleTextConverter))]
    public string? M { get; init; }

    public string Describe()
    {
        var parts = new List<string>(3);

        if (V == true)
        {
            parts.Add("V");
        }

        if (S == true)
        {
            parts.Add("S");
        }

        if (!string.IsNullOrWhiteSpace(M))
        {
            parts.Add($"M（{M}）");
        }

        return string.Join("、", parts);
    }
}

/// <summary>持续时间的一段（<c>duration[]</c>）：<c>{type, duration?, concentration?, ends?}</c>。</summary>
public sealed record SpellDuration
{
    public string Type { get; init; } = string.Empty;

    public SpellDistance? Duration { get; init; }

    public bool? Concentration { get; init; }

    /// <summary>终止条件（如 <c>["dispel"]</c>）。</summary>
    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Ends { get; init; } = [];

    public string Describe()
    {
        var text = Type;

        if (Duration is { } duration)
        {
            text += $" {duration.Describe()}";
        }

        if (Concentration == true)
        {
            text += "（专注）";
        }

        return Ends.Count == 0 ? text : $"{text}（终止：{string.Join('、', Ends)}）";
    }
}

/// <summary>
/// 升环 / 戏法递增（<c>scaling[]</c>）：
/// 两种形状——<c>{base, perLevel}</c>（升环加骰）与 <c>{variants:[...]}</c>（戏法按等级换整段骰）。
/// <b>variants 不是多段伤害</b>，别把它拼进 <c>damage[]</c>。
/// </summary>
public sealed record SpellScaling
{
    public string? Base { get; init; }

    public string? PerLevel { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Variants { get; init; } = [];

    public string Describe() => Variants.Count > 0
        ? string.Join(" / ", Variants)
        : string.IsNullOrWhiteSpace(Base) ? string.Empty : $"{Base}（每环 +{PerLevel}）";
}

/// <summary>怪物的血量（<c>hp</c>）：<c>{"average":59,"formula":"7d10 + 21"}</c>；<c>/4</c> 起才有。</summary>
public sealed record MonsterHitPoints
{
    public int? Average { get; init; }

    public string? Formula { get; init; }
}

/// <summary>怪物的一种护甲来源（<c>ac[]</c>）：<c>{"value":11,"from":["hide armor"]}</c>。</summary>
public sealed record MonsterArmorClass
{
    public int? Value { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> From { get; init; } = [];
}

/// <summary>六项属性（<c>abilities</c>）：<c>{"str":19,"dex":8,…}</c>；缺项保持 null，<b>不补默认值</b>。</summary>
public sealed record MonsterAbilities
{
    public int? Str { get; init; }

    public int? Dex { get; init; }

    public int? Con { get; init; }

    public int? Int { get; init; }

    public int? Wis { get; init; }

    public int? Cha { get; init; }

    /// <summary>按 STR/DEX/CON/INT/WIS/CHA 顺序给一串数字（缺项写 —），给紧凑回执用。</summary>
    public string Describe() =>
        string.Join(' ', new[] { Str, Dex, Con, Int, Wis, Cha }.Select(value => value?.ToString() ?? "—"));

    public bool IsEmpty => Str is null && Dex is null && Con is null && Int is null && Wis is null && Cha is null;
}

/// <summary>
/// 怪物的一条特性 / 动作 / 传奇动作 / 附赠动作（<c>traits[] / actions[] / bonusActions[] /
/// reactions[] / legendary[] / mythic[] / spellcasting[]</c>）：同一形状，<b>复用同一个类型</b>。
/// </summary>
public sealed record MonsterAttack
{
    public string Name { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>命中加值（只有攻击类动作有）。</summary>
    public int? AttackBonus { get; init; }

    /// <summary>伤害骰（如 <c>2d8 + 4</c>；豁免型只给这段骰，不给加值）。</summary>
    public string? DamageDice { get; init; }
}

/// <summary>
/// 怪物模板（schema <c>meetingroom-rules/3</c> 起；<c>/4</c> 补齐了数值与特性）。
/// <para>
/// <c>cr</c> / <c>type</c> / <c>size</c> 在真实包里形状不固定（字符串、<c>{cr,xp}</c>、<c>{choose:[...]}</c>），
/// 一律用 <see cref="FlexibleTextConverter"/> 归一成文本。
/// </para>
/// <para>
/// <c>speed</c> 同理：可能是 <c>{"walk":40}</c>、<c>{"fly":{"number":60,"condition":"hover"}}</c>、
/// <c>{"alternate":{…}}</c> 或 <c>{"choose":{…}}</c>，归一成文本来显示；本工具只做查询，不拿它算移动力。
/// </para>
/// </summary>
public sealed record MonsterTemplate
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>中文名：只给显示与匹配用，<b>不参与逻辑</b>。</summary>
    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary>类型（英文；也可能是 <c>{choose:[...]}</c> 的二选一）。</summary>
    [JsonConverter(typeof(FlexibleTextConverter))]
    public string? Type { get; init; }

    /// <summary>体型代码（T/S/M/L/H/G）。</summary>
    [JsonConverter(typeof(FlexibleTextConverter))]
    public string? Size { get; init; }

    /// <summary>阵营代码数组（如 <c>["L","E"]</c>）；少数条目是 <c>{alignment,chance}</c> 的随机表。</summary>
    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Alignment { get; init; } = [];

    /// <summary>挑战等级；可能是 <c>"1/4"</c> 或 <c>{cr,xp}</c>，统一归一成文本。</summary>
    [JsonConverter(typeof(FlexibleTextConverter))]
    public string? Cr { get; init; }

    [JsonConverter(typeof(FlexibleTextConverter))]
    public string? Habitat { get; init; }

    public string Fluff { get; init; } = string.Empty;

    /// <summary>血量（均值 + 骰式）；<c>/4</c> 起才有，685 / 706 条带它。</summary>
    public MonsterHitPoints? Hp { get; init; }

    /// <summary>护甲（可能多条来源）；缺项 = 该条目不写 AC。</summary>
    public IReadOnlyList<MonsterArmorClass> Ac { get; init; } = [];

    /// <summary>速度（形状见类型注释），归一成文本，如 <c>walk 40、fly 80</c>。</summary>
    [JsonConverter(typeof(MonsterSpeedConverter))]
    public string? Speed { get; init; }

    /// <summary>六项属性（<c>/4</c> 起）。</summary>
    public MonsterAbilities? Abilities { get; init; }

    /// <summary>豁免加值（只列非零项）。</summary>
    [JsonConverter(typeof(FlexibleTextMapConverter))]
    public IReadOnlyDictionary<string, string> Saves { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>技能加值（只列有熟练的项）。</summary>
    [JsonConverter(typeof(FlexibleTextMapConverter))]
    public IReadOnlyDictionary<string, string> Skills { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>被动察觉（<c>passive</c>）。</summary>
    public int? Passive { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Senses { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Languages { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Resist { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Immune { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Vulnerable { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> ConditionImmune { get; init; } = [];

    /// <summary>特性（被动能力，<c>traits[]</c>）：名字 + 英文原文，个别条带 <c>damageDice</c>。</summary>
    public IReadOnlyList<MonsterAttack> Traits { get; init; } = [];

    public IReadOnlyList<MonsterAttack> Actions { get; init; } = [];

    public IReadOnlyList<MonsterAttack> BonusActions { get; init; } = [];

    public IReadOnlyList<MonsterAttack> Reactions { get; init; } = [];

    public IReadOnlyList<MonsterAttack> Legendary { get; init; } = [];

    public IReadOnlyList<MonsterAttack> Mythic { get; init; } = [];

    public IReadOnlyList<MonsterAttack> Spellcasting { get; init; } = [];

    /// <summary>AC 的紧凑文本（多条来源拼起来；没有 AC 时为空）。</summary>
    public string AcText => Ac.Count == 0
        ? string.Empty
        : string.Join(" / ", Ac.Select(item => item.Value?.ToString() ?? "?"));
}


/// <summary>法术（数据包 <c>spells[]</c>）。时间 / 距离 / 成分 / 持续时间 / 递增都是结构化对象。</summary>
public sealed record SpellDefinition
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary>环级（0 = 戏法）。</summary>
    public int Level { get; init; }

    /// <summary>学派（英文枚举：V / A / C …）。</summary>
    public string School { get; init; } = string.Empty;

    public IReadOnlyList<SpellTime> Time { get; init; } = [];

    public SpellRange? Range { get; init; }

    public SpellComponents? Components { get; init; }

    public IReadOnlyList<SpellDuration> Duration { get; init; } = [];

    /// <summary>伤害段（真实包里只给首段；戏法递增在 <see cref="Scaling"/> 的 variants 里）。</summary>
    public IReadOnlyList<DamagePartDefinition> Damage { get; init; } = [];

    public IReadOnlyList<SpellScaling> Scaling { get; init; } = [];

    /// <summary>豁免属性（英文枚举；null = 不豁免）。</summary>
    public string? Save { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> AreaTags { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    /// <summary>给提示词 / 日志用的便捷文本（不参与反序列化）。</summary>
    public string TimeText => string.Join("；", Time.Select(item => item.Describe()));

    public string RangeText => Range?.Describe() ?? string.Empty;

    public string ComponentsText => Components?.Describe() ?? string.Empty;

    public string DurationText => string.Join("；", Duration.Select(item => item.Describe()));

    public string ScalingText => string.Join("；", Scaling.Select(item => item.Describe()));
}

/// <summary>装备（数据包 <c>items[]</c>）：武器 / 护甲 / 盾牌 / 消耗品 / 杂物。</summary>
public sealed record ItemDefinition
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary><c>weapon</c> / <c>armor</c> / <c>shield</c> / <c>consumable</c> / <c>gear</c>。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>装备分类代码（真实包里的 <c>type</c>：如 <c>G</c> / <c>M</c> / <c>$G</c>；也有空串）。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>单手伤害（<c>1d8</c>）；真实包里非武器为 null。</summary>
    public string? Dmg1 { get; init; }

    /// <summary>双手 / 多用武器伤害（<c>1d10</c>）；没有双手形态时为 null（不是空串）。</summary>
    public string? Dmg2 { get; init; }

    /// <summary>精通（Sap / Vex / Topple / Nick …；未建模的由 <see cref="WeaponMasteries"/> 显式报告）。</summary>
    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Mastery { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Properties { get; init; } = [];

    /// <summary>远程射程（原生字符串，如 <c>"150/600"</c>；空 = 近战）。</summary>
    public string? Range { get; init; }

    /// <summary>近战触及（尺）：默认 5，带 Reach（R）属性的 10。远程武器也会有 5（导出冗余）——<b>有 range 就不看它</b>。</summary>
    public int? Reach { get; init; }

    /// <summary>重量（磅；真实包是数字，缺失为 null）。</summary>
    public double? Weight { get; init; }

    /// <summary>价值（铜币单位；真实包是数字，缺失为 null）。</summary>
    public double? Value { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>状态（数据包 <c>conditions[]</c>）。</summary>
public sealed record ConditionDefinition
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// 技能（数据包 <c>skills[]</c>，2024 版 18 项）：id / name / cnName / keywords / source / ability / text。
/// <para>
/// <b>叙事检定靠它做"技能 → 属性"映射</b>，不要硬编码 18 项——数据源换了只换包，不改代码。
/// </para>
/// </summary>
public sealed record SkillDefinition
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary>关联属性缩写（str / dex / con / int / wis / cha）；由 <see cref="AbilityText"/> 解析。</summary>
    public string Ability { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;
}

/// <summary>动作（数据包 <c>actions[]</c>；比状态多一个 <c>time</c>，形状同法术的施法时间）。</summary>
public sealed record ActionDefinition
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary>规则版本（<c>2014</c> / <c>2024</c>）；V4 包才有，V3 为空。</summary>
    public string Ruleset { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Time { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public string TimeText => string.Join("；", Time);
}

/// <summary>条目之间的一条交叉引用（<c>features[].references[]</c>）。</summary>
/// <remarks>
/// 形如 <c>{"type":"refOptionalfeature","id":"Agonizing Blast|XPHB"}</c>：它告诉模型
/// "这条特性底下还有 N 个可选条目，用 <c>kind=optional</c> 去查"。
/// </remarks>
public sealed record RuleReference
{
    public string Type { get; init; } = string.Empty;

    public string Id { get; init; } = string.Empty;
}

/// <summary>职业 / 子职的一条特性（<c>features[]</c>）：等级 + 中英文名 + 英文原文 + 可选引用。</summary>
public sealed record FeatureEntry
{
    public int Level { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<RuleReference> References { get; init; } = [];
}

/// <summary>技能选择（<c>skillChoices[]</c>）：从 <see cref="From"/> 里挑 <see cref="Count"/> 项。</summary>
public sealed record SkillChoice
{
    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> From { get; init; } = [];

    public int Count { get; init; }
}

/// <summary>职业（数据包 <c>classes[]</c>；V4 的 13 个职业，2014 / 2024 两套规则各一条）。</summary>
public sealed record ClassEntry
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary>规则版本：<c>2014</c> / <c>2024</c>。</summary>
    public string Ruleset { get; init; } = string.Empty;

    /// <summary>生命骰面数（如 6 = d6）；个别条目缺失。</summary>
    public int? HitDie { get; init; }

    public int HitDieCount { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Saves { get; init; } = [];

    /// <summary>施法主属性（英文缩写，如 <c>int</c>）；非施法职业为空。</summary>
    public string? SpellcastingAbility { get; init; }

    /// <summary>子职称呼（如 <c>Wizard Subclass</c>）。</summary>
    public string? SubclassTitle { get; init; }

    [JsonConverter(typeof(FlexibleTextConverter))]
    public string? MulticlassRequirement { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Subclasses { get; init; } = [];

    public IReadOnlyList<SkillChoice> SkillChoices { get; init; } = [];

    public IReadOnlyList<FeatureEntry> Features { get; init; } = [];

    /// <summary>生命骰文本（<c>d6</c>；缺 <c>hitDie</c> 时写 <c>?</c>）。</summary>
    public string HitDieText => HitDie is { } die ? $"d{die}" : "?";
}

/// <summary>
/// 子职（数据包 <c>subclasses[]</c>，200 条）：挂在某个职业下，特性是它自己的那几级。
/// <para>
/// <c>featuresInheritedFrom</c> 表示"特性原文取自另一本书的老版本"（例如 2024 的子职沿用 PHB 原文），
/// 回执里要照实写出来，别让模型以为这是两份不同的东西。
/// </para>
/// </summary>
public sealed record SubclassEntry
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    /// <summary>所属职业名（英文，如 <c>Monk</c>）。</summary>
    public string ClassName { get; init; } = string.Empty;

    public string ClassSource { get; init; } = string.Empty;

    /// <summary>所属职业的规则版本（<c>2014</c> / <c>2024</c>）。</summary>
    public string ClassRuleset { get; init; } = string.Empty;

    /// <summary>简称（如 <c>Drunken Master</c>）。</summary>
    public string ShortName { get; init; } = string.Empty;

    /// <summary>特性原文继承自哪本书（如 <c>PHB</c>）；空 = 本条目自己的原文。</summary>
    public string? FeaturesInheritedFrom { get; init; }

    public IReadOnlyList<FeatureEntry> Features { get; init; } = [];
}

/// <summary>专长（数据包 <c>feats[]</c>，128 条；2014 与 2024 各一套）。</summary>
public sealed record FeatEntry
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    public string Ruleset { get; init; } = string.Empty;

    /// <summary>类别代码（如 <c>O</c> = 起源专长、<c>G</c> = 通用）；可能为空。</summary>
    public string? Category { get; init; }

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Prerequisite { get; init; } = [];

    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// 可选特性（数据包 <c>optionalFeatures[]</c>，156 条）：邪术师祈唤 / 战斗风格 / 元素亲和这类
/// "从一组里挑"的条目。<c>featureTypes</c> 是类别代码（EI = 邪术师祈唤、FS:F = 战斗风格…）。
/// </summary>
public sealed record OptionalFeatureEntry
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    public string Ruleset { get; init; } = string.Empty;

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> FeatureTypes { get; init; } = [];

    [JsonConverter(typeof(FlexibleTextListConverter))]
    public IReadOnlyList<string> Prerequisite { get; init; } = [];

    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// 规则数据包（schema <c>meetingroom-rules/3</c> 与 <c>/4</c>）：<b>只读的静态资料</b>，不含任何角色数值。
/// <para>
/// id 一律 <c>name|source</c>，枚举一律英文；<c>cnName</c> 只给显示与匹配用，不参与逻辑。
/// V4 包约 4.2 MB：monsterTemplates 706 / classes 13 / subclasses 200 / feats 128 /
/// optionalFeatures 156 / spells 488 / items 965 / conditions 27 / actions 31 / skills 18。
/// </para>
/// </summary>
public sealed record RulesData
{
    public required string Schema { get; init; }

    /// <summary>规则版本（真实包：<c>5e-2024</c>）。</summary>
    public string Ruleset { get; init; } = string.Empty;

    /// <summary>导出策略说明（真实包：<c>english-primary-chinese-keywords</c>）。</summary>
    public string Strategy { get; init; } = string.Empty;

    /// <summary>参与的书籍缩写（PHB / XPHB / DMG …）。</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public IReadOnlyList<MonsterTemplate> MonsterTemplates { get; init; } = [];

    public IReadOnlyList<SpellDefinition> Spells { get; init; } = [];

    public IReadOnlyList<ItemDefinition> Items { get; init; } = [];

    public IReadOnlyList<ConditionDefinition> Conditions { get; init; } = [];

    public IReadOnlyList<ActionDefinition> Actions { get; init; } = [];

    /// <summary>技能表（18 项）；叙事检定用它把"游说 / 洞悉…"映射到属性。</summary>
    public IReadOnlyList<SkillDefinition> Skills { get; init; } = [];

    /// <summary>职业（13 条，2014 / 2024 各一套）。</summary>
    public IReadOnlyList<ClassEntry> Classes { get; init; } = [];

    /// <summary>子职（200 条，按职业挂）。</summary>
    public IReadOnlyList<SubclassEntry> Subclasses { get; init; } = [];

    /// <summary>专长（128 条）。</summary>
    public IReadOnlyList<FeatEntry> Feats { get; init; } = [];

    /// <summary>可选特性（156 条：祈唤 / 战斗风格 / 元素亲和…）。</summary>
    public IReadOnlyList<OptionalFeatureEntry> OptionalFeatures { get; init; } = [];

    // 条目量级是"几百到几千"，线性查找足够；真成热点再加索引，别过早优化。
    public MonsterTemplate? FindMonster(string id) => Find(MonsterTemplates, id, item => item.Id);

    public SpellDefinition? FindSpell(string id) => Find(Spells, id, item => item.Id);

    public ItemDefinition? FindItem(string id) => Find(Items, id, item => item.Id);

    public ConditionDefinition? FindCondition(string id) => Find(Conditions, id, item => item.Id);

    public ActionDefinition? FindAction(string id) => Find(Actions, id, item => item.Id);

    public ClassEntry? FindClass(string id) => Find(Classes, id, item => item.Id);

    public SubclassEntry? FindSubclass(string id) => Find(Subclasses, id, item => item.Id);

    public FeatEntry? FindFeat(string id) => Find(Feats, id, item => item.Id);

    public OptionalFeatureEntry? FindOptionalFeature(string id) => Find(OptionalFeatures, id, item => item.Id);

    /// <summary>
    /// 按 id / 英文名 / 中文名找技能（模型可能给 <c>persuasion</c> 或「游说」）。
    /// id 精确优先，其次英文名忽略大小写，最后中文名。
    /// </summary>
    public SkillDefinition? FindSkill(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var value = idOrName.Trim();

        return Skills.FirstOrDefault(skill => string.Equals(skill.Id, value, StringComparison.Ordinal))
            ?? Skills.FirstOrDefault(skill => string.Equals(skill.Name, value, StringComparison.OrdinalIgnoreCase))
            ?? Skills.FirstOrDefault(skill => string.Equals(skill.CnName, value, StringComparison.Ordinal));
    }

    private static T? Find<T>(IReadOnlyList<T> items, string id, Func<T, string> getId)
        where T : class =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : items.FirstOrDefault(item => string.Equals(getId(item), id, StringComparison.Ordinal));
}
