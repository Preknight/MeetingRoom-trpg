using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Combat;

/// <summary>叙事检定的四种形态（《战斗托管设计》§6.3）。</summary>
public enum CheckKind
{
    Ability,
    Skill,
    Contest,
    Passive,
}

/// <summary>检定失败/成功的后果（写进事件，供说书人叙述；没有后果的检定不该做）。</summary>
public sealed record CheckStakes(string? OnSuccess = null, string? OnFailure = null, string? OnPartial = null);

/// <summary>对抗检定的另一方（防御方）。</summary>
public sealed record CheckOpposition
{
    public required string ActorId { get; init; }

    public string? Skill { get; init; }

    public string? Ability { get; init; }

    public int? Modifier { get; init; }

    public int? AbilityModifier { get; init; }

    public int? ProficiencyBonus { get; init; }

    public bool Proficient { get; init; }

    public bool Expertise { get; init; }
}

/// <summary>
/// 一次叙事检定的请求（设计文档 §3 / §6）。
/// <para><b>不进入回合状态机、不消耗行动经济</b>：战斗中是否花 Action 由 DM 判。</para>
/// </summary>
/// <summary>
/// 检定的可见性（C2/§25）：<see cref="Public"/> 公开、<see cref="Secret"/> 暗骰（数值只进 DM 面）。
/// <b>用枚举而不是裸字符串</b>：工具 schema 里会生成 <c>enum</c>，模型才看得见"只有这两个值"。
/// </summary>
public enum CheckVisibility
{
    Public,
    Secret,
}

public sealed record CheckRequest
{
    public required string ActorId { get; init; }

    public CheckKind Kind { get; init; } = CheckKind.Skill;

    /// <summary>技能 id / 英文名 / 中文名（<see cref="RulesData.FindSkill"/> 解析）。</summary>
    public string? Skill { get; init; }

    /// <summary>属性（缩写或全名）；<see cref="CheckKind.Skill"/> 时由技能表推导。</summary>
    public string? Ability { get; init; }

    public int? Dc { get; init; }

    /// <summary><c>dm</c> / <c>narrator</c> / <c>rulesTable</c>（说书人不能既定 DC 又宣称成功）。</summary>
    public string DcSource { get; init; } = "dm";

    /// <summary>直接给总加值（优先）。</summary>
    public int? Modifier { get; init; }

    /// <summary>属性调整值（与熟练拼成总加值）。</summary>
    public int? AbilityModifier { get; init; }

    public int? ProficiencyBonus { get; init; }

    public bool Proficient { get; init; }

    /// <summary>专精：熟练加值双倍（技能检定）。</summary>
    public bool Expertise { get; init; }

    /// <summary>
    /// 角色数值表（F7-①）：请求没给 <see cref="Modifier"/> 时从这里回落加值。
    /// **引擎保持纯函数**：表由会话层从 <c>SheetStore</c> 取来，不在这里注入仓储。
    /// </summary>
    public MeetingRoom.Core.Rules.Sheet? Sheet { get; init; }

    /// <summary><c>normal</c> / <c>advantage</c> / <c>disadvantage</c>。</summary>
    public string Advantage { get; init; } = "normal";

    public string Reason { get; init; } = string.Empty;

    /// <summary>公开还是暗骰（暗骰：数值只进 DM 视角，不进角色上下文）。</summary>
    public CheckVisibility Visibility { get; init; } = CheckVisibility.Public;

    /// <summary>加值来源（F7-①）：<c>request</c> / <c>sheet</c> / <c>none</c>，让 DM 一眼看出这个 +N 是哪来的。</summary>
    public string ModifierSource { get; init; } = "request";

    public CheckStakes? Stakes { get; init; }

    /// <summary>被检定/被说服的对象（防刷 key 的一部分；可空）。</summary>
    public string? TargetId { get; init; }

    public CheckOpposition? OpposedBy { get; init; }
}

/// <summary>
/// 一次检定的结果。<b>两个面</b>：
/// <list type="bullet">
///   <item><see cref="Roll"/> / <see cref="Success"/> / <see cref="Dc"/> —— DM 与日志面（暗骰也照记）；</item>
///   <item><see cref="CharacterFacingSummary"/> —— 角色可见面，暗骰<b>没有数值也没有成败结论</b>。</item>
/// </list>
/// 分流在这里定死，不靠提示词"请勿剧透"。
/// </summary>
public sealed record CheckResult
{
    public required string CheckId { get; init; }

    public required string ActorId { get; init; }

    public CheckKind Kind { get; init; }

    public string? Skill { get; init; }

    public string? Ability { get; init; }

    public int Dc { get; init; }

    public string DcSource { get; init; } = "dm";

    /// <summary>防刷：同一目标同一理由的第二次检定，DC +5。</summary>
    public bool DcAdjusted { get; init; }

    public CheckVisibility Visibility { get; init; } = CheckVisibility.Public;

    /// <summary>加值来源（F7-①）：<c>request</c> / <c>sheet</c> / <c>none</c>。</summary>
    public string ModifierSource { get; init; } = "request";

    public RollEvent? Roll { get; init; }

    /// <summary>对抗检定时防守方的掷骰。</summary>
    public RollEvent? OppositionRoll { get; init; }

    /// <summary>被动检定的定值（10 + 加值）。</summary>
    public int? PassiveValue { get; init; }

    public bool Success { get; init; }

    public CheckStakes? Stakes { get; init; }

    public string? Reason { get; init; }

    /// <summary>DM 是否采纳（<c>DmOverride</c> 可置 false；状态不变）。</summary>
    public bool Applied { get; init; } = true;

    /// <summary>引擎是否算得出来（false = 参数不全/被防刷拒绝，见 <see cref="Code"/>）。</summary>
    public bool Ok { get; init; } = true;

    public string? Code { get; init; }

    public string? Message { get; init; }

    public bool IsSecret => Visibility == CheckVisibility.Secret;

    /// <summary>
    /// 角色可见的那一面：暗骰只给"你无法确定"，公开骰给结论但不给数值。
    /// <b>引擎里就分开</b>，避免下游靠提示词遮蔽。
    /// </summary>
    public string CharacterFacingSummary() => IsSecret
        ? "（暗骰）你无法确定结果。"
        : Success ? "你感觉自己做到了。" : "你感觉自己没能做到。";

    /// <summary>DM / 日志面：一句带全部数值的说明。</summary>
    public string DmFacingSummary()
    {
        if (!Ok)
        {
            return $"检定没有执行：{Message}（{Code}）";
        }

        var roll = Roll is { } value
            ? $"d20 {value.Natural}{(value.Modifier == 0 ? "" : $"{(value.Modifier > 0 ? "+" : "")}{value.Modifier}")} = {value.Total}"
            : $"被动 {PassiveValue}";

        var opposition = OppositionRoll is { } defense ? $"，对方 {defense.Total}" : string.Empty;
        var dc = Kind == CheckKind.Contest ? string.Empty : $"，DC {Dc}{(DcAdjusted ? "（防刷 +5）" : "")}";

        return $"{roll}{opposition}{dc} → {(Success ? "成功" : "失败")}" +
               (IsSecret ? "（暗骰：只给 DM）" : string.Empty);
    }
}

/// <summary>
/// 防刷记录：同一 (actor, target, reason) 的第几次。
/// <para>只记内存——刷检定是"一轮内"的毛病，重启会话后不该继续惩罚。</para>
/// </summary>
public sealed class CheckHistory
{
    /// <summary>第二次起 DC +5。</summary>
    public const int DcStepPerRepeat = 5;

    /// <summary>同一理由最多尝试两次；第三次直接拒绝。</summary>
    public const int MaxAttempts = 2;

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>已经发起过几次。</summary>
    public int Count(string actorId, string? targetId, string reason) =>
        _counts.TryGetValue(Key(actorId, targetId, reason), out var count) ? count : 0;

    public void Record(string actorId, string? targetId, string reason)
    {
        var key = Key(actorId, targetId, reason);
        _counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static string Key(string actorId, string? targetId, string reason) =>
        string.Join(
            '\u0001',
            actorId?.Trim() ?? string.Empty,
            targetId?.Trim() ?? string.Empty,
            (reason ?? string.Empty).Trim());
}

/// <summary>
/// 叙事检定引擎（C2）：四种形态、暗骰、Stakes、防刷。
/// <para>
/// 加值来源：优先用请求里直接给的 <c>modifier</c>；否则 = 属性调整值 +（熟练 ? 熟练加值 : 0）×（专精 ? 2 : 1）。
/// 技能 → 属性<b>从数据包的 <c>skills</c> 段查</b>，不硬编码 18 项。
/// </para>
/// </summary>
public static class NarrativeCheckEngine
{
    public static CheckResult Resolve(
        RulesData rules,
        CheckRequest request,
        CheckHistory history,
        string checkId,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);

        if (string.IsNullOrWhiteSpace(request.ActorId))
        {
            return Reject(request, checkId, "invalid-actor", "检定要给发起者。");
        }

        // 技能 → 属性（技能表是唯一来源）。
        string? skill = null;
        var abilityText = request.Ability;

        if (!string.IsNullOrWhiteSpace(request.Skill))
        {
            var definition = rules.FindSkill(request.Skill);

            if (definition is null)
            {
                return Reject(request, checkId, "unknown-skill", $"规则包里没有「{request.Skill}」这项技能。");
            }

            skill = definition.Id;
            abilityText ??= definition.Ability;
        }

        var ability = AbilityText.Parse(abilityText);

        if (request.Kind is CheckKind.Ability or CheckKind.Skill && ability is null)
        {
            return Reject(request, checkId, "missing-ability", "属性检定 / 技能检定要能确定属性（技能表里没有就显式给 ability）。");
        }

        // 防刷：同一目标同一理由，第二次 +5，第三次拒绝。
        var reason = request.Reason?.Trim() ?? string.Empty;
        var prior = history.Count(request.ActorId, request.TargetId, reason);

        if (prior >= CheckHistory.MaxAttempts)
        {
            return Reject(
                request,
                checkId,
                "check-spam",
                $"「{request.ActorId}」已经为同一件事试过 {prior} 次，对方不吃这套；换办法或让剧情往前走。");
        }

        var dcAdjusted = prior > 0;
        var dc = (request.Dc ?? 0) + (dcAdjusted ? CheckHistory.DcStepPerRepeat : 0);

        // 被动检定没有难度：它给的是一个被动值（10 + 加值），由 DM 拿去和 DC 比。
        if (request.Kind is not (CheckKind.Contest or CheckKind.Passive) && request.Dc is null)
        {
            return Reject(request, checkId, "missing-dc", "这次检定没有 DC——没有难度就没有检定。");
        }

        var modes = ParseModes(request.Advantage);

        if (modes is null)
        {
            return Reject(request, checkId, "invalid-advantage", "advantage 只能是 normal / advantage / disadvantage。");
        }

        var (modifier, modifierSource) = ResolveModifier(rules, request);

        // 被动：不掷骰，10 + 加值。
        if (request.Kind == CheckKind.Passive)
        {
            var passive = 10 + modifier;

            history.Record(request.ActorId, request.TargetId, reason);

            return new CheckResult
            {
                CheckId = checkId,
                ActorId = request.ActorId,
                Kind = request.Kind,
                Skill = skill,
                Ability = ability is { } value ? AbilityText.Wire(value) : null,
                Dc = request.Dc ?? 0,
                DcSource = request.DcSource,
                DcAdjusted = dcAdjusted,
                Visibility = request.Visibility,
                ModifierSource = modifierSource,
                PassiveValue = passive,
                Success = passive >= (request.Dc ?? 0),
                Stakes = request.Stakes,
                Reason = reason,
            };
        }

        // 对抗：双方各掷，平手按防守方胜（发起方失败）。
        if (request.Kind == CheckKind.Contest)
        {
            if (request.OpposedBy is not { } opposed)
            {
                return Reject(request, checkId, "missing-opposition", "对抗检定要给出对方（opposedBy）。");
            }

            var mine = D20.RollCheck(dc, [new RollSource("加值", modifier)], modes, random);
            var defenseModifier = ModifierOf(opposed.Modifier, opposed.AbilityModifier, opposed.ProficiencyBonus,
                opposed.Proficient, opposed.Expertise);
            var opposedModes = ParseModes(request.Advantage) is { } value
                ? value.Select(mode => mode switch
                {
                    AdvantageMode.Advantage => AdvantageMode.Disadvantage,
                    AdvantageMode.Disadvantage => AdvantageMode.Advantage,
                    _ => AdvantageMode.Normal,
                })
                : null;

            var theirs = D20.RollCheck(
                dc,
                [new RollSource("对方加值", defenseModifier)],
                opposedModes,
                random);

            history.Record(request.ActorId, request.TargetId, reason);

            return new CheckResult
            {
                CheckId = checkId,
                ActorId = request.ActorId,
                Kind = request.Kind,
                Skill = skill,
                Ability = ability is { } contested ? AbilityText.Wire(contested) : null,
                Dc = 0,
                DcSource = request.DcSource,
                Visibility = request.Visibility,
                ModifierSource = modifierSource,
                Roll = mine,
                OppositionRoll = theirs,
                Success = mine.Total > theirs.Total,
                Stakes = request.Stakes,
                Reason = reason,
            };
        }

        var roll = D20.RollCheck(dc, [new RollSource("加值", modifier)], modes, random);

        history.Record(request.ActorId, request.TargetId, reason);

        return new CheckResult
        {
            CheckId = checkId,
            ActorId = request.ActorId,
            Kind = request.Kind,
            Skill = skill,
            Ability = ability is { } checkedAbility ? AbilityText.Wire(checkedAbility) : null,
            Dc = dc,
            DcSource = request.DcSource,
            DcAdjusted = dcAdjusted,
            Visibility = request.Visibility,
            ModifierSource = modifierSource,
            Roll = roll,
            Success = roll.Success == true,
            Stakes = request.Stakes,
            Reason = reason,
        };
    }

    /// <summary>
    /// 加值解析（F7-①）：请求直给优先；否则从角色的数值表回落（技能 → `sheet.skills`、
    /// 属性/豁免 → 属性调整值 + 熟练）；都没有就是 <c>none</c>——**并让回执说出来**，
    /// 不让"静默 +0"看起来像一次正常检定。
    /// </summary>
    private static (int Value, string Source) ResolveModifier(RulesData rules, CheckRequest request)
    {
        if (request.Modifier is { } direct)
        {
            return (direct, "request");
        }

        if (request.Sheet is not { } sheet)
        {
            return (0, "none");
        }

        // 被动检定也是技能检定的一种（只是不掷骰），加值同样查技能表。
        if (request.Kind is CheckKind.Skill or CheckKind.Passive && !string.IsNullOrWhiteSpace(request.Skill))
        {
            var skill = rules.FindSkill(request.Skill);
            var key = (skill?.Name ?? request.Skill).ToLowerInvariant();

            if (sheet.Skills is { } skills && skills.TryGetValue(key, out var bonus))
            {
                return (bonus, "sheet");
            }
        }

        if (TryResolveAbility(rules, request, out var ability))
        {
            var value = AbilityModifierOf(sheet, ability);

            if (sheet.SaveProficiencies.Contains(ability))
            {
                value += sheet.ProficiencyBonus ?? 0;
            }

            return (value, "sheet");
        }

        return (0, "none");
    }

    private static bool TryResolveAbility(RulesData rules, CheckRequest request, out Ability ability)
    {
        ability = Ability.Strength;

        var text = request.Ability;

        if (string.IsNullOrWhiteSpace(text) && request.Kind is CheckKind.Skill or CheckKind.Passive && !string.IsNullOrWhiteSpace(request.Skill))
        {
            // 技能检定：技能表里带属性。
            text = rules.FindSkill(request.Skill)?.Ability.ToString();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parsed = AbilityText.Parse(text);

        if (parsed is { } value)
        {
            ability = value;
            return true;
        }

        return false;
    }

    private static int AbilityModifierOf(MeetingRoom.Core.Rules.Sheet sheet, Ability ability) =>
        sheet.Abilities is { } abilities && abilities.TryGetValue(ability, out var score)
            ? (int)Math.Floor((score - 10) / 2.0)
            : 0;

    private static int ModifierOf(
        int? modifier,
        int? abilityModifier,
        int? proficiencyBonus,
        bool proficient,
        bool expertise)
    {
        if (modifier is { } direct)
        {
            return direct;
        }

        var value = abilityModifier ?? 0;

        if (proficient)
        {
            value += (proficiencyBonus ?? 0) * (expertise ? 2 : 1);
        }

        return value;
    }

    private static IEnumerable<AdvantageMode>? ParseModes(string? advantage) =>
        advantage?.Trim().ToLowerInvariant() switch
        {
            null or "" or "normal" => [],
            "advantage" or "adv" => [AdvantageMode.Advantage],
            "disadvantage" or "dis" => [AdvantageMode.Disadvantage],
            _ => null,
        };

    private static CheckResult Reject(CheckRequest request, string checkId, string code, string message) => new()
    {
        CheckId = checkId,
        ActorId = request.ActorId,
        Kind = request.Kind,
        Dc = request.Dc ?? 0,
        DcSource = request.DcSource,
        Visibility = request.Visibility,
        Success = false,
        Reason = request.Reason,
        Ok = false,
        Code = code,
        Message = message,
    };
}
