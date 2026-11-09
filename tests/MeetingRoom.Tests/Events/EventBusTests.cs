using System.Threading.Channels;
using MeetingRoom.Core.Events;

namespace MeetingRoom.Tests.Events;

/// <summary>
/// 事件总线（实现步骤 3.1）：Seq 单调、扇出不漏、慢订阅者只丢自己的旧事件。
/// </summary>
public sealed class EventBusTests
{
    [Fact]
    public async Task EverySubscriberReceivesAllEventsInSequence()
    {
        var bus = new EventBus();
        using var a = bus.Subscribe();
        using var b = bus.Subscribe();
        using var c = bus.Subscribe();

        for (var i = 1; i <= 100; i++)
        {
            bus.Publish("participant.delta", new { id = "product-manager", turnId = "turn-1", text = i.ToString() });
        }

        foreach (var subscription in new[] { a, b, c })
        {
            var received = await DrainAsync(subscription.Reader, 100);

            Assert.Equal(100, received.Count);
            Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), received.Select(e => e.Seq));
            Assert.All(received, e => Assert.Equal("participant.delta", e.Type));
        }

        Assert.Equal(100, bus.NextSeq);
    }

    [Fact]
    public void PayloadIsSerializedAsCamelCaseJson()
    {
        var bus = new EventBus();

        var ev = bus.Publish("material.reviewed", new { MaterialId = "mat-001", MaterialVersion = 3 });

        Assert.NotNull(ev.Payload);
        Assert.Equal("mat-001", ev.Payload!["materialId"]!.GetValue<string>());
        Assert.Equal(3, ev.Payload["materialVersion"]!.GetValue<int>());
    }

    [Fact]
    public void RecentKeepsOnlyTheNewestEntries()
    {
        var bus = new EventBus();
        for (var i = 1; i <= 20; i++)
        {
            bus.Publish("meeting.error", new { code = $"E{i}" });
        }

        var recent = bus.Recent(5);

        Assert.Equal(5, recent.Count);
        Assert.Equal(Enumerable.Range(16, 5).Select(i => (long)i), recent.Select(e => e.Seq));
    }

    [Fact]
    public void DisposedSubscriptionStopsReceiving()
    {
        var bus = new EventBus();
        var subscription = bus.Subscribe();
        bus.Publish("meeting.error", new { code = "E1" });
        subscription.Dispose();

        bus.Publish("meeting.error", new { code = "E2" });

        Assert.True(subscription.Reader.TryRead(out var only));
        Assert.Equal(1, only.Seq);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CancelledTokenUnsubscribes()
    {
        var bus = new EventBus();
        using var cts = new CancellationTokenSource();
        using var subscription = bus.Subscribe(cts.Token);

        cts.Cancel();
        bus.Publish("meeting.error", new { code = "after-cancel" });

        Assert.False(await subscription.Reader.WaitToReadAsync());
    }

    [Fact]
    public async Task SlowSubscriberDropsItsOwnOldestEventsWithoutBlocking()
    {
        var bus = new EventBus();
        using var slow = bus.Subscribe();
        using var fast = bus.Subscribe();

        var total = EventBus.SubscriberCapacity + 500;
        for (var i = 1; i <= total; i++)
        {
            bus.Publish("participant.delta", new { id = "p1", text = i.ToString() });
        }

        // 从不读取的订阅者只保留最新的 SubscriberCapacity 条（DropOldest），发布者始终不被阻塞。
        var kept = await DrainAsync(slow.Reader, EventBus.SubscriberCapacity);
        Assert.Equal(EventBus.SubscriberCapacity, kept.Count);
        Assert.Equal(total - EventBus.SubscriberCapacity + 1, kept[0].Seq);

        Assert.Equal(total, kept[^1].Seq);

        // 另一个订阅者同样只保留最后一段；DropOldest 丢的是最旧的，末条必然还在。
        var otherKept = await DrainAsync(fast.Reader, EventBus.SubscriberCapacity);
        Assert.Equal(total, otherKept[^1].Seq);
    }

    private static async Task<List<MeetingEvent>> DrainAsync(ChannelReader<MeetingEvent> reader, int count)
    {
        var list = new List<MeetingEvent>(count);
        while (list.Count < count && await reader.WaitToReadAsync())
        {
            while (list.Count < count && reader.TryRead(out var ev))
            {
                list.Add(ev);
            }
        }

        return list;
    }
}
