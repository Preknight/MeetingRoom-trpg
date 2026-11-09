using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace MeetingRoom.Core.Events;

/// <summary>
/// 进程内事件总线。每个订阅者一条<b>有界</b>通道（<see cref="BoundedChannelFullMode.DropOldest"/>）：
/// 慢客户端只丢自己的事件、拖不垮后端，重连时用全量快照校准即可。
/// </summary>
public sealed class EventBus : IEventBus
{
    /// <summary>单个订阅者的积压上限（事件条数）。</summary>
    public const int SubscriberCapacity = 1024;

    /// <summary>诊断用环形缓冲的容量。</summary>
    private const int RecentCapacity = 512;

    private readonly ConcurrentDictionary<Guid, Channel<MeetingEvent>> _subscribers = new();
    private readonly Queue<MeetingEvent> _recent = new();
    private readonly Lock _recentGate = new();
    private long _seq;

    public long NextSeq => Interlocked.Read(ref _seq);

    public MeetingEvent Publish(string type, object? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var ev = new MeetingEvent(
            Interlocked.Increment(ref _seq),
            type,
            payload is null ? null : JsonSerializer.SerializeToNode(payload, EventJson.Options),
            DateTimeOffset.UtcNow);

        Remember(ev);

        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(ev);
        }

        return ev;
    }

    public IEventSubscription Subscribe(CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<MeetingEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            // SSE 循环每轮并发等待"读者就绪"与"心跳到期"，被丢弃的那次等待仍可能完成，
            // 因此这里必须允许并发读者。
            SingleReader = false,
            SingleWriter = false,
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;

        return new Subscription(this, id, channel, cancellationToken);
    }

    public IReadOnlyList<MeetingEvent> Recent(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        lock (_recentGate)
        {
            return _recent.Skip(Math.Max(0, _recent.Count - count)).ToArray();
        }
    }

    private void Remember(MeetingEvent ev)
    {
        lock (_recentGate)
        {
            _recent.Enqueue(ev);
            while (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }
        }
    }

    private void Unsubscribe(Guid id, Channel<MeetingEvent> channel)
    {
        if (_subscribers.TryRemove(id, out _))
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed class Subscription : IEventSubscription
    {
        private readonly EventBus _bus;
        private readonly Guid _id;
        private readonly Channel<MeetingEvent> _channel;
        private readonly CancellationTokenRegistration _registration;
        private bool _disposed;

        public Subscription(EventBus bus, Guid id, Channel<MeetingEvent> channel, CancellationToken cancellationToken)
        {
            _bus = bus;
            _id = id;
            _channel = channel;
            _registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((Subscription)state!).Dispose(), this)
                : default;
        }

        public ChannelReader<MeetingEvent> Reader => _channel.Reader;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registration.Dispose();
            _bus.Unsubscribe(_id, _channel);
        }
    }
}
