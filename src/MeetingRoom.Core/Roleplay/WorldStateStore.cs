using System.Text;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 世界状态的提交与校验（设计文档 §11.4-6）。
/// <para>
/// 与会议模式的 <c>MaterialStore</c> 一样先校验、后生效（copy-on-write，任何一处不合法则整次不生效）。
/// 但它有一条<b>相反的默认</b>：叙事里正文是提交边界，<b>结算失败只表示"这次状态没变"，
/// 绝不撤销已经生成的正文</b>（§11.3-4）。所以这里从不抛异常让上层回滚剧情，
/// 只把失败原因装进结果、由调用方写进事件与后台记录。
/// </para>
/// </summary>
public sealed class WorldStateStore
{
    /// <summary>属性取值范围：绝对值超过它一律拒绝（防止模型把好感写成 9999）。</summary>
    /// <summary>
    /// HP 类属性键（§26）：世界状态不允许直接记 HP——它是 <c>SheetStore</c> 的真相，
    /// 记两份必然打架（端到端实测里模型就自编过 <c>hp -13</c>）。
    /// </summary>
    public static bool IsHitPointKey(string? key) =>
        key?.Trim().ToLowerInvariant() is "hp" or "maxhp" or "hpmax" or "currenthp" or "temphp";

    public const int MaxAttributeMagnitude = 100;

    private readonly Lock _gate = new();
    private WorldState _state;

    public WorldStateStore(
        IEnumerable<CharacterState> characters,
        string time = "",
        string place = "")
    {
        ArgumentNullException.ThrowIfNull(characters);

        var map = new Dictionary<string, CharacterState>(StringComparer.Ordinal);

        foreach (var character in characters)
        {
            map[character.Id] = character;
        }

        _state = new WorldState(0)
        {
            Time = time ?? string.Empty,
            Place = place ?? string.Empty,
            Characters = map,
        };
    }

    public WorldState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public long Version => Current.Version;

    /// <summary>登记一个角色（入场新人物；不经过它，变更里出现未知角色会被拒）。</summary>
    public void Register(CharacterState character)
    {
        ArgumentNullException.ThrowIfNull(character);

        lock (_gate)
        {
            var map = new Dictionary<string, CharacterState>(_state.Characters, StringComparer.Ordinal)
            {
                [character.Id] = character,
            };

            _state = _state with { Characters = map };
        }
    }

    /// <summary>
    /// 应用一次变更。返回结果里带失败原因；<b>失败时状态一点没变</b>，
    /// 调用方应当把原因写进后台记录，然后照常继续（正文已经成立）。
    /// </summary>
    public WorldStateResult Apply(WorldStateMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_gate)
        {
            // 名字 → id 归一：模型给的是名字（"Ella"），内部一律按 id 存。
            // 只在这里做一次，工具层不重复解析（见 CharacterReferences 的注释）。
            var resolved = CharacterReferences.Resolve(mutation, _state.Characters);

            if (resolved.Errors.Count > 0)
            {
                // 重名无法判定：整次不生效，也绝不替调用方挑一个。
                return new WorldStateResult(false, _state, resolved.Errors);
            }

            mutation = resolved.Mutation;

            var errors = new List<string>();
            var characters = new Dictionary<string, CharacterState>(_state.Characters, StringComparer.Ordinal);

            foreach (var id in Touched(mutation))
            {
                if (!characters.ContainsKey(id))
                {
                    errors.Add($"未知角色：{id}");
                }
            }

            if (errors.Count > 0)
            {
                return new WorldStateResult(false, _state, errors);
            }

            foreach (var id in mutation.Enter)
            {
                characters[id] = characters[id] with { Present = true };
            }

            foreach (var id in mutation.Leave)
            {
                characters[id] = characters[id] with { Present = false };
            }

            foreach (var group in mutation.Attributes.GroupBy(delta => delta.Character, StringComparer.Ordinal))
            {
                var attributes = new Dictionary<string, int>(characters[group.Key].Attributes, StringComparer.Ordinal);

                foreach (var delta in group)
                {
                    if (string.IsNullOrWhiteSpace(delta.Key))
                    {
                        errors.Add("属性名不能为空");
                        continue;
                    }

                    if (IsHitPointKey(delta.Key))
                    {
                        // HP 只有战斗引擎（SheetStore）一个真相；世界状态里再记一份必然打架。
                        errors.Add($"属性「{delta.Key}」不能直接改：HP 由战斗引擎管理；要改请用 dm_override 或走 combat_action。");
                        continue;
                    }

                    var value = attributes.GetValueOrDefault(delta.Key) + delta.Delta;

                    if (Math.Abs(value) > MaxAttributeMagnitude)
                    {
                        errors.Add($"{group.Key} 的 {delta.Key} 会变成 {value}，超出 ±{MaxAttributeMagnitude}");
                        continue;
                    }

                    attributes[delta.Key] = value;
                }

                characters[group.Key] = characters[group.Key] with { Attributes = attributes };
            }

            foreach (var group in mutation.Items.GroupBy(delta => delta.Character, StringComparer.Ordinal))
            {
                var items = characters[group.Key].Items.ToList();

                foreach (var delta in group)
                {
                    if (string.IsNullOrWhiteSpace(delta.Item))
                    {
                        errors.Add("物品名不能为空");
                        continue;
                    }

                    if (delta.Remove && !items.Remove(delta.Item))
                    {
                        errors.Add($"{group.Key} 并不持有「{delta.Item}」");
                    }
                    else if (!delta.Remove && !items.Contains(delta.Item, StringComparer.Ordinal))
                    {
                        items.Add(delta.Item);
                    }
                }

                characters[group.Key] = characters[group.Key] with { Items = items };
            }

            foreach (var group in mutation.Knowledge.GroupBy(delta => delta.Character, StringComparer.Ordinal))
            {
                var known = characters[group.Key].Known.ToList();

                foreach (var delta in group)
                {
                    if (string.IsNullOrWhiteSpace(delta.Fact))
                    {
                        errors.Add("已知信息不能为空");
                        continue;
                    }

                    if (!known.Contains(delta.Fact, StringComparer.Ordinal))
                    {
                        known.Add(delta.Fact);
                    }
                }

                characters[group.Key] = characters[group.Key] with { Known = known };
            }

            if (errors.Count > 0)
            {
                // 原子性：任何一处不合法 → 整次结算不生效；状态保持原样，正文照旧成立。
                return new WorldStateResult(false, _state, errors);
            }

            _state = _state with
            {
                Version = _state.Version + 1,
                Time = string.IsNullOrWhiteSpace(mutation.Time) ? _state.Time : mutation.Time.Trim(),
                Place = string.IsNullOrWhiteSpace(mutation.Place) ? _state.Place : mutation.Place.Trim(),
                Characters = characters,
            };

            return new WorldStateResult(true, _state, []);
        }
    }

    /// <summary>
    /// <b>公共视图</b>（进共享节）：剧情内时间、当前地点、在场与不在场名单。
    /// <b>绝不含"谁知道什么"</b>——那是一个角色的秘密，只能经 <see cref="SnapshotFor"/> 交给他自己（§11.4-3）。
    /// </summary>
    public string SnapshotPublic(int maxChars = 600)
    {
        var state = Current;
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(state.Time))
        {
            builder.Append("时间：").Append(state.Time).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(state.Place))
        {
            builder.Append("地点：").Append(state.Place).Append('\n');
        }

        builder.Append("在场：").Append(
            Names(state.Characters.Values.Where(character => character.Present)));
        builder.Append('\n').Append("不在场：").Append(
            Names(state.Characters.Values.Where(character => !character.Present)));

        return Trim(builder.ToString(), maxChars);
    }

    /// <summary>
    /// 某个角色的<b>私有视图</b>（进他的角色节）：他自己的状态、持有物与已知信息。
    /// 别人的这些细节一概不给——"谁知道什么"互相保密（§11.4-3）；什么都没有时返回空串，不占 token。
    /// </summary>
    public string SnapshotFor(string actorId, int maxChars = 600)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !Current.Characters.TryGetValue(actorId, out var character))
        {
            return string.Empty;
        }

        if (character.Attributes.Count == 0 && character.Items.Count == 0 && character.Known.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("关于你自己（").Append(character.Name).Append("）：");

        if (character.Attributes.Count > 0)
        {
            builder.Append('\n').Append("- 状态：")
                .Append(string.Join("、", character.Attributes.Select(pair => $"{pair.Key} {pair.Value}")));
        }

        if (character.Items.Count > 0)
        {
            builder.Append('\n').Append("- 持有：").Append(string.Join("、", character.Items));
        }

        if (character.Known.Count > 0)
        {
            builder.Append('\n').Append("- 已知：").Append(string.Join("、", character.Known));
        }

        return Trim(builder.ToString(), maxChars);
    }

    /// <summary>
    /// 给提示词用的紧凑全量快照（§7.2 常驻节）：时间、地点、在场人物与关键状态。
    /// 受 <paramref name="maxChars"/> 限制——世界状态会随剧情膨胀，不能无上限地进前缀。
    /// 全量视图只交给<b>说书人</b>（它控场，需要看到"谁知道什么"）与第 1 列展示；场内角色走
    /// <see cref="SnapshotPublic"/> + <see cref="SnapshotFor"/> 两段。
    /// </summary>
    public string Snapshot(int maxChars = 600)
    {
        var state = Current;
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(state.Time))
        {
            builder.Append("时间：").Append(state.Time).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(state.Place))
        {
            builder.Append("地点：").Append(state.Place).Append('\n');
        }

        foreach (var character in state.Characters.Values)
        {
            builder.Append("- ").Append(character.Name).Append(character.Present ? "（在场）" : "（不在场）");

            if (character.Attributes.Count > 0)
            {
                builder.Append(" | ")
                    .Append(string.Join("、", character.Attributes.Select(pair => $"{pair.Key} {pair.Value}")));
            }

            if (character.Items.Count > 0)
            {
                builder.Append(" | 持有：").Append(string.Join("、", character.Items));
            }

            if (character.Known.Count > 0)
            {
                builder.Append(" | 已知：").Append(string.Join("、", character.Known));
            }

            builder.Append('\n');
        }

        return Trim(builder.ToString().TrimEnd(), maxChars);
    }

    private static string Names(IEnumerable<CharacterState> characters)
    {
        var names = characters.Select(character => character.Name).ToArray();
        return names.Length == 0 ? "（无）" : string.Join('、', names);
    }

    private static string Trim(string text, int maxChars) =>
        maxChars > 0 && text.Length > maxChars ? text[..maxChars] + "…" : text;

    private static IEnumerable<string> Touched(WorldStateMutation mutation)
    {
        foreach (var id in mutation.Enter.Concat(mutation.Leave))
        {
            yield return id;
        }

        foreach (var id in mutation.Attributes.Select(delta => delta.Character)
                     .Concat(mutation.Items.Select(delta => delta.Character))
                     .Concat(mutation.Knowledge.Select(delta => delta.Character)))
        {
            yield return id;
        }
    }
}
