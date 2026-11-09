using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Rules;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 一条剧情发言（谁说的、说了什么）。
/// </summary>
public sealed record StoryLine(string Author, string Text)
{
    /// <summary>
    /// 知情者（空 = 公开）。不在这份名单里的角色<b>回忆不到</b>这条——
    /// 长程记忆按角色视角检索就是靠它（§11.4-3、§11.4-7）。
    /// </summary>
    public IReadOnlyList<string> Witnesses { get; init; } = [];
}

/// <summary>
/// 一次前台回合（设计文档 §11.4-2）：导演指令 + 随后的角色正文。
/// 它是叙事的"提交边界"——<b>正文一旦产生就成立</b>，后续的状态结算失败也不得撤销它（§11.3-4）。
/// </summary>
public sealed record StoryTurn(
    long Revision,
    string DirectorInput,
    IReadOnlyList<StoryLine> Lines,
    DateTimeOffset At);

/// <summary>
/// 回忆查询（<c>recall_story</c>，§2）：全部可选。都不给 = 最近 10 条发言。
/// </summary>
public sealed record StoryRecallQuery
{
    /// <summary>关键词（在正文里做子串匹配，忽略大小写）。</summary>
    public string? Text { get; init; }

    /// <summary>只看某个角色（作者名）。</summary>
    public string? Author { get; init; }

    /// <summary>只看这些类型：<c>speech|world|roll|request|check|combat|warning</c>。</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>从第几幕之后开始（含本幕）。</summary>
    public long SinceRevision { get; init; }

    /// <summary>最近 N 条（默认 10，上限 40）。</summary>
    public int Last { get; init; } = 10;
}

/// <summary>一条回忆结果（按时间正序）。</summary>
public sealed record StoryRecallLine(long Revision, string Kind, string Author, string Text)
{
    /// <summary>
    /// 见证者（空 = 公开）。只有 <c>speech</c> 有值——它的来源是 <see cref="StoryLine.Witnesses"/>：
    /// 会话层按它决定"这个角色回忆不回忆得到"，不必再回查时间线。
    /// </summary>
    public IReadOnlyList<string> Witnesses { get; init; } = [];

    /// <summary>给模型的纯文本行：<c>第 N 幕 · 谁：……</c>。</summary>
    public string ToPromptText() => Revision > 0
        ? $"第 {Revision} 幕 · {Author}：{Text}"
        : $"{Author}：{Text}";
}

/// <summary>
/// 一次世界状态变更的落盘记录（设计文档 §11.4-2 / §11.4-6）：
/// 与剧情时间线同文件、同序，重启后按序重放即得回同一份世界状态。
/// </summary>
public sealed record StoryWorldRecord(
    long Version,
    WorldStateMutation Mutation,
    string Reason,
    DateTimeOffset At);

/// <summary>
/// 一次问答的落盘记录（设计文档 §14.6）：谁在什么时候问了什么、用户选了什么。
/// 只追加、不参与重放——它是审计用的（"当时他选的到底是什么"），不是状态。
/// </summary>
public sealed record StoryInteractionRecord(
    string Kind,
    IReadOnlyList<string> Prompts,
    IReadOnlyList<InteractionAnswer> Answers,
    bool TimedOut,
    bool Skipped,
    string? SkipReason,
    DateTimeOffset At);

/// <summary>
/// 一次掷骰的落盘记录（《枭熊地图接入实现方案》V2）：<c>story.jsonl</c> 里的 <c>kind=roll</c> 行。
/// 只追加、不参与重放——它是审计用的（"当时到底掷出了什么"），不是世界状态。
/// <para>明细（掷了哪些面、留了哪些）一起落盘，所以<b>同样的明细能重放出同样的 total</b>；
/// 写进正文的数字必须来自这条记录，说书人不能自己改。</para>
/// </summary>
public sealed record StoryRollRecord(
    string Who,
    string Expression,
    string Mode,
    string Reason,
    IReadOnlyList<DiceAttack> Attacks,
    int Total,
    int? Dc,
    bool? Success,
    DateTimeOffset At);

/// <summary>
/// 一次 d20 判定的落盘记录（V3，<c>kind=check</c>）：属性/技能检定与豁免共用，用 <see cref="Kind"/> 区分
/// （<c>check</c> / <c>save</c>）。只追加、不参与重放——与 <c>kind=roll</c> 同一条规则。
/// </summary>
public sealed record StoryCheckRecord(
    string Who,
    string Kind,
    string Purpose,
    int Dc,
    RollEvent? Roll,
    DateTimeOffset At)
{
    /// <summary>检定 id（<c>chk-N</c>）。</summary>
    public string? CheckId { get; init; }

    /// <summary><c>ability</c> / <c>skill</c> / <c>contest</c> / <c>passive</c>。</summary>
    public string? KindType { get; init; }

    public string? Skill { get; init; }

    public string? Ability { get; init; }

    /// <summary><c>dm</c> / <c>narrator</c> / <c>rulesTable</c>。</summary>
    public string DcSource { get; init; } = "dm";

    /// <summary><c>public</c> / <c>secret</c>。暗骰的数值只在这里与 DM 视角出现。</summary>
    public string Visibility { get; init; } = "public";

    /// <summary>后果（成功 / 失败 / 部分成功），给说书人叙述用。</summary>
    public IReadOnlyList<string> Stakes { get; init; } = [];

    /// <summary>DM 是否采纳（<c>false</c> 时状态不变，但留痕）。</summary>
    public bool Applied { get; init; } = true;

    /// <summary>被动检定的定值（10 + 加值）；普通检定为空。</summary>
    public int? PassiveValue { get; init; }

    /// <summary>死亡豁免成功后累计几次（D1，仅 <c>kindType=death-save</c> 有值）。</summary>
    public int? Successes { get; init; }

    /// <summary>死亡豁免失败后累计几次（D1）。</summary>
    public int? Failures { get; init; }

    /// <summary>这次检定之后的生命状态（D1，仅死亡豁免有值）。</summary>
    public VitalStatus? StatusAfter { get; init; }

    /// <summary>加值来源（F7-①）：<c>request</c> / <c>sheet</c> / <c>none</c>。</summary>
    public string? ModifierSource { get; init; }
}

/// <summary>
/// 警告记录（§30，<c>kind=warning</c>）：<b>回执说了什么，日志里就该查得到</b>。
/// 通用一类，不塞进 <c>combat-rejected</c>——那是战斗域，混进去会让"战斗被拒率"统计变脏。
/// <para>
/// <see cref="Source"/> 写发起者（工具名 / <c>sheet-loader</c>），<see cref="Code"/> 是机器可读原因码。
/// </para>
/// </summary>
public sealed record StoryWarningRecord(
    string Code,
    string Message,
    string Source,
    DateTimeOffset At);

/// <summary>
/// DM 覆盖记录（D3，<c>kind=combat-override</c>）：<c>from</c> 由引擎填、调用方只给 <c>to</c>；
/// 事件自带 from/to，所以重放时直接应用、<b>不重新校验</b>。
/// </summary>
public sealed record StoryCombatOverrideRecord(
    string EncounterId,
    string Target,
    string Field,
    string? From,
    string? To,
    string? Reason,
    DateTimeOffset At)
{
    /// <summary>这张数值表是这次覆盖**创建**的（§26.5）：否则"凭空出现一张表"无从解释。</summary>
    public bool CreatedSheet { get; init; }
}

/// <summary>
/// 状态迁移记录（D1，<c>kind=combat-status</c>）：<c>from</c> → <c>to</c>，
/// 说书人靠它叙述"倒下了 / 稳定了 / 死了"，重放也靠它。
/// </summary>
public sealed record StoryCombatStatusRecord(
    string EncounterId,
    string Actor,
    string From,
    string To,
    string Reason,
    int Round,
    DateTimeOffset At);

/// <summary>一次攻击的落盘记录（V3，<c>kind=attack</c>）：命中与否 + 骰面明细（暴击/大失败都在 <see cref="RollEvent"/> 里）。</summary>
public sealed record StoryAttackRecord(
    string Who,
    string WeaponId,
    int ArmorClass,
    RollEvent Roll,
    DateTimeOffset At);

/// <summary>
/// 一次伤害的落盘记录（V3，<c>kind=damage</c>）：按类型分桶后的明细一起落盘，
/// 所以能核对"抗性折半 / 易伤翻倍 / 免疫归零"到底作用在哪一桶上。
/// </summary>
public sealed record StoryDamageRecord(
    string Who,
    string Target,
    IReadOnlyList<DamageBucket> Buckets,
    int RawTotal,
    int Total,
    bool Crit,
    DateTimeOffset At)
{
    /// <summary>扣血前的当前 HP（目标没有数值表时为空）。</summary>
    public int? HpBefore { get; init; }

    /// <summary>扣血后的当前 HP。</summary>
    public int? HpAfter { get; init; }

    /// <summary>扣血后的临时 HP。</summary>
    public int? TempHpAfter { get; init; }

    /// <summary><c>alive</c> / <c>unconscious</c> / <c>dead</c>。</summary>
    public string? Status { get; init; }
}

/// <summary>
/// 开战记录（C1，<c>kind=combat-start</c>）：<b>带 seed 与完整参战名单</b>——
/// 少了任何一项，先攻顺序就重放不出来。
/// </summary>
public sealed record StoryCombatStartRecord(
    string EncounterId,
    int Round,
    int Seed,
    IReadOnlyList<CombatOrderEntry> Order,
    IReadOnlyList<Combatant> Combatants,
    DateTimeOffset At);

/// <summary>回合开始记录（C1，<c>kind=combat-turn</c>）：谁在第几轮、还剩多少行动经济。</summary>
public sealed record StoryCombatTurnRecord(
    string EncounterId,
    int Round,
    string Actor,
    CombatBudget Budget,
    DateTimeOffset At);

/// <summary>收场记录（C1，<c>kind=combat-end</c>）：胜负与打了多少轮。</summary>
public sealed record StoryCombatEndRecord(
    string EncounterId,
    string Outcome,
    int Rounds,
    DateTimeOffset At);

/// <summary>
/// 一次非攻击动作的落盘记录（C3b，<c>kind=combat-action</c>）：移动 / Dash / Dodge / Disengage / Hide / Help。
/// 攻击与施法走 <c>kind=attack</c> + <c>kind=damage</c>，不重复记。
/// </summary>
public sealed record StoryCombatActionRecord(
    string EncounterId,
    string Actor,
    string Action,
    string? Target,
    double? DistanceFeet,
    string? Detail,
    DateTimeOffset At);

/// <summary>拒绝记录（C1，<c>kind=combat-rejected</c>）：拒绝也是正史，说书人要照着它叙述原因。</summary>
public sealed record StoryCombatRejectedRecord(
    string EncounterId,
    string Code,
    string Message,
    DateTimeOffset At);

/// <summary>
/// 剧情时间线（设计文档 §11.4-2、§11.2）：<b>revision 只增不减</b>，落盘为 JSONL（append-only），
/// 崩溃后可按行重建。它就是叙事的权威状态（对应会议模式里那份 <c>materialVersion</c>）。
/// <para>
/// 刻意<b>不做</b>回退 / 分支 / 正文候选版本切换（设计文档 §11.4-4）：本项目的角色扮演里
/// 用户是导演（DM）、剧情由角色 Agent 推进，剧情跑偏由导演下指令纠正，而不是回滚正文版本。
/// </para>
/// </summary>
public sealed class StoryTimeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<StoryTurn> _turns = [];
    private readonly List<StoryWorldRecord> _worldRecords = [];
    private readonly List<StoryRollRecord> _rollRecords = [];
    private readonly List<StoryCheckRecord> _checkRecords = [];
    private readonly List<StoryAttackRecord> _attackRecords = [];
    private readonly List<StoryDamageRecord> _damageRecords = [];
    private readonly List<StoryCombatStartRecord> _combatStartRecords = [];
    private readonly List<StoryCombatTurnRecord> _combatTurnRecords = [];
    private readonly List<StoryCombatEndRecord> _combatEndRecords = [];
    private readonly List<StoryCombatRejectedRecord> _combatRejectedRecords = [];
    private readonly List<StoryCombatActionRecord> _combatActionRecords = [];
    private readonly List<StoryCombatStatusRecord> _combatStatusRecords = [];
    private readonly List<StoryCombatOverrideRecord> _combatOverrideRecords = [];
    private readonly List<StoryWarningRecord> _warningRecords = [];
    private readonly List<StoryRequestRecord> _requestRecords = [];
    private readonly Lock _gate = new();
    private readonly string? _path;
    private long _revision;

    public StoryTimeline(string? path = null)
    {
        _path = path;

        if (path is not null && File.Exists(path))
        {
            Load();
        }
    }

    /// <summary>
    /// 当前 revision：<b>只增不减</b>——用独立计数器，而不是从回合数量推导，
    /// 这样即使将来引入"重写某一幕"也只会在时间线上留下新的版本号，历史不会被抹掉。
    /// </summary>
    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }


    public IReadOnlyList<StoryTurn> Turns
    {
        get
        {
            lock (_gate)
            {
                return _turns.ToArray();
            }
        }
    }

    /// <summary>已落盘的世界状态变更（按序；构造会话时据此重放，§11.4-6）。</summary>
    public IReadOnlyList<StoryWorldRecord> WorldRecords
    {
        get
        {
            lock (_gate)
            {
                return _worldRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的掷骰记录（按序；只用于审计与诊断，不参与状态重放）。</summary>
    public IReadOnlyList<StoryRollRecord> RollRecords
    {
        get
        {
            lock (_gate)
            {
                return _rollRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的 d20 判定记录（check / save）。</summary>
    public IReadOnlyList<StoryCheckRecord> CheckRecords
    {
        get
        {
            lock (_gate)
            {
                return _checkRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的攻击记录。</summary>
    public IReadOnlyList<StoryAttackRecord> AttackRecords
    {
        get
        {
            lock (_gate)
            {
                return _attackRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的伤害记录（含按类型分桶的明细）。</summary>
    public IReadOnlyList<StoryDamageRecord> DamageRecords
    {
        get
        {
            lock (_gate)
            {
                return _damageRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的开战记录（按序；重放先攻与参战名单用）。</summary>
    public IReadOnlyList<StoryCombatStartRecord> CombatStartRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatStartRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的回合开始记录。</summary>
    public IReadOnlyList<StoryCombatTurnRecord> CombatTurnRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatTurnRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的收场记录。</summary>
    public IReadOnlyList<StoryCombatEndRecord> CombatEndRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatEndRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的警告记录（§30）。</summary>
    public IReadOnlyList<StoryWarningRecord> WarningRecords
    {
        get
        {
            lock (_gate)
            {
                return _warningRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的 DM 覆盖记录（D3）。</summary>
    public IReadOnlyList<StoryCombatOverrideRecord> CombatOverrideRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatOverrideRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的状态迁移记录（D1）。</summary>
    public IReadOnlyList<StoryCombatStatusRecord> CombatStatusRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatStatusRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的非攻击动作记录（C3b）。</summary>
    public IReadOnlyList<StoryCombatActionRecord> CombatActionRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatActionRecords.ToArray();
            }
        }
    }

    /// <summary>已落盘的拒绝记录。</summary>
    public IReadOnlyList<StoryCombatRejectedRecord> CombatRejectedRecords
    {
        get
        {
            lock (_gate)
            {
                return _combatRejectedRecords.ToArray();
            }
        }
    }

    /// <summary>追加一条世界状态变更：与回合同文件、同序，重放时不必再对齐两份日志。</summary>
    public StoryWorldRecord AppendWorld(WorldStateMutation mutation, long version, string reason)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_gate)
        {
            var record = new StoryWorldRecord(version, mutation, reason ?? string.Empty, DateTimeOffset.UtcNow);
            _worldRecords.Add(record);
            PersistWorld(record);
            return record;
        }
    }

    /// <summary>
    /// 追加一次掷骰（V2）：<c>kind=roll</c>。<b>数值只能由工具产生</b>——正文引用它，
    /// 说书人不能自己改（《枭熊地图接入实现方案》V2 的约束）。
    /// </summary>
    public StoryRollRecord AppendRoll(string who, DiceRollResult result, string reason)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Ok)
        {
            throw new ArgumentException("失败的掷骰不入库：先回灌给模型，让它改表达式。", nameof(result));
        }

        lock (_gate)
        {
            var record = new StoryRollRecord(
                who ?? string.Empty,
                result.Expression,
                result.Mode.ToString().ToLowerInvariant(),
                reason ?? string.Empty,
                result.Attacks,
                result.Total,
                result.Dc,
                result.Success,
                DateTimeOffset.UtcNow);

            _rollRecords.Add(record);
            PersistRoll(record);
            return record;
        }
    }

    /// <summary>追加一份已经拼好的检定记录（C2：带 visibility / stakes / applied 等扩展字段）。</summary>
    public StoryCheckRecord AppendCheck(StoryCheckRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            var stored = record.At == default ? record with { At = DateTimeOffset.UtcNow } : record;
            _checkRecords.Add(stored);
            PersistNamed("check", stored);
            return stored;
        }
    }

    /// <summary>
    /// 追加一次 d20 判定（V3）：<c>kind=check</c>，<paramref name="kind"/> 区分 check / save。
    /// 与 <c>kind=roll</c> 同规则：只追加、不进重放、不推 revision。
    /// </summary>
    public StoryCheckRecord AppendCheck(string who, string kind, string purpose, int dc, RollEvent roll)
    {
        ArgumentNullException.ThrowIfNull(roll);

        lock (_gate)
        {
            var record = new StoryCheckRecord(
                who ?? string.Empty,
                string.IsNullOrWhiteSpace(kind) ? "check" : kind.Trim().ToLowerInvariant(),
                purpose ?? string.Empty,
                dc,
                roll,
                DateTimeOffset.UtcNow);

            _checkRecords.Add(record);
            PersistNamed("check", record);
            return record;
        }
    }

    /// <summary>追加一次攻击（V3）：<c>kind=attack</c>。</summary>
    public StoryAttackRecord AppendAttack(string who, string weaponId, int armorClass, RollEvent roll)
    {
        ArgumentNullException.ThrowIfNull(roll);

        lock (_gate)
        {
            var record = new StoryAttackRecord(
                who ?? string.Empty,
                weaponId ?? string.Empty,
                armorClass,
                roll,
                DateTimeOffset.UtcNow);

            _attackRecords.Add(record);
            PersistNamed("attack", record);
            return record;
        }
    }

    /// <summary>
    /// 追加一次伤害（V3 / C3a）：<c>kind=damage</c>，分桶明细 + 扣血前后 HP 一起落盘。
    /// <paramref name="application"/> 为空表示目标没有数值表（伤害未扣，DM 自行记）。
    /// </summary>
    public StoryDamageRecord AppendDamage(
        string who,
        string target,
        DamageResult result,
        DamageApplication? application = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            var record = new StoryDamageRecord(
                who ?? string.Empty,
                target ?? string.Empty,
                result.Buckets,
                result.RawTotal,
                result.Total,
                result.Crit,
                DateTimeOffset.UtcNow)
            {
                HpBefore = application?.HpBefore,
                HpAfter = application?.HpAfter,
                TempHpAfter = application?.TempHpAfter,
                Status = application?.Status is { } status ? status.ToString().ToLowerInvariant() : null,
            };

            _damageRecords.Add(record);
            PersistNamed("damage", record);
            return record;
        }
    }

    /// <summary>追加一次开战（C1）。</summary>
    public StoryCombatStartRecord AppendCombatStart(CombatStartEvent start)
    {
        ArgumentNullException.ThrowIfNull(start);

        lock (_gate)
        {
            var record = new StoryCombatStartRecord(
                start.EncounterId,
                start.Round,
                start.Seed,
                start.Order,
                start.Combatants,
                DateTimeOffset.UtcNow);

            _combatStartRecords.Add(record);
            PersistNamed("combat-start", record);
            return record;
        }
    }

    /// <summary>追加一次回合开始（C1）。</summary>
    public StoryCombatTurnRecord AppendCombatTurn(CombatTurnEvent turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        lock (_gate)
        {
            var record = new StoryCombatTurnRecord(
                turn.EncounterId,
                turn.Round,
                turn.Actor,
                turn.Budget,
                DateTimeOffset.UtcNow);

            _combatTurnRecords.Add(record);
            PersistNamed("combat-turn", record);
            return record;
        }
    }

    /// <summary>追加一次收场（C1）。</summary>
    public StoryCombatEndRecord AppendCombatEnd(CombatEndEvent end)
    {
        ArgumentNullException.ThrowIfNull(end);

        lock (_gate)
        {
            var record = new StoryCombatEndRecord(
                end.EncounterId,
                end.Outcome,
                end.Rounds,
                DateTimeOffset.UtcNow);

            _combatEndRecords.Add(record);
            PersistNamed("combat-end", record);
            return record;
        }
    }

    /// <summary>
    /// 追加一条警告（§30）：与 <c>roleplay.warning</c> 事件<b>成对</b>使用——
    /// 事件给前端看，记录给日志查。判据是"回执说了什么，日志里就该查得到"。
    /// </summary>
    public StoryWarningRecord AppendWarning(string code, string message, string source)
    {
        lock (_gate)
        {
            var record = new StoryWarningRecord(
                code ?? string.Empty,
                message ?? string.Empty,
                source ?? string.Empty,
                DateTimeOffset.UtcNow);

            _warningRecords.Add(record);
            PersistNamed("warning", record);
            return record;
        }
    }

    /// <summary>
    /// 追加一条"角色请求"（M9b）：<c>request_judgement</c> / <c>request_combat_action</c> 落这里。
    /// <b>只落请求、不产生任何结算</b>——真正执行仍由说书人决定。
    /// </summary>
    public StoryRequestRecord AppendRequest(StoryRequestRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            _requestRecords.Add(request);
            PersistNamed("request", request);
            return request;
        }
    }

    /// <summary>最近的请求（说书人复盘时从 <c>SpokenContext</c> 看到它们）。</summary>
    public IReadOnlyList<StoryRequestRecord> RequestRecords
    {
        get
        {
            lock (_gate)
            {
                return _requestRecords.ToArray();
            }
        }
    }

    /// <summary>追加一次 DM 覆盖（D3）：<c>from</c> 由引擎填，重放时直接应用、不重新校验。</summary>
    public StoryCombatOverrideRecord AppendCombatOverride(
        string encounterId,
        string target,
        string field,
        string? from,
        string? to,
        string? reason,
        bool createdSheet = false)
    {
        lock (_gate)
        {
            var record = new StoryCombatOverrideRecord(
                encounterId ?? string.Empty,
                target ?? string.Empty,
                field ?? string.Empty,
                from,
                to,
                reason,
                DateTimeOffset.UtcNow)
            {
                CreatedSheet = createdSheet,
            };

            _combatOverrideRecords.Add(record);
            PersistNamed("combat-override", record);
            return record;
        }
    }

    /// <summary>
    /// 把某条检定记录的 DC 改掉（D3 的 <c>field=dc</c>）：只改指定 <paramref name="checkId"/>，
    /// 其它检定不受影响。返回是否找到那条记录。
    /// </summary>
    public bool ApplyCheckDcOverride(string checkId, int dc)
    {
        lock (_gate)
        {
            for (var i = 0; i < _checkRecords.Count; i++)
            {
                if (string.Equals(_checkRecords[i].CheckId, checkId, StringComparison.Ordinal))
                {
                    _checkRecords[i] = _checkRecords[i] with { Dc = dc };
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 追加一次状态迁移（D1）：<c>dying → stable</c> / <c>dying → dead</c> / <c>dying → alive</c> 等。
    /// 迁移必须落盘——说书人要照着它叙述，重放也要靠它。
    /// </summary>
    public StoryCombatStatusRecord AppendCombatStatus(
        string encounterId,
        string actor,
        string from,
        string to,
        string reason,
        int round)
    {
        lock (_gate)
        {
            var record = new StoryCombatStatusRecord(
                encounterId ?? string.Empty,
                actor ?? string.Empty,
                from ?? string.Empty,
                to ?? string.Empty,
                reason ?? string.Empty,
                round,
                DateTimeOffset.UtcNow);

            _combatStatusRecords.Add(record);
            PersistNamed("combat-status", record);
            return record;
        }
    }

    /// <summary>追加一次非攻击动作（C3b）：移动 / Dash / Dodge / Disengage / Hide / Help。</summary>
    public StoryCombatActionRecord AppendCombatAction(
        string encounterId,
        string actor,
        string action,
        string? target = null,
        double? distanceFeet = null,
        string? detail = null)
    {
        lock (_gate)
        {
            var record = new StoryCombatActionRecord(
                encounterId ?? string.Empty,
                actor ?? string.Empty,
                action ?? string.Empty,
                target,
                distanceFeet,
                detail,
                DateTimeOffset.UtcNow);

            _combatActionRecords.Add(record);
            PersistNamed("combat-action", record);
            return record;
        }
    }

    /// <summary>追加一次拒绝（C1）：必须落盘，否则说书人无从叙述"为什么没打成"。</summary>
    public StoryCombatRejectedRecord AppendCombatRejected(CombatRejectedEvent rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);

        lock (_gate)
        {
            var record = new StoryCombatRejectedRecord(
                rejected.EncounterId,
                rejected.Code,
                rejected.Message,
                DateTimeOffset.UtcNow);

            _combatRejectedRecords.Add(record);
            PersistNamed("combat-rejected", record);
            return record;
        }
    }

    /// <summary>追加一次问答（说书人的提议或提问、导演怎么答的）。</summary>
    public StoryInteractionRecord AppendInteraction(
        string kind,
        IReadOnlyList<string> prompts,
        InteractionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(outcome);

        var record = new StoryInteractionRecord(
            kind,
            prompts,
            outcome.Answers,
            outcome.TimedOut,
            outcome.Skipped,
            outcome.SkipReason,
            DateTimeOffset.UtcNow);

        PersistInteraction(record);
        return record;
    }

    /// <summary>
    /// 回忆落盘记录（<c>recall_story</c>，§2）：只读。把剧情发言与各类审计记录归一成
    /// "第 N 幕 · 谁 · 发生了什么"，按时间正序返回最近 <see cref="StoryRecallQuery.Last"/> 条。
    /// <para>
    /// 非发言记录没有自己的 revision，按"落在哪一幕之后"归到最近一个 turn；这样
    /// <c>since</c> 过滤与显示用的是同一把尺子。
    /// </para>
    /// </summary>
    public IReadOnlyList<StoryRecallLine> Recall(StoryRecallQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            var all = new List<StoryRecallLine>();

            void Add(long revision, string kind, string author, string text, DateTimeOffset at, IReadOnlyList<string>? witnesses = null)
            {
                if (text.Length == 0)
                {
                    return;
                }

                if (query.Kinds.Count > 0 && !query.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(query.Author)
                    && !string.Equals(author, query.Author, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(query.Text)
                    && !text.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (revision < query.SinceRevision)
                {
                    return;
                }

                all.Add(new StoryRecallLine(revision, kind, author, text) { Witnesses = witnesses ?? [] });
            }

            foreach (var turn in _turns)
            {
                foreach (var line in turn.Lines)
                {
                    Add(turn.Revision, "speech", line.Author, line.Text, turn.At, line.Witnesses);
                }
            }

            foreach (var record in _worldRecords)
            {
                Add(RevisionAt(record.At), "world", "世界", DescribeMutation(record.Mutation, record.Reason), record.At);
            }

            foreach (var record in _rollRecords)
            {
                Add(RevisionAt(record.At), "roll", record.Who, DescribeRoll(record), record.At);
            }

            foreach (var record in _checkRecords)
            {
                Add(RevisionAt(record.At), "check", record.Who, DescribeCheck(record), record.At);
            }

            foreach (var record in _requestRecords)
            {
                Add(RevisionAt(record.At), "request", record.ActorId, record.Summary, record.At);
            }

            foreach (var record in _warningRecords)
            {
                Add(RevisionAt(record.At), "warning", record.Source, record.Message, record.At);
            }

            foreach (var record in _combatStartRecords)
            {
                Add(RevisionAt(record.At), "combat", "战斗", $"开战 {record.EncounterId}（第 {record.Round} 轮）", record.At);
            }

            foreach (var record in _combatTurnRecords)
            {
                Add(RevisionAt(record.At), "combat", record.Actor, $"第 {record.Round} 轮行动", record.At);
            }

            foreach (var record in _combatEndRecords)
            {
                Add(RevisionAt(record.At), "combat", "战斗", $"收场 {record.Outcome}（{record.Rounds} 轮）", record.At);
            }

            foreach (var record in _combatActionRecords)
            {
                var target = string.IsNullOrWhiteSpace(record.Target) ? string.Empty : $" → {record.Target}";
                Add(RevisionAt(record.At), "combat", record.Actor, $"{record.Action}{target}", record.At);
            }

            foreach (var record in _combatRejectedRecords)
            {
                Add(RevisionAt(record.At), "combat", "战斗", $"被拒：{record.Message}", record.At);
            }

            foreach (var record in _combatStatusRecords)
            {
                Add(RevisionAt(record.At), "combat", record.Actor, $"{record.From} → {record.To}", record.At);
            }

            foreach (var record in _combatOverrideRecords)
            {
                Add(
                    RevisionAt(record.At),
                    "combat",
                    record.Target,
                    $"DM 覆盖 {record.Field}：{record.From ?? "?"} → {record.To ?? "?"}",
                    record.At);
            }

            foreach (var record in _attackRecords)
            {
                Add(
                    RevisionAt(record.At),
                    "combat",
                    record.Who,
                    $"攻击 {record.WeaponId}（AC {record.ArmorClass}）：{(record.Roll.Success == true ? "命中" : "未命中")}",
                    record.At);
            }

            foreach (var record in _damageRecords)
            {
                Add(RevisionAt(record.At), "combat", record.Who, $"对 {record.Target} 造成 {record.Total} 点伤害", record.At);
            }

            // 时间正序；Last 只留最近几条（用户默认 10，上限 40）。
            var ordered = all.OrderBy(line => line.Revision).ToList();
            var last = Math.Clamp(query.Last <= 0 ? 10 : query.Last, 1, 40);

            return ordered.Count <= last ? ordered : ordered.GetRange(ordered.Count - last, last);
        }
    }

    /// <summary>这条记录落在第几幕：最近的 <c>turn.At &lt;= at</c>。</summary>
    private long RevisionAt(DateTimeOffset at)
    {
        var revision = 0L;

        foreach (var turn in _turns)
        {
            if (turn.At > at)
            {
                break;
            }

            revision = turn.Revision;
        }

        return revision;
    }

    private static string DescribeMutation(WorldStateMutation mutation, string reason)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(mutation.Time))
        {
            parts.Add($"时间 {mutation.Time}");
        }

        if (!string.IsNullOrWhiteSpace(mutation.Place))
        {
            parts.Add($"地点 {mutation.Place}");
        }

        if (mutation.Enter.Count > 0)
        {
            parts.Add($"进场 {string.Join('、', mutation.Enter)}");
        }

        if (mutation.Leave.Count > 0)
        {
            parts.Add($"离场 {string.Join('、', mutation.Leave)}");
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add(reason);
        }

        return parts.Count == 0 ? "世界状态变更" : string.Join("；", parts);
    }

    private static string DescribeRoll(StoryRollRecord record)
    {
        var text = $"掷骰 {record.Expression}（{record.Mode}）= {record.Total}";

        if (record.Dc is { } dc)
        {
            text += $" vs DC {dc}：{(record.Success == true ? "成功" : "失败")}";
        }

        return text;
    }

    private static string DescribeCheck(StoryCheckRecord record)
    {
        var text = $"{record.Purpose}（DC {record.Dc}）";

        if (record.Roll is { } roll)
        {
            text += $"：{roll.Total} {(roll.Success == true ? "成功" : "失败")}";
        }

        if (string.Equals(record.Visibility, "secret", StringComparison.OrdinalIgnoreCase))
        {
            text += "（暗骰）";
        }

        return text;
    }

    /// <summary>追加一次前台回合。</summary>
    public StoryTurn Append(string? directorInput, IReadOnlyList<StoryLine> lines)
    {
        lock (_gate)
        {
            var turn = new StoryTurn(++_revision, directorInput ?? string.Empty, lines, DateTimeOffset.UtcNow);
            _turns.Add(turn);
            Persist(turn);
            return turn;
        }
    }

    private void Persist(StoryTurn turn)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(new { kind = "turn", turn }, JsonOptions);
        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void PersistWorld(StoryWorldRecord record)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(new { kind = "world", world = record }, JsonOptions);
        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void PersistInteraction(StoryInteractionRecord record)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(new { kind = "interaction", interaction = record }, JsonOptions);
        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void PersistRoll(StoryRollRecord record)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(new { kind = "roll", roll = record }, JsonOptions);
        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 写一行 <c>{ kind, &lt;kind&gt;: record }</c>（check / attack / damage 三种新记录共用）。
    /// 属性名就是 kind 的值，读回时按同一个键取——三种记录的形状不同，没必要为此写三份 Persist。
    /// </summary>
    private void PersistNamed(string kind, object record)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind, [kind] = record },
            JsonOptions);

        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void Load()
    {
        foreach (var line in File.ReadLines(_path!))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var kind = document.RootElement.GetProperty("kind").GetString();

                if (kind == "world")
                {
                    if (document.RootElement.GetProperty("world").Deserialize<StoryWorldRecord>(JsonOptions) is { } world)
                    {
                        _worldRecords.Add(world);
                    }

                    continue;
                }

                if (kind == "roll")
                {
                    // 掷骰记录只做审计：它不改世界状态，也不推 revision。
                    if (document.RootElement.GetProperty("roll").Deserialize<StoryRollRecord>(JsonOptions) is { } roll)
                    {
                        _rollRecords.Add(roll);
                    }

                    continue;
                }

                if (kind == "request")
                {
                    // 角色请求（M9b）：重放回内存，刷新 / 重启后快照才带得出来。
                    // 与 check / roll 同规则：只审计，不改世界状态、不推 revision。
                    if (document.RootElement.TryGetProperty("request", out var requestElement)
                        && requestElement.Deserialize<StoryRequestRecord>(JsonOptions) is { } request)
                    {
                        _requestRecords.Add(request);
                    }

                    continue;
                }

                if (kind is "check" or "attack" or "damage")
                {
                    // V3 的三类判定记录同样是"只审计"：不改世界状态，也不推 revision。
                    LoadRuleRecord(kind, document.RootElement);
                    continue;
                }

                if (kind is "combat-start" or "combat-turn" or "combat-end" or "combat-rejected"
                    or "combat-action" or "combat-status" or "combat-override" or "warning")
                {
                    // 战斗记录与 roll/check 同规则：只审计与重放，不推 revision。
                    LoadCombatRecord(kind, document.RootElement);
                    continue;
                }

                if (kind != "turn")
                {
                    continue;
                }

                var turn = document.RootElement.GetProperty("turn").Deserialize<StoryTurn>(JsonOptions);
                if (turn is not null && turn.Lines.Count > 0)
                {
                    _turns.Add(turn);
                    _revision = Math.Max(_revision, turn.Revision);
                }
            }
            catch (JsonException)
            {
                // 半截行（崩溃残留）跳过：恢复不需要完美。
            }
        }
    }

    /// <summary>读回一行 check / attack / damage 记录（形状不同，按 kind 分派）。</summary>
    private void LoadRuleRecord(string kind, JsonElement root)
    {
        switch (kind)
        {
            case "check":
                if (root.GetProperty("check").Deserialize<StoryCheckRecord>(JsonOptions) is { } check)
                {
                    _checkRecords.Add(check);
                }

                break;

            case "attack":
                if (root.GetProperty("attack").Deserialize<StoryAttackRecord>(JsonOptions) is { } attack)
                {
                    _attackRecords.Add(attack);
                }

                break;

            case "damage":
                if (root.GetProperty("damage").Deserialize<StoryDamageRecord>(JsonOptions) is { } damage)
                {
                    _damageRecords.Add(damage);
                }

                break;
        }
    }

    /// <summary>读回一行 combat-* 记录（开战 / 回合 / 收场 / 拒绝）。</summary>
    private void LoadCombatRecord(string kind, JsonElement root)
    {
        switch (kind)
        {
            case "combat-start":
                if (root.GetProperty("combat-start").Deserialize<StoryCombatStartRecord>(JsonOptions) is { } start)
                {
                    _combatStartRecords.Add(start);
                }

                break;

            case "combat-turn":
                if (root.GetProperty("combat-turn").Deserialize<StoryCombatTurnRecord>(JsonOptions) is { } turn)
                {
                    _combatTurnRecords.Add(turn);
                }

                break;

            case "combat-end":
                if (root.GetProperty("combat-end").Deserialize<StoryCombatEndRecord>(JsonOptions) is { } end)
                {
                    _combatEndRecords.Add(end);
                }

                break;

            case "warning":
                if (root.GetProperty("warning").Deserialize<StoryWarningRecord>(JsonOptions) is { } warning)
                {
                    _warningRecords.Add(warning);
                }

                break;

            case "combat-override":
                if (root.GetProperty("combat-override").Deserialize<StoryCombatOverrideRecord>(JsonOptions) is { } overridden)
                {
                    _combatOverrideRecords.Add(overridden);
                }

                break;

            case "combat-status":
                if (root.GetProperty("combat-status").Deserialize<StoryCombatStatusRecord>(JsonOptions) is { } migrated)
                {
                    _combatStatusRecords.Add(migrated);
                }

                break;

            case "combat-action":
                if (root.GetProperty("combat-action").Deserialize<StoryCombatActionRecord>(JsonOptions) is { } action)
                {
                    _combatActionRecords.Add(action);
                }

                break;

            case "combat-rejected":
                if (root.GetProperty("combat-rejected").Deserialize<StoryCombatRejectedRecord>(JsonOptions) is { } rejected)
                {
                    _combatRejectedRecords.Add(rejected);
                }

                break;
        }
    }
}

/// <summary>
/// 角色提交的一次请求（M9b）：<c>requestKind</c> 为 <c>check</c> 或 <c>combat_action</c>。
/// 它只是"我想做这个"，**不产生任何结算**——说书人看到后决定执行还是叙事化解。
/// </summary>
public sealed record StoryRequestRecord(
    string ActorId,
    string RequestKind,
    string Summary,
    string Reason,
    IReadOnlyDictionary<string, string>? Arguments,
    DateTimeOffset At);
