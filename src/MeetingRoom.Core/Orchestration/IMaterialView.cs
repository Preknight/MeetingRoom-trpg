namespace MeetingRoom.Core.Orchestration;

/// <summary>
/// 资料在"可见性判定"中需要暴露的最小信息（设计文档 §11.3-2）。
/// 会议模式只用 Id/Author/Status；角色扮演模式将来按标签过滤（§11.4-3），届时扩展这个接口，
/// 不要改判定点的位置。
/// </summary>
public interface IMaterialView
{
    string Id { get; }

    string Author { get; }

    string Status { get; }

    /// <summary>
    /// 资料标签：叙事模式按角色"已知标签"过滤（§11.3-2、§11.4-3）；
    /// 会议模式忽略它（全员公开）。默认空 = 人人可见。
    /// </summary>
    IReadOnlyList<string> Tags => [];
}
