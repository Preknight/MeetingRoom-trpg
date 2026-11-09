using System.Text;

namespace MeetingRoom.Core.Rules;

/// <summary>规则查询能查的九类东西（对应数据包里的九节）。</summary>
public enum RuleKind
{
    Spell,
    Item,
    Condition,
    Action,
    Skill,
    Feat,
    Class,
    Subclass,
    Optional,
}

/// <summary>
/// 一条命中结果：紧凑行（<see cref="Compact"/>）与完整原文（<see cref="Detail"/>）都<b>按类别现算</b>——
/// 规则包有 1800+ 条，给每条都预生成两段文本既慢又浪费。
/// </summary>
public sealed record RuleHit(
    RuleKind Kind,
    string Id,
    string Name,
    string CnName,
    string Source,
    string Compact,
    string Detail);

/// <summary>
/// 规则查询（<c>lookup_rule</c> / <c>lookup_monster</c>）的检索与格式化。
/// <para>
/// 检索顺序（用户定，命中即止）：① <c>id</c> / 英文名 / 中文名<b>全等</b>（忽略大小写）→
/// ② <c>keywords</c> 任一元素<b>包含</b> query（子串，所以「火球」能命中「火球术」）→
/// ③ 指定了 <c>kind</c> 时在同类里搜 <c>text</c>。限 5 条候选。
/// </para>
/// <para>
/// 这里<b>只读</b>：不结算、不改状态。数值（伤害、骰子）由战斗与检定工具负责。
/// </para>
/// </summary>
public static class RulesLookup
{
    /// <summary>候选最多列几条（再多也没人看，还会把前缀预算吃掉）。</summary>
    public const int MaxCandidates = 5;

    /// <summary>完整原文的上限：超过就截断并标 <c>[truncated]</c>。</summary>
    public const int DetailTextLimit = 4000;

    private static readonly IReadOnlyDictionary<string, RuleKind> KindAliases =
        new Dictionary<string, RuleKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["spell"] = RuleKind.Spell,
            ["spells"] = RuleKind.Spell,
            ["法术"] = RuleKind.Spell,
            ["item"] = RuleKind.Item,
            ["items"] = RuleKind.Item,
            ["装备"] = RuleKind.Item,
            ["物品"] = RuleKind.Item,
            ["condition"] = RuleKind.Condition,
            ["conditions"] = RuleKind.Condition,
            ["状态"] = RuleKind.Condition,
            ["action"] = RuleKind.Action,
            ["actions"] = RuleKind.Action,
            ["动作"] = RuleKind.Action,
            ["skill"] = RuleKind.Skill,
            ["skills"] = RuleKind.Skill,
            ["技能"] = RuleKind.Skill,
            ["feat"] = RuleKind.Feat,
            ["feats"] = RuleKind.Feat,
            ["专长"] = RuleKind.Feat,
            ["class"] = RuleKind.Class,
            ["classes"] = RuleKind.Class,
            ["职业"] = RuleKind.Class,
            ["subclass"] = RuleKind.Subclass,
            ["subclasses"] = RuleKind.Subclass,
            ["子职"] = RuleKind.Subclass,
            ["optional"] = RuleKind.Optional,
            ["optionalFeature"] = RuleKind.Optional,
            ["optionalFeatures"] = RuleKind.Optional,
            ["可选特性"] = RuleKind.Optional,
            ["祈唤"] = RuleKind.Optional,
        };

    private static readonly IReadOnlyDictionary<RuleKind, string> KindLabels =
        new Dictionary<RuleKind, string>
        {
            [RuleKind.Spell] = "法术",
            [RuleKind.Item] = "装备",
            [RuleKind.Condition] = "状态",
            [RuleKind.Action] = "动作",
            [RuleKind.Skill] = "技能",
            [RuleKind.Feat] = "专长",
            [RuleKind.Class] = "职业",
            [RuleKind.Subclass] = "子职",
            [RuleKind.Optional] = "可选特性",
        };

    // ——— 人话映射：数据包一律英文枚举，回执给中文（§27） ———

    private static readonly IReadOnlyDictionary<string, string> SchoolNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "防护",
            ["C"] = "咒法",
            ["D"] = "预言",
            ["E"] = "附魔",
            ["V"] = "塑能",
            ["I"] = "幻术",
            ["N"] = "死灵",
            ["T"] = "变化",
            ["P"] = "灵能",
        };

    private static readonly IReadOnlyDictionary<string, string> AbilityNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["strength"] = "力量",
            ["dexterity"] = "敏捷",
            ["constitution"] = "体质",
            ["intelligence"] = "智力",
            ["wisdom"] = "感知",
            ["charisma"] = "魅力",
            ["str"] = "力量",
            ["dex"] = "敏捷",
            ["con"] = "体质",
            ["int"] = "智力",
            ["wis"] = "感知",
            ["cha"] = "魅力",
        };

    private static readonly IReadOnlyDictionary<string, string> DamageTypeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["acid"] = "强酸",
            ["bludgeoning"] = "钝击",
            ["cold"] = "寒冷",
            ["fire"] = "火焰",
            ["force"] = "力场",
            ["lightning"] = "闪电",
            ["necrotic"] = "黯蚀",
            ["piercing"] = "穿刺",
            ["poison"] = "毒素",
            ["psychic"] = "心灵",
            ["radiant"] = "光耀",
            ["slashing"] = "挥砍",
            ["thunder"] = "雷鸣",
        };

    private static readonly IReadOnlyDictionary<string, string> UnitNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "动作",
            ["bonus"] = "附赠动作",
            ["reaction"] = "反应",
            ["minute"] = "分钟",
            ["hour"] = "小时",
            ["round"] = "轮",
            ["day"] = "天",
        };

    private static readonly IReadOnlyDictionary<string, string> DistanceNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["feet"] = "尺",
            ["miles"] = "里",
            ["touch"] = "触及",
            ["self"] = "自身",
            ["sight"] = "视线内",
            ["unlimited"] = "无限",
        };

    private static readonly IReadOnlyDictionary<string, string> ItemKindNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon"] = "武器",
            ["armor"] = "护甲",
            ["shield"] = "盾牌",
            ["consumable"] = "消耗品",
            ["gear"] = "杂物",
        };

    private static readonly IReadOnlyDictionary<string, string> FeatureTypeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EI"] = "邪术师祈唤",
            ["FS:F"] = "战斗风格：格斗",
            ["FS:R"] = "战斗风格：远程",
            ["FS:P"] = "战斗风格：防御",
            ["FS:B"] = "战斗风格：双武器",
            ["MV:B"] = "战技：武技",
            ["MV:S"] = "战技：策略",
            ["AS"] = "元素亲和",
            ["ED"] = "额外专长",
            ["MM"] = "超魔法",
            ["RP"] = "契约赠礼",
            ["RN"] = "契约恩赐",
            ["AI"] = "祈愿祈唤",
            ["PB"] = "契约：书页",
        };

    /// <summary>把模型给的 <c>kind</c> 参数解析成枚举；空 = 不限类别；不认识 = 报错（不猜）。</summary>
    public static RuleKind? ParseKind(string? raw, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (KindAliases.TryGetValue(raw.Trim(), out var kind))
        {
            return kind;
        }

        error = $"不认识的 kind「{raw.Trim()}」，可选：spell | item | condition | action | skill | feat | class | subclass | optional。";
        return null;
    }

    /// <summary>类别中文名（给回执用）。</summary>
    public static string Label(RuleKind kind) => KindLabels[kind];

    /// <summary>
    /// 按"全等 → keywords 包含 → 同类 text 包含"的顺序检索，命中即止；最多返回 <see cref="MaxCandidates"/> 条。
    /// 只对命中的条目现算紧凑/完整文本。
    /// </summary>
    public static IReadOnlyList<RuleHit> Search(RulesData data, string query, RuleKind? kind)
    {
        ArgumentNullException.ThrowIfNull(data);

        var text = (query ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return [];
        }

        var scoped = Ref.All(data, kind);

        var stage = scoped.Where(item => item.MatchesName(text)).ToList();

        if (stage.Count == 0)
        {
            // 关键词命中常常一堆（「火球」会连「延迟爆裂火球」一起命中）：
            // 名字越短越可能是用户要的那条，所以按名字长度升序——「火球术」排在第一位。
            stage = scoped
                .Where(item => item.MatchesKeyword(text))
                .OrderBy(item => item.Name.Length)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
        }

        if (stage.Count == 0 && kind is not null)
        {
            // ③ 只对指定类别做正文搜索：不限类别时全文搜索会命中一片（"攻击"能命中几百条）。
            stage = scoped.Where(item => item.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return stage.Take(MaxCandidates).Select(item => item.ToHit(data)).ToList();
    }

    /// <summary>
    /// <c>lookup_rule</c> 的回执：<paramref name="detail"/> = 只给一条完整原文；否则给紧凑行或候选列表。
    /// </summary>
    public static string Describe(IReadOnlyList<RuleHit> hits, string query, RuleKind? kind, bool detail)
    {
        ArgumentNullException.ThrowIfNull(hits);

        if (hits.Count == 0)
        {
            var scope = kind is null ? "九类条目" : $"「{Label(kind.Value)}」";
            return $"规则包里没有匹配「{query}」的{scope}（已按 名称 → 关键词 → 正文 的顺序查过）。"
                + "换个说法试试（中文名 / 英文名 / 部分词），或用 kind 指定类别。"
                + "这只说明资料里没有，不代表规则上不存在——真拿不准就按导演的判断走。";
        }

        if (detail)
        {
            var hit = hits[0];
            var builder = new StringBuilder(hit.Detail);

            if (hits.Count > 1)
            {
                builder.Append('\n').Append('(').Append("另有 ").Append(hits.Count - 1)
                    .Append(" 条也匹配「").Append(query).Append("」，detail 只显示第一条；")
                    .Append("要别的请用 kind 收窄或写全名。)");
            }

            return builder.ToString();
        }

        if (hits.Count == 1)
        {
            return hits[0].Compact;
        }

        var lines = new StringBuilder();
        var kinds = hits.Select(hit => Label(hit.Kind)).Distinct().ToList();
        lines.Append('「').Append(query).Append("」命中 ").Append(hits.Count).Append(" 条候选")
            .Append(kinds.Count > 1 ? $"（跨 {kinds.Count} 类：{string.Join('、', kinds)}）" : $"（同类：{kinds[0]}）")
            .Append("：");

        foreach (var hit in hits)
        {
            lines.Append('\n').Append("- ").Append(Line(hit));
        }

        lines.Append('\n').Append("用 kind 收窄（spell|item|condition|action|skill|feat|class|subclass|optional），"
            + "或给更完整的名称；要完整原文加 detail=true。");

        return lines.ToString();

        static string Line(RuleHit hit)
        {
            var name = string.IsNullOrWhiteSpace(hit.CnName) ? hit.Name : $"{hit.CnName}（{hit.Name}）";
            return $"{name} | {hit.Source} | {Label(hit.Kind)}";
        }
    }

    /// <summary>
    /// <c>lookup_monster</c> 的回执。模板只是基线——回执末尾必须说清"开战时 DM 可调 HP 与数量"。
    /// </summary>
    public static string DescribeMonster(RulesData data, string query, bool detail)
    {
        ArgumentNullException.ThrowIfNull(data);

        var text = (query ?? string.Empty).Trim();
        var baseline = "（这是模板基线，开战时 DM 可按战况调 HP 与数量。）";

        if (text.Length == 0)
        {
            return "要查哪个怪物？给一个名字（中文 / 英文 / id 都行）。" + baseline;
        }

        var all = data.MonsterTemplates;
        var hits = all.Where(item => MatchesMonsterName(item, text)).ToList();

        if (hits.Count == 0)
        {
            hits = all.Where(item => item.Keywords.Any(keyword => keyword.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        if (hits.Count == 0)
        {
            return $"规则包里没有匹配「{text}」的怪物模板（已按 名称 → 关键词 查过）。"
                + "换成英文名或更短的部分词试试（例如「哥布林」「Goblin」「龙」）。" + baseline;
        }

        if (detail)
        {
            var monster = hits[0];
            var builder = new StringBuilder();

            builder.AppendLine(MonsterHeader(monster));

            if (!string.IsNullOrWhiteSpace(monster.Speed))
            {
                builder.Append("速度 ").Append(monster.Speed).Append('\n');
            }

            AppendMonsterBlock(builder, "特性", monster.Traits);
            AppendMonsterBlock(builder, "动作", monster.Actions);
            AppendMonsterBlock(builder, "附赠动作", monster.BonusActions);
            AppendMonsterBlock(builder, "反应", monster.Reactions);
            AppendMonsterBlock(builder, "传奇动作", monster.Legendary);
            AppendMonsterBlock(builder, "神话动作", monster.Mythic);
            AppendMonsterBlock(builder, "施法", monster.Spellcasting);

            if (!string.IsNullOrWhiteSpace(monster.Fluff))
            {
                builder.Append('\n').Append(monster.Fluff).Append('\n');
            }

            var body = builder.ToString();

            if (body.Length > DetailTextLimit)
            {
                body = body[..DetailTextLimit] + "\n[truncated]";
            }

            if (hits.Count > 1)
            {
                body += $"\n(另有 {hits.Count - 1} 条也匹配「{text}」，detail 只显示第一条。)";
            }

            return body + baseline;
        }

        if (hits.Count == 1)
        {
            return MonsterCompact(hits[0]) + "\n" + baseline;
        }

        var list = new StringBuilder();
        list.Append('「').Append(text).Append("」命中 ").Append(hits.Count).Append(" 个怪物模板（最多列 ")
            .Append(MaxCandidates).Append(" 条）：");

        foreach (var monster in hits.Take(MaxCandidates))
        {
            var name = string.IsNullOrWhiteSpace(monster.CnName) ? monster.Name : $"{monster.CnName}（{monster.Name}）";
            list.Append('\n').Append("- ").Append(name).Append(" | ").Append(monster.Source)
                .Append(" | CR ").Append(monster.Cr ?? "?");
        }

        list.Append('\n').Append("写更完整的名字，或加 detail=true 看第一条的完整数据。").Append(baseline);

        return list.ToString();
    }

    /// <summary>怪物紧凑行：CR / 类型 / 体型 / HP(均值+骰式) / AC / 六属性 / 特性名列表。</summary>
    public static string MonsterCompact(MonsterTemplate monster)
    {
        ArgumentNullException.ThrowIfNull(monster);

        var builder = new StringBuilder();
        builder.Append(MonsterHeader(monster));
        builder.Append('\n').Append("HP ")
            .Append(monster.Hp?.Average?.ToString() ?? "?")
            .Append('（').Append(string.IsNullOrWhiteSpace(monster.Hp?.Formula) ? "?" : monster.Hp!.Formula).Append('）')
            .Append(" | AC ").Append(string.IsNullOrWhiteSpace(monster.AcText) ? "?" : monster.AcText);

        if (monster.Abilities is { IsEmpty: false } abilities)
        {
            builder.Append('\n').Append("STR ").Append(abilities.Describe());
        }

        var traitNames = monster.Traits.Select(trait => trait.Name).Where(name => name.Length > 0).ToList();

        if (traitNames.Count > 0)
        {
            builder.Append('\n').Append("特性：").Append(string.Join('、', traitNames));
        }

        return builder.ToString();
    }

    /// <summary>怪物的第一行：中英文名 | 来源 | CR | 类型 · 体型。</summary>
    public static string MonsterHeader(MonsterTemplate monster)
    {
        var name = string.IsNullOrWhiteSpace(monster.CnName) ? monster.Name : $"{monster.CnName}（{monster.Name}）";
        var type = string.IsNullOrWhiteSpace(monster.Type) ? "类型?" : monster.Type!;
        var size = string.IsNullOrWhiteSpace(monster.Size) ? "体型?" : monster.Size!;

        return $"{name} | {monster.Source} | CR {monster.Cr ?? "?"} | {type} · {size}";
    }

    private static void AppendMonsterBlock(StringBuilder builder, string title, IReadOnlyList<MonsterAttack> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.Append('\n').Append("【").Append(title).Append('】');

        foreach (var item in items)
        {
            builder.Append('\n').Append("- ").Append(item.Name);

            if (item.AttackBonus is { } bonus)
            {
                builder.Append("（命中 +").Append(bonus).Append('）');
            }

            if (!string.IsNullOrWhiteSpace(item.DamageDice))
            {
                builder.Append("（伤害 ").Append(item.DamageDice).Append('）');
            }

            if (!string.IsNullOrWhiteSpace(item.Text))
            {
                builder.Append('：').Append(item.Text);
            }
        }

        builder.Append('\n');
    }

    private static bool MatchesMonsterName(MonsterTemplate monster, string query) =>
        string.Equals(monster.Id, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(monster.Name, query, StringComparison.OrdinalIgnoreCase)
        || string.Equals(monster.CnName, query, StringComparison.OrdinalIgnoreCase);

    /// <summary>检索用的轻量视图：只带匹配需要的字段，命中后再按类别现算文本。</summary>
    private sealed record Ref(
        RuleKind Kind,
        string Id,
        string Name,
        string CnName,
        string Source,
        IReadOnlyList<string> Keywords,
        string Text)
    {
        public static IReadOnlyList<Ref> All(RulesData data, RuleKind? kind)
        {
            var refs = new List<Ref>(1800);

            void Add(RuleKind itemKind, string id, string name, string cnName, string source,
                IReadOnlyList<string> keywords, string text)
            {
                if (kind is null || kind == itemKind)
                {
                    refs.Add(new Ref(itemKind, id, name, cnName, source, keywords, text));
                }
            }

            foreach (var spell in data.Spells)
            {
                Add(RuleKind.Spell, spell.Id, spell.Name, spell.CnName, spell.Source, spell.Keywords, spell.Text);
            }

            foreach (var item in data.Items)
            {
                Add(RuleKind.Item, item.Id, item.Name, item.CnName, item.Source, item.Keywords, item.Text);
            }

            foreach (var condition in data.Conditions)
            {
                Add(RuleKind.Condition, condition.Id, condition.Name, condition.CnName, condition.Source, condition.Keywords, condition.Text);
            }

            foreach (var action in data.Actions)
            {
                Add(RuleKind.Action, action.Id, action.Name, action.CnName, action.Source, action.Keywords, action.Text);
            }

            foreach (var skill in data.Skills)
            {
                Add(RuleKind.Skill, skill.Id, skill.Name, skill.CnName, skill.Source, skill.Keywords, skill.Text);
            }

            foreach (var feat in data.Feats)
            {
                Add(RuleKind.Feat, feat.Id, feat.Name, feat.CnName, feat.Source, feat.Keywords, feat.Text);
            }

            foreach (var entry in data.Classes)
            {
                Add(RuleKind.Class, entry.Id, entry.Name, entry.CnName, entry.Source, entry.Keywords, FeatureText(entry.Features));
            }

            foreach (var entry in data.Subclasses)
            {
                Add(RuleKind.Subclass, entry.Id, entry.Name, entry.CnName, entry.Source, entry.Keywords, FeatureText(entry.Features));
            }

            foreach (var entry in data.OptionalFeatures)
            {
                Add(RuleKind.Optional, entry.Id, entry.Name, entry.CnName, entry.Source, entry.Keywords, entry.Text);
            }

            return refs;
        }

        public bool MatchesName(string query) =>
            string.Equals(Id, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(CnName, query, StringComparison.OrdinalIgnoreCase);

        public bool MatchesKeyword(string query) =>
            Keywords.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase));

        public RuleHit ToHit(RulesData data)
        {
            var (compact, detail) = Kind switch
            {
                RuleKind.Spell => SpellText(data.FindSpell(Id)!),
                RuleKind.Item => ItemText(data.FindItem(Id)!),
                RuleKind.Condition => ConditionText(data.FindCondition(Id)!),
                RuleKind.Action => ActionText(data.FindAction(Id)!),
                RuleKind.Skill => SkillText(data.Skills.First(item => item.Id == Id)),
                RuleKind.Feat => FeatText(data.FindFeat(Id)!),
                RuleKind.Class => ClassText(data.FindClass(Id)!),
                RuleKind.Subclass => SubclassText(data.FindSubclass(Id)!),
                RuleKind.Optional => OptionalText(data.FindOptionalFeature(Id)!),
                _ => (Name, Name),
            };

            return new RuleHit(Kind, Id, Name, CnName, Source, compact, detail);
        }

        private static string FeatureText(IReadOnlyList<FeatureEntry> features) =>
            string.Join('\n', features.Select(feature => $"{feature.Name} {feature.CnName} {feature.Text}"));
    }

    // ——— 各类别的紧凑行 + 完整详情 ———

    private static (string Compact, string Detail) SpellText(SpellDefinition spell)
    {
        var level = spell.Level == 0 ? "戏法" : $"{spell.Level} 环";
        var school = SchoolNames.TryGetValue(spell.School, out var schoolName) ? schoolName : spell.School;
        var head = $"{Name(spell.CnName, spell.Name)} | {spell.Source} | {level}·{school}";

        var compact = new StringBuilder(head);
        compact.Append('\n').Append("施法 ").Append(DescribeTime(spell.Time))
            .Append(" | 射程 ").Append(DescribeRange(spell.Range))
            .Append(" | 成分 ").Append(DescribeComponents(spell.Components))
            .Append(" | 持续 ").Append(DescribeDuration(spell.Duration)).Append('\n')
            .Append("伤害 ").Append(DescribeDamage(spell.Damage))
            .Append(" | 豁免 ").Append(DescribeSave(spell.Save));

        if (spell.Scaling.Count > 0)
        {
            compact.Append('\n').Append("升环 ").Append(spell.ScalingText);
        }

        var detail = new StringBuilder(compact.ToString());
        detail.Append('\n').Append(Trim(spell.Text));

        return (compact.ToString(), detail.ToString());
    }

    private static (string Compact, string Detail) ItemText(ItemDefinition item)
    {
        var kind = ItemKindNames.TryGetValue(item.Kind, out var kindName) ? kindName : item.Kind;
        var compact = new StringBuilder($"{Name(item.CnName, item.Name)} | {item.Source} | {kind}");

        if (!string.IsNullOrWhiteSpace(item.Dmg1))
        {
            compact.Append(" | 伤害 ").Append(item.Dmg1);

            if (!string.IsNullOrWhiteSpace(item.Dmg2))
            {
                compact.Append("（双手 ").Append(item.Dmg2).Append('）');
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Range))
        {
            compact.Append(" | 射程 ").Append(item.Range);
        }

        if (item.Mastery.Count > 0)
        {
            compact.Append(" | 精通 ").Append(string.Join('、', item.Mastery));
        }

        if (item.Properties.Count > 0)
        {
            compact.Append(" | 属性 ").Append(string.Join('、', item.Properties));
        }

        var detail = new StringBuilder(compact.ToString());
        detail.Append('\n').Append(Trim(item.Text));

        return (compact.ToString(), detail.ToString());
    }

    private static (string Compact, string Detail) ConditionText(ConditionDefinition condition)
    {
        var head = $"{Name(condition.CnName, condition.Name)} | {condition.Source}";
        var compact = new StringBuilder(head);
        var summary = FirstSentence(condition.Text);

        if (summary.Length > 0)
        {
            compact.Append('\n').Append(summary);
        }

        return (compact.ToString(), head + "\n" + Trim(condition.Text));
    }

    private static (string Compact, string Detail) ActionText(ActionDefinition action)
    {
        var time = action.Time.Count > 0 ? action.TimeText : "—";
        var head = $"{Name(action.CnName, action.Name)} | {action.Source} | {time}";
        var compact = new StringBuilder(head);
        var summary = FirstSentence(action.Text);

        if (summary.Length > 0)
        {
            compact.Append('\n').Append(summary);
        }

        return (compact.ToString(), head + "\n" + Trim(action.Text));
    }

    private static (string Compact, string Detail) SkillText(SkillDefinition skill)
    {
        var ability = AbilityNames.TryGetValue(skill.Ability, out var abilityName) ? abilityName : skill.Ability;
        var head = $"{Name(skill.CnName, skill.Name)} | {skill.Source} | {ability}";
        var compact = new StringBuilder(head);
        var summary = FirstSentence(skill.Text);

        if (summary.Length > 0)
        {
            compact.Append('\n').Append(summary);
        }

        return (compact.ToString(), head + "\n" + Trim(skill.Text));
    }

    private static (string Compact, string Detail) FeatText(FeatEntry feat)
    {
        var head = $"{Name(feat.CnName, feat.Name)} | {feat.Source} | {feat.Ruleset}"
            + (string.IsNullOrWhiteSpace(feat.Category) ? string.Empty : $" | 类别 {feat.Category}");

        var compact = new StringBuilder(head);
        var summary = FirstSentence(feat.Text);

        if (summary.Length > 0)
        {
            compact.Append('\n').Append(summary);
        }

        if (feat.Prerequisite.Count > 0 && feat.Prerequisite.Any(item => item.Length > 0))
        {
            compact.Append('\n').Append("前提：").Append(string.Join('、', feat.Prerequisite.Where(item => item.Length > 0)));
        }

        return (compact.ToString(), head + "\n" + Trim(feat.Text));
    }

    private static (string Compact, string Detail) OptionalText(OptionalFeatureEntry feature)
    {
        var types = feature.FeatureTypes
            .Select(type => FeatureTypeNames.TryGetValue(type, out var name) ? $"{name}（{type}）" : type)
            .ToList();

        var head = $"{Name(feature.CnName, feature.Name)} | {feature.Source} | {feature.Ruleset}"
            + (types.Count == 0 ? string.Empty : $" | {string.Join('、', types)}");

        var compact = new StringBuilder(head);
        var summary = FirstSentence(feature.Text);

        if (summary.Length > 0)
        {
            compact.Append('\n').Append(summary);
        }

        return (compact.ToString(), head + "\n" + Trim(feature.Text));
    }

    private static (string Compact, string Detail) ClassText(ClassEntry entry)
    {
        var ability = string.IsNullOrWhiteSpace(entry.SpellcastingAbility)
            ? "非施法"
            : (AbilityNames.TryGetValue(entry.SpellcastingAbility, out var abilityName) ? abilityName : entry.SpellcastingAbility);

        var head = $"{Name(entry.CnName, entry.Name)} | {entry.Source} | {entry.Ruleset} | {entry.HitDieText}"
            + $" | 豁免 {string.Join('/', entry.Saves)}"
            + $" | 施法 {ability}"
            + $" | 子职 {entry.Subclasses.Count} 个"
            + $" | 特性 {entry.Features.Count} 条";

        var compact = new StringBuilder(head);

        if (entry.SkillChoices.Count > 0)
        {
            var choice = entry.SkillChoices[0];
            compact.Append('\n').Append("技能：从 ").Append(string.Join('、', choice.From))
                .Append(" 里选 ").Append(choice.Count).Append(" 项");
        }

        var detail = new StringBuilder(head);
        AppendFeatures(detail, entry.Features);

        // 职业特性加起来常常 5000–10000 字符（法师 7127）：detail 也要守住 4000 上限。
        return (compact.ToString(), Trim(detail.ToString()));
    }

    private static (string Compact, string Detail) SubclassText(SubclassEntry entry)
    {
        var inherited = string.IsNullOrWhiteSpace(entry.FeaturesInheritedFrom)
            ? string.Empty
            : $" | 原文继承 {entry.FeaturesInheritedFrom}";

        var head = $"{Name(entry.CnName, entry.Name)} | {entry.Source}"
            + $" | {entry.ClassName}（职业源 {entry.ClassSource}，{entry.ClassRuleset}）"
            + $" | 特性 {entry.Features.Count} 条{inherited}";

        var compact = new StringBuilder(head);
        var levels = entry.Features.Select(feature => feature.Level).Distinct().OrderBy(level => level).ToList();

        if (levels.Count > 0)
        {
            compact.Append('\n').Append("等级：").Append(string.Join('、', levels));
        }

        var detail = new StringBuilder(head);
        AppendFeatures(detail, entry.Features);

        return (compact.ToString(), Trim(detail.ToString()));
    }

    private static void AppendFeatures(StringBuilder builder, IReadOnlyList<FeatureEntry> features)
    {
        foreach (var feature in features)
        {
            builder.Append('\n').Append("- 第 ").Append(feature.Level).Append(" 级 ");
            builder.Append(string.IsNullOrWhiteSpace(feature.CnName) ? feature.Name : $"{feature.CnName}（{feature.Name}）");
            builder.Append('：').Append(Trim(feature.Text));

            if (feature.References.Count > 0)
            {
                builder.Append("（另有 ").Append(feature.References.Count).Append(" 个可选条目，用 kind=optional 查）");
            }
        }
    }

    // ——— 小工具 ———

    private static string Name(string cnName, string name) =>
        string.IsNullOrWhiteSpace(cnName) ? name : $"{cnName}（{name}）";

    private static string DescribeTime(IReadOnlyList<SpellTime> times)
    {
        if (times.Count == 0)
        {
            return "—";
        }

        return string.Join("；", times.Select(time =>
        {
            var unit = UnitNames.TryGetValue(time.Unit, out var unitName) ? unitName : time.Unit;
            var text = time.Number is { } number ? $"{number} {unit}" : unit;
            return string.IsNullOrWhiteSpace(time.Condition) ? text : $"{text}（{time.Condition}）";
        }));
    }

    private static string DescribeRange(SpellRange? range)
    {
        if (range is null)
        {
            return "—";
        }

        if (range.Distance is not { } distance)
        {
            return range.Type;
        }

        var unit = DistanceNames.TryGetValue(distance.Type, out var unitName) ? unitName : distance.Type;
        return distance.Amount is { } amount ? $"{amount} {unit}" : unit;
    }

    /// <summary>成分只写 V / S / M 三个字母：材料说明可能很长，细节留给 detail。</summary>
    private static string DescribeComponents(SpellComponents? components)
    {
        if (components is null)
        {
            return "—";
        }

        var parts = new List<string>(3);

        if (components.V == true)
        {
            parts.Add("V");
        }

        if (components.S == true)
        {
            parts.Add("S");
        }

        if (!string.IsNullOrWhiteSpace(components.M))
        {
            parts.Add("M");
        }

        return parts.Count == 0 ? "—" : string.Join(',', parts);
    }

    private static string DescribeDuration(IReadOnlyList<SpellDuration> durations)
    {
        if (durations.Count == 0)
        {
            return "—";
        }

        return string.Join("；", durations.Select(duration =>
        {
            var text = duration.Type switch
            {
                "instant" => "立即",
                "permanent" => "永久",
                "special" => "特殊",
                "timed" => duration.Duration is { } span
                    ? (span.Amount is { } amount
                        ? $"{amount} {(UnitNames.TryGetValue(span.Type, out var unitName) ? unitName : span.Type)}"
                        : (UnitNames.TryGetValue(span.Type, out var onlyUnit) ? onlyUnit : span.Type))
                    : "计时",
                _ => duration.Type,
            };

            if (duration.Concentration == true)
            {
                text += "（专注）";
            }

            return duration.Ends.Count > 0 ? $"{text}（终止：{string.Join('、', duration.Ends)}）" : text;
        }));
    }

    private static string DescribeDamage(IReadOnlyList<DamagePartDefinition> damage)
    {
        if (damage.Count == 0)
        {
            return "—";
        }

        return string.Join(" + ", damage.Select(part =>
        {
            if (string.IsNullOrWhiteSpace(part.Type))
            {
                return part.Dice;
            }

            return DamageTypeNames.TryGetValue(part.Type, out var type) ? $"{part.Dice} {type}" : $"{part.Dice} {part.Type}";
        }));
    }

    /// <summary>
    /// 豁免只写属性名，<b>不写"成功减半"</b>——减半是具体法术的措辞（有的写"成功则不受影响"），
    /// 数据包里没有这个字段，编一个会误导。
    /// </summary>
    private static string DescribeSave(string? save)
    {
        if (string.IsNullOrWhiteSpace(save))
        {
            return "无";
        }

        return AbilityNames.TryGetValue(save, out var abilityName) ? abilityName : save;
    }

    /// <summary>正文摘要：取第一句（够模型判断"是不是这条"），压成一行。</summary>
    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flat = text.ReplaceLineEndings(" ").Trim();
        var end = flat.IndexOfAny(['.', '。', '!', '！']);

        if (end > 20)
        {
            flat = flat[..(end + 1)];
        }

        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    /// <summary>完整原文：超 <see cref="DetailTextLimit"/> 截断并标注（用户明确要截断标记）。</summary>
    private static string Trim(string text)
    {
        if (text.Length <= DetailTextLimit)
        {
            return text;
        }

        return text[..DetailTextLimit] + "\n[truncated]";
    }
}
