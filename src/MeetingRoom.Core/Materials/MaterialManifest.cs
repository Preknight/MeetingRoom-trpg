namespace MeetingRoom.Core.Materials;

/// <summary>
/// <c>manifest.json</c>（设计文档 §5.5）：会议状态 + 资料登记表 + <c>materialVersion</c>。
/// 它是"模型可见投影"的版本基准——原则 3 的判定全部以它为准。
/// </summary>
public sealed record MaterialManifest
{
    public string MeetingId { get; init; } = string.Empty;

    /// <summary>adopted 集合的版本号，每次采纳 +1。</summary>
    public int MaterialVersion { get; init; }

    public IReadOnlyList<MaterialCard> Materials { get; init; } = [];

    /// <summary>下一个资料编号（mat-001、mat-002 …）。</summary>
    public int NextMaterialNumber { get; init; } = 1;
}
