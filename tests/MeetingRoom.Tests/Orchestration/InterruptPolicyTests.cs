using MeetingRoom.Core.Orchestration;

namespace MeetingRoom.Tests.Orchestration;

/// <summary>
/// 打断策略的合并窗口（设计文档 §6.2 / §13.1-4 / 实现步骤 7.2）：
/// 连续变更必须折成一次注入——每次软注入都必然造成一次前缀缓存 miss。
/// </summary>
public sealed class InterruptPolicyTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestsInsideTheWindowAreMergedIntoOne()
    {
        var policy = new InterruptPolicy(2000);

        policy.Request(new SteerNotice("r1", "第一条", Origin));
        policy.Request(new SteerNotice("r2", "第二条", Origin.AddMilliseconds(500)));
        policy.Request(new SteerNotice("r3", "第三条", Origin.AddSeconds(1)));

        // 窗口从最近一次请求重新计时，所以此刻还不到期。
        Assert.Null(policy.Drain(Origin.AddSeconds(1.5)));
        Assert.Equal(3, policy.PendingCount);

        var merged = policy.Drain(Origin.AddSeconds(3));

        Assert.NotNull(merged);
        Assert.Equal("r3", merged!.Reason);
        Assert.Contains("第一条", merged.Text, StringComparison.Ordinal);
        Assert.Contains("第二条", merged.Text, StringComparison.Ordinal);
        Assert.Contains("第三条", merged.Text, StringComparison.Ordinal);
        Assert.Equal(0, policy.PendingCount);
    }

    [Fact]
    public void DrainReturnsNothingBeforeTheFirstRequest()
    {
        var policy = new InterruptPolicy(2000);

        Assert.Null(policy.Drain(Origin.AddHours(1)));
        Assert.Null(policy.DrainImmediately());
    }

    [Fact]
    public void ZeroWindowDrainsOnTheNextTick()
    {
        var policy = new InterruptPolicy(0);
        policy.Request(new SteerNotice("r", "立刻", Origin));

        Assert.NotNull(policy.Drain(Origin));
        Assert.Equal(0, policy.PendingCount);
    }

    [Fact]
    public void DrainImmediatelyIgnoresTheWindow()
    {
        var policy = new InterruptPolicy(60_000);
        policy.Request(new SteerNotice("r", "关闭时的一次性应用", Origin.AddSeconds(10)));

        var drained = policy.DrainImmediately();

        Assert.NotNull(drained);
        Assert.Equal("关闭时的一次性应用", drained!.Text);
        Assert.Equal(0, policy.PendingCount);
    }
}
