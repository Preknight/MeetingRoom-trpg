namespace MeetingRoom.Core.Paths;

/// <summary>
/// 全部运行期路径的唯一起点：一律从 exe 所在目录派生（设计原则 1）。
/// 用 <see cref="Environment.ProcessPath"/> 而非 AppContext.BaseDirectory / 当前工作目录：
/// 实测结论见设计文档 §10.2——它在 SelfContained 目录形态与单文件形态下都指向真实 exe 位置，
/// 而 CurrentDirectory 取决于启动方式、不可采信。
/// </summary>
public static class PathLayout
{
    public static string InstallDir { get; } = ResolveInstallDir();

    public static string WwwRoot => Path.Combine(InstallDir, "wwwroot");

    public static string ConfigFile => Path.Combine(InstallDir, "meetingroom.json");

    public static string DataDir => Path.Combine(InstallDir, "data");

    public static string RolesDir => Path.Combine(DataDir, "roles");

    public static string MeetingsDir => Path.Combine(DataDir, "meetings");

    /// <summary>每场叙事（角色扮演）一个目录：剧情时间线落在 <c>story.jsonl</c>（§11.4-2）。</summary>
    public static string StoriesDir => Path.Combine(DataDir, "stories");

    public static string LogsDir => Path.Combine(InstallDir, "logs");

    /// <summary>
    /// 程序自己的状态目录——工作空间清单、上次打开的工作空间等（设计方案 §15.8）。
    /// <b>不是用户的资料</b>：用户的资料在工作空间里（见 <see cref="Workspaces.WorkspaceLayout"/>）。
    /// </summary>
    public static string AppDataDir => Path.Combine(InstallDir, "appdata");

    private static string ResolveInstallDir()
    {
        var processPath = Environment.ProcessPath;
        var dir = processPath is null ? null : Path.GetDirectoryName(processPath);

        return string.IsNullOrEmpty(dir)
            ? throw new InvalidOperationException(
                $"无法从 Environment.ProcessPath（实际值：{processPath ?? "<null>"}）推导安装目录。")
            : dir;
    }
}
