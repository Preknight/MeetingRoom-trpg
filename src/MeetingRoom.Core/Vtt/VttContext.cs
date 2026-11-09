using System.Text;

using MeetingRoom.Core.Config;
using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Core.Vtt;

/// <summary>
/// 把一份地图快照转成<b>给模型看的一段话</b>（《枭熊地图接入实现方案》§3 V1）：
/// 人物 → 距离（尺）、最近标尺读数。
/// <para>
/// 两条口径（写进文档与测试）：
/// <list type="number">
///   <item>只注入 <see cref="VttToken.Visible"/> 为真的 token；<c>vtt.includeHidden</c> 为假时隐藏项一律不出现——
///     枭熊的显示开关是唯一的信息闸门（扩展读不到迷雾）。</item>
///   <item>没在 <see cref="WorldState.Characters"/> 里登记的 token 一律标为「一个中型生物」，
///     <b>不给名字</b>——那是 DM 还没介绍的对手。</item>
/// </list>
/// </para>
/// <para>
/// 距离只按场景的 <c>measurement</c> 算，客户端不覆盖规则：不给模型"挑一套更有利算法"的机会。
/// </para>
/// </summary>
public sealed class VttContext
{
    /// <summary>未登记 token 的称呼：不给名字，也不猜体型细节。</summary>
    public const string UnregisteredTarget = "一个中型生物";

    private readonly VttSnapshot _snapshot;
    private readonly WorldState _world;
    private readonly VttOptions _options;
    private readonly IReadOnlyDictionary<string, string> _tokenIds;

    public VttContext(
        VttSnapshot snapshot,
        WorldState world,
        VttOptions options,
        IReadOnlyDictionary<string, string>? tokenIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);

        _snapshot = snapshot;
        _world = world;
        _options = options;
        _tokenIds = tokenIds ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>地图可用（有网格、场景就绪）才有内容可注入。</summary>
    public bool HasMap => _snapshot.Available;

    /// <summary>
    /// 某个角色看到的那一份：以他自己的棋子为锚点，只给注入半径内的可见目标。
    /// 没有地图、或图上没有他的棋子时返回空串 / 一句"距离无从判断"，绝不编造距离。
    /// </summary>
    public string ForCharacter(string characterId)
    {
        if (!HasMap || string.IsNullOrWhiteSpace(characterId))
        {
            return string.Empty;
        }

        return DescribeAround(characterId, Math.Max(0, _options.InjectRadiusFeet), redact: true);
    }

    /// <summary>
    /// 说书人的那一份：全量 token（未登记的标出来）、当前选中、标尺读数。
    /// 隐藏项仍受 <c>vtt.includeHidden</c> 约束——说书人写出正文就等于剧透。
    /// </summary>
    public string ForNarrator()
    {
        if (!HasMap)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("[地图] 场景已就绪；").Append(GridText()).Append('。');

        var tokens = VisibleTokens().ToArray();

        if (tokens.Length == 0)
        {
            builder.Append('\n').Append("- 场上还没有棋子。");
        }
        else
        {
            foreach (var token in tokens)
            {
                builder.Append('\n').Append("- ").Append(NameForNarrator(token));
                builder.Append("：坐标 (").Append(Number(token.X)).Append(", ").Append(Number(token.Y)).Append(')');

                if (!token.Visible)
                {
                    builder.Append("，隐藏（导演关掉了显示）");
                }
            }
        }

        if (_snapshot.Selected.Count > 0)
        {
            builder.Append('\n').Append("当前选中：").Append(string.Join('、', _snapshot.Selected));
        }

        AppendRulers(builder);

        return builder.ToString();
    }

    /// <summary>
    /// 说书人的主动查询（<c>look_around</c>）：给某个角色周围的目标与距离；
    /// 不给角色就返回整张图的那一份。
    /// </summary>
    public string LookAround(string? characterId, double? radiusFeet)
    {
        if (!HasMap)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(characterId))
        {
            return ForNarrator();
        }

        return DescribeAround(characterId, radiusFeet ?? Math.Max(0, _options.InjectRadiusFeet), redact: false);
    }

    private string DescribeAround(string characterId, double radiusFeet, bool redact)
    {
        var anchor = AnchorFor(characterId);
        var name = _world.Characters.TryGetValue(characterId, out var character) && !string.IsNullOrWhiteSpace(character.Name)
            ? character.Name
            : characterId;

        if (anchor is null)
        {
            // 宁可什么都不给，也不给错的距离：名字对不上是最常见的原因，直接说出来让人去改 token 名。
            return $"[地图] 图上没有名为「{name}」的棋子，位置与距离无从判断——不要自己估算。";
        }

        var builder = new StringBuilder();
        builder.Append("[地图] 网格 ").Append(GridText());
        builder.Append("；以「").Append(name).Append("」为中心，").Append(Distance.Describe(radiusFeet)).Append("内：");

        var targets = new List<(string Label, double Feet)>();

        foreach (var token in VisibleTokens())
        {
            if (string.Equals(token.Id, anchor.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var feet = Distance.InFeet(_snapshot.Grid, new VttPoint(anchor.X, anchor.Y), new VttPoint(token.X, token.Y));

            if (feet > radiusFeet)
            {
                continue;
            }

            targets.Add((redact ? NameForCharacter(token) : NameForNarrator(token), feet));
        }

        if (targets.Count == 0)
        {
            builder.Append('\n').Append("- 附近没有别的目标。");
        }
        else
        {
            foreach (var (label, feet) in targets.OrderBy(target => target.Feet))
            {
                builder.Append('\n').Append("- ").Append(label).Append('：').Append(Distance.Describe(feet));
            }
        }

        AppendRulers(builder);
        return builder.ToString();
    }

    /// <summary>
    /// 以这个角色的棋子为锚点。<paramref name="characterId"/> 既可以是角色 id，也可以是角色名——
    /// 说书人看到的是名字（"Ella"），它不会记得 id。退化链（顺序即语义，测试钉住）：
    /// <list type="number">
    ///   <item>角色卡上写的 <c>vttTokenId</c> 精确匹配 <c>token.id</c> —— 最稳，DM 改名也不断；</item>
    ///   <item><c>token.id</c> 等于调用方给的值（老数据 / 手写场景 / 直接点 token id）；</item>
    ///   <item><c>token.name</c> 等于调用方给的值（<c>look_around</c> 点 token 名或角色名）；</item>
    ///   <item><c>token.name</c> 等于角色名（DM 摆 token 时一般直接写角色名）。</item>
    /// </list>
    /// 全都对不上就返回 <c>null</c>：调用方回一句「图上没有…的棋子」，<b>不给任何估算</b>。
    /// </summary>
    private VttToken? AnchorFor(string characterId)
    {
        var character = CharacterByAnyId(characterId);

        // 1) 稳定映射：角色卡 → token.id。设置了但图上找不到时**继续往下退**，
        //    而不是让整段地图消失（换场景时 DM 重新摆 token 很常见）。
        //    映射按角色 **id** 存，所以这里用解析出来的 character.Id，而不是调用方给的名字。
        if (character is not null
            && VttTokenIdFor(character.Id) is { } explicitId
            && _snapshot.Tokens.FirstOrDefault(token => string.Equals(token.Id, explicitId, StringComparison.Ordinal)) is { } byTokenId)
        {
            return byTokenId;
        }

        // 2) 老数据 / 手写场景：token.id 就是调用方给的那个。
        if (_snapshot.Tokens.FirstOrDefault(token => string.Equals(token.Id, characterId, StringComparison.Ordinal)) is { } byId)
        {
            return byId;
        }

        // 3) look_around 允许直接点 token 的名字（说书人未必记得角色 id）。
        if (_snapshot.Tokens.FirstOrDefault(token => string.Equals(token.Name, characterId, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            return byName;
        }

        return character is null
            ? null
            : _snapshot.Tokens.FirstOrDefault(token =>
                string.Equals(token.Name, character.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 两个角色在图上的距离（尺）。任一方没有棋子、或地图不可用时返回 <c>false</c>——
    /// 调用方据此决定"用地图距离还是用调用方给的距离"，不在这里猜。
    /// </summary>
    public bool TryDistanceFeet(string fromCharacterId, string toCharacterId, out double feet)
    {
        feet = 0;

        if (!HasMap
            || AnchorFor(fromCharacterId) is not { } from
            || AnchorFor(toCharacterId) is not { } to)
        {
            return false;
        }

        feet = Distance.InFeet(_snapshot.Grid, new VttPoint(from.X, from.Y), new VttPoint(to.X, to.Y));
        return true;
    }

    /// <summary>
    /// 这个角色（id 或名字都行）在图上有棋子吗——<c>look_around</c> 用它决定是"查"还是"回一句没有"。
    /// 判定与 <see cref="AnchorFor"/> 完全同源，避免"预检查说没有、真正解析却有"这种两套口径。
    /// </summary>
    public bool HasAnchor(string characterId) =>
        !string.IsNullOrWhiteSpace(characterId) && AnchorFor(characterId) is not null;

    /// <summary>按 id 或名字找角色（模型的输入是名字，内部一律按 id 存）。</summary>
    private CharacterState? CharacterByAnyId(string characterId) =>
        _world.Characters.TryGetValue(characterId, out var byId)
            ? byId
            : _world.Characters.Values.FirstOrDefault(character =>
                string.Equals(character.Name, characterId, StringComparison.OrdinalIgnoreCase));

    /// <summary>角色卡上的 vttTokenId（去掉空白；没配就是 <c>null</c>）。</summary>
    private string? VttTokenIdFor(string characterId) =>
        _tokenIds.TryGetValue(characterId, out var tokenId) && !string.IsNullOrWhiteSpace(tokenId)
            ? tokenId.Trim()
            : null;

    /// <summary>角色视角的称呼：登记过的给名字，没登记的只给一个泛称（不给模型剧透）。</summary>
    private string NameForCharacter(VttToken token)
    {
        var registered = Registered(token);
        return registered is { } character && !string.IsNullOrWhiteSpace(character.Name)
            ? character.Name
            : UnregisteredTarget;
    }

    /// <summary>说书人视角的称呼：未登记的也标出来，方便它决定"这个怪要不要在正文里出场"。</summary>
    private static string NameForNarrator(VttToken token) =>
        string.IsNullOrWhiteSpace(token.Name) ? token.Id : token.Name;

    /// <summary>token 对应的已登记角色：id 或名字对得上都算（OBR 里的 token 名由 DM 自己起）。</summary>
    private CharacterState? Registered(VttToken token) =>
        _world.Characters.TryGetValue(token.Id, out var byId)
            ? byId
            : _world.Characters.Values.FirstOrDefault(character =>
                string.Equals(character.Name, token.Name, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<VttToken> VisibleTokens() =>
        _snapshot.Tokens.Where(token => _options.IncludeHidden || token.Visible);

    private void AppendRulers(StringBuilder builder)
    {
        foreach (var ruler in _snapshot.Rulers)
        {
            if (string.IsNullOrWhiteSpace(ruler.Measurement))
            {
                continue;
            }

            builder.Append('\n').Append("标尺读数：").Append(ruler.Measurement);
        }
    }

    private string GridText()
    {
        var grid = _snapshot.Grid;
        var unit = string.IsNullOrWhiteSpace(grid.Unit) ? "ft" : grid.Unit;

        return $"{Number(grid.Multiplier)} {unit}/格（{Distance.DescribeMeasurement(grid.Measurement)}，dpi {grid.Dpi}）";
    }

    private static string Number(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
