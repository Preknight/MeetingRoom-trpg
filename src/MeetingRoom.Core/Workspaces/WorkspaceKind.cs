using System.Text.Json.Serialization;

namespace MeetingRoom.Core.Workspaces;

/// <summary>
/// 工作空间的类型（设计方案 §15.2）：<b>创建时就定</b>，之后这个工作空间里只开这一种会话——
/// 侧栏用两个添加按钮表达这一点，不做"建完再选类型"。
/// </summary>
public enum WorkspaceKind
{
    /// <summary>会议型：只能开会。</summary>
    Meeting,

    /// <summary>叙事型：只能开叙事（角色扮演）。</summary>
    Roleplay,

    /// <summary>
    /// 过渡形态：安装目录下那份旧的 <c>data/</c>（两种会话都在里面，见设计方案 §15.8 的迁移说明）。
    /// 只为了让升级前的数据继续能用；**新建的工作空间一律是前两种之一**。
    /// </summary>
    Legacy,
}

/// <summary>工作空间的元信息（落在工作空间根的 <c>workspace.json</c>，注册表里也存一份）。</summary>
public sealed record WorkspaceDescriptor(
    string Path,
    WorkspaceKind Kind,
    string Name,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// 这个工作空间的目录布局（每次新建——它只是个路径计算器，很轻）。
    /// <b>不落盘</b>：从 <see cref="Path"/> 就能推出来，写进清单只会让文件随工作空间数量膨胀。
    /// </summary>
    [JsonIgnore]
    public WorkspaceLayout Layout => new(Path);

    /// <summary>类型在界面与配置里的写法（侧栏类型标识；JSON 那边由 <c>JsonStringEnumConverter</c> 负责）。</summary>
    [JsonIgnore]
    public string KindWire => WireOf(Kind);

    /// <summary>类型的线上写法。会话元信息（<c>session.json</c>）与工作空间清单共用这一份。</summary>
    public static string WireOf(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Meeting => "meeting",
        WorkspaceKind.Roleplay => "roleplay",
        WorkspaceKind.Legacy => "legacy",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>从 <c>meeting</c> / <c>roleplay</c> / <c>legacy</c> 读回类型（认不出来就按调用方给的兜底值）。</summary>
    public static WorkspaceKind ParseKind(string? wire, WorkspaceKind fallback = WorkspaceKind.Roleplay) => wire switch
    {
        "meeting" => WorkspaceKind.Meeting,
        "roleplay" => WorkspaceKind.Roleplay,
        "legacy" => WorkspaceKind.Legacy,
        _ => fallback,
    };
}
