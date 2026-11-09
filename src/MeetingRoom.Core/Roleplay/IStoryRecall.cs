namespace MeetingRoom.Core.Roleplay;

/// <summary>
/// <c>recall_story</c> 的数据面（《叙事模式后台与上下文设计》§2）：由会话层实现——
/// 它才拿得到 <c>StoryTimeline</c> 与"按角色视角过滤"的见证者视图。
/// <para>
/// 工具层只负责参数解析与限流；<b>查询本身不改变任何状态</b>。
/// </para>
/// </summary>
public interface IStoryRecall
{
    /// <summary>按 <paramref name="participantId"/> 的视角回忆（说书人看全量）。</summary>
    string RecallStory(string participantId, StoryRecallQuery query);
}
