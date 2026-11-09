namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 世界状态里的角色引用归一（名字 → id）。
/// <para>
/// 为什么需要它：模型填 <c>propose_world_state</c> 时给的是**名字**（"Ella"），而世界状态内部一律按 **id** 存，
/// 于是真机上出现「世界状态没有改变：未知角色：Ella」（《枭熊地图接入实现方案》§7.4 记的那条）。
/// </para>
/// <para>
/// <b>只在这里做一次</b>：工具层（<c>NarratorToolDispatcher</c> / <c>RoleplaySession</c>）不再重复解析——
/// 两处各写一套判定就会像地图那边一样出现"预检查说没有、真正解析却有"，那是上一轮已经付过学费的坑。
/// </para>
/// <para>
/// 规则：id 按 <c>Ordinal</c> 命中优先（系统标识、大小写敏感）→ 否则按名字忽略大小写找<b>唯一</b>候选 →
/// 重名拒绝（绝不替调用方挑一个）→ 未知名字原样传下去，由 <see cref="WorldStateStore"/> 照旧报「未知角色：…」。
/// 归一不改写存储里的 id，也不回写调用方拿到的 mutation（一直构造新对象）。
/// </para>
/// </summary>
internal static class CharacterReferences
{
    /// <summary>归一结果：只用于本次结算的副本 + 无法判定的错误（重名）。</summary>
    public sealed record Resolution(WorldStateMutation Mutation, IReadOnlyList<string> Errors);

    /// <summary>把 mutation 里所有"按角色引用"的字段换成 id；覆盖 Enter / Leave / Attributes / Items / Knowledge。</summary>
    public static Resolution Resolve(
        WorldStateMutation mutation,
        IReadOnlyDictionary<string, CharacterState> characters)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(characters);

        var errors = new List<string>();
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var reportedAmbiguity = new HashSet<string>(StringComparer.Ordinal);

        string Map(string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return reference;
            }

            if (cache.TryGetValue(reference, out var cached))
            {
                return cached;
            }

            var id = ResolveOne(reference, characters, errors, reportedAmbiguity);
            cache[reference] = id;
            return id;
        }

        var normalized = new WorldStateMutation
        {
            Time = mutation.Time,
            Place = mutation.Place,
            Enter = mutation.Enter.Select(Map).ToArray(),
            Leave = mutation.Leave.Select(Map).ToArray(),
            Attributes = mutation.Attributes.Select(delta => delta with { Character = Map(delta.Character) }).ToArray(),
            Items = mutation.Items.Select(delta => delta with { Character = Map(delta.Character) }).ToArray(),
            Knowledge = mutation.Knowledge.Select(delta => delta with { Character = Map(delta.Character) }).ToArray(),
        };

        return new Resolution(normalized, errors);
    }

    private static string ResolveOne(
        string reference,
        IReadOnlyDictionary<string, CharacterState> characters,
        List<string> errors,
        HashSet<string> reportedAmbiguity)
    {
        // 1) id 优先：id 是系统标识，大小写不同就是不同的人。
        if (characters.ContainsKey(reference))
        {
            return reference;
        }

        // 2) 名字：忽略大小写，只认唯一候选（重名不猜）。
        var matches = characters.Values
            .Where(character => !string.IsNullOrWhiteSpace(character.Name)
                && string.Equals(character.Name, reference, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 1)
        {
            return matches[0].Id;
        }

        if (matches.Length > 1 && reportedAmbiguity.Add(reference))
        {
            errors.Add(
                $"「{reference}」对应多个角色（{string.Join('、', matches.Select(Describe))}），请用 id 指定。"
                + Environment.NewLine
                + $"已知角色：{Known(characters)}");
        }

        // 3) 未知或重名：原样返回。未知的交给 WorldStateStore 报原来那句；重名的已经记过错误。
        return reference;
    }

    private static string Describe(CharacterState character) => $"{character.Id}（{character.Name}）";

    private static string Known(IReadOnlyDictionary<string, CharacterState> characters)
    {
        var all = characters.Values.Select(Describe).ToArray();
        return all.Length == 0 ? "（无）" : string.Join('、', all);
    }
}
