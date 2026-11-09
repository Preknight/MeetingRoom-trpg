using MeetingRoom.Agent;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Fakes;

/// <summary>包一层真实装配器并记录每次发给模型的消息——用来断言"思考全文没有进上下文"。</summary>
internal sealed class RecordingComposer : IContextComposer
{
    private readonly IContextComposer _inner;

    public RecordingComposer(IContextComposer inner) => _inner = inner;

    /// <summary>每次 Compose 产生的完整消息序列，按调用顺序。</summary>
    public List<IReadOnlyList<ChatMessage>> SentMessages { get; } = [];

    public ComposedRequest Compose(
        string participantId,
        IReadOnlyList<ChatMessage> conversation,
        string turnInstruction)
    {
        var composed = _inner.Compose(participantId, conversation, turnInstruction);
        SentMessages.Add(composed.Messages);
        return composed;
    }
}
