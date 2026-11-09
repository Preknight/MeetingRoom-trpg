using System.Threading.Channels;

namespace MeetingRoom.Core.Events;

/// <summary>
/// 事件扇出中心：全部状态变化都从这里出去，Host 只把它接到唯一的一条 SSE（设计文档 §8.1）。
/// </summary>
public interface IEventBus
{
    /// <summary>已分配的最大序号（诊断用）。</summary>
    long NextSeq { get; }

    /// <summary>发布一条事件：分配 Seq、序列化载荷、扇出给全部订阅者。</summary>
    MeetingEvent Publish(string type, object? payload);

    /// <summary>订阅事件。令牌取消或 Dispose 时注销。</summary>
    IEventSubscription Subscribe(CancellationToken cancellationToken = default);

    /// <summary>最近的若干条事件（环形缓冲，供诊断与重连校准）。</summary>
    IReadOnlyList<MeetingEvent> Recent(int count);
}

/// <summary>一个订阅者的读取端。</summary>
public interface IEventSubscription : IDisposable
{
    ChannelReader<MeetingEvent> Reader { get; }
}

/// <summary>
/// 全量状态快照的来源。任何一次重连都先推一份 <c>meeting.state</c>，前端据此无歧义重建 store，
/// 因此服务端不做逐条补发（设计文档 §8.4）。
/// </summary>
public interface IMeetingStateSource
{
    /// <summary>构造 <c>meeting.state</c> 事件的载荷（status / materialVersion / participants / materials）。</summary>
    object Snapshot();
}

/// <summary>
/// 叙事（角色扮演）会话的全量快照来源。与 <see cref="IMeetingStateSource"/> 并列而不是合并：
/// 两种模式的快照结构不同（会议是资料 + 轮次，叙事是剧情时间线 + 世界状态），
/// 前端按 <c>roleplay.state</c> / <c>meeting.state</c> 分别重建（设计文档 §11.3-5、§11.6）。
/// </summary>
public interface IRoleplayStateSource
{
    /// <summary>构造 <c>roleplay.state</c> 事件的载荷；没有活跃会话时也要给出合法的空快照。</summary>
    object Snapshot();
}
