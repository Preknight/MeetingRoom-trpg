using System.Text.Json.Nodes;

namespace MeetingRoom.Core.Events;

/// <summary>
/// 一条领域事件。<see cref="Seq"/> 单调递增（写进 SSE 的 <c>id:</c> 字段），
/// <see cref="Type"/> 从一开始就带命名空间（<c>meeting.*</c> / <c>participant.*</c> / <c>material.*</c> /
/// <c>host.*</c>），前端只订阅自己认识的类型——这是双模式共存的前提（设计文档 §8.3、§11.3-5）。
/// </summary>
public sealed record MeetingEvent(long Seq, string Type, JsonNode? Payload, DateTimeOffset At);
