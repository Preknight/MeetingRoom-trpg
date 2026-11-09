namespace MeetingRoom.Core.Interaction;

/// <summary>
/// 给模型用的提问形状（设计文档 §14.2）：<b>不要求它填 id</b>——那是程序的事，
/// 让模型多填一个字段只会多一个出错点。规范形状见 <see cref="InteractionQuestion"/>。
/// </summary>
public sealed record AskQuestion(
    string Prompt,
    AskOption[]? Options = null,
    string? DefaultOptionId = null);

/// <summary>
/// 给模型用的选项形状：<see cref="Description"/> <b>必填</b>——
/// 用户要凭这一行说明判断"选哪个"，只给标签等于让他猜（设计文档 §14.2）。
/// </summary>
public sealed record AskOption(
    string Label,
    string? Description = null,
    string? Id = null);
