using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MeetingRoom.Agent.ChatClients;

/// <summary>
/// OpenAI 兼容端点的 provider 工厂（DeepSeek 属此类）：只用底层 <see cref="IChatClient"/>，
/// <b>不套</b>任何自动工具循环装饰器——step 循环由我们自己调度（设计文档 §6.6）。
/// </summary>
public sealed class OpenAiCompatibleModelFactory : IChatModelFactory
{
    public IChatClient Create(ChatModelSettings settings)
    {
        var apiKey = Environment.GetEnvironmentVariable(settings.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"环境变量 {settings.ApiKeyEnv} 未设置，无法创建 provider“{settings.Name}”的客户端。"
                + "密钥只从环境变量读（配置文件里存的是变量名）。");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(settings.BaseUrl) });

        return client.GetChatClient(settings.Model).AsIChatClient();
    }
}
