namespace MeetingRoom.Core.Config;

/// <summary>
/// 配置非法：拒绝启动并说明原因，绝不静默回退默认值（设计文档 §10.5 规则 3）。
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(IReadOnlyList<string> problems)
        : base(string.Join(Environment.NewLine, problems.Select(p => $"- {p}")))
    {
        Problems = problems;
    }

    /// <summary>逐条问题描述，便于调用方分类展示。</summary>
    public IReadOnlyList<string> Problems { get; }
}
