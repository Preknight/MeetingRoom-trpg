using Microsoft.Extensions.AI;

namespace MeetingRoom.Agent;

/// <summary>
/// 每 step 装配请求的抽象。接口定义在<b>消费方</b>（Agent）、实现放在 <c>MeetingRoom.Core</c>，
/// 这样依赖方向始终是 Core → Agent，与设计文档 §三 的架构图一致（Core 的 Participant 持有 AgentSession）。
/// </summary>
public interface IContextComposer
{
    /// <summary>装配一条完整的消息序列（共享前缀 + 角色卡节 + 私有历史 + 本轮任务）。</summary>
    /// <param name="participantId">当前与会者（可见性判定需要）。</param>
    /// <param name="conversation">该会话当前的私有历史（含本轮已消费的注入与工具往返）。</param>
    /// <param name="turnInstruction">本轮任务指令，始终放在最后。</param>
    ComposedRequest Compose(string participantId, IReadOnlyList<ChatMessage> conversation, string turnInstruction);
}

/// <summary>装配结果：消息序列 + 共享前缀指纹（诊断用，设计文档 §7.4）。</summary>
public sealed record ComposedRequest(IReadOnlyList<ChatMessage> Messages, string PrefixFingerprint);
