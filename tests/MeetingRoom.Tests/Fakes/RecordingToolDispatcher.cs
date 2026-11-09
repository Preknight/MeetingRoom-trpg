using MeetingRoom.Agent.Tools;
using Microsoft.Extensions.AI;

namespace MeetingRoom.Tests.Fakes;

/// <summary>记录每次工具调用的假调度器（取消点 B 的验收靠它数调用次数）。</summary>
internal sealed class RecordingToolDispatcher : IToolDispatcher
{
    private readonly Action<int>? _onInvoke;

    public RecordingToolDispatcher(Action<int>? onInvoke = null) => _onInvoke = onInvoke;

    /// <summary>被调用过的工具名，按顺序。</summary>
    public List<string> Invocations { get; } = [];

    /// <summary>每次调用声明的调用者（用来断言工具确实拿到了与会者身份）。</summary>
    public List<string> Participants { get; } = [];

    public Task<FunctionResultContent> InvokeAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        Participants.Add(participantId);
        Invocations.Add(call.Name);
        _onInvoke?.Invoke(Invocations.Count);
        return Task.FromResult(new FunctionResultContent(call.CallId, $"{call.Name} 的结果"));
    }
}
