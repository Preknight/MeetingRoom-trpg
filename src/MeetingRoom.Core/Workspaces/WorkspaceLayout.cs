namespace MeetingRoom.Core.Workspaces;

/// <summary>
/// 一个工作空间内部的目录布局（设计方案 §15.2 / §15.8）。
/// <para>
/// 与 <see cref="Paths.PathLayout"/> 的分工：那边是**安装级**（程序本体、配置、日志、程序自己的 <c>appdata/</c>），
/// 这里是**用户数据**——用户在"新建工作空间"时选的那个目录里的结构。
/// 工作空间的路径随时可能变（切工作空间），所以这个类是<b>实例</b>，不是静态类。
/// </para>
/// </summary>
public sealed class WorkspaceLayout
{
    public WorkspaceLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>工作空间根（用户选的那个目录）。</summary>
    public string Root { get; }

    /// <summary>元信息：名字 + 类型 + 创建时间（<c>workspace.json</c>）。</summary>
    public string DescriptorFile => Path.Combine(Root, "workspace.json");

    /// <summary>角色卡：跨本工作空间的**所有会话**共享（§15.2 的硬规则 2）。</summary>
    public string RolesDir => Path.Combine(Root, "roles");

    /// <summary>已归档资料（§15.6 的上半区）。</summary>
    public string MaterialsDir => Path.Combine(Root, "materials");

    /// <summary>世界书条目（叙事型工作空间）。</summary>
    public string WorldBookDir => Path.Combine(Root, "worldbook");

    /// <summary>待整理区（§15.6 的下半区）：引导页导入的、用户随手输入的都先落这里。</summary>
    public string DraftsDir => Path.Combine(Root, "drafts");

    public string MeetingsDir => Path.Combine(Root, "meetings");

    public string StoriesDir => Path.Combine(Root, "stories");

    /// <summary>
    /// 图片资产（参考图 + 生成结果）：独立于会话，供用户手动加入角色卡（图片生成模块，设计方案 §三）。
    /// 放在工作空间级而不是会话级 —— 角色卡本来就在工作空间根、跨会话共享（§15.2 硬规则 2），
    /// 同层才好"手动加进角色卡"。
    /// </summary>
    public string ImagesDir => Path.Combine(Root, "images");

    /// <summary>战术板底图（F2）：<c>maps/&lt;id&gt;.webp|png</c> + <c>&lt;id&gt;.json</c>。
    /// **独立于 <c>images/</c>**：那里是生成的图，这里是用户导入的地图，混在一起列表会互相污染。</summary>
    public string MapsDir => Path.Combine(Root, "maps");

    /// <summary>某一场会话的目录：会议进 <c>meetings/</c>、叙事进 <c>stories/</c>（§15.2）。</summary>
    public string SessionDir(WorkspaceKind kind, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var parent = kind == WorkspaceKind.Roleplay ? StoriesDir : MeetingsDir;
        return Path.Combine(parent, sessionId);
    }

    /// <summary>把这个工作空间的骨架建出来（幂等：已存在的不动、不覆盖已有文件）。</summary>
    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     Root, RolesDir, MaterialsDir, WorldBookDir, DraftsDir, MeetingsDir, StoriesDir, ImagesDir, MapsDir,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>这个目录是不是已经是（或看起来像一个）工作空间——只看有没有元信息文件。</summary>
    public bool Exists => File.Exists(DescriptorFile);
}
