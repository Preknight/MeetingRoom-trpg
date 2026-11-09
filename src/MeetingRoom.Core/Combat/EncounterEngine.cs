namespace MeetingRoom.Core.Combat;

/// <summary>
/// 参战者描述（C1 数值由调用方给：先攻掷骰用 <see cref="DexterityModifier"/>，移动上限用 <see cref="SpeedFeet"/>）。
/// 之后接 <c>Sheet</c> 时，这些字段从数值表推导，而不是在 <see cref="Combatant"/> 里缓存 HP。
/// </summary>
public sealed record CombatantSetup
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? CnName { get; init; }

    public CombatSide Side { get; init; } = CombatSide.Neutral;

    public string? SheetId { get; init; }

    public string? TemplateId { get; init; }

    /// <summary>
    /// 这场战斗用的数值表（§26.2 第 2 层）：怪物 / 临时 NPC 在这里直接给，
    /// 会覆盖 <c>roles/&lt;id&gt;.sheet.json</c>。纯状态机不读它，由会话层写入 <c>SheetStore</c>。
    /// </summary>
    public MeetingRoom.Core.Rules.Sheet? Sheet { get; init; }

    /// <summary>敏捷调整值（先攻 = d20 + 它）。</summary>
    public int DexterityModifier { get; init; }

    /// <summary>
    /// 先攻直填（可选）：给了就<b>不掷骰</b>，用这个值。
    /// 平手仍按 DEX → 实体 id；这也是"DM 直接指定顺序"的唯一入口（不再另设脚本级覆盖表）。
    /// </summary>
    public int? Initiative { get; init; }

    /// <summary>速度（尺）。</summary>
    public int SpeedFeet { get; init; } = 30;

    public CombatantControl ControlledBy { get; init; } = CombatantControl.Ai;

    /// <summary>
    /// 内联 HP（r10）：没有角色卡的临时敌人靠这三个字段开战——会话层会用它们
    /// <c>CreateMinimal</c> 出一张最小表。<b>纯状态机不读</b>（它不存 HP），
    /// 所以这三个字段只在会话层生效。
    /// </summary>
    public int? Hp { get; init; }

    public int? MaxHp { get; init; }

    public int? Ac { get; init; }

    public string? VttTokenId { get; init; }
}

/// <summary>状态机动作的结果：成功给新快照，失败给可叙述的 code + message（不抛异常）。</summary>
public sealed record EncounterResult(bool Ok, Encounter? Encounter, string? Code, string? Message)
{
    public static EncounterResult Success(Encounter encounter) => new(true, encounter, null, null);

    public static EncounterResult Fail(string code, string message) => new(false, null, code, message);
}

/// <summary>一次借机攻击（C4）：谁对谁触发。攻击本身由调用方结算（消耗 reaction 已在这里记）。</summary>
public sealed record OpportunityAttack(string AttackerId, string TargetId);

/// <summary>行动经济校验的结果（C3b/C4）：成功给新快照，失败给可叙述的 code + message。</summary>
public sealed record ActionEconomyResult(
    bool Ok,
    Encounter? Encounter,
    string? Code,
    string? Message,
    IReadOnlyList<OpportunityAttack> Opportunities)
{
    public static ActionEconomyResult Success(
        Encounter encounter,
        IReadOnlyList<OpportunityAttack>? opportunities = null) =>
        new(true, encounter, null, null, opportunities ?? []);

    public static ActionEconomyResult Fail(string code, string message) => new(false, null, code, message, []);
}

/// <summary>
/// 遭遇状态机（《战斗托管设计》§4）。<b>纯函数</b>：输入旧快照，返回新快照；
/// 随机源由 <c>seed</c> 决定，所以同一 seed + 同一 setups 必然得到同一顺序（可重放）。
/// </summary>
public static class EncounterEngine
{
    public const int DefaultSpeedFeet = 30;

    public const string CodeInvalidSetup = "invalid-setup";
    public const string CodeDuplicateId = "duplicate-id";
    public const string CodeAlreadyActive = "already-active";
    public const string CodeNotActive = "not-active";
    public const string CodeAlreadyEnded = "already-ended";

    /// <summary>
    /// 开战：掷先攻（或使用 <paramref name="initiativeOverrides"/> 的直填值）→ 排序 → 进入 Active 并开始第一个回合。
    /// 平手规则：先攻相同比 DEX 调整值；再相同按实体 id（<c>Ordinal</c>，保证跨机器一致）。
    /// </summary>
    public static EncounterResult Start(
        string encounterId,
        IReadOnlyList<CombatantSetup> setups,
        int seed)
    {
        if (string.IsNullOrWhiteSpace(encounterId))
        {
            return EncounterResult.Fail(CodeInvalidSetup, "encounterId 不能为空。");
        }

        ArgumentNullException.ThrowIfNull(setups);

        if (setups.Count == 0)
        {
            return EncounterResult.Fail(CodeInvalidSetup, "至少要有一个参战者。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var setup in setups)
        {
            if (string.IsNullOrWhiteSpace(setup.Id))
            {
                return EncounterResult.Fail(CodeInvalidSetup, "参战者的 id 不能为空。");
            }

            if (!seen.Add(setup.Id))
            {
                return EncounterResult.Fail(CodeDuplicateId, $"参战者 id 重复：{setup.Id}（同一实体不能重复参战）。");
            }

            if (setup.DexterityModifier is < -10 or > 20)
            {
                return EncounterResult.Fail(CodeInvalidSetup, $"{setup.Id} 的敏捷调整值超出合理范围：{setup.DexterityModifier}。");
            }

            if (setup.SpeedFeet < 0)
            {
                return EncounterResult.Fail(CodeInvalidSetup, $"{setup.Id} 的速度不能为负：{setup.SpeedFeet}。");
            }
        }

        var rng = new Random(seed);
        var rolled = new List<Combatant>(setups.Count);

        foreach (var setup in setups)
        {
            // 直填优先（DM 指定 / 测试平手）；否则 d20 + DEX。
            var initiative = setup.Initiative ?? rng.Next(1, 21) + setup.DexterityModifier;

            rolled.Add(new Combatant
            {
                Id = setup.Id,
                DisplayName = setup.Name ?? setup.Id,
                CnName = setup.CnName ?? string.Empty,
                Side = setup.Side,
                SheetId = setup.SheetId,
                TemplateId = setup.TemplateId,
                Initiative = initiative,
                DexterityModifier = setup.DexterityModifier,
                SpeedFeet = setup.SpeedFeet,
                ControlledBy = setup.ControlledBy,
                VttTokenId = setup.VttTokenId,
            });
        }

        var sorted = rolled
            .OrderByDescending(combatant => combatant.Initiative)
            .ThenByDescending(combatant => combatant.DexterityModifier)
            .ThenBy(combatant => combatant.Id, StringComparer.Ordinal)
            .ToList();

        // 平手标记按"排序后的前一位"来标：先攻相同才叫平手，DEX 不同归因于 dex，否则归因于 id。
        var ordered = new List<Combatant>(sorted.Count);

        for (var index = 0; index < sorted.Count; index++)
        {
            var previous = index == 0 ? null : sorted[index - 1];
            ordered.Add(sorted[index] with
            {
                InitiativeTiebreak = previous is null ? string.Empty : Tiebreak(previous, sorted[index]),
            });
        }

        var encounter = new Encounter
        {
            Id = encounterId,
            Round = 1,
            Seed = seed,
            Phase = EncounterPhase.Active,
            Combatants = ordered,
            Order = ordered.Select(combatant => combatant.Id).ToArray(),
            CurrentIndex = 0,
        };

        return EncounterResult.Success(BeginTurn(encounter, ordered[0]));
    }

    /// <summary>
    /// 结束当前回合并让下一个可行动者开始：CurrentIndex 走到末尾时回到 0、Round + 1
    /// （与设计文档一致：一轮在索引回到 0 时递增）。
    /// </summary>
    public static EncounterResult Advance(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        if (encounter.Phase == EncounterPhase.Ended)
        {
            return EncounterResult.Fail(CodeAlreadyEnded, "这场战斗已经结束。");
        }

        if (encounter.Phase != EncounterPhase.Active || encounter.Turn is null || encounter.Order.Count == 0)
        {
            return EncounterResult.Fail(CodeNotActive, "还没有开始战斗，先 start_combat。");
        }

        var next = encounter.CurrentIndex + 1;
        var round = encounter.Round;

        if (next >= encounter.Order.Count)
        {
            next = 0;
            round++;
        }

        var actor = encounter.Find(encounter.Order[next]);

        if (actor is null)
        {
            return EncounterResult.Fail(CodeInvalidSetup, $"先攻顺序里有人不在参战名单：{encounter.Order[next]}。");
        }

        return EncounterResult.Success(BeginTurn(encounter with { Round = round, CurrentIndex = next }, actor));
    }

    /// <summary>结束整场战斗（DM 宣布胜负 / 撤退）。</summary>
    public static EncounterResult End(Encounter encounter, CombatOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        if (encounter.Phase == EncounterPhase.Ended)
        {
            return EncounterResult.Fail(CodeAlreadyEnded, "这场战斗已经结束。");
        }

        return EncounterResult.Success(encounter with
        {
            Phase = EncounterPhase.Ended,
            Outcome = outcome,
            Turn = encounter.Turn is { } turn ? turn with { EndedBy = "dm" } : null,
        });
    }

    public const string CodeNotYourTurn = "not-your-turn";
    public const string CodeActionUsed = "action-already-used";
    public const string CodeBonusUsed = "bonus-already-used";
    public const string CodeNotEnoughMovement = "not-enough-movement";
    public const string CodeMissingDistance = "missing-distance";
    public const string CodeMissingTarget = "missing-target";

    /// <summary>
    /// 消耗行动经济并更新战斗者状态（C3b）：Move / Dash / Dodge / Disengage / Hide / Help / Attack。
    /// <b>只做校验与标记</b>；命中与伤害仍由 <see cref="CombatEngine"/> 结算。
    /// </summary>
    public static ActionEconomyResult SubmitAction(
        Encounter encounter,
        string actorId,
        CombatActionKind kind,
        bool useBonus = false,
        double? distanceFeet = null,
        string? helpTargetId = null,
        IReadOnlyList<string>? provokesOpportunityFrom = null,
        string? readyTrigger = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        if (encounter.Phase != EncounterPhase.Active || encounter.Turn is not { } turn)
        {
            return ActionEconomyResult.Fail(CodeNotActive, "现在不在战斗中，先用 start_combat 开战。");
        }

        if (!string.Equals(turn.ActorId, actorId, StringComparison.Ordinal))
        {
            return ActionEconomyResult.Fail(CodeNotYourTurn, $"现在轮到 {turn.ActorId}，不是 {actorId}。");
        }

        var actor = encounter.Find(actorId);

        if (actor is null)
        {
            return ActionEconomyResult.Fail("unknown-combatant", $"战斗名单里没有「{actorId}」。");
        }

        var combatants = encounter.Combatants;
        var opportunities = new List<OpportunityAttack>();

        switch (kind)
        {
            case CombatActionKind.Move:
                {
                    if (distanceFeet is not { } distance || distance < 0)
                    {
                        return ActionEconomyResult.Fail(CodeMissingDistance, "移动要给出距离（尺）。");
                    }

                    var remaining = turn.MovementMaxFeet - turn.MovementUsedFeet;

                    if (distance > remaining + 1e-6)
                    {
                        return ActionEconomyResult.Fail(
                            CodeNotEnoughMovement,
                            $"剩余移动 {remaining} 尺，走不了 {distance:0.#} 尺。");
                    }

                    turn = turn with { MovementUsedFeet = turn.MovementUsedFeet + (int)Math.Ceiling(distance) };

                    // 借机攻击（C4）：离开谁的触及，谁就能用 reaction 打一次；
                    // 移动者 Disengage 过就不触发；同一轮对同一目标只触发一次。
                    if (provokesOpportunityFrom is { Count: > 0 } && !actor.Disengaged)
                    {
                        foreach (var attackerId in provokesOpportunityFrom)
                        {
                            if (string.Equals(attackerId, actorId, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var attacker = encounter.Find(attackerId);

                            if (attacker is null
                                || attacker.ReactionUsedRound == encounter.Round
                                || (attacker.OpportunityRound == encounter.Round
                                    && string.Equals(attacker.OpportunityTarget, actorId, StringComparison.Ordinal)))
                            {
                                continue;
                            }

                            combatants = WithFlag(combatants, attackerId, combatant => combatant with
                            {
                                ReactionUsedRound = encounter.Round,
                                OpportunityTarget = actorId,
                                OpportunityRound = encounter.Round,
                            });

                            opportunities.Add(new OpportunityAttack(attackerId, actorId));
                        }
                    }

                    break;
                }

            case CombatActionKind.Dash:
                {
                    if (turn.ActionUsed)
                    {
                        return ActionEconomyResult.Fail(CodeActionUsed, "本回合的 Action 已经用过了。");
                    }

                    var speed = actor.SpeedFeet > 0 ? actor.SpeedFeet : DefaultSpeedFeet;
                    turn = turn with { ActionUsed = true, MovementMaxFeet = turn.MovementMaxFeet + speed };
                    break;
                }

            case CombatActionKind.Dodge:
                {
                    if (turn.ActionUsed)
                    {
                        return ActionEconomyResult.Fail(CodeActionUsed, "本回合的 Action 已经用过了。");
                    }

                    turn = turn with { ActionUsed = true };
                    combatants = WithFlag(combatants, actorId, combatant => combatant with { Dodging = true });
                    break;
                }

            case CombatActionKind.Disengage:
                {
                    // 2024 规则：Disengage 消耗 Bonus Action。
                    if (turn.BonusUsed)
                    {
                        return ActionEconomyResult.Fail(CodeBonusUsed, "本回合的 Bonus Action 已经用过了。");
                    }

                    turn = turn with { BonusUsed = true };
                    combatants = WithFlag(combatants, actorId, combatant => combatant with { Disengaged = true });
                    break;
                }

            case CombatActionKind.Hide:
                {
                    if (turn.ActionUsed)
                    {
                        return ActionEconomyResult.Fail(CodeActionUsed, "本回合的 Action 已经用过了。");
                    }

                    turn = turn with { ActionUsed = true };
                    combatants = WithFlag(combatants, actorId, combatant => combatant with { Hidden = true });
                    break;
                }

            case CombatActionKind.Help:
                {
                    if (turn.ActionUsed)
                    {
                        return ActionEconomyResult.Fail(CodeActionUsed, "本回合的 Action 已经用过了。");
                    }

                    if (string.IsNullOrWhiteSpace(helpTargetId))
                    {
                        return ActionEconomyResult.Fail(CodeMissingTarget, "Help 要给被帮助的人（helpTargetId）。");
                    }

                    if (string.Equals(helpTargetId, actorId, StringComparison.Ordinal))
                    {
                        return ActionEconomyResult.Fail(CodeMissingTarget, "Help 不能帮自己。");
                    }

                    if (encounter.Find(helpTargetId) is null)
                    {
                        return ActionEconomyResult.Fail("unknown-combatant", $"战斗名单里没有「{helpTargetId}」。");
                    }

                    turn = turn with { ActionUsed = true };
                    combatants = WithFlag(combatants, helpTargetId, combatant => combatant with { HelpTargetId = actorId });
                    break;
                }

            case CombatActionKind.Ready:
                {
                    if (turn.ActionUsed)
                    {
                        return ActionEconomyResult.Fail(CodeActionUsed, "本回合的 Action 已经用过了。");
                    }

                    if (string.IsNullOrWhiteSpace(readyTrigger))
                    {
                        return ActionEconomyResult.Fail("missing-trigger", "Ready 要写清触发条件（readyTrigger）。");
                    }

                    turn = turn with { ActionUsed = true };
                    combatants = WithFlag(combatants, actorId, combatant => combatant with { ReadyTrigger = readyTrigger.Trim() });
                    break;
                }

            default:
                {
                    // Attack / CastSpell：默认吃 Action；UseBonus 时吃 Bonus（双持等）。
                    if (useBonus)
                    {
                        if (turn.BonusUsed)
                        {
                            return ActionEconomyResult.Fail(CodeBonusUsed, "本回合的 Bonus Action 已经用过了。");
                        }

                        turn = turn with { BonusUsed = true };
                    }
                    else
                    {
                        if (turn.ActionUsed)
                        {
                            return ActionEconomyResult.Fail(CodeActionUsed, "本回合的 Action 已经用过了（一人一回合只能一次）。");
                        }

                        turn = turn with { ActionUsed = true };
                    }

                    break;
                }
        }

        return ActionEconomyResult.Success(encounter with { Turn = turn, Combatants = combatants }, opportunities);
    }

    /// <summary>先攻顺序（事件与回执用）。</summary>
    public static IReadOnlyList<CombatOrderEntry> OrderOf(Encounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        return encounter.Order
            .Select(id => encounter.Find(id))
            .Where(combatant => combatant is not null)
            .Select(combatant => new CombatOrderEntry(
                combatant!.Id,
                combatant.Initiative,
                combatant.DexterityModifier,
                combatant.InitiativeTiebreak))
            .ToArray();
    }

    /// <summary>
    /// 开始某人的回合：重置行动经济（action/bonus/reaction/移动），Round 不动。
    /// 同时清掉他自己身上的 <c>Dodging</c>——Dodge 只持续到自己的下一回合开始（5e 规则）。
    /// </summary>
    private static Encounter BeginTurn(Encounter encounter, Combatant actor)
    {
        var expired = new List<string>();
        var combatants = new List<Combatant>(encounter.Combatants.Count);

        foreach (var combatant in encounter.Combatants)
        {
            if (!string.Equals(combatant.Id, actor.Id, StringComparison.Ordinal))
            {
                combatants.Add(combatant);
                continue;
            }

            // 自己的新回合：Dodge 结束、Ready 过期、reaction 恢复；带回合数的状态递减。
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (name, rounds) in combatant.ConditionRounds)
            {
                if (rounds > 1)
                {
                    remaining[name] = rounds - 1;
                }
                else
                {
                    expired.Add($"{combatant.Id}:{name}");
                }
            }

            combatants.Add(combatant with
            {
                Dodging = false,
                ReadyTrigger = null,
                ReactionUsedRound = null,
                ConditionRounds = remaining,
            });
        }

        return encounter with
        {
            Phase = EncounterPhase.Active,
            Combatants = combatants,
            ExpiredConditions = expired,
            Turn = new TurnState
            {
                ActorId = actor.Id,
                MovementMaxFeet = actor.SpeedFeet > 0 ? actor.SpeedFeet : DefaultSpeedFeet,
            },
        };
    }

    /// <summary>
    /// 消费 Help 给的优势（C4）：被帮助者下一次攻击 / 检定用掉后清除。
    /// 返回是否真的消费了（调用方据此决定这次是否带优势）。
    /// </summary>
    public static (Encounter Encounter, bool Consumed) ConsumeHelp(Encounter encounter, string actorId)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        var actor = encounter.Find(actorId);

        if (actor?.HelpTargetId is null)
        {
            return (encounter, false);
        }

        return (encounter with
        {
            Combatants = WithFlag(encounter.Combatants, actorId, combatant => combatant with { HelpTargetId = null }),
        }, true);
    }

    /// <summary>
    /// 给某个战斗者挂一个带回合数的状态（C4）：在自己的回合开始时递减，到 0 移除。
    /// 状态名用规则包里的正式名（如 <c>Prone</c>），不在这里翻译。
    /// </summary>
    public static Encounter ApplyCondition(Encounter encounter, string actorId, string condition, int rounds)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);

        if (encounter.Find(actorId) is null)
        {
            throw new ArgumentException($"战斗名单里没有「{actorId}」。", nameof(actorId));
        }

        return encounter with
        {
            Combatants = WithFlag(encounter.Combatants, actorId, combatant =>
            {
                var conditions = new Dictionary<string, int>(combatant.ConditionRounds, StringComparer.Ordinal)
                {
                    [condition.Trim()] = Math.Max(1, rounds),
                };

                return combatant with { ConditionRounds = conditions };
            }),
        };
    }

    /// <summary>移除一个状态（D3）：不改其它状态、不动回合。</summary>
    public static Encounter RemoveCondition(Encounter encounter, string actorId, string condition)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);

        if (encounter.Find(actorId) is null)
        {
            throw new ArgumentException($"战斗名单里没有「{actorId}」。", nameof(actorId));
        }

        return encounter with
        {
            Combatants = WithFlag(encounter.Combatants, actorId, combatant =>
            {
                if (!combatant.ConditionRounds.ContainsKey(condition))
                {
                    return combatant;
                }

                var conditions = new Dictionary<string, int>(combatant.ConditionRounds, StringComparer.Ordinal);
                conditions.Remove(condition);
                return combatant with { ConditionRounds = conditions };
            }),
        };
    }

    /// <summary>
    /// 改先攻并重排（D3）：<b>不打断当前回合</b>——`Turn` 与 `CurrentIndex` 仍指向同一个人。
    /// </summary>
    public static Encounter SetInitiative(Encounter encounter, string actorId, int initiative)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        if (encounter.Find(actorId) is null)
        {
            throw new ArgumentException($"战斗名单里没有「{actorId}」。", nameof(actorId));
        }

        var combatants = WithFlag(encounter.Combatants, actorId, combatant => combatant with { Initiative = initiative });
        var currentId = encounter.Turn?.ActorId;
        var ordered = combatants
            .OrderByDescending(combatant => combatant.Initiative)
            .ThenByDescending(combatant => combatant.DexterityModifier)
            .ThenBy(combatant => combatant.Id, StringComparer.Ordinal)
            .ToList();

        var index = currentId is null
            ? Math.Min(encounter.CurrentIndex, Math.Max(0, ordered.Count - 1))
            : Math.Max(0, ordered.FindIndex(combatant => string.Equals(combatant.Id, currentId, StringComparison.Ordinal)));

        return encounter with
        {
            Combatants = combatants,
            Order = ordered.Select(combatant => combatant.Id).ToList(),
            CurrentIndex = index,
        };
    }

    /// <summary>
    /// 改当前回合的行动经济（D3）：<paramref name="movementUsedFeet"/> 是"已用"多少尺
    /// （与 <see cref="TurnState.MovementUsedFeet"/> 同义），不是剩余。只传了哪几项就改哪几项。
    /// </summary>
    public static Encounter SetBudget(
        Encounter encounter,
        bool? action = null,
        bool? bonus = null,
        bool? reaction = null,
        int? movementUsedFeet = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        if (encounter.Turn is not { } turn)
        {
            return encounter;
        }

        return encounter with
        {
            Turn = turn with
            {
                ActionUsed = action ?? turn.ActionUsed,
                BonusUsed = bonus ?? turn.BonusUsed,
                ReactionUsed = reaction ?? turn.ReactionUsed,
                MovementUsedFeet = movementUsedFeet ?? turn.MovementUsedFeet,
            },
        };
    }

    private static IReadOnlyList<Combatant> WithFlag(
        IReadOnlyList<Combatant> combatants,
        string id,
        Func<Combatant, Combatant> update) =>
        combatants.Select(combatant => string.Equals(combatant.Id, id, StringComparison.Ordinal) ? update(combatant) : combatant).ToArray();

    /// <summary>平手标记：与前一位相比，先攻相同才算平手；DEX 不同记 <c>dex</c>，DEX 也相同记 <c>id</c>。</summary>
    private static string Tiebreak(Combatant previous, Combatant current)
    {
        if (previous.Initiative != current.Initiative)
        {
            return string.Empty;
        }

        return previous.DexterityModifier != current.DexterityModifier ? "dex" : "id";
    }
}
