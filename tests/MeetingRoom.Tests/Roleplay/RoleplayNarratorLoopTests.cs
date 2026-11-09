using System.Runtime.CompilerServices;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Participants;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Roles;
using MeetingRoom.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>
/// 编排循环（设计文档 §11.4-5）：一轮 = 导演指令 → 在场角色时序接话 → <b>说书人复盘</b>
/// → 执行它登记的意图（点名 / 描写）→ 收幕。复盘每轮一次、意图执行有界，防止说书人把场面跑飞。
/// </summary>
public sealed class RoleplayNarratorLoopTests
{
    [Fact]
    public async Task EveryRoundEndsWithTheNarratorsClosing()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero));

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var lines = session.Timeline.Turns[^1].Lines;

        // 角色接话之后，说书人补了一段收束描写。
        Assert.Contains(lines, line => line.Author == "narrator");
        Assert.Contains(lines, line => line.Author == "ella");
    }

    [Fact]
    public async Task ANudgedActorSpeaksWithinTheSameTurn()
    {
        await using var session = NewSession(NarratorWhoNudges());

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var lines = session.Timeline.Turns[^1].Lines;

        // 复盘正文、被点名者的接话，都在同一幕里。
        Assert.Contains(lines, line => line.Text.Contains("说书人收了一句", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Text.Contains("冉说：门外有人。", StringComparison.Ordinal));
        Assert.Equal(0, session.PendingNarratorIntents);
    }

    [Fact]
    public async Task TheNarratorCannotRunAwayWithTheScene()
    {
        // 复盘一次点两个人：有界（默认每轮一拍），所以只执行第一个，剩下的留在队列里。
        await using var session = NewSession(NarratorWhoNudgesTwice());

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        var lines = session.Timeline.Turns[^1].Lines;

        // 有界：一轮里因复盘而多出来的接话最多一段——点名点再多也跑不出第二步。
        Assert.Single(
            lines,
            line => line.Text.Contains("冉说：门外有人。", StringComparison.Ordinal)
                || line.Text.Contains("艾拉说：我听见了。", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisablingBeatsFallsBackToDirectorDrivenOnly()
    {
        await using var session = NewSession(FakeChatClient.Text(1, TimeSpan.Zero), beats: 0);

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我推门进去。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        // 说书人不复盘：这一幕只有角色接话。
        Assert.DoesNotContain(session.Timeline.Turns[^1].Lines, line => line.Author == "narrator");
    }

    /// <summary>说书人在复盘里建议收束：本场停进 <c>Paused</c>，且这一轮剩下的意图不再执行。</summary>
    [Fact]
    public async Task ASuggestionToEndStopsTheScene()
    {
        await using var session = NewSession(NarratorWhoEndsTheScene());

        await session.StartAsync();
        await WaitForAsync(() => session.Revision >= 1, "开场");

        await session.SayAsync("我说完就走了。");
        await WaitForAsync(() => session.Revision >= 2, "导演回合");

        Assert.Equal(MeetingStatus.Paused, session.Status);

        // 只建议、不结束：本场还在，导演可以接着写。
        Assert.NotEqual(MeetingStatus.Ended, session.Status);
    }

    private static FakeChatClient NarratorWhoNudges() =>
        new((call, cancellationToken) => call switch
        {
            1 => Say("开场：雨下得很大。", cancellationToken),
            2 => Say("艾拉说她听到了脚步声。", cancellationToken),
            3 => Say("冉握紧了拳头。", cancellationToken),
            4 => FakeChatClient.ToolCallStream(
                Narrator.NudgeCharacter,
                new Dictionary<string, object?> { ["characterId"] = "ran", ["task"] = "说说你看到了什么" },
                cancellationToken),
            5 => Say("说书人收了一句：灯影晃了晃。", cancellationToken),
            _ => Say("冉说：门外有人。", cancellationToken),
        });

    private static FakeChatClient NarratorWhoNudgesTwice() =>
        new((call, cancellationToken) => call switch
        {
            1 => Say("开场：雨下得很大。", cancellationToken),
            2 => Say("艾拉说她听到了脚步声。", cancellationToken),
            3 => Say("冉握紧了拳头。", cancellationToken),
            4 => FakeChatClient.ToolCallStream(
                Narrator.NudgeCharacter,
                new Dictionary<string, object?> { ["characterId"] = "ran", ["task"] = "说说你看到了什么" },
                cancellationToken),
            5 => Say("说书人收了一句：灯影晃了晃。", cancellationToken),
            6 => FakeChatClient.ToolCallStream(
                Narrator.NudgeCharacter,
                new Dictionary<string, object?> { ["characterId"] = "ella", ["task"] = "你也说说" },
                cancellationToken),
            7 => Say("冉说：门外有人。", cancellationToken),
            _ => Say("艾拉说：我听见了。", cancellationToken),
        });

    private static FakeChatClient NarratorWhoEndsTheScene() =>
        new((call, cancellationToken) => call switch
        {
            1 => Say("开场：雨下得很大。", cancellationToken),
            2 => Say("艾拉说她听到了脚步声。", cancellationToken),
            3 => Say("冉握紧了拳头。", cancellationToken),
            4 => FakeChatClient.ToolCallStream(
                Narrator.DeclareSceneEnd,
                new Dictionary<string, object?> { ["reason"] = "这一夜的事讲完了" },
                cancellationToken),
            _ => Say("说书人收了一句：灯影晃了晃。", cancellationToken),
        });

    private static async IAsyncEnumerable<ChatResponseUpdate> Say(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    private static RoleplaySession NewSession(IChatClient client, int beats = 1)
    {
        IReadOnlyList<RoleCard> cards =
        [
            new RoleCard { Id = "narrator", Name = "说书人", Role = ParticipantRole.Host, Body = "你控场，不出台词。" },
            new RoleCard { Id = "ella", Name = "艾拉", Body = "灯塔看守人的女儿。" },
            new RoleCard { Id = "ran", Name = "冉", Body = "路过的水手。" },
        ];

        return new RoleplaySession(
            new RoleplayOptions
            {
                StoryId = "test",
                World = "一座靠海的小镇，灯塔在夜里亮着。",
                Situation = "暴雨夜，灯塔看守人倒在塔下。",
                UserRole = "本场导演，兼旁白",
                NarratorId = "narrator",
                NarratorBeatsPerTurn = beats,
            },
            client,
            new EventBus(),
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
