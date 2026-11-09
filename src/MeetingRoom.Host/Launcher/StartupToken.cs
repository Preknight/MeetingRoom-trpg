using System.Security.Cryptography;

namespace MeetingRoom.Host.Launcher;

/// <summary>
/// 启动时生成的一次性 token：本地服务防同机其他程序跨源调用（设计文档 §10.4）。
/// 阶段 0 负责生成、打印并附在打开的 URL 上；端点侧的校验在接入 API 时启用。
/// </summary>
public static class StartupToken
{
    private const int DefaultByteLength = 16;

    public static string Generate(int byteLength = DefaultByteLength) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength)).ToLowerInvariant();
}
