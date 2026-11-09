using Microsoft.Extensions.AI;

namespace MeetingRoom.Agent.ChatClients;

/// <summary>
/// 按配置造 <see cref="IChatClient"/>。密钥只从 <see cref="ChatModelSettings.ApiKeyEnv"/> 指定的
/// 环境变量读，绝不写进配置文件（设计文档 §10.5 规则 4）。
/// </summary>
public interface IChatModelFactory
{
    IChatClient Create(ChatModelSettings settings);
}

/// <summary>
/// provider 连接参数。刻意不复用 Core 的配置类型：Agent 层不依赖 Core
/// （依赖方向始终是 Core → Agent，与设计文档 §三 的架构图一致），
/// 由 Host 把 <c>meetingroom.json</c> 的 provider 节映射成这里。
/// </summary>
public sealed record ChatModelSettings(string Name, string BaseUrl, string Model, string ApiKeyEnv);
