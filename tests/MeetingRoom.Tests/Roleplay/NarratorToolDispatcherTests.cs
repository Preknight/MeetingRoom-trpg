using System.Text.Json;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Roleplay;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 说书人工具面（设计文档 §11.4-5 / §11.4 第 6b 项）：权限在工具内部判定、参数与空变更各有拒绝路径，
/// 以及"工具集与提示词对账"——改了工具忘了改提示，说书人就会去调一个不存在的工具。
/// </summary>
public sealed class NarratorToolDispatcherTests
{
    [Fact]
    public void TheToolSetIsExactlyWhatThePromptDeclares()
    {
        var (dispatcher, _) = New();

        Assert.Equal(
            Narrator.ToolNames.OrderBy(name => name, StringComparer.Ordinal),
            dispatcher.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));

        // 提示词里必须能找到每一个工具名。
        var prompt = Narrator.BuildSystemPrompt("雨夜的小镇", "钟楼下", string.Empty);
        Assert.All(Narrator.ToolNames, tool => Assert.Contains(tool, prompt, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Narrator.ProposeWorldState)]
    [InlineData(Narrator.NudgeCharacter)]
    [InlineData(Narrator.IdleBeat)]
    [InlineData(Narrator.DeclareSceneEnd)]
    public async Task NpcsCannotCallTheNarratorTools(string tool)
    {
        var (dispatcher, narrator) = New();

        var result = await dispatcher.InvokeAsync(
            "ella",
            new FunctionCallContent("call-1", tool, new Dictionary<string, object?> { ["reason"] = "随便" }),
            CancellationToken.None);

        Assert.Contains("只有说书人", TextOf(result), StringComparison.Ordinal);
        // 越权连副作用都没有：提议、点名、描写、收束一个都没发生。
        Assert.Empty(narrator.Proposals);
        Assert.Empty(narrator.Nudges);
        Assert.Empty(narrator.Beats);
        Assert.Empty(narrator.Endings);
    }

    [Fact]
    public async Task UnknownToolIsRejected()
    {
        var (dispatcher, _) = New();

        var result = await dispatcher.InvokeAsync(
            "narrator",
            new FunctionCallContent("call-1", "set_world_state"),
            CancellationToken.None);

        Assert.Contains("未知工具", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyProposalIsRejectedBeforeItReachesTheNarrator()
    {
        var (dispatcher, narrator) = New();

        var result = await CallAsync(dispatcher, Narrator.ProposeWorldState, new Dictionary<string, object?>
        {
            ["reason"] = "想变点什么",
        });

        Assert.Contains("什么都没变", TextOf(result), StringComparison.Ordinal);
        Assert.Empty(narrator.Proposals);
    }

    [Fact]
    public async Task AMalformedArrayArgumentIsReportedInsteadOfSilentlyDropped()
    {
        var (dispatcher, narrator) = New();

        // attributes 给了一个对象而不是数组：宁可回灌"格式不对"，也不要静默丢掉这次变更。
        var result = await CallAsync(dispatcher, Narrator.ProposeWorldState, new Dictionary<string, object?>
        {
            ["reason"] = "提高好感",
            ["attributes"] = JsonDocument.Parse("""{"character":"ella","key":"好感","delta":5}""").RootElement,
        });

        Assert.Contains("参数格式不对", TextOf(result), StringComparison.Ordinal);
        Assert.Empty(narrator.Proposals);
    }

    [Fact]
    public async Task ProposalsCarryEveryFieldTheModelFilledIn()
    {
        var (dispatcher, narrator) = New();

        await CallAsync(dispatcher, Narrator.ProposeWorldState, new Dictionary<string, object?>
        {
            ["time"] = "当晚 21:00",
            ["place"] = "灯塔顶层",
            ["enter"] = new[] { "ella" },
            ["leave"] = new[] { "ran" },
            ["attributes"] = JsonDocument
                .Parse("""[{"character":"ella","key":"好感","delta":5}]""").RootElement,
            ["items"] = JsonDocument
                .Parse("""[{"character":"ella","item":"钥匙","remove":false}]""").RootElement,
            ["knowledge"] = JsonDocument
                .Parse("""[{"character":"ella","fact":"灯塔看守人不是意外坠塔"}]""").RootElement,
            ["reason"] = "他们上了楼",
        });

        var proposal = Assert.Single(narrator.Proposals);

        Assert.Equal("当晚 21:00", proposal.Mutation.Time);
        Assert.Equal("灯塔顶层", proposal.Mutation.Place);
        Assert.Equal(new[] { "ella" }, proposal.Mutation.Enter);
        Assert.Equal(new[] { "ran" }, proposal.Mutation.Leave);
        var attribute = Assert.Single(proposal.Mutation.Attributes);
        Assert.Equal("好感", attribute.Key);
        Assert.Equal(5, attribute.Delta);
        Assert.Equal("钥匙", Assert.Single(proposal.Mutation.Items).Item);
        Assert.Equal("灯塔看守人不是意外坠塔", Assert.Single(proposal.Mutation.Knowledge).Fact);
        Assert.Equal("他们上了楼", proposal.Reason);
    }

    [Fact]
    public async Task MissingReasonFallsBackToAGenericOne()
    {
        var (dispatcher, narrator) = New();

        await CallAsync(dispatcher, Narrator.ProposeWorldState, new Dictionary<string, object?>
        {
            ["place"] = "灯塔顶层",
        });

        Assert.Equal("剧情推进", Assert.Single(narrator.Proposals).Reason);
    }

    [Fact]
    public async Task NudgeNeedsBothACharacterAndATask()
    {
        var (dispatcher, narrator) = New();

        var result = await CallAsync(dispatcher, Narrator.NudgeCharacter, new Dictionary<string, object?>
        {
            ["characterId"] = "ella",
        });

        Assert.Contains("缺少", TextOf(result), StringComparison.Ordinal);
        Assert.Empty(narrator.Nudges);
    }

    [Fact]
    public async Task NudgeBeatAndSceneEndReachTheNarrator()
    {
        var (dispatcher, narrator) = New();

        await CallAsync(dispatcher, Narrator.NudgeCharacter, new Dictionary<string, object?>
        {
            ["characterId"] = "ella",
            ["task"] = "说说你在灯塔上看到了什么",
        });

        await CallAsync(dispatcher, Narrator.IdleBeat, new Dictionary<string, object?>
        {
            ["hint"] = "风把门吹开了",
        });

        await CallAsync(dispatcher, Narrator.DeclareSceneEnd, new Dictionary<string, object?>
        {
            ["reason"] = "这一夜的事已经讲完",
        });

        Assert.Equal(("ella", "说说你在灯塔上看到了什么"), Assert.Single(narrator.Nudges));
        Assert.Equal("风把门吹开了", Assert.Single(narrator.Beats));
        Assert.Equal("这一夜的事已经讲完", Assert.Single(narrator.Endings));
    }

    private static (NarratorToolDispatcher Dispatcher, RecordingNarrator Narrator) New()
    {
        var narrator = new RecordingNarrator();
        return (new NarratorToolDispatcher(narrator, id => string.Equals(id, "narrator", StringComparison.Ordinal)), narrator);
    }

    private static Task<FunctionResultContent> CallAsync(
        NarratorToolDispatcher dispatcher,
        string tool,
        IDictionary<string, object?>? arguments = null) =>
        dispatcher.InvokeAsync("narrator", new FunctionCallContent("call-1", tool, arguments), CancellationToken.None);

    private static string TextOf(FunctionResultContent result) => result.Result?.ToString() ?? string.Empty;

    /// <summary>记账用的假说书人：只记录被请求了什么，不碰任何真实状态。</summary>
    private sealed class RecordingNarrator : INarratorActions
    {
        public List<(WorldStateMutation Mutation, string Reason)> Proposals { get; } = [];

        public List<(string CharacterId, string Task)> Nudges { get; } = [];

        public List<string?> Beats { get; } = [];

        public List<string> Endings { get; } = [];

        public List<IReadOnlyList<InteractionQuestion>> Asks { get; } = [];

        public NarratorActionOutcome Outcome { get; set; } = new(true, "已受理。");

        public Task<NarratorActionOutcome> ProposeRevealFogAsync(
            IReadOnlyList<MeetingRoom.Core.Maps.GridCell>? cells,
            MeetingRoom.Core.Maps.FogShape? shape,
            string reason,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NarratorActionOutcome(true, "（stub）迷雾提议"));

        public Task<NarratorActionOutcome> ProposeWorldStateAsync(
            WorldStateMutation mutation,
            string reason,
            CancellationToken cancellationToken)
        {
            Proposals.Add((mutation, reason));
            return Task.FromResult(Outcome);
        }

        public NarratorActionOutcome NudgeCharacter(string characterId, string task)
        {
            Nudges.Add((characterId, task));
            return Outcome;
        }

        public NarratorActionOutcome IdleBeat(string? hint)
        {
            Beats.Add(hint);
            return Outcome;
        }

        public NarratorActionOutcome DeclareSceneEnd(string reason)
        {
            Endings.Add(reason);
            return Outcome;
        }

        public Task<NarratorActionOutcome> AskUserAsync(
            IReadOnlyList<InteractionQuestion> questions,
            CancellationToken cancellationToken)
        {
            Asks.Add(questions);
            return Task.FromResult(Outcome);
        }
    }
}
