using Microsoft.Extensions.AI;

namespace MeetingRoom.Host.Meeting;

/// <summary>
/// 没有配置 provider 时的占位客户端：<b>开会时才报错</b>，服务本身照常启动——
/// 这样"没有 API key 也能起界面看演示数据"这条路径不会被破坏。
/// </summary>
internal sealed class UnconfiguredChatClient : IChatClient
{
    private readonly string _reason;

    public UnconfiguredChatClient(string reason) => _reason = reason;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(_reason);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(_reason);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
