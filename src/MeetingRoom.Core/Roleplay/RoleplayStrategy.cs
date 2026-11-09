using MeetingRoom.Core.Orchestration;

namespace MeetingRoom.Core.Roleplay;

/// <summary>叙事模式下的一个角色（设计文档 §11.4-3）：他知道什么、在不在场。</summary>
public sealed record RoleplayActor(string Id, string Name)
{
    /// <summary>这个角色已知的标签集合——资料按标签披露（"谁知道什么，互相保密"）。</summary>
    public IReadOnlySet<string> KnownTags { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>是否在场：不在场的角色不该发言，也不该"看到"本场发生的事。</summary>
    public bool Present { get; init; } = true;
}

/// <summary>
/// 角色扮演模式的编排策略（设计文档 §11.2 / §11.5）：与会议模式<b>共用内核</b>，差异全部收在这里。
/// 两条与会议相反的默认：
/// <list type="number">
///   <item><b>打断策略反转</b>：不因状态或资料更新去打断角色说话——叙事里那是破坏性的（§11.5-1）；</item>
///   <item><b>提交边界</b>换成"一轮导演指令 + 正文结束"（前台回合），而不是"资料被采纳"。</item>
/// </list>
/// </summary>
public sealed class RoleplayStrategy : IOrchestrationStrategy
{
    /// <summary>
    /// 角色 id → 演员。<b>大小写不敏感</b>：说书人常把 <c>Ella</c> 写进工具参数，而角色卡 id 是 <c>ella</c>；
    /// 用 Ordinal 会让"角色明明在场却查不到"（端到端实测：nudge_character → 没有这个角色：Ella）。
    /// 与 <c>SheetStore</c> 保持同一口径。
    /// </summary>
    private readonly Dictionary<string, RoleplayActor> _actors = new(StringComparer.OrdinalIgnoreCase);

    public RoleplayStrategy(IEnumerable<RoleplayActor> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);

        foreach (var actor in actors)
        {
            _actors[actor.Id] = actor;
        }
    }

    public string Name => "roleplay";

    public TurnDispatch Dispatch => TurnDispatch.Sequential;

    public CommitBoundary CommitBoundary => CommitBoundary.PlayerTurnCompleted;

    /// <summary>§11.5-1：叙事模式下状态更新只软注入、绝不打断。</summary>
    public bool InterruptsOnMaterialChange => false;

    /// <summary>§11.5-2：叙事模式下 NPC 不带工具——工具调用会产生元话语、破坏沉浸。</summary>
    public bool MembersCarryTools => false;

    /// <summary>说书人（控场者）的角色 id：只有它带工具（见 <see cref="Narrator.ToolNames"/>）。</summary>
    public string NarratorId { get; init; } = "director";

    /// <summary>
    /// 按角色的"已知标签"披露资料（§11.4-3）：没有标签的资料人人可见；
    /// 带标签的资料只有已知该标签的角色能看到——一个角色的秘密不能自动泄漏给别人。
    /// </summary>
    public bool VisibleTo(string participantId, IMaterialView material)
    {
        if (material.Tags.Count == 0)
        {
            return true;
        }

        return _actors.TryGetValue(participantId, out var actor)
            && material.Tags.All(tag => actor.KnownTags.Contains(tag));
    }

    public RoleplayActor? Actor(string id) => _actors.GetValueOrDefault(id);

    public IReadOnlyList<RoleplayActor> Actors => _actors.Values.ToArray();

    /// <summary>在场角色（能发言的那些）。</summary>
    public IReadOnlyList<RoleplayActor> PresentActors => _actors.Values.Where(actor => actor.Present).ToArray();

    /// <summary>把一个标签加入某个角色的已知集合（"这件事他知道了"）。</summary>
    public void Reveal(string actorId, string tag)
    {
        if (!_actors.TryGetValue(actorId, out var actor))
        {
            return;
        }

        var known = new HashSet<string>(actor.KnownTags, StringComparer.Ordinal) { tag };
        _actors[actorId] = actor with { KnownTags = known };
    }
}
