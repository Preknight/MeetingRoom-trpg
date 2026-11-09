using MeetingRoom.Core.Orchestration;

namespace MeetingRoom.Core.Materials;

/// <summary>
/// 资料卡（设计文档 §5.5 的一条记录）。这是模型可见的<b>投影</b>：
/// 只带标题 + 摘要 + 路径，正文从不随卡片进上下文（渐进式披露，§5.3）。
/// </summary>
public sealed record MaterialCard : IMaterialView
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    /// <summary>摘要来源：<c>tool</c>（与会者给的）或 <c>fellback</c>（程序截取正文兜底）。</summary>
    public string SummarySource { get; init; } = "tool";

    /// <summary>相对会议目录的正文路径（<c>materials/xxx.md</c>）——给 Agent 的是路径，不是正文。</summary>
    public required string Path { get; init; }

    public required string Author { get; init; }

    public MaterialStatus Status { get; init; } = MaterialStatus.Draft;

    /// <summary>提交时的资料版本，用于迟到产出判定（§6.3）。</summary>
    public int BasedOnVersion { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    public string? ReviewNote { get; init; }

    /// <summary>最近一次审核人（<c>host</c> 或 <c>user</c>）。</summary>
    public string? Reviewer { get; init; }

    /// <summary>可见性标签：叙事模式按角色的已知标签披露；会议模式一律忽略（§11.3-2）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>可见性判定用的状态取值（域内是枚举，跨层用 wire 字符串）。</summary>
    string IMaterialView.Status => Status.ToWire();
}
