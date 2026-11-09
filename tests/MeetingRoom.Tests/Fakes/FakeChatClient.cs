using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Fakes;

/// <summary>
/// 可控的假 <see cref="IChatClient"/>：第几次请求、产出什么、每块之间延迟多久都由调用方决定，
/// 用来<b>确定性</b>地验证三个取消点与 step 边界的注入消费（实现步骤 4.2 的验收）。
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<int, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _stream;
    private int _calls;

    public FakeChatClient(Func<int, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream) => _stream = stream;

    /// <summary>已经发起的流式请求次数。</summary>
    public int StreamCalls => Volatile.Read(ref _calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("step 循环只用流式接口。");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        return _stream(call, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>逐块吐正文；<paramref name="onChunk"/> 在每块产出前回调（用来注入取消或 steer）。</summary>
    public static FakeChatClient Text(int chunks, TimeSpan delay, Action<int>? onChunk = null) =>
        new((_, cancellationToken) => TextStream(chunks, delay, onChunk, cancellationToken));

    public static async IAsyncEnumerable<ChatResponseUpdate> TextStream(
        int chunks,
        TimeSpan delay,
        Action<int>? onChunk,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= chunks; i++)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            onChunk?.Invoke(i);
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"块{i}");
        }
    }

    /// <summary>先吐一段正文（并在首块前回调），再要一批工具调用——用来制造第二个 step。</summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> TextThenToolCalls(
        string text,
        int toolCallCount,
        Action? onFirstChunk,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        onFirstChunk?.Invoke();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        AIContent[] calls = [.. Enumerable.Range(1, toolCallCount)
            .Select(i => (AIContent)new FunctionCallContent($"call-{i}", "read_material"))];

        yield return new ChatResponseUpdate(ChatRole.Assistant, calls);
    }

    /// <summary>先吐思考（<see cref="TextReasoningContent"/>），再吐正文。</summary>
    public static FakeChatClient ThoughtThenText(string thought, string text) =>
        new((_, cancellationToken) => ThoughtThenTextStream(thought, text, cancellationToken));

    private static async IAsyncEnumerable<ChatResponseUpdate> ThoughtThenTextStream(
        string thought,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(thought)]);
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    /// <summary>返回一个指定的工具调用（例如用 <c>nudge_member</c> 驱动主持人做决策）。</summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> ToolCallStream(
        string name,
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent($"call-{name}", name, arguments)]);
    }

    /// <summary>直接要一批工具调用（不产正文）。</summary>
    public static FakeChatClient ToolCalls(int count) =>
        new((_, cancellationToken) => ToolCallStream(count, cancellationToken));

    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallStream(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        AIContent[] calls = [.. Enumerable.Range(1, count)
            .Select(i => (AIContent)new FunctionCallContent($"call-{i}", "read_material"))];

        yield return new ChatResponseUpdate(ChatRole.Assistant, calls);
    }
}
