using MeetingRoom.Core.Roleplay;

namespace MeetingRoom.Tests.Roleplay;

/// <summary>发言调度（设计文档 §11.4-1）：会议全员并行；叙事时序、一次一个。</summary>
public sealed class TurnSchedulerTests
{
    [Fact]
    public void ParallelSchedulerPlansEveryoneOnce()
    {
        var plan = new ParallelTurnScheduler().Plan(new SchedulingContext
        {
            Candidates = ["a", "b", "c"],
            Spoken = ["a"],
        });

        Assert.Equal(["b", "c"], plan.Select(turn => turn.ParticipantId));
    }

    [Fact]
    public void SequentialSchedulerPrefersMentionsThenDirectorThenCardOrder()
    {
        var scheduler = new SequentialTurnScheduler();

        var mentioned = scheduler.Plan(new SchedulingContext
        {
            Candidates = ["a", "b", "c"],
            Mentions = ["c"],
        });
        Assert.Equal("c", Assert.Single(mentioned).ParticipantId);

        var directed = scheduler.Plan(new SchedulingContext
        {
            Candidates = ["a", "b"],
            DirectorPick = "b",
        });
        Assert.Equal("b", Assert.Single(directed).ParticipantId);

        var byOrder = scheduler.Plan(new SchedulingContext { Candidates = ["a", "b"], Spoken = ["a"] });
        Assert.Equal("b", Assert.Single(byOrder).ParticipantId);
    }

    [Fact]
    public void SequentialSchedulerRemembersWhoAlreadySpoke()
    {
        var plan = new SequentialTurnScheduler().Plan(new SchedulingContext
        {
            Candidates = ["a", "b"],
            Spoken = ["a", "b"],
        });

        Assert.Empty(plan);
    }

    [Fact]
    public void SequentialSchedulerStopsAtTheSpeakerLimit()
    {
        var scheduler = new SequentialTurnScheduler(maxSpeakersPerTurn: 2);
        var spoken = new List<string>();
        string[] candidates = ["a", "b", "c", "d"];

        // 调用方按"Plan 返回空"判断本轮结束，所以上限必须在 Plan 里生效。
        while (true)
        {
            var plan = scheduler.Plan(new SchedulingContext { Candidates = candidates, Spoken = spoken });
            if (plan.Count == 0)
            {
                break;
            }

            spoken.Add(plan[0].ParticipantId);
        }

        Assert.Equal(new[] { "a", "b" }, spoken);
    }
}
