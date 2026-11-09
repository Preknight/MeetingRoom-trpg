namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 人物状态（设计文档 §11.4-6）：位置、属性（好感 / 伤病 …）、持有物、已知信息。
/// <para><b>已知信息只增不减</b>：人无法"忘记被告知的事"；要回退得靠剧情时间线（§11.4-2）。</para>
/// </summary>
public sealed record CharacterState(string Id, string Name)
{
    public string Location { get; init; } = string.Empty;

    public bool Present { get; init; } = true;

    public IReadOnlyDictionary<string, int> Attributes { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyList<string> Items { get; init; } = [];

    public IReadOnlyList<string> Known { get; init; } = [];
}

/// <summary>
/// 世界状态（设计文档 §11.4-6）：剧情内时间、当前地点、在场人物、人物状态。
/// 由说书人按导演（用户）的意图提交、或提议后经其确认，再由程序校验——它决定"下一轮角色看到的现场"。
/// </summary>
public sealed record WorldState(long Version)
{
    public string Time { get; init; } = string.Empty;

    public string Place { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, CharacterState> Characters { get; init; } =
        new Dictionary<string, CharacterState>(StringComparer.Ordinal);

    /// <summary>在场人物（按登记顺序）。</summary>
    public IReadOnlyList<string> Present =>
        Characters.Values.Where(character => character.Present).Select(character => character.Id).ToArray();
}

/// <summary>属性增减。</summary>
public sealed record AttributeDelta(string Character, string Key, int Delta);

/// <summary>持有物获得 / 失去。</summary>
public sealed record ItemDelta(string Character, string Item, bool Remove = false);

/// <summary>获得一条已知信息。</summary>
public sealed record KnowledgeDelta(string Character, string Fact);

/// <summary>一次结构化世界状态变更（来自说书人的控场工具调用；前提是导演的意图或确认）。</summary>
public sealed record WorldStateMutation
{
    public string? Time { get; init; }

    public string? Place { get; init; }

    public IReadOnlyList<string> Enter { get; init; } = [];

    public IReadOnlyList<string> Leave { get; init; } = [];

    public IReadOnlyList<AttributeDelta> Attributes { get; init; } = [];

    public IReadOnlyList<ItemDelta> Items { get; init; } = [];

    public IReadOnlyList<KnowledgeDelta> Knowledge { get; init; } = [];
}

/// <summary>
/// 一次结算的结果。被拒时 <paramref name="State"/> 是<b>未改动</b>的旧状态，
/// 而<b>已经写出来的正文不受影响</b>（§11.3-4）。
/// </summary>
public sealed record WorldStateResult(bool Applied, WorldState State, IReadOnlyList<string> Errors);
