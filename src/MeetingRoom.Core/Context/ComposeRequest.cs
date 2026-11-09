using MeetingRoom.Core.Orchestration;

namespace MeetingRoom.Core.Context;

/// <summary>
/// 一条待注入的资料索引：<see cref="Material"/> 用于可见性判定，<see cref="Line"/> 是要拼进共享节的文本行。
/// 分离两者是为了让"谁看得见"与"长什么样"互不耦合——渐进式披露只给标题+摘要+路径（设计文档 §5.3）。
/// </summary>
public sealed record MaterialIndexEntry(IMaterialView Material, string Line);

/// <summary>
/// 一条剧情历史消息（L2，三层上下文 §1）：<b>只追加、不修改</b>。
/// <para>
/// 它不进共享节（那是前缀），而是作为独立的对话消息排在共享节之后、角色节之前——
/// 每轮只是"把新的一条接在末尾"，前缀字节不变，缓存才真能命中。
/// </para>
/// </summary>
/// <param name="Revision">第几幕（<c>StoryTimeline.Revision</c>）。</param>
/// <param name="Author">谁说的（角色名 / 旁白）。</param>
/// <param name="Text">正文（已按 <c>lineChars</c> 截断）。</param>
public sealed record HistoryLine(long Revision, string Author, string Text)
{
    /// <summary>拼进上下文的一行文本（与指纹用同一份，避免"发出去的"和"算指纹的"不一致）。</summary>
    public string ToPromptText() => $"[第 {Revision} 幕] {Author}：{Text}";
}

/// <summary>
/// 一次 turn 的装配输入（turn 内固定）。共享节随会议推进变化，所以它在共享前缀里是唯一的变化点。
/// </summary>
public sealed record ComposeRequest
{
    /// <summary>会议公共节：议题 + 用户纠偏 + 常驻资料索引。</summary>
    public string SharedSection { get; init; } = string.Empty;

    /// <summary>
    /// 剧情历史（L2）：<b>摘要点之后到现在的全部发言</b>，只追加、不滚动。
    /// 会议模式留空（它没有剧情时间线）。
    /// </summary>
    public IReadOnlyList<HistoryLine> History { get; init; } = [];

    /// <summary>角色卡节：身份 / 背景 / 行为准则（各与会者不同）。</summary>
    public string RoleSection { get; init; } = string.Empty;

    /// <summary>候选资料索引（装配时按可见性过滤）。</summary>
    public IReadOnlyList<MaterialIndexEntry> Materials { get; init; } = [];

    /// <summary>
    /// 角色节的图像附件（M8b）：地图合成视图。<b>只进角色节</b>——共享节与系统提示一个字节都不能变，
    /// 否则前缀缓存（<c>PrefixFingerprint</c>）就废了。空列表 = 不发图（默认）。
    /// </summary>
    public IReadOnlyList<ImageAttachment> Images { get; init; } = [];
}

/// <summary>一张随角色节下发的图（M8b）：<paramref name="Caption"/> 写在图前做一句说明。</summary>
public sealed record ImageAttachment(byte[] Bytes, string MediaType = "image/png", string Caption = "");
