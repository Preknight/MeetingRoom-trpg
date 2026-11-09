using MeetingRoom.Core.Participants;

namespace MeetingRoom.Core.Roles;

/// <summary>
/// 角色卡（设计文档 §5.4）：跨会议复用，与会议资料物理分离——换会议角色还在，
/// 改角色背景不影响历史会议资料。
/// </summary>
public sealed record RoleCard
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>与会者卡片上的"简述"。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>一句话摘要（渐进式披露：卡片只给标题 + 摘要）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary><c>host</c> 标记主持人 Agent。</summary>
    public ParticipantRole Role { get; init; } = ParticipantRole.Member;

    public string Model { get; init; } = string.Empty;

    public string Effort { get; init; } = string.Empty;

    /// <summary>
    /// 角色卡声明的工具白名单。注意硬约束：<b>工具集不按角色裁剪</b>（否则 tools hash 变化会破坏前缀缓存），
    /// 要限权就用工具内校验；这里的信息供工具内权限判断使用。
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// 枭熊（OBR）里这个角色的 token id（《枭熊地图接入实现方案》V1 的稳定映射）：
    /// 地图锚点先用它精确匹配 <c>token.id</c>，比"靠名字"稳——DM 改名、重名、中文名都不会断。
    /// <b>留空 = 退回现有的名字匹配</b>，老数据行为不变。
    /// </summary>
    public string VttTokenId { get; init; } = string.Empty;

    public bool ReadOnly { get; init; }

    /// <summary>背景知识 / 职责 / 行为准则 / 发言风格。只进该会话的角色卡节，绝不写进共享前缀。</summary>
    public string Body { get; init; } = string.Empty;
}
