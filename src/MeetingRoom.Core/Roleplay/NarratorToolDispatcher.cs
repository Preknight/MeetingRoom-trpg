using System.ComponentModel;
using System.Text.Json;
using MeetingRoom.Agent.Tools;
using MeetingRoom.Core.Combat;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Rules;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 说书人的工具面（设计文档 §11.4-5、§11.4 第 6b 项）：<c>propose_world_state</c> /
/// <c>nudge_character</c> / <c>idle_beat</c> / <c>declare_scene_end</c>。
/// <para>
/// 与会议侧 <c>MeetingToolDispatcher</c> 同构：权限在<b>工具内部</b>判定——只有说书人的调用会被受理。
/// 工具集本身不按角色裁剪（NPC 一个工具都不带，§11.5-2——那道闸门在编排层，
/// 见 <c>ParticipantRegistry.CarriesHostTools</c>）。
/// </para>
/// <para>
/// <b>说书人不能自己落盘剧情决策</b>：世界状态只有 <see cref="INarratorActions.ProposeWorldStateAsync"/>
/// 一个入口，它要么由导演的指令触发、要么先问过导演（超时按"采用"收口）。
/// </para>
/// </summary>
public sealed class NarratorToolDispatcher : IToolDispatcher
{
    private static readonly JsonSerializerOptions ArgumentOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>参战者内联数值表：与规则包同一条规矩——未知键必须报错。</summary>
    private static readonly JsonSerializerOptions SheetOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    private readonly INarratorActions _narrator;
    private readonly Func<string, bool> _isNarrator;
    private readonly ITableActions? _table;
    private readonly ICharacterActions? _character;
    private readonly RulesData? _rules;
    private readonly IStoryRecall? _storyRecall;

    /// <summary>
    /// 本轮规则查询次数（§27：一轮最多 3 次）。由 <see cref="ResetRuleQuota"/> 在每轮开头清零——
    /// 额度与"一次请求"（<c>_requestsThisRound</c>）互不相干：查资料不该消耗请求额度。
    /// </summary>
    private int _ruleLookupsThisRound;

    /// <summary>本轮的剧情回忆次数（§2：一轮一次）。</summary>
    private int _storyRecallsThisRound;

    public NarratorToolDispatcher(
        INarratorActions narrator,
        Func<string, bool> isNarrator,
        ITableActions? table = null,
        ICharacterActions? character = null,
        RulesData? rules = null,
        IStoryRecall? storyRecall = null)
    {
        ArgumentNullException.ThrowIfNull(narrator);
        ArgumentNullException.ThrowIfNull(isNarrator);

        _narrator = narrator;
        _isNarrator = isNarrator;
        _table = table;
        _character = character;
        _rules = rules;
        _storyRecall = storyRecall;
        Tools = BuildTools();
        CharacterTools = BuildCharacterTools();
    }

    /// <summary>每轮开头清零查询额度（由 <c>RoleplaySession.SayAsync</c> 调用）。</summary>
    public void ResetRuleQuota() => _ruleLookupsThisRound = 0;

    /// <summary>每轮开头清零回忆额度（§2：一轮一次）。</summary>
    public void ResetStoryQuota() => _storyRecallsThisRound = 0;

    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>
    /// 角色子集（M9b）：<c>look_around</c> / <c>check_terrain</c> / <c>dice_roll</c> /
    /// <c>request_judgement</c> / <c>request_combat_action</c>——<b>就这 5 个</b>。
    /// 每多一个 schema 就多一份 token 与延迟，所以不做"角色也能用某说书人工具"的临时口子。
    /// </summary>
    public IReadOnlyList<AITool> CharacterTools { get; }

    public Task<FunctionResultContent> InvokeAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!Narrator.AllToolNames.Contains(call.Name, StringComparer.Ordinal))
        {
            return Result(call, $"未知工具：{call.Name}");
        }

        // 越权：说书人工具只有说书人能调；角色只开放 `Narrator.CharacterToolNames` 那 6 个。
        // 规则查询的权限靠结构保证：`lookup_monster` 只在说书人表里，角色表里根本没有这个名字。
        if (!_isNarrator(participantId) && !Narrator.CharacterToolNames.Contains(call.Name, StringComparer.Ordinal))
        {
            return Result(call, $"只有说书人可以调用 {call.Name}。");
        }

        try
        {
            return call.Name switch
            {
                Narrator.ProposeWorldState => ProposeAsync(call, cancellationToken),
                Narrator.ProposeRevealFog => RevealFogAsync(call, cancellationToken),
                Narrator.NudgeCharacter => NudgeAsync(call),
                Narrator.IdleBeat => BeatAsync(call),
                Narrator.DeclareSceneEnd => EndAsync(call),
                Narrator.AskUser => AskAsync(call, cancellationToken),
                // 角色调 look_around 时走"按该角色视角过滤"的那条（说书人仍是全图）。
                Narrator.LookAround => _isNarrator(participantId)
                    ? LookAroundAsync(call, cancellationToken)
                    : CharacterLookAroundAsync(participantId, call, cancellationToken),
                Narrator.DiceRoll => DiceAsync(call, cancellationToken),
                Narrator.CheckTerrain => CharacterCheckTerrainAsync(participantId, call, cancellationToken),
                Narrator.RequestJudgement => CharacterRequestJudgementAsync(participantId, call, cancellationToken),
                Narrator.RequestCombatAction => CharacterRequestCombatActionAsync(participantId, call, cancellationToken),
                Narrator.StartCombat => StartCombatAsync(call, cancellationToken),
                Narrator.NextTurn => NextTurnAsync(call, cancellationToken),
                Narrator.EndCombat => EndCombatAsync(call, cancellationToken),
                Narrator.CombatAction => CombatActionAsync(call, cancellationToken),
                Narrator.RequestCheck => RequestCheckAsync(call, cancellationToken),
                Narrator.DmOverride => DmOverrideAsync(call, cancellationToken),
                Narrator.LookupRule => Task.FromResult(LookupRuleAsync(call)),
                Narrator.RecallStory => Task.FromResult(RecallStoryAsync(participantId, call)),
                Narrator.LookupMonster => Task.FromResult(LookupMonsterAsync(call)),
                _ => Result(call, $"未知工具：{call.Name}"),
            };
        }
        catch (OperationCanceledException)
        {
            // 取消是正常路径（本场结束 / 进程退出），继续往上抛。
            throw;
        }
        catch (Exception ex)
        {
            // **工具出错不能冒泡到 SayAsync**：那会让整回合静默消失（r15 就是这么丢的：
            // shape 传成 JSON 字符串 → 解析抛异常 → SayAsync 的 catch 一吞，正文与 turn 全没了）。
            // 按 function calling 的标准做法：把错误当成工具回执交回给模型，它自己修正参数再试。
            return Result(call, $"工具 {call.Name} 执行出错：{ex.Message}。检查参数形状后重试，或换个办法。");
        }
    }

    /// <summary>查地图（V1）：半径内的 token 与距离都由桥的快照算出，模型只读不算。</summary>
    private async Task<FunctionResultContent> LookAroundAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var outcome = await _table
            .LookAroundAsync(
                Text(call.Arguments, "characterId") ?? Text(call.Arguments, "id"),
                Number(call.Arguments, "radiusFeet"),
                cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>掷骰（V2）：结果落 <c>story.jsonl</c> 的 <c>kind=roll</c>，回执里带明细。</summary>
    private async Task<FunctionResultContent> DiceAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var expression = Text(call.Arguments, "expression");

        if (string.IsNullOrWhiteSpace(expression))
        {
            return await Result(call, "缺少 expression：形如 1d20+5、2d6+3 或 3x(1d8+2)。").ConfigureAwait(false);
        }

        var outcome = await _table
            .RollDiceAsync(
                Text(call.Arguments, "who") ?? "有人",
                expression,
                Text(call.Arguments, "mode"),
                Text(call.Arguments, "reason") ?? "剧情推进",
                (int?)Number(call.Arguments, "dc"),
                cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>
    /// DM 覆盖（D3/§25）：<b>仅当导演明确要求</b>时使用；<c>reason</c> 必填且要来自导演原话。
    /// 九字段白名单由引擎判，未知字段会被拒绝并列出支持项。
    /// </summary>
    private async Task<FunctionResultContent> DmOverrideAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var target = Text(call.Arguments, "target");
        var field = Text(call.Arguments, "field");
        var reason = Text(call.Arguments, "reason");

        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(field))
        {
            return await Result(
                call,
                "dm_override 要给 target 与 field（支持：hp / tempHp / maxHp / ac / status / condition / initiative / budget / dc）。")
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return await Result(call, "dm_override 必须带 reason，并且要来自导演的原话——不要自己编理由。")
                .ConfigureAwait(false);
        }

        var outcome = await _table
            .DmOverrideAsync(
                new DmOverrideRequest
                {
                    Target = target,
                    Field = field,
                    Value = ToJsonElement(call.Arguments, "value"),
                    Reason = reason,
                    CheckId = Text(call.Arguments, "checkId"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>
    /// 把 <c>value</c> 统一成 <see cref="JsonElement"/>：模型给的多半是 JsonElement，
    /// 测试 / 脚本可能直接给数字、布尔或 JSON 文本。
    /// </summary>
    private static JsonElement? ToJsonElement(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement element => element,
            string text when text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[') =>
                JsonDocument.Parse(text).RootElement.Clone(),
            string text => JsonSerializer.SerializeToElement(text),
            _ => JsonSerializer.SerializeToElement(value),
        };
    }

    /// <summary>开战（C1）：参战者由模型给，先攻与顺序由引擎算。</summary>
    private async Task<FunctionResultContent> StartCombatAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        IReadOnlyList<CombatantArgument> raws;

        try
        {
            raws = Typed<CombatantArgument>(call.Arguments, "combatants");
        }
        catch (JsonException ex)
        {
            return new FunctionResultContent(call.CallId, $"参数格式不对，这次没有开战：{ex.Message}");
        }

        CombatantSetup[] setups;

        try
        {
            setups = raws.Select(ToSetup).ToArray();
        }
        catch (JsonException ex)
        {
            return new FunctionResultContent(call.CallId, $"参战者的 sheet 格式不对，这次没有开战：{ex.Message}");
        }
        var outcome = await _table
            .StartCombatAsync(setups, (int?)Number(call.Arguments, "seed"), Text(call.Arguments, "encounterId"), cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>战斗内出手（C2）：逐字段读参数，避免模型给嵌套对象时整体失败。</summary>
    private async Task<FunctionResultContent> CombatActionAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var arguments = call.Arguments;
        var request = new CombatActionRequest
        {
            ActorId = Text(arguments, "actorId") ?? Text(arguments, "actor") ?? string.Empty,
            TargetId = Text(arguments, "targetId") ?? Text(arguments, "target") ?? string.Empty,
            Kind = ParseActionKind(Text(arguments, "kind")),
            WeaponId = Text(arguments, "weaponId"),
            SpellId = Text(arguments, "spellId"),
            TargetArmorClass = (int?)Number(arguments, "targetArmorClass") ?? 10,
            AttackBonus = (int?)Number(arguments, "attackBonus"),
            DamageBonus = (int?)Number(arguments, "damageBonus"),
            DamageDice = Text(arguments, "damageDice"),
            DamageType = Text(arguments, "damageType"),
            SaveDc = (int?)Number(arguments, "saveDc"),
            TargetSaveBonus = (int?)Number(arguments, "targetSaveBonus"),
            Masteries = TypedList(arguments, "masteries"),
            DistanceFeet = Number(arguments, "distanceFeet"),
            UseBonusAction = IsTrue(Text(arguments, "useBonusAction")),
            HelpTargetId = Text(arguments, "helpTargetId"),
            CastAtLevel = (int?)Number(arguments, "castAtLevel"),
            CharacterLevel = (int?)Number(arguments, "characterLevel"),
            ProvokesOpportunityFrom = TypedList(arguments, "provokesOpportunityFrom"),
            ReadyTrigger = Text(arguments, "readyTrigger"),
            Reason = Text(arguments, "reason"),
        };

        var outcome = await _table.SubmitIntentAsync(request, cancellationToken).ConfigureAwait(false);
        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>叙事检定（C2）：支持四种形态、暗骰、Stakes 与防刷。</summary>
    private async Task<FunctionResultContent> RequestCheckAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var arguments = call.Arguments;
        var stakes = Single<CheckStakesArgument>(arguments, "stakes");
        var opposition = Single<CheckOppositionArgument>(arguments, "opposedBy");

        var request = new CheckRequest
        {
            ActorId = Text(arguments, "actorId") ?? Text(arguments, "actor") ?? string.Empty,
            Kind = ParseCheckKind(Text(arguments, "kind")),
            Skill = Text(arguments, "skill"),
            Ability = Text(arguments, "ability"),
            Dc = (int?)Number(arguments, "dc"),
            DcSource = Text(arguments, "dcSource") ?? "dm",
            Modifier = (int?)Number(arguments, "modifier"),
            AbilityModifier = (int?)Number(arguments, "abilityModifier"),
            ProficiencyBonus = (int?)Number(arguments, "proficiencyBonus"),
            Proficient = IsTrue(Text(arguments, "proficient")),
            Expertise = IsTrue(Text(arguments, "expertise")),
            Advantage = Text(arguments, "advantage") ?? "normal",
            Reason = Text(arguments, "reason") ?? string.Empty,
            Visibility = ParseVisibility(Text(arguments, "visibility")),
            TargetId = Text(arguments, "targetId"),
            Stakes = stakes is null
                ? null
                : new CheckStakes(stakes.OnSuccess, stakes.OnFailure, stakes.OnPartial),
            OpposedBy = opposition is null
                ? null
                : new CheckOpposition
                {
                    ActorId = opposition.ActorId ?? string.Empty,
                    Skill = opposition.Skill,
                    Ability = opposition.Ability,
                    Modifier = opposition.Modifier,
                    AbilityModifier = opposition.AbilityModifier,
                    ProficiencyBonus = opposition.ProficiencyBonus,
                    Proficient = opposition.Proficient == true,
                    Expertise = opposition.Expertise == true,
                },
        };

        var outcome = await _table.RequestCheckAsync(request, cancellationToken).ConfigureAwait(false);
        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>换手（C1）：把场面交给下一位。</summary>
    private async Task<FunctionResultContent> NextTurnAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var outcome = await _table.EndTurnAsync(cancellationToken).ConfigureAwait(false);
        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>收场（C1）：记录胜负并结束战斗。</summary>
    private async Task<FunctionResultContent> EndCombatAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        if (_table is null)
        {
            return await Result(call, NotConnected).ConfigureAwait(false);
        }

        var outcome = await _table
            .EndCombatAsync(Text(call.Arguments, "outcome") ?? "dm_end", cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>提议一次世界状态变更；等导演确认，或超时按默认选项（采用）收口。</summary>
    private async Task<FunctionResultContent> ProposeAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        WorldStateMutation mutation;

        try
        {
            mutation = ReadMutation(call.Arguments);
        }
        catch (JsonException ex)
        {
            return new FunctionResultContent(call.CallId, $"参数格式不对，这次提议没有提交：{ex.Message}");
        }

        var hitPointKey = mutation.Attributes.FirstOrDefault(delta => WorldStateStore.IsHitPointKey(delta.Key));

        if (hitPointKey is not null)
        {
            return new FunctionResultContent(
                call.CallId,
                $"属性键「{hitPointKey.Key}」不能这样改：HP 由战斗引擎管理；要改请用 dm_override 或走 combat_action。");
        }

        if (IsEmpty(mutation))
        {
            return new FunctionResultContent(
                call.CallId,
                "这次提议什么都没变：至少要给出时间、地点、进出场、属性、持有物或已知信息中的一项。");
        }

        var reason = Text(call.Arguments, "reason");
        var outcome = await _narrator
            .ProposeWorldStateAsync(
                mutation,
                string.IsNullOrWhiteSpace(reason) ? "剧情推进" : reason.Trim(),
                cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    /// <summary>提议揭示迷雾（M6b）：参数二选一（cells / shape），由会话层统一成格索引。</summary>
    private async Task<FunctionResultContent> RevealFogAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        // 参数容错（r15）：模型可能把结构化参数**序列化成 JSON 字符串**传进来
        // （实测：shape = "{\"type\":\"circle\",...}"）。这里三种形状都认：
        // 真值（数组/对象）、JSON 字符串、以及对象形式的 {x,y}。解析失败返回可读回执，不抛。
        var arguments = call.Arguments ?? new Dictionary<string, object?>();
        var cells = ToolArguments.ReadCells(arguments, "cells", out var cellsError);
        var shape = ToolArguments.ReadFogShape(arguments, "shape", out var shapeError);

        if (cellsError is not null)
        {
            return await Result(call, cellsError).ConfigureAwait(false);
        }

        if (shapeError is not null)
        {
            return await Result(call, shapeError).ConfigureAwait(false);
        }

        if (cells is null && shape is null)
        {
            return await Result(call, "要揭示迷雾：给 cells（[[gx,gy],…]）或 shape（{\"type\":\"circle\",\"center\":[x,y],\"radiusCells\":2}）。");
        }

        var reason = Text(call.Arguments, "reason");

        var outcome = await _narrator
            .ProposeRevealFogAsync(
                cells,
                shape,
                string.IsNullOrWhiteSpace(reason) ? "剧情推进" : reason.Trim(),
                cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    private Task<FunctionResultContent> NudgeAsync(FunctionCallContent call)
    {
        var id = Text(call.Arguments, "characterId") ?? Text(call.Arguments, "id");
        var task = Text(call.Arguments, "task");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(task))
        {
            return Result(call, "缺少 characterId 或 task。");
        }

        return Result(call, _narrator.NudgeCharacter(id, task).Message);
    }

    private Task<FunctionResultContent> BeatAsync(FunctionCallContent call) =>
        Result(call, _narrator.IdleBeat(Text(call.Arguments, "hint")).Message);

    private Task<FunctionResultContent> EndAsync(FunctionCallContent call) =>
        Result(
            call,
            _narrator.DeclareSceneEnd(Text(call.Arguments, "reason") ?? "说书人认为这一幕该收束了。").Message);

    /// <summary>向导演提问：校验（选项说明必填、数量受 maxOptions 限）在会话侧做，这里只管读懂参数。</summary>
    private async Task<FunctionResultContent> AskAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InteractionQuestion> questions;

        try
        {
            var raw = call.Arguments is { } arguments && arguments.TryGetValue("questions", out var value)
                ? value
                : null;

            questions = AskUserSpec.ReadQuestions(raw);
        }
        catch (JsonException ex)
        {
            return new FunctionResultContent(call.CallId, $"参数格式不对，这次提问没有发出去：{ex.Message}");
        }

        var outcome = await _narrator.AskUserAsync(questions, cancellationToken).ConfigureAwait(false);
        return new FunctionResultContent(call.CallId, outcome.Message);
    }

    private static WorldStateMutation ReadMutation(IDictionary<string, object?>? arguments) => new()
    {
        Time = Text(arguments, "time"),
        Place = Text(arguments, "place"),
        Enter = Strings(arguments, "enter"),
        Leave = Strings(arguments, "leave"),
        Attributes = Typed<AttributeDelta>(arguments, "attributes"),
        Items = Typed<ItemDelta>(arguments, "items"),
        Knowledge = Typed<KnowledgeDelta>(arguments, "knowledge"),
    };

    private static bool IsEmpty(WorldStateMutation mutation) =>
        string.IsNullOrWhiteSpace(mutation.Time)
        && string.IsNullOrWhiteSpace(mutation.Place)
        && mutation.Enter.Count == 0
        && mutation.Leave.Count == 0
        && mutation.Attributes.Count == 0
        && mutation.Items.Count == 0
        && mutation.Knowledge.Count == 0;

    /// <summary>
    /// 角色子集（M9b；B2 起 7 个）：从说书人表里挑 <c>look_around</c> / <c>dice_roll</c> /
    /// <c>lookup_rule</c> / <c>recall_story</c> 的现成 schema，再加 3 个角色专属工具。
    /// <b>没有 <c>lookup_monster</c></b>——怪物数值是 DM 侧的东西，权限靠"表里没这个名"保证。
    /// 描述都压到一句话（多一份 schema 就多一份 token 与延迟）。
    /// </summary>
    public static IReadOnlyList<AITool> BuildCharacterTools()
    {
        var reuse = BuildTools()
            .Where(tool => tool.Name is Narrator.LookAround or Narrator.DiceRoll
                or Narrator.LookupRule or Narrator.RecallStory)
            .ToList();

        reuse.Add(AIFunctionFactory.Create(
            () => "schema",
            Narrator.CheckTerrain,
            "看你脚下与相邻一格的区域效果、是否困难地形；只回你该知道的部分"));

        reuse.Add(AIFunctionFactory.Create(
            ([Description("技能：athletics / arcana / perception 等（与 ability 二选一）")] string? skill,
                [Description("属性检定：str / dex / con / int / wis / cha（与 skill 二选一）")] string? ability,
                [Description("判定类型（可选）：例如 attack / save / contest")] string? kind,
                [Description("建议难度（可选）")] int? dcHint,
                [Description("为什么想掷：一句话")] string reason) => "schema",
            Narrator.RequestJudgement,
            "请求说书人让你做一次判定（只提交请求，不掷骰、不算结果）"));

        reuse.Add(AIFunctionFactory.Create(
            ([Description("动作类型：attack / spell / dash / dodge / hide / help / other")] string kind,
                [Description("目标：角色 id 或名字")] string targetId,
                [Description("武器 id（可选）")] string? weaponId,
                [Description("法术 id（可选）")] string? spellId,
                [Description("你想达成什么：一句话")] string intent,
                [Description("为什么这么做：一句话，可省")] string? reason) => "schema",
            Narrator.RequestCombatAction,
            "请求说书人结算一次战斗动作（只提交请求，不算命中/伤害）"));

        return reuse;
    }

    // ——— 角色分支的实际执行（M9b）：全部带 participantId，按该角色视角 ———

    private async Task<FunctionResultContent> CharacterLookAroundAsync(string participantId, FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return await Result(call, "这一场还没接上地图。").ConfigureAwait(false);
        }

        var outcome = await _character
            .LookAroundForCharacterAsync(participantId, Number(call.Arguments, "radiusFeet"), cancellationToken)
            .ConfigureAwait(false);

        return await Result(call, outcome.Message).ConfigureAwait(false);
    }

    private async Task<FunctionResultContent> CharacterCheckTerrainAsync(string participantId, FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return await Result(call, "这一场还没接上地图。").ConfigureAwait(false);
        }

        var outcome = await _character.CheckTerrainAsync(participantId, cancellationToken).ConfigureAwait(false);
        return await Result(call, outcome.Message).ConfigureAwait(false);
    }

    private async Task<FunctionResultContent> CharacterRequestJudgementAsync(string participantId, FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return await Result(call, "这一场还没接上说书人。").ConfigureAwait(false);
        }

        var outcome = await _character.RequestJudgementAsync(
            participantId,
            Text(call.Arguments, "skill"),
            Text(call.Arguments, "ability"),
            Text(call.Arguments, "kind"),
            (int?)Number(call.Arguments, "dcHint"),
            Text(call.Arguments, "reason") ?? string.Empty,
            cancellationToken).ConfigureAwait(false);

        return await Result(call, outcome.Message).ConfigureAwait(false);
    }

    private async Task<FunctionResultContent> CharacterRequestCombatActionAsync(string participantId, FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return await Result(call, "这一场还没接上说书人。").ConfigureAwait(false);
        }

        var outcome = await _character.RequestCombatActionAsync(
            participantId,
            Text(call.Arguments, "kind") ?? "other",
            Text(call.Arguments, "targetId") ?? string.Empty,
            Text(call.Arguments, "weaponId"),
            Text(call.Arguments, "spellId"),
            Text(call.Arguments, "intent") ?? string.Empty,
            Text(call.Arguments, "reason"),
            cancellationToken).ConfigureAwait(false);

        return await Result(call, outcome.Message).ConfigureAwait(false);
    }

    /// 只用来生成 schema：真正的执行走 <see cref="InvokeAsync"/>（step 循环自己调度工具，§6.6）。
    /// </summary>
    public static IReadOnlyList<AITool> BuildTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("剧情内时间，如「当晚 21:00」；不改就留空")] string? time,
                [Description("当前地点；不改就留空")] string? place,
                [Description("进场的角色 id")] string[]? enter,
                [Description("离场的角色 id")] string[]? leave,
                [Description("属性增减，如好感 / 伤病")] AttributeDelta[]? attributes,
                [Description("持有物的获得与失去")] ItemDelta[]? items,
                [Description("某人新得知的信息")] KnowledgeDelta[]? knowledge,
                [Description("为什么要变：一句话，写给导演看的提议理由")] string reason) => "schema",
            Narrator.ProposeWorldState,
            "（仅说书人）提议一次世界状态变更（时间 / 地点 / 进出场 / 属性 / 持有物 / 已知信息）；经导演确认后才生效"),

        AIFunctionFactory.Create(
            ([Description("要揭示的格子 [gx,gy] 数组；与 shape 二选一")] int[][]? cells,
                [Description("圆形区域 {type:\"circle\",center:[x,y],radiusCells:n}；与 cells 二选一")] FogShapeArgument? shape,
                [Description("为什么建议揭示：一句话，写给导演看的理由")] string reason) => "schema",
            Narrator.ProposeRevealFog,
            "（仅说书人）提议揭示迷雾（格子或圆形区域）；经导演同意后才生效，超时视为不揭示"),

        AIFunctionFactory.Create(
            ([Description("要接话的角色 id")] string characterId,
                [Description("让他做什么")] string task) => "schema",
            Narrator.NudgeCharacter,
            "（仅说书人）点名某个角色接话，维持场面节奏。"
            + "同一轮内对同一角色多次点名只会执行一次（内容取最后一次）；不要重复点名。"),

        AIFunctionFactory.Create(
            ([Description("这一拍想写什么，可留空")] string? hint) => "schema",
            Narrator.IdleBeat,
            "（仅说书人）导演没给新指令时，自己描写一拍（环境变化、NPC 主动动作）。"
            + "同一轮内多次描写只会执行一次（内容取最后一次）；每轮一拍就够。"),

        AIFunctionFactory.Create(
            ([Description("为什么该收束了")] string reason) => "schema",
            Narrator.DeclareSceneEnd,
            "（仅说书人）建议本场收束，把场面停下等导演决定下一步（只建议，不给结论）"),

        AIFunctionFactory.Create(
            ([Description("要问导演的问题，一次最多 3 个；每个选项都要有 description")] AskQuestion[]? questions) => "schema",
            Narrator.AskUser,
            "（仅说书人）向导演（用户）提问，请他拍板；会挂着等答复，超时按默认选项收口"),

        AIFunctionFactory.Create(
            ([Description("以谁为中心：角色 id；留空 = 整张图")] string? characterId,
                [Description("半径（尺）；留空 = 配置里的注入半径")] double? radiusFeet) => "schema",
            Narrator.LookAround,
            "（仅说书人）查枭熊地图：某个角色周围有谁、各在多远；距离按场景的网格规则算，不要自己估"),

        AIFunctionFactory.Create(
            ([Description("谁在掷：角色 id 或名字")] string who,
                [Description("表达式：NdM±P 或 Nx(NdM+P)，如 1d20+5、2d6+3、3x(1d8+2)")] string expression,
                [Description("normal / advantage / disadvantage（优劣势只作用于 d20）")] string? mode,
                [Description("为什么掷：一句话")] string? reason,
                [Description("难度（可选）：给了就判成功 / 失败")] int? dc) => "schema",
            Narrator.DiceRoll,
            "（仅说书人）让程序掷骰：支持优势 / 劣势与多次攻击，结果写进剧情日志的 kind=roll 记录。战斗内的攻击 / 伤害结算用 combat_action，不要用 dice_roll 手搓；战斗外的杂项掷骰才用它。"),

        AIFunctionFactory.Create(
            ([Description("参战者：id / name / side(party|foe|neutral) / dex（敏捷调整值）/ speedFeet / sheetId / hp / maxHp / ac（没有角色卡的敌人必须给 hp / maxHp / ac，否则开不了战）")] CombatantArgument[] combatants,
                [Description("随机种子（可选；同种子可重放先攻）")] int? seed,
                [Description("遭遇 id（可选；默认 enc-N）")] string? encounterId) => "schema",
            Narrator.StartCombat,
            "（仅说书人）何时用：玩家明确进入对抗、需要逐回合结算时；何时不用：能用一次检定解决的小冲突不要开战。"
            + "同一时间只能有一场；被拒时照回执里的原因叙述，不要说“系统不允许”。"),

        AIFunctionFactory.Create(
            () => "schema",
            Narrator.NextTurn,
            "（仅说书人）结束当前回合、把场面交给下一位（索引回到 0 时轮数 +1）。"
            + "只在当前行动者真的行动完之后调用；被拒时照回执里的原因叙述。"),

        AIFunctionFactory.Create(
            ([Description("victory / defeat / retreat / dm_end")] string outcome) => "schema",
            Narrator.EndCombat,
            "（仅说书人）胜负已定时结束整场战斗并记录结果（victory / defeat / retreat / dm_end）。"
            + "还没分出结果时不要调用；被拒时照回执里的原因叙述。"),

        AIFunctionFactory.Create(
            ([Description("谁在出手（combatantId）")] string actorId,
                [Description("打谁（combatantId）")] string targetId,
                [Description("动作：attack | cast_spell | move | dash | dodge | disengage | hide | help | ready（默认 attack）")] string? kind,
                [Description("武器 id（attack 必填），如 Longsword|XPHB")] string? weaponId,
                [Description("法术 id（cast_spell 必填），如 Fireball|XPHB")] string? spellId,
                [Description("目标 AC（DM 给）")] int? targetArmorClass,
                [Description("命中加值")] int? attackBonus,
                [Description("伤害固定加值（如 +3）")] int? damageBonus,
                [Description("伤害骰（可选；默认武器 dmg1 / 法术首段）")] string? damageDice,
                [Description("伤害类型（必填：规则包不含），如 slashing / fire")] string? damageType,
                [Description("豁免型法术的 DC")] int? saveDc,
                [Description("目标豁免加值")] int? targetSaveBonus,
                [Description("精通名（可选；默认用武器 mastery[]）")] string[]? masteries,
                [Description("距离（尺）：目标离你多远。给了才做射程校验（超出会被拒绝，理由可叙述）")] double? distanceFeet,
                [Description("是否消耗 Bonus Action 而不是 Action（双持 / 2024 Disengage）")] bool? useBonusAction,
                [Description("Help 的帮助对象 id（kind=help 必填）")] string? helpTargetId,
                [Description("用几环法术位施放（cast_spell 可选；空 = 法术自身环级）")] int? castAtLevel,
                [Description("施法者等级（戏法档位用；空 = 1）")] int? characterLevel,
                [Description("这次移动离开了谁的触及（kind=move 用；会触发借机攻击）")] string[]? provokesOpportunityFrom,
                [Description("Ready 的触发条件（kind=ready 必填）")] string? readyTrigger,
                [Description("为什么出手")] string? reason) => "schema",
            Narrator.CombatAction,
            "（仅说书人）战斗内一次攻击或施法：武器 / 法术从规则包查，命中、伤害与扣血都由程序结算"
            + "（HP 以数值表为唯一真相，不要自己算）。一次只提一个 combat_action，等回执再提下一个——连发会被连续拒绝；若上一个 combat_action 已被拒，不要重试同一动作，改调 next_turn 或结束本轮；被拒时按回执原因叙述"
            + "（例如“目标在 60 尺外，弯刀只能打到 5 尺”），不要说“系统不允许”。"),

        AIFunctionFactory.Create(
            ([Description("谁做检定")] string actorId,
                [Description("ability | skill | contest | passive（默认 skill）")] string? kind,
                [Description("技能 id / 英文名 / 中文名（skill 时必填）")] string? skill,
                [Description("属性缩写 str/dex/con/int/wis/cha")] string? ability,
                [Description("DC（对抗检定不需要）")] int? dc,
                [Description("DC 来源 dm | narrator | rulesTable")] string? dcSource,
                [Description("直接给总加值（优先）")] int? modifier,
                [Description("属性调整值")] int? abilityModifier,
                [Description("熟练加值")] int? proficiencyBonus,
                [Description("是否熟练（true/false）")] string? proficient,
                [Description("是否专精（熟练双倍，true/false）")] string? expertise,
                [Description("normal | advantage | disadvantage")] string? advantage,
                [Description("为什么要检定")] string? reason,
                [Description("可见性取值 Public 或 Secret（也接受小写 public / secret）：Secret = 暗骰，结果不给角色看，只有 DM 面有数值")] CheckVisibility? visibility,
                [Description("被检定的对象（防刷 key 用）")] string? targetId,
                [Description("后果：{onSuccess, onFailure, onPartial}")] JsonElement? stakes,
                [Description("对抗方：{actorId, skill, ability, modifier}")] JsonElement? opposedBy) => "schema",
            Narrator.RequestCheck,
            "（仅说书人）叙事检定（属性 / 技能 / 对抗 / 被动），暗骰结果不会进入角色上下文。"
            + "何时用：结果不确定且失败有后果时；何时不用：没有失败后果、或同一理由反复刷。"
            + "只要结果不确定且失败有后果就先用它：战斗外、战斗中非攻击的观察 / 判断 / 游说都一样；不要用 dice_roll 或纯叙事替代。要暗骰就传 visibility=Secret（正文不得出现数值、DC 或成败）。也不要为了检定而检定。同一目标重复尝试会加 DC；被拒时照回执里的原因叙述。"),

        AIFunctionFactory.Create(
            ([Description("改谁：战斗者 / 角色 id")] string target,
                [Description("改哪个字段：hp / tempHp / maxHp / ac / status / condition / initiative / budget / dc")] string field,
                [Description("改成什么：数字，或对象（condition: {name,rounds?,remove?}；budget: {action,bonus,reaction,movementFeet}）")] JsonElement? value,
                [Description("导演的原话（必须来自导演，不得自行编造理由）")] string reason,
                [Description("field=dc 时要改哪条检定（checkId）")] string? checkId) => "schema",
            Narrator.DmOverride,
            "（仅说书人）DM 覆盖：仅当导演在对话中明确要求改数值 / 状态时调用。"
            + "不得用它给自己方便；reason 必须是导演原话。"
            + "支持字段：hp / tempHp / maxHp / ac / status / condition / initiative / budget / dc。"),

        AIFunctionFactory.Create(
            ([Description("查什么：中文名 / 英文名 / 部分词都行，如「火球术」「Fireball」「火球」")] string query,
                [Description("限定类别（可选）：spell | item | condition | action | skill | feat | class | subclass | optional；留空 = 九类都查")] string? kind,
                [Description("true = 只给 1 条并附完整英文原文；默认 false 给紧凑摘要")] bool? detail) => "schema",
            Narrator.LookupRule,
            "（规则原文）查规则资料：法术 / 装备 / 状态 / 动作 / 技能 / 专长 / 职业 / 子职 / 可选特性。"
            + "这是只读知识：查到的文字不能当结算结果——伤害、命中、成败仍要走 request_judgement / request_combat_action"
            + "（说书人这边是 request_check / combat_action），不要自己掷骰或自己宣布结果。"
            + "命中多条时只列候选，用 kind 收窄或给更完整的名字。每轮最多查 3 次。"),

        AIFunctionFactory.Create(
            ([Description("查什么：中文名 / 英文名 / 部分词都行，如「地精」「Goblin」")] string query,
                [Description("true = 给完整 traits / actions 原文（含命中加值与伤害骰）；默认 false 给 CR / HP / AC / 六属性 / 特性名")] bool? detail) => "schema",
            Narrator.LookupMonster,
            "（仅说书人）查怪物模板：CR / 类型 / 体型 / HP（均值 + 骰式）/ AC / 六属性 / 特性与动作原文。"
            + "这是模板基线，开战时你可按战况调 HP 与数量；命中、伤害与扣血仍由 combat_action 结算。每轮最多查 3 次。"),

        AIFunctionFactory.Create(
            ([Description("关键词（可选）：在记录正文里做子串匹配")] string? query,
                [Description("只看某个角色（可选）：作者名字")] string? author,
                [Description("只看这些类型（可选）：speech | world | roll | request | check | combat | warning")] string[]? kinds,
                [Description("从第几幕之后开始（可选）")] int? since,
                [Description("最近几条（可选，默认 10，上限 40）")] int? last) => "schema",
            Narrator.RecallStory,
            "（只读）回忆本场已经落盘的记录：谁在第几幕说了什么、掷过什么骰、世界怎么变的。"
            + "**用于忘掉前文时复查，不是每次发言前都查**（一轮一次）；它不会改变任何状态，"
            + "也不能代替 request_judgement / request_combat_action 的结算。"),
    ];

    /// <summary>一轮最多查几次规则（说书人与角色共用同一个额度，§27）。</summary>
    private const int RuleLookupLimit = 3;

    /// <summary>查规则原文（§27）：只读知识，<b>不结算</b>——伤害与结果仍要走判定 / 战斗流程。</summary>
    private FunctionResultContent LookupRuleAsync(FunctionCallContent call)
    {
        if (_rules is null)
        {
            return new FunctionResultContent(call.CallId, "规则包未加载（rules-data.json）：这次查不了规则。");
        }

        if (_ruleLookupsThisRound >= RuleLookupLimit)
        {
            return new FunctionResultContent(
                call.CallId,
                $"本轮的规则查询次数已用完（每轮最多 {RuleLookupLimit} 次）。要查的下一轮再说，或直接用回执里已有的信息。");
        }

        var query = Text(call.Arguments, "query");

        if (string.IsNullOrWhiteSpace(query))
        {
            return new FunctionResultContent(
                call.CallId,
                "query 必填：给名字（中文 / 英文 / 部分词都行），例如「火球术」「Fireball」「火球」。");
        }

        var kind = RulesLookup.ParseKind(Text(call.Arguments, "kind"), out var kindError);

        if (kindError is not null)
        {
            return new FunctionResultContent(call.CallId, kindError);
        }

        _ruleLookupsThisRound++;
        var hits = RulesLookup.Search(_rules, query, kind);

        return new FunctionResultContent(
            call.CallId,
            RulesLookup.Describe(hits, query.Trim(), kind, Flag(call.Arguments, "detail")));
    }

    /// <summary>查怪物模板（§27）：<b>只有说书人</b>能调到（角色工具表里没有这个名字）。</summary>
    private FunctionResultContent LookupMonsterAsync(FunctionCallContent call)
    {
        if (_rules is null)
        {
            return new FunctionResultContent(call.CallId, "规则包未加载（rules-data.json）：这次查不了怪物。");
        }

        if (_ruleLookupsThisRound >= RuleLookupLimit)
        {
            return new FunctionResultContent(
                call.CallId,
                $"本轮的规则查询次数已用完（每轮最多 {RuleLookupLimit} 次）。要查的下一轮再说。");
        }

        var query = Text(call.Arguments, "query");

        if (string.IsNullOrWhiteSpace(query))
        {
            return new FunctionResultContent(
                call.CallId,
                "query 必填：给怪物名字（中文 / 英文 / 部分词都行），例如「地精」「Goblin」。");
        }

        _ruleLookupsThisRound++;

        return new FunctionResultContent(
            call.CallId,
            RulesLookup.DescribeMonster(_rules, query.Trim(), Flag(call.Arguments, "detail")));
    }

    /// <summary>回忆落盘记录（§2）：一轮一次，只读。</summary>
    private FunctionResultContent RecallStoryAsync(string participantId, FunctionCallContent call)
    {
        if (_storyRecall is null)
        {
            return new FunctionResultContent(call.CallId, "这一场没有接上剧情记录：这次回忆不了。");
        }

        if (_storyRecallsThisRound >= 1)
        {
            return new FunctionResultContent(
                call.CallId,
                "本轮已经回忆过一次了。先按拿到的记录写；这一轮不要再查，下一轮再说。");
        }

        _storyRecallsThisRound++;

        var kinds = Strings(call.Arguments, "kinds");

        var query = new StoryRecallQuery
        {
            Text = Text(call.Arguments, "query"),
            Author = Text(call.Arguments, "author"),
            Kinds = kinds,
            SinceRevision = (long)(Number(call.Arguments, "since") ?? 0),
            Last = (int)(Number(call.Arguments, "last") ?? 10),
        };

        return new FunctionResultContent(call.CallId, _storyRecall.RecallStory(participantId, query));
    }

    /// <summary>布尔参数（detail）：模型给的多半是 JSON true / false，测试里可能给字符串。</summary>
    private static bool Flag(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        switch (value)
        {
            case bool flag:
                return flag;
            case JsonElement { ValueKind: JsonValueKind.True }:
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                return false;
            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var text = element.GetString();
                return text is not null && bool.TryParse(text.Trim(), out var parsed) && parsed;
            }

            case string text:
                return bool.TryParse(text.Trim(), out var parsedText) && parsedText;
            default:
                return false;
        }
    }

    private const string NotConnected = "地图 / 骰子还没接入（vtt.enabled = false）：这一轮按没有地图与骰子处理，别自己估算。";

    private static IReadOnlyList<string> Strings(IDictionary<string, object?>? arguments, string key) =>
        Typed<string>(arguments, key);

    /// <summary>
    /// 数组参数取值：模型给的是 JSON 数组（<see cref="JsonElement"/>），测试与脚本里常直接给 JSON 文本或强类型集合。
    /// 三种都收；其它形状一律当参数错误抛出去——宁可回灌一句"格式不对"，也不要静默丢掉一半变更。
    /// </summary>
    private static IReadOnlyList<T> Typed<T>(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.Deserialize<List<T>>(ArgumentOptions) ?? [],
            JsonElement { ValueKind: JsonValueKind.String } text => Parse<List<T>>(text.GetString()),
            JsonElement { ValueKind: JsonValueKind.Null } => [],
            string text => Parse<List<T>>(text),
            IEnumerable<T> typed => typed.ToArray(),
            _ => throw new JsonException($"{key} 期望一个数组"),
        };
    }

    private static T Parse<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "[]" : json, ArgumentOptions)
        ?? throw new JsonException("期望一个 JSON 数组");

    private static Task<FunctionResultContent> Result(FunctionCallContent call, string text) =>
        Task.FromResult(new FunctionResultContent(call.CallId, text));

    /// <summary>工具参数取值：模型给的多半是 JsonElement，测试里直接给字符串。</summary>
    private static string? Text(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>数值参数（半径 / DC）：模型给的多半是 JsonElement，测试里直接给数字或数字字符串。</summary>
    private static double? Number(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double number => number,
            int number => number,
            long number => number,
            float number => number,
            decimal number => (double)number,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element => ParseNumber(element.GetString()),
            string text => ParseNumber(text),
            _ => null,
        };

        static double? ParseNumber(string? text) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    /// <summary>把工具参数收敛成引擎要的参战者描述。</summary>
    private static CombatantSetup ToSetup(CombatantArgument argument) => new()
    {
        Id = argument.Id ?? string.Empty,
        Name = argument.Name,
        CnName = argument.CnName,
        Side = ParseSide(argument.Side),
        SheetId = argument.SheetId,
        TemplateId = argument.TemplateId,
        DexterityModifier = argument.Dex ?? argument.DexterityModifier ?? 0,
        SpeedFeet = argument.SpeedFeet ?? EncounterEngine.DefaultSpeedFeet,
        VttTokenId = argument.VttTokenId,
        Sheet = ParseSheet(argument.Sheet),
        Hp = argument.Hp,
        MaxHp = argument.MaxHp,
        Ac = argument.Ac,
    };

    /// <summary>参战者内联数值表（§26.2 第 2 层）：只收 JSON 对象；坏形状抛 <see cref="JsonException"/>，不静默丢。</summary>
    private static MeetingRoom.Core.Rules.Sheet? ParseSheet(System.Text.Json.JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }

        return obj.Deserialize<MeetingRoom.Core.Rules.Sheet>(SheetOptions)
            ?? throw new JsonException("sheet 解析结果为空");
    }

    /// <summary>单个对象参数（stakes / opposedBy）：模型多半给 JsonElement 对象。</summary>
    private static T? Single<T>(IDictionary<string, object?>? arguments, string key)
        where T : class
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize<T>(ArgumentOptions),
                JsonElement { ValueKind: JsonValueKind.String } text => JsonSerializer.Deserialize<T>(text.GetString() ?? "{}", ArgumentOptions),
                string text => JsonSerializer.Deserialize<T>(text, ArgumentOptions),
                T typed => typed,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>字符串数组参数（masteries）：接受 JSON 数组或单个字符串。</summary>
    private static IReadOnlyList<string>? TypedList(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element
                => element.Deserialize<string[]>(ArgumentOptions) ?? [],
            JsonElement { ValueKind: JsonValueKind.String } text
                => string.IsNullOrWhiteSpace(text.GetString()) ? [] : [text.GetString()!],
            string text => string.IsNullOrWhiteSpace(text) ? [] : [text],
            IEnumerable<string> typed => typed.ToArray(),
            _ => [],
        };
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static CombatActionKind ParseActionKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "cast_spell" or "cast" or "spell" => CombatActionKind.CastSpell,
            "move" => CombatActionKind.Move,
            "dash" => CombatActionKind.Dash,
            "dodge" => CombatActionKind.Dodge,
            "disengage" => CombatActionKind.Disengage,
            "hide" => CombatActionKind.Hide,
            "help" => CombatActionKind.Help,
            "ready" => CombatActionKind.Ready,
            _ => CombatActionKind.Attack,
        };

    /// <summary>
    /// 可见性解析：枚举优先，同时<b>兼容旧字符串</b>（<c>"public"</c> / <c>"secret"</c>，大小写不敏感）——
    /// 脚本、旧记录与模型给的字符串都还能用。
    /// </summary>
    private static CheckVisibility ParseVisibility(string? visibility) =>
        string.Equals(visibility?.Trim(), "secret", StringComparison.OrdinalIgnoreCase)
            ? CheckVisibility.Secret
            : CheckVisibility.Public;

    private static CheckKind ParseCheckKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "ability" => CheckKind.Ability,
            "contest" or "opposed" => CheckKind.Contest,
            "passive" => CheckKind.Passive,
            _ => CheckKind.Skill,
        };

    private static CombatSide ParseSide(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "party" or "ally" or "队伍" => CombatSide.Party,
        "foe" or "enemy" or "敌人" => CombatSide.Foe,
        _ => CombatSide.Neutral,
    };
}

/// <summary>工具参数里的一个参战者（模型可能写 dex 或 dexterityModifier，两个都收）。</summary>
internal sealed record CombatantArgument
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? CnName { get; init; }

    public string? Side { get; init; }

    public int? Dex { get; init; }

    public int? DexterityModifier { get; init; }

    public int? SpeedFeet { get; init; }

    public string? SheetId { get; init; }

    /// <summary>
    /// 内联 HP（r10）：没有角色卡的敌人必须给这三个——否则开战会被 <c>combatants-need-hp</c> 拒。
    /// 会话层用它建最小表，之后的伤害 / 胜负校验就走 SheetStore。
    /// </summary>
    public int? Hp { get; init; }

    public int? MaxHp { get; init; }

    public int? Ac { get; init; }

    public string? TemplateId { get; init; }

    public string? VttTokenId { get; init; }

    /// <summary>这场战斗用的数值表（§26.2 第 2 层）：怪物 / 临时 NPC 直接给一份 JSON。</summary>
    public System.Text.Json.JsonElement? Sheet { get; init; }
}
/// <summary>检定后果参数（<c>{onSuccess, onFailure, onPartial}</c>）。</summary>
internal sealed record CheckStakesArgument(string? OnSuccess, string? OnFailure, string? OnPartial);

/// <summary>对抗检定的一方（<c>{actorId, skill, ability, modifier, ...}</c>）。</summary>
internal sealed record CheckOppositionArgument(
    string? ActorId,
    string? Skill,
    string? Ability,
    int? Modifier,
    int? AbilityModifier,
    int? ProficiencyBonus,
    bool? Proficient,
    bool? Expertise);

/// <summary>圆形区域参数（M6b）：<c>{type:"circle", center:[x,y], radiusCells:n}</c>。</summary>
internal sealed record FogShapeArgument(string? Type, double[]? Center, double? RadiusCells);

/// <summary>
/// 工具参数的**容错读取**（r15）：真实模型经常把结构化参数写成 JSON 字符串
/// （`shape = "{\"type\":\"circle\",...}"`），也可能把 `[gx,gy]` 写成 `{x,y}`。
/// <para>
/// 这些都不是"模型出错"，而是 function calling 里很常见的形态差异——所以读取要宽松，
/// 但**失败必须给可读回执**，绝不能抛异常（抛出去会让整回合静默消失）。
/// </para>
/// </summary>
public static class ToolArguments
{
    /// <summary>取一个参数的原始值：真值 → JSON 字符串 → 序列化后的 JsonElement。</summary>
    public static JsonElement? Raw(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        switch (value)
        {
            case JsonElement element:
                return element;

            case string text when !string.IsNullOrWhiteSpace(text):
                try
                {
                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }

            default:
                return JsonSerializer.SerializeToElement(value);
        }
    }

    /// <summary>格子数组：接受 `[[gx,gy],…]`、`[{x,y},…]`，以及它们的 JSON 字符串形式。</summary>
    public static IReadOnlyList<MeetingRoom.Core.Maps.GridCell>? ReadCells(
        IDictionary<string, object?> arguments,
        string key,
        out string? error)
    {
        error = null;

        if (Raw(arguments, key) is not { } node)
        {
            // 区分"没给这个参数"（合法，二选一）与"给了但解析不了"（要报错）。
            if (arguments.TryGetValue(key, out var provided) && provided is not null)
            {
                error = $"{key} 解析失败：应传 [[gx,gy],…]、[{{x,y}},…] 或它们的 JSON 字符串。";
            }

            return null;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            // 单个 {x,y} 也接受：`cells: {x:1,y:2}`
            return ParseCell(node, out error) is { } single ? [single] : null;
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            error = $"{key} 必须是数组：[[gx,gy],…] 或 [{{x,y}},…]。";
            return null;
        }

        var cells = new List<MeetingRoom.Core.Maps.GridCell>();

        foreach (var item in node.EnumerateArray())
        {
            if (ParseCell(item, out var problem) is { } cell)
            {
                cells.Add(cell);
            }
            else
            {
                error = $"cells 里有一项格式不对：{problem}";
                return null;
            }
        }

        return cells.Count > 0 ? cells : null;
    }

    /// <summary>圆形区域：接受对象或 JSON 字符串；`center` 可以是 `[x,y]` 或 `{x,y}`。</summary>
    public static MeetingRoom.Core.Maps.FogShape? ReadFogShape(
        IDictionary<string, object?> arguments,
        string key,
        out string? error)
    {
        error = null;

        if (Raw(arguments, key) is not { } node)
        {
            if (arguments.TryGetValue(key, out var provided) && provided is not null)
            {
                error = $"shape 解析失败，应传 {{\"type\":\"circle\",\"center\":[x,y],\"radiusCells\":2}}。";
            }

            return null;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            // 双重编码（字符串里还是字符串）——再拆一层。
            if (Raw(new Dictionary<string, object?> { [key] = node.GetString() ?? string.Empty }, key) is not { } inner ||
                inner.ValueKind != JsonValueKind.Object)
            {
                error = $"shape 解析失败，应传 {{\"type\":\"circle\",\"center\":[x,y],\"radiusCells\":2}}。";
                return null;
            }

            node = inner;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            error = $"shape 必须是对象：{{\"type\":\"circle\",\"center\":[x,y],\"radiusCells\":2}}。";
            return null;
        }

        var type = node.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String
            ? typeNode.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(type))
        {
            error = "shape.type 缺失：目前只支持 circle。";
            return null;
        }

        var center = new List<double>();

        if (node.TryGetProperty("center", out var centerNode))
        {
            if (centerNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in centerNode.EnumerateArray())
                {
                    if (item.TryGetDouble(out var value))
                    {
                        center.Add(value);
                    }
                }
            }
            else if (centerNode.ValueKind == JsonValueKind.Object &&
                centerNode.TryGetProperty("x", out var x) && x.TryGetDouble(out var cx) &&
                centerNode.TryGetProperty("y", out var y) && y.TryGetDouble(out var cy))
            {
                center.Add(cx);
                center.Add(cy);
            }
        }

        if (center.Count < 2)
        {
            error = "shape.center 需要两个数：[x,y]（图片像素坐标）。";
            return null;
        }

        var radius = node.TryGetProperty("radiusCells", out var radiusNode) && radiusNode.TryGetDouble(out var r) ? r : 2;

        return new MeetingRoom.Core.Maps.FogShape(type, center, radius);
    }

    private static MeetingRoom.Core.Maps.GridCell? ParseCell(JsonElement node, out string? problem)
    {
        problem = null;

        if (node.ValueKind == JsonValueKind.Array)
        {
            var pair = node.EnumerateArray().ToArray();

            if (pair.Length >= 2 && pair[0].TryGetInt32(out var gx) && pair[1].TryGetInt32(out var gy))
            {
                return new MeetingRoom.Core.Maps.GridCell(gx, gy);
            }

            problem = "数组项要两个整数 [gx,gy]。";
            return null;
        }

        if (node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty("x", out var x) && x.TryGetInt32(out var ox) &&
            node.TryGetProperty("y", out var y) && y.TryGetInt32(out var oy))
        {
            return new MeetingRoom.Core.Maps.GridCell(ox, oy);
        }

        problem = "要 [gx,gy] 或 {x,y}。";
        return null;
    }
}
