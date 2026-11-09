namespace MeetingRoom.Core.Combat;

/// <summary>遭遇阶段（《战斗托管设计》§4）：Setup → Initiative → Active → Ended。</summary>
public enum EncounterPhase
{
    Setup,
    Initiative,
    Active,
    Ended,
}

/// <summary>阵营：队伍 / 敌人 / 中立。</summary>
public enum CombatSide
{
    Party,
    Foe,
    Neutral,
}

/// <summary>谁在替这个战斗者做决定：AI（角色 Agent）或 DM（说书人/用户）。</summary>
public enum CombatantControl
{
    Ai,
    Dm,
}

/// <summary>遭遇结果：胜利 / 失败 / 撤退 / DM 结束。</summary>
public enum CombatOutcome
{
    Victory,
    Defeat,
    Retreat,
    DmEnded,
}

/// <summary>
/// 战斗者（设计文档 §3）。<b>不缓存 HP</b>——数值真相永远在 <c>Sheet</c> 里；
/// 这里只放"排序与身份"要用的东西（先攻、敏捷调整值、阵营、由谁控制）。
/// </summary>
public sealed record Combatant
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string CnName { get; init; } = string.Empty;

    public CombatSide Side { get; init; } = CombatSide.Neutral;

    /// <summary>角色卡 / 数值表的 id（可空：C1 数值由调用方给，不强制挂 Sheet）。</summary>
    public string? SheetId { get; init; }

    /// <summary>怪物模板 id（数据包 <c>monsterTemplates[].id</c>；模板本身没有数值）。</summary>
    public string? TemplateId { get; init; }

    /// <summary>先攻总值（d20 + 敏捷调整值，或 DM 直填）。</summary>
    public int Initiative { get; init; }

    /// <summary>平手判定用的敏捷调整值（只用于排序，不是数值缓存）。</summary>
    public int DexterityModifier { get; init; }

    /// <summary>速度（尺）：回合移动上限的来源（C1 由调用方给，默认 30）。</summary>
    public int SpeedFeet { get; init; } = 30;

    /// <summary>这次平手是靠什么定的：<c>dex</c> / <c>id</c> / 空串（没有平手）。</summary>
    public string InitiativeTiebreak { get; init; } = string.Empty;

    public CombatantControl ControlledBy { get; init; } = CombatantControl.Ai;

    /// <summary>枭熊地图上的 token id（可空）。</summary>
    public string? VttTokenId { get; init; }

    /// <summary>Dodge：持续到它自己的下一回合开始（攻击它时有劣势）。</summary>
    public bool Dodging { get; init; }

    /// <summary>Disengage：本回合不再引发借机攻击（C4 用）。</summary>
    public bool Disengaged { get; init; }

    /// <summary>Hide：DM 可随时撤销（C3b 只置位）。</summary>
    public bool Hidden { get; init; }

    /// <summary>Help 给了谁：被帮助者下一次检定获得优势（一次性，C4 消费）。</summary>
    public string? HelpTargetId { get; init; }

    /// <summary>哪一轮用过 reaction（每轮一次，自己的回合开始时恢复）。</summary>
    public int? ReactionUsedRound { get; init; }

    /// <summary>这一轮已经对谁用过借机攻击（同一轮对同一目标只一次）。</summary>
    public string? OpportunityTarget { get; init; }

    public int? OpportunityRound { get; init; }

    /// <summary>Ready 的触发条件（持续到自己的下一回合开始）。</summary>
    public string? ReadyTrigger { get; init; }

    /// <summary>带回合数的状态：名字 → 剩余回合（自己的回合开始时递减，到 0 移除）。</summary>
    public IReadOnlyDictionary<string, int> ConditionRounds { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>当前回合的行动经济（C1 只重置与记录；校验在 C3）。</summary>
public sealed record TurnState
{
    public required string ActorId { get; init; }

    public bool ActionUsed { get; init; }

    public bool BonusUsed { get; init; }

    public bool ReactionUsed { get; init; }

    public int MovementUsedFeet { get; init; }

    public int MovementMaxFeet { get; init; } = 30;

    /// <summary>这个回合是谁结束的：<c>dm</c> / <c>engine</c>（未结束时为空）。</summary>
    public string? EndedBy { get; init; }
}

/// <summary>
/// 一场遭遇（设计文档 §3）。<b>不可变快照</b>：每次推进都返回新的 <see cref="Encounter"/>，
/// 这样事件与快照能一一对应，也方便重放。
/// </summary>
public sealed record Encounter
{
    public required string Id { get; init; }

    public int Round { get; init; } = 1;

    /// <summary>随机种子（必进 <c>combat-start</c> 事件，否则先攻重放不出来）。</summary>
    public int Seed { get; init; }

    public EncounterPhase Phase { get; init; } = EncounterPhase.Setup;

    public IReadOnlyList<Combatant> Combatants { get; init; } = [];

    /// <summary>按先攻排序的 combatantId（平手：DEX 高者先，再按 id）。</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    public int CurrentIndex { get; init; }

    public TurnState? Turn { get; init; }

    public CombatOutcome? Outcome { get; init; }

    public long StartedAtSeq { get; init; }

    /// <summary>最近一次 <c>BeginTurn</c> 过期的状态（<c>actorId:condition</c>），供会话层落事件。</summary>
    public IReadOnlyList<string> ExpiredConditions { get; init; } = [];

    public long EndedAtSeq { get; init; }

    /// <summary>当前该行动的人（没有回合时为 null）。</summary>
    public Combatant? Current =>
        CurrentIndex >= 0 && CurrentIndex < Order.Count ? Find(Order[CurrentIndex]) : null;

    public Combatant? Find(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Combatants.FirstOrDefault(combatant => string.Equals(combatant.Id, id, StringComparison.Ordinal));
}
