namespace MeetingRoom.Agent;

/// <summary>一轮运行的落盘记录（思考全文 / 正文）。</summary>
/// <param name="Kind">记录类型：<c>thought</c> 或 <c>speech</c>。</param>
/// <param name="ParticipantId">产生它的与会者。</param>
/// <param name="TurnId">所属 turn。</param>
/// <param name="Text">内容。</param>
public sealed record TurnRecord(string Kind, string ParticipantId, string TurnId, string Text);

/// <summary>
/// 宿主侧的一轮记录器（依赖倒置：Agent 不引用 Core，实现见 <c>MeetingRoom.Core.Persistence.TranscriptWriter</c>）。
/// <b>思考全文只在这里落盘、绝不再进上下文</b>——进上下文的只有程序生成的有界摘要（设计文档 §4.4）。
/// </summary>
public interface ITurnRecorder
{
    ValueTask RecordAsync(TurnRecord record, CancellationToken cancellationToken = default);
}
