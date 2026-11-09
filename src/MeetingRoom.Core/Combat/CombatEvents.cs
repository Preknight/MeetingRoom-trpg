namespace MeetingRoom.Core.Combat;

/// <summary>先攻顺序里的一项（<c>combat-start</c> 事件的 <c>order[]</c>）。</summary>
public sealed record CombatOrderEntry(
    string Actor,
    int Initiative,
    int DexterityModifier,
    string Tiebreak);

/// <summary>回合预算（进 <c>combat-turn</c> 事件，给说书人叙述"他还剩什么"）。</summary>
public sealed record CombatBudget(bool Action, bool Bonus, bool Reaction, int MovementFeet)
{
    public static CombatBudget Of(TurnState turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return new CombatBudget(
            Action: !turn.ActionUsed,
            Bonus: !turn.BonusUsed,
            Reaction: !turn.ReactionUsed,
            MovementFeet: Math.Max(0, turn.MovementMaxFeet - turn.MovementUsedFeet));
    }
}

/// <summary>
/// 战斗事件（《战斗托管设计》§8）。领域事件与落盘记录分开：
/// 事件给 SSE / 工具回执用，落盘记录在 <c>StoryTimeline</c> 里带时间戳。
/// </summary>
public sealed record CombatStartEvent(
    string EncounterId,
    int Round,
    int Seed,
    IReadOnlyList<CombatOrderEntry> Order,
    IReadOnlyList<Combatant> Combatants);

public sealed record CombatTurnEvent(
    string EncounterId,
    int Round,
    string Actor,
    CombatBudget Budget);

public sealed record CombatEndEvent(string EncounterId, string Outcome, int Rounds);

/// <summary>拒绝也是正史：说书人要照着它叙述"你够不到他"，不能让 AI 静默重试。</summary>
public sealed record CombatRejectedEvent(string EncounterId, string Code, string Message);
