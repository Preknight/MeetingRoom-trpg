using System.Text.Json;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Interaction;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using MeetingRoom.Tests.Support;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Interaction;

/// <summary>
/// 交互模式（设计文档 §14.2 / §14.5.1）：<c>ask_user</c> 工具会把待决事项抛给用户，
/// 编排层则在每轮结束时按配置收口——"每轮必须问"由<b>程序</b>保证，而不是靠提示词。
/// </summary>
public sealed class AskUserTests
{
    [Fact]
    public void DescribeAnswersNamesWhatTheUserPicked()
    {
        var questions = AskUserSpec.DefaultRoundQuestions();
        var answered = new InteractionOutcome(
            "d1",
            [new InteractionAnswer("round", ["conclude"])],
            TimedOut: false,
            Skipped: false,
            SkipReason: null);

        var described = AskUserSpec.DescribeAnswers(answered, questions);

        Assert.Contains("用户选了", described, StringComparison.Ordinal);
        Assert.Contains("结束并出结论", described, StringComparison.Ordinal);

        var timedOut = AskUserSpec.DescribeAnswers(
            new InteractionOutcome("d2", [new InteractionAnswer("round", ["next"])], true, false, null),
            questions);

        Assert.Contains("超时未答，按默认", timedOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeFillsInIdsAndTheDefaultOption()
    {
        var normalized = AskUserSpec.Normalize(
        [
            new InteractionQuestion(string.Empty, "  要不要继续？  ", [new InteractionOption(string.Empty, "继续")]),
        ]);

        var question = Assert.Single(normalized);

        Assert.Equal("q1", question.Id);
        Assert.Equal("要不要继续？", question.Prompt);
        Assert.Equal("opt1", Assert.Single(question.Options).Id);
        Assert.Equal("opt1", question.DefaultOptionId);
    }

    [Fact]
    public async Task MissingOptionDescriptionIsReportedInsteadOfAsked()
    {
        var bus = new EventBus();
        await using var session = NewSession(
            bus,
            new InteractionOptions(),
            NarratorWhoAsks(
            [
                new AskQuestion("要不要继续？", [new AskOption("继续", null), new AskOption("停", null)]),
            ]));

        await session.StartAsync();
        await WaitForAsync(() => bus.Recent(200).Any(ev => ev.Type == "roleplay.turn"), "开场");

        // 选项缺 description 属于"没发出去"：用户这边看不到提问，说书人收到的是一句回执。
        Assert.DoesNotContain(bus.Recent(200), ev => ev.Type == "interaction.request");
    }

    [Fact]
    public async Task TooManyOptionsIsReportedInsteadOfAsked()
    {
        var bus = new EventBus();
        await using var session = NewSession(
            bus,
            new InteractionOptions { MaxOptions = 2 },
            NarratorWhoAsks(
            [
                new AskQuestion(
                    "选哪个？",
                    [
                        new AskOption("A", "第一条路"),
                        new AskOption("B", "第二条路"),
                        new AskOption("C", "第三条路"),
                    ]),
            ]));

        await session.StartAsync();
        await WaitForAsync(() => bus.Recent(200).Any(ev => ev.Type == "roleplay.turn"), "开场");

        Assert.DoesNotContain(bus.Recent(200), ev => ev.Type == "interaction.request");
    }

    [Fact]
    public async Task AWellFormedQuestionReachesTheDirector()
    {
        var bus = new EventBus();
        await using var session = NewSession(bus, new InteractionOptions(), NarratorWhoAsks(
        [
            new AskQuestion(
                "要不要继续查下去？",
                [
                    new AskOption("继续", "顺着这条线往下查", "keep"),
                    new AskOption("收手", "先把已知的写下来", "stop"),
                ],
                DefaultOptionId: "stop"),
        ]));

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "说书人提问");

        var request = bus.Recent(200).Last(ev => ev.Type == "interaction.request");
        var questions = request.Payload!["questions"]!;

        Assert.Equal("要不要继续查下去？", questions[0]!["prompt"]!.GetValue<string>());
        Assert.Equal(2, questions[0]!["options"]!.AsArray().Count);
        // 声明了默认选项就按它收口（用户不回答时走"收手"这条）。
        Assert.Equal(
            "stop",
            request.Payload!["defaultSelection"]![0]!["selected"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ForcingEveryRoundAsksEvenWhenTheModelDoesNot()
    {
        var bus = new EventBus();
        await using var session = NewSession(
            bus,
            new InteractionOptions { EveryRound = true, AllowSelfSkip = false });

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        var saying = session.SayAsync("我推门进去。");

        // 模型这一轮没提问，但配置要求"每轮必须问"——编排层用默认问题集兜底（§14.5.1）。
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "编排层兜底发问");

        var request = bus.Recent(200).Last(ev => ev.Type == "interaction.request");

        Assert.Contains(
            "接下来怎么走",
            request.Payload!["questions"]![0]!["prompt"]!.GetValue<string>(),
            StringComparison.Ordinal);

        Assert.True(session.AnswerPendingDecision(
            request.Payload!["id"]!.GetValue<string>(),
            [new InteractionAnswer("round", ["next"])]));

        await saying.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeetingStatus.Running, session.Status);
    }

    [Fact]
    public async Task SkippingLeavesATraceWithoutBlockingTheRound()
    {
        var bus = new EventBus();
        await using var session = NewSession(
            bus,
            new InteractionOptions { AllowSelfSkip = true, MaxConsecutiveSkips = 3 });

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        // 允许自己跳过：不留问题，但要留痕——"连着几轮没问我"必须看得见。
        Assert.Contains(bus.Recent(200), ev => ev.Type == "interaction.skipped");
        Assert.Equal(MeetingStatus.Running, session.Status);
    }

    [Fact]
    public async Task ConsecutiveSkipsForceAQuestion()
    {
        var bus = new EventBus();
        await using var session = NewSession(
            bus,
            new InteractionOptions { AllowSelfSkip = true, MaxConsecutiveSkips = 1 });

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        var saying = session.SayAsync("我推门进去。");

        // 连续跳过到上限：这一次不再是"跳过"，而是真的问。
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "连续跳过到上限后强制问一次");

        session.AnswerPendingDecision(
            bus.Recent(200).Last(ev => ev.Type == "interaction.request").Payload!["id"]!.GetValue<string>(),
            [new InteractionAnswer("round", ["deeper"])]);

        await saying.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisabledInteractionModeNeverAsks()
    {
        var bus = new EventBus();
        await using var session = NewSession(bus, new InteractionOptions { Enabled = false });

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        Assert.DoesNotContain(bus.Recent(200), ev => ev.Type == "interaction.request");
        Assert.DoesNotContain(bus.Recent(200), ev => ev.Type == "interaction.skipped");
    }

    /// <summary>问答要留档（设计文档 §14.6）：重启后还能回答"当时他选的是什么"。</summary>
    [Fact]
    public async Task TheAnswerIsWrittenToTheStoryJournal()
    {
        using var temp = new TempDir();
        var path = temp.File("story.jsonl");
        var bus = new EventBus();

        await using var session = NewSession(
            bus,
            new InteractionOptions(),
            NarratorWhoAsks(
            [
                new AskQuestion(
                    "要不要继续查下去？",
                    [
                        new AskOption("继续", "顺着这条线往下查", "keep"),
                        new AskOption("收手", "先把已知的写下来", "stop"),
                    ]),
            ]),
            path);

        await session.StartAsync();
        await WaitForAsync(() => session.Status == MeetingStatus.AwaitingInput, "说书人提问");

        session.AnswerPendingDecision(
            bus.Recent(200).Last(ev => ev.Type == "interaction.request").Payload!["id"]!.GetValue<string>(),
            [new InteractionAnswer("q1", ["keep"])]);

        await WaitForAsync(
            () => File.Exists(path)
                && File.ReadAllLines(path).Any(line => line.Contains("\"kind\":\"interaction\"", StringComparison.Ordinal)),
            "问答写进剧情日志");

        var written = File.ReadAllLines(path)
            .First(line => line.Contains("\"kind\":\"interaction\"", StringComparison.Ordinal));

        Assert.Contains("要不要继续查下去？", written, StringComparison.Ordinal);
        Assert.Contains("keep", written, StringComparison.Ordinal);
    }

    /// <summary>说书人在开场就调 <c>ask_user</c>：用真实会话走完整的校验与提问链路。</summary>
    private static FakeChatClient NarratorWhoAsks(AskQuestion[] questions) =>
        new((call, cancellationToken) => call == 1
            ? FakeChatClient.ToolCallStream(
                Narrator.AskUser,
                new Dictionary<string, object?>
                {
                    ["questions"] = JsonSerializer.SerializeToElement(questions),
                },
                cancellationToken)
            : FakeChatClient.TextStream(1, TimeSpan.Zero, null, cancellationToken));

    private static RoleplaySession NewSession(
        IEventBus bus,
        InteractionOptions interaction,
        IChatClient? client = null,
        string? storyPath = null)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                NarratorId = "narrator",
                Interaction = interaction,
                StoryPath = storyPath,
            },
            client ?? FakeChatClient.Text(1, TimeSpan.Zero),
            bus,
            cards);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"等待「{what}」超时。");
            }

            await Task.Delay(10);
        }
    }
}
