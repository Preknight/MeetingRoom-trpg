using System.Text;

namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// 说书人 Agent 的叙事化控场（设计文档 §11.4-5）。
/// 会议里主持人推进议程；叙事里**说书人**负责：转写导演（用户）的指令与旁白、理解他的意图、
/// 控场发言顺序、描写环境；他没有给新指令时按已定设定推进一拍。
/// <b>剧情决策不由说书人做</b>：世界状态 / 时空关系 / 收束要么由导演的指令触发，
/// 要么由说书人提议、经导演确认后才落盘（§11.4 第 6b 项）。
/// <para>
/// 提示词与工具名<b>出自同一处</b>——否则改了工具忘了改提示，说书人会去调一个不存在的工具。
/// 这也让"提示词与工具集对账"变成可测的（见 <c>NarrativeDirectorTests</c>）。
/// </para>
/// </summary>
public static class Narrator
{
    /// <summary>
    /// <b>提议</b>一次世界状态变更（时间、地点、进出场、属性、持有物、已知信息）：
    /// 会先问导演（用户），他同意——或没在期限内回答——才落盘（§11.4 第 6b 项）。
    /// </summary>
    public const string ProposeWorldState = "propose_world_state";

    /// <summary>提议揭示迷雾（M6b）：说书人不能自己揭，只能建议，由导演确认。</summary>
    public const string ProposeRevealFog = "propose_reveal_fog";

    /// <summary>点某个角色接话（维持节奏）。</summary>
    public const string NudgeCharacter = "nudge_character";

    /// <summary>导演没给新指令时，说书人自己描写一拍（环境变化、NPC 主动动作）。</summary>
    public const string IdleBeat = "idle_beat";

    /// <summary>本场收尾：说书人写一个收束并留一句建议，然后等导演（用户）决定下一步走向。</summary>
    public const string DeclareSceneEnd = "declare_scene_end";

    /// <summary>向导演（用户）提问：这是他的场子，岔路口该他拍板（设计文档 §14.2）。</summary>
    public const string AskUser = "ask_user";

    /// <summary>查地图：某个角色周围有谁、各在多远（《枭熊地图接入实现方案》V1 的 <c>look_around</c>）。</summary>
    public const string LookAround = "look_around";

    /// <summary>掷骰：数值只能由程序产生（《枭熊地图接入实现方案》V2 的 <c>dice_roll</c>）。</summary>
    public const string DiceRoll = "dice_roll";

    /// <summary>开战（《战斗托管设计》C1）：掷先攻、排顺序、进入回合制。</summary>
    public const string StartCombat = "start_combat";

    /// <summary>结束当前回合并把场面交给下一位（C1）。</summary>
    public const string NextTurn = "next_turn";

    /// <summary>结束整场战斗并记录胜负（C1）。</summary>
    public const string EndCombat = "end_combat";

    /// <summary>战斗内攻击 / 施法（C2）：武器与法术从规则包查，命中与伤害由程序掷。</summary>
    public const string CombatAction = "combat_action";

    /// <summary>叙事检定（C2）：属性 / 技能 / 对抗 / 被动，支持暗骰与 Stakes。</summary>
    public const string RequestCheck = "request_check";

    /// <summary>
    /// DM 覆盖（D3/§25）：<b>仅当导演在对话中明确要求</b>改数值或状态时调用；
    /// 不得用它绕过规则，<c>reason</c> 必须来自导演原话。
    /// </summary>
    public const string DmOverride = "dm_override";

    /// <summary>
    /// 查规则原文（§27）：法术 / 装备 / 状态 / 动作 / 技能 / 专长 / 职业 / 子职 / 可选特性。
    /// <b>只读知识</b>——查完不能自己结算伤害与结果，仍需走判定 / 战斗流程。
    /// </summary>
    public const string LookupRule = "lookup_rule";

    /// <summary>
    /// 回忆落盘的剧情记录（《叙事模式后台与上下文设计》§2）：只读，用于"忘掉前文时复查"。
    /// 角色与说书人都挂；<b>一轮一次</b>，否则模型会拿它代替记忆。
    /// </summary>
    public const string RecallStory = "recall_story";

    /// <summary>
    /// 查怪物模板（§27）：CR / 类型 / HP / AC / 六属性 / 特性与动作原文。
    /// <b>只给说书人</b>——角色看不到同类的 <c>lookup_monster</c>（不在 <see cref="CharacterToolNames"/> 里，
    /// 权限靠工具表结构保证，不靠运行时检查）。
    /// </summary>
    public const string LookupMonster = "lookup_monster";

    // ——— 角色侧工具（M9b）：只读 + 只请求，一个不多 ———

    /// <summary>脚下 / 相邻一格的地形与区域效果（按该角色视角）。</summary>
    public const string CheckTerrain = "check_terrain";

    /// <summary>**请求**一次判定：只落请求，不结算。</summary>
    public const string RequestJudgement = "request_judgement";

    /// <summary>**请求**一次战斗动作：只落请求，不结算。</summary>
    public const string RequestCombatAction = "request_combat_action";

    /// <summary>
    /// 角色能调的工具名（M9b 的白名单；§27 起是 6 个）。
    /// <b>反证用</b>：`request_check` / `combat_action` / `dm_override` / `propose_reveal_fog` /
    /// `lookup_monster` 都不在这里。
    /// </summary>
    public static readonly string[] CharacterToolNames =
    [
        LookAround,
        CheckTerrain,
        DiceRoll,
        RequestJudgement,
        RequestCombatAction,
        LookupRule,
        RecallStory,
    ];

    /// <summary>
    /// 系统提示前缀的版本号（§25.2）：改工具表 / 工具说明 / 提示段落时同步更新，
    /// 让"这一批导致一次 cache miss"可解释。它<b>不进入</b>提示文本本身，只用于诊断与文档对账。
    /// </summary>
    public const string PromptPrefixVersion = "p11-intent-dedup-2026-11-03";

    /// <summary>说书人可用的工具（NPC 一个都不带，§11.5-2）。</summary>
    /// <summary>
    /// <b>全部</b>合法工具名（说书人 + 角色侧）。只用于 <c>InvokeAsync</c> 的"这是不是个工具"判断；
    /// <b>不要</b>拼进系统提示——<see cref="ToolNames"/> 才是进提示的那份，它必须字节稳定。
    /// </summary>
    public static IReadOnlyList<string> AllToolNames =>
        [.. ToolNames, .. CharacterToolNames];

    public static IReadOnlyList<string> ToolNames =>
    [
        ProposeWorldState, NudgeCharacter, IdleBeat, DeclareSceneEnd, AskUser, LookAround, DiceRoll,
        StartCombat, NextTurn, EndCombat, CombatAction, RequestCheck, DmOverride, ProposeRevealFog,
        LookupRule, LookupMonster, RecallStory,
    ];

    /// <summary>控场职责（§11.4-5），与设计文档逐条对应。</summary>
    public static IReadOnlyList<string> Duties =>
    [
        "描述环境：把当前地点、时间与气氛用一两句写出来",
        $"切换时间地点：需要时用 {ProposeWorldState} 提议推进剧情内时间或换场景——它会先问导演，得到确认才生效",
        $"决定谁在场：用 {ProposeWorldState} 提议让角色进场或离场（同样要导演确认）",
        $"维持节奏：场面拖沓或卡住时用 {NudgeCharacter} 点人接话",
        $"导演没输入时自主推进：用 {IdleBeat} 描写一拍，不要把场面停住",
        $"该收束时：用 {DeclareSceneEnd} 说明理由并建议收束，然后停下等导演决定——收不收束不是你说了算；战斗进行中不要用它（会被拒绝）",
        $"导演在岔路口犹豫时：用 {AskUser} 直接问他（每个选项都要写一行说明），别替他拿主意",
        $"确认位置与距离：需要具体数字时用 {LookAround} 查（以枭熊地图与场景的网格规则为准），不要自己估算",
        $"需要判定时：用 {DiceRoll} 让程序掷（支持优势 / 劣势与多次攻击），不要把结果自己编出来",
        $"进入战斗：用 {StartCombat} 开战（先攻与顺序由程序算，不要自己排）；没有角色卡的怪要带上 hp / maxHp / ac（没 HP 就开不了战）",
        $"回合推进：一个人行动完就用 {NextTurn} 把场面交给下一位",
        $"战斗收场：胜负已定时用 {EndCombat} 记录结果并结束战斗",
        $"战斗中出手：用 {CombatAction} 结算攻击 / 施法（伤害骰与精通从规则包查，命中与伤害由程序掷，不要自己编）",
        $"需要叙事判定：用 {RequestCheck}（属性 / 技能 / 对抗 / 被动；暗骰的结果不会告诉角色）",
        $"改数值或状态：仅当导演在对话中明确要求时用 {DmOverride}（reason 必须是导演原话，不得用它给自己方便）；不要用它把人改成 0 HP",
        $"揭示迷雾：想让玩家看到某块区域时用 {ProposeRevealFog} 提议（导演同意才会生效；你只加「已探索」，不碰当前视野）",
        $"查规则原文：法术 / 装备 / 状态 / 动作 / 技能 / 专长 / 职业 / 子职 / 可选特性的确切文字用 {LookupRule}"
            + $"（只读知识，查完也不能自己结算伤害与结果，仍要走 {RequestCheck} / {CombatAction}）；"
            + $"怪物的模板数值与特性原文用 {LookupMonster}（只有你能用；那是模板基线，开战时你可按战况调 HP 与数量）",
        $"忘了前文：用 {RecallStory} 回忆落盘记录（关键词 / 作者 / 类型 / 从第几幕起，默认最近 10 条）"
            + "——它只在接不上前文时复查用，不要每次发言前都查，也不能改变已经发生的事",
    ];

    /// <summary>构造说书人的系统提示。世界状态快照由 <c>WorldStateStore.Snapshot</c> 提供。</summary>
    public static string BuildSystemPrompt(string world, string situation, string worldSnapshot)
    {
        var builder = new StringBuilder();

        builder.Append("你是本场的说书人：替导演（用户）把场面写出来，负责控场与转写，而不是替角色说话。");
        builder.Append('\n').Append('\n').Append("你的职责：");

        foreach (var duty in Duties)
        {
            builder.Append('\n').Append("- ").Append(duty);
        }

        builder.Append('\n').Append('\n').Append("世界：").Append(world);
        builder.Append('\n').Append("当前情境：").Append(situation);

        if (!string.IsNullOrWhiteSpace(worldSnapshot))
        {
            builder.Append('\n').Append('\n').Append("当前世界状态：");
            builder.Append('\n').Append(worldSnapshot);
        }

        builder.Append('\n').Append('\n').Append("可用工具：").Append(string.Join("、", ToolNames));
        builder.Append('\n').Append("工具调用不产生正文：你要推进剧情，必须在正文里写出来。");
        builder.Append('\n').Append("世界状态的变更只能提议：")
            .Append(ProposeWorldState).Append(" 会先问导演，他同意（或没在期限内回答）之后才生效。");
        builder.Append('\n').Append(
            "距离与位置以【地图】段为准：它给的是实数（N 格 / M 尺），不要自己估步数或“几步远”；"
            + "【地图】说隔墙时，就是隔墙。"
            + "【地图】不存在时，再按叙事惯例描述距离——不要编造地图数据。"
            + "骰子结果以工具回执为准，不要改写。");
        builder.Append('\n').Append("不要替导演或其他角色决定行动，也不要替角色写台词——那是角色自己的回合。");

        builder.Append('\n').Append('\n').Append("战斗与检定的调用时机：");
        builder.Append('\n').Append("- 何时开战：玩家明确进入对抗、且需要逐回合结算时才用 ").Append(StartCombat)
            .Append("；叙事冲突能用一次检定解决，就不要一律开战。");
        builder.Append('\n').Append("- 意图翻译：把角色 Agent 的意图翻成一条 ").Append(CombatAction)
            .Append("（目标、武器 / 法术、理由）；一次只提一个动作，不要堆成连环三招。"
                + "同一个 combat_action（同 kind、同目标、同关键参数）被拒后，同一条回复里不得原样重试——"
                + "要么换成合法动作，要么 next_turn；被拒时角色第一人称也不能先写成命中或成功。");
        builder.Append('\n').Append(
            "- 战斗中写受伤要分清两种：擦伤、流血、皮肉伤这类描写可以直接写；"
            + "但机制性结论——谁倒下、失去意识、死亡、HP 归零、彻底丧失行动能力——"
            + "必须由 combat_action 的伤害回执支持。拿不到回执时，只能写擦伤与侥幸脱开，"
            + "不能写倒下或“这一刀要了她的命”。");
        builder.Append('\n').Append("- 被拒怎么叙述：照 combat_action 回执里的原因写。"
            + "回执说“目标在 60 尺外，弯刀只能打到 5 尺”，正文就写这个距离差；"
            + "回执说“本回合的 Action 已经用过了”，正文就写她还没缓过手来——"
            + "而不是把这一下当成没砍中。也不要说“系统不允许”或“规则不允许”。");
        builder.Append('\n').Append("- 判定时机：只要结果不确定且失败有后果，就先 ").Append(RequestCheck)
            .Append("。战斗外用它；战斗中攻击与伤害走 ").Append(CombatAction)
            .Append("，而观察、判断、游说、兑定这类非攻击判定也走 ").Append(RequestCheck)
            .Append("，不得用 ").Append(DiceRoll).Append(" 或纯叙事代替。"
                + "要暗骰就传 visibility=Secret——正文不得出现数值、DC 或成败。"
                + "也不要为了检定而检定：不需要掷骰的叙述直接写。");
        builder.Append('\n').Append("- 一次一个判定：").Append(RequestCheck)
            .Append(" 一轮只提一个；等回执回来、正文写完，再决定要不要下一个。"
                + "同一个场面连提三个判定、或对同一件事反复掷，都是错的——那会把一次行动拆成三次运气。");
        builder.Append('\n').Append("- 战斗不要自己收场：").Append(StartCombat)
            .Append(" 之后，只有胜负已定（一方全部倒下或失去行动能力）或导演明确要求收场时，才用 ").Append(EndCombat)
            .Append("。导演这一轮没说话不等于战斗结束——他在听；此时用 ").Append(NextTurn)
            .Append(" 把场面交下去，或按当前行动者的意图用 ").Append(CombatAction).Append(" 推进。"
                + "用 dm_end 收场时，正文要引用导演原话（他哪一句说要收的）；导演这一轮没说话，就是没有收场指令。"
                + "victory / defeat 会被程序校验：还有敌人站着时调用会被拒，按回执继续打。");
        builder.Append('\n').Append("- 暗骰：只写角色能感知的部分；不要出现 DC、骰值或成败判定。");
        builder.Append('\n').Append("- 正文用中文：不要混入英文数字或术语（写“六十尺”，不要写 “sixty feet”）。");
        builder.Append('\n').Append("- 数值权威：HP、伤害、先攻、距离一律来自工具回执；不要自行加血、改命中或编造伤害。");
        builder.Append('\n').Append("- ").Append(DmOverride)
            .Append("：只在导演明确说“把它改成 X”时调用；不要用它绕过规则给自己方便，也不要替导演做决定。");

        return builder.ToString();
    }

    /// <summary>
    /// 一轮演完之后让说书人复盘（§11.4-5）：它<b>每轮只有一次</b>机会决定"现在最该做的一件事"，
    /// 做什么由工具表达——改世界状态要提议、点名 / 描写 / 建议收束各有其工具，都不需要就别调工具。
    /// </summary>
    public static string BuildReviewTask(string directorInput, IReadOnlyList<string> recentLines, long revision)
    {
        var builder = new StringBuilder();

        builder.Append("第 ").Append(revision).Append(" 幕演完了。");

        if (!string.IsNullOrWhiteSpace(directorInput))
        {
            builder.Append("导演这一句是：").Append(directorInput);
        }

        if (recentLines.Count > 0)
        {
            builder.Append('\n').Append('\n').Append("[这一幕发生的事]");

            foreach (var line in recentLines)
            {
                builder.Append('\n').Append("- ").Append(line);
            }
        }

        builder.Append('\n').Append('\n')
            .Append("请判断现在最该做的一件事：")
            .Append("时间 / 地点 / 在场 / 人物状态该变了，就用 ").Append(ProposeWorldState).Append(" 提议（会先问导演）；")
            .Append("场面卡住或有人该接话，就用 ").Append(NudgeCharacter).Append(" 点名；")
            .Append("只差一句环境描写，就用 ").Append(IdleBeat).Append("；")
            .Append("这一场该收束了，就用 ").Append(DeclareSceneEnd).Append("。");
        builder.Append('\n')
            .Append("都不需要就别调用工具：只写一两句收束描写，或者什么都不写。");

        return builder.ToString();
    }

    /// <summary>说书人自己描写一拍（由 <c>idle_beat</c> 意图触发）。</summary>
    public static string BuildBeatTask(string hint)
    {
        var builder = new StringBuilder();
        builder.Append("导演没有新指令。请自己描写一拍：只写环境变化与场上发生的事，不要替角色写台词。");

        if (!string.IsNullOrWhiteSpace(hint))
        {
            builder.Append('\n').Append("你想写的是：").Append(hint);
        }

        builder.Append('\n').Append("两三句即可，然后停下把场面交回。");
        return builder.ToString();
    }

    /// <summary>
    /// 开场任务（把开场交给说书人，而不是让角色抢跑）。
    /// 设定由用户给（对应会议里的"议题"），导演直接开写——不做开局预览与重掷（§11.4-8）。
    /// </summary>
    public static string BuildOpeningTask(
        string world,
        string situation,
        string userRole,
        IReadOnlyList<string> cast)
    {
        var lines = new List<string>
        {
            "本场刚开始。请写出这一场的开场（你自己不出台词，只写环境与在场角色的反应）。",
            $"世界：{world}",
            $"当前局面：{situation}",
        };

        if (cast.Count > 0)
        {
            lines.Add($"开场在场：{string.Join('、', cast)}");
        }

        if (!string.IsNullOrWhiteSpace(userRole))
        {
            lines.Add($"导演（用户）身份：{userRole}");
        }

        lines.Add("只写开场与在场角色的反应，不要替导演或其他角色决定行动；把场面交回导演。");
        return string.Join(Environment.NewLine, lines);
    }
}
