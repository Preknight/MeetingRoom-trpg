namespace MeetingRoom.Agent;

/// <summary>
/// step 边界可消费的中途注入源（steer 队列）。
/// 由 <see cref="AgentSession"/> 实现，<see cref="StepLoop"/> 在 step 边界调用（设计文档 §6.2）。
/// </summary>
public interface ISteerSource
{
    /// <summary>取走全部待消费的注入；没有则返回空。</summary>
    IReadOnlyList<string> Drain();
}
