using Microsoft.Extensions.AI;

namespace MeetingRoom.Agent.Tools;

/// <summary>
/// 工具调度：step 循环<b>自己</b>决定何时执行工具（设计文档 §6.6）。
/// 绝不用会自动跑完工具循环的封装——它会把 step 边界藏起来，取消点与注入点就全失效了。
/// </summary>
public interface IToolDispatcher
{
    /// <summary>执行一次工具调用，返回要回灌给模型的结果。</summary>
    /// <param name="participantId">谁在调用——工具内的权限与可见性判定需要它（设计文档 §6.2）。</param>
    Task<FunctionResultContent> InvokeAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken);
}

/// <summary>
/// 还没注册任何工具时的实现：把"工具不可用"作为结果回给模型，而不是抛异常打断循环。
/// 真实的工具面（read_material / submit_material …）在阶段 4 落地。
/// </summary>
public sealed class NullToolDispatcher : IToolDispatcher
{
    public Task<FunctionResultContent> InvokeAsync(
        string participantId,
        FunctionCallContent call,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FunctionResultContent(call.CallId, $"工具 {call.Name} 尚未注册。"));
}
