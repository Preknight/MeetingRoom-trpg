using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeetingRoom.Core.Events;

namespace MeetingRoom.Core.Materials;

/// <summary>提交一份待审批资料。</summary>
public sealed record SubmitRequest
{
    public required string Author { get; init; }

    public required string Title { get; init; }

    /// <summary>与会者给的摘要；为空则由程序截取正文前 N 字兜底（§5.3）。</summary>
    public string? Summary { get; init; }

    /// <summary>正文文本；给出时由程序命名落盘（<c>materials/&lt;slug&gt;-&lt;seq&gt;.md</c>）。</summary>
    public string? Body { get; init; }

    /// <summary>正文相对路径（相对 <c>materials/</c>）；给定时按 §6.1 做路径防护。</summary>
    public string? BodyPath { get; init; }

    /// <summary>提交时的资料版本（迟到产出判定用，§6.3）。</summary>
    public int BasedOnVersion { get; init; }
}

/// <summary>
/// 资料库（设计文档 §4.2 / §5.5）：manifest + 正文文件 + 六态两级状态机 + <c>materialVersion</c>。
/// 三条硬约束：
/// <list type="number">
///   <item>采纳权在用户——主持人只能"通过/驳回"，越过用户直接采纳会被拒；</item>
///   <item>正文路径必须落在本会议的 <c>materials/</c> 内；</item>
///   <item>提交失败隔离（§11.3-4）：状态结算失败<b>不得</b>回滚已经写好的正文。</item>
/// </list>
/// manifest 读写全程持锁，多与会者并发提交/审核时串行化。
/// </summary>
public sealed class MaterialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MaterialSummaryPolicy _summaryPolicy;
    private readonly IEventBus? _bus;
    private MaterialManifest _manifest;

    public MaterialStore(string meetingDirectory, MaterialSummaryPolicy summaryPolicy, IEventBus? bus = null)
    {
        MeetingDirectory = Path.GetFullPath(meetingDirectory);
        MaterialsDirectory = Path.Combine(MeetingDirectory, "materials");
        ManifestPath = Path.Combine(MeetingDirectory, "manifest.json");
        _summaryPolicy = summaryPolicy;
        _bus = bus;

        Directory.CreateDirectory(MaterialsDirectory);

        var loaded = LoadManifest();
        if (loaded is null)
        {
            // manifest 一开始就落盘：它的存在即"这场会议已创建"的标志。
            _manifest = new MaterialManifest { MeetingId = Path.GetFileName(MeetingDirectory.TrimEnd(Path.DirectorySeparatorChar)) };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(_manifest, JsonOptions), new UTF8Encoding(false));
        }
        else
        {
            _manifest = loaded;
        }
    }

    public string MeetingDirectory { get; }

    public string MaterialsDirectory { get; }

    public string ManifestPath { get; }

    /// <summary>adopted 集合的版本号（原则 3 的判定基准）。</summary>
    public int Version => _manifest.MaterialVersion;

    /// <summary>资料索引（不含正文）。</summary>
    public IReadOnlyList<MaterialCard> List() => _manifest.Materials.ToArray();

    public MaterialCard? Find(string id) =>
        _manifest.Materials.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>提交一份资料：落正文 → 登记 manifest → 进入主持人初审。</summary>
    public async Task<MaterialCard> SubmitAsync(SubmitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("资料标题不能为空。", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ① 先把正文落盘：后面就算状态结算失败，正文也留在磁盘上（提交失败隔离，§11.3-4）。
            var (relativePath, body) = await WriteBodyAsync(request, cancellationToken).ConfigureAwait(false);

            var number = _manifest.NextMaterialNumber;
            var (summary, source) = _summaryPolicy.Resolve(request.Summary, body);

            var card = new MaterialCard
            {
                Id = $"mat-{number:D3}",
                Title = request.Title.Trim(),
                Summary = summary,
                SummarySource = source,
                Path = relativePath,
                Author = request.Author,
                // 提交这一步完成后即进入主持人初审（§4.2）。
                Status = MaterialStatus.HostReview,
                BasedOnVersion = request.BasedOnVersion,
                SubmittedAt = DateTimeOffset.UtcNow,
            };

            // ② 状态结算：只动 manifest；失败不回滚正文。
            var next = _manifest with
            {
                Materials = [.. _manifest.Materials, card],
                NextMaterialNumber = number + 1,
            };

            // 先落盘、成功后再切换内存状态：写盘失败时内存与磁盘保持一致，
            // 而正文已经写在磁盘上、不会被回滚（提交失败隔离，§11.3-4）。
            await SaveManifestAsync(next, ManifestPath, cancellationToken).ConfigureAwait(false);
            _manifest = next;

            Publish("material.submitted", Payload(card, _manifest.MaterialVersion));
            return card;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 审核/流转一份资料。非法转移与越权都不抛异常，而是返回带原因的
    /// <see cref="MaterialTransition"/>（这是可预期的领域结果）。
    /// </summary>
    public async Task<MaterialTransition> ReviewAsync(
        string id,
        ReviewDecision decision,
        ReviewActorKind actor,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = IndexOf(id);
            if (index < 0)
            {
                return MaterialTransition.Rejected($"资料 {id} 不存在。");
            }

            var card = _manifest.Materials[index];
            var target = NextStatus(card.Status, decision);
            if (target is null)
            {
                return MaterialTransition.Rejected($"非法转移：{card.Status.ToWire()} 不能执行 {decision}。");
            }

            if (!IsAllowedBy(card, decision, actor))
            {
                return MaterialTransition.Rejected(
                    $"越权：{actor} 不能对 {card.Status.ToWire()} 状态的资料执行 {decision}。");
            }

            var updated = card with
            {
                Status = target.Value,
                ReviewNote = note ?? card.ReviewNote,
                Reviewer = actor switch
                {
                    ReviewActorKind.User => "user",
                    ReviewActorKind.Host => "host",
                    _ => card.Reviewer,
                },
            };

            var version = _manifest.MaterialVersion;
            if (target.Value == MaterialStatus.Adopted)
            {
                version++;
            }

            var materials = _manifest.Materials.ToArray();
            materials[index] = updated;
            var next = _manifest with { Materials = materials, MaterialVersion = version };

            await SaveManifestAsync(next, ManifestPath, cancellationToken).ConfigureAwait(false);
            _manifest = next;

            Publish("material.reviewed", Payload(updated, version, note));
            return MaterialTransition.Changed(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读正文。<b>只有这里（和 read_material 工具）会把正文交给调用方</b>——正文不进上下文。</summary>
    public string ReadBody(string id)
    {
        var card = Find(id) ?? throw new KeyNotFoundException($"资料 {id} 不存在。");
        var relativeToMaterials = Path.GetRelativePath(MaterialsDirectory, Path.Combine(MeetingDirectory, card.Path));
        var full = MaterialPathGuard.ResolveWithin(MaterialsDirectory, relativeToMaterials);

        return File.ReadAllText(full);
    }

    private static MaterialStatus? NextStatus(MaterialStatus current, ReviewDecision decision) => (current, decision) switch
    {
        (MaterialStatus.Draft, ReviewDecision.Submit) => MaterialStatus.HostReview,
        (MaterialStatus.HostReview, ReviewDecision.Approve) => MaterialStatus.UserReview,
        (MaterialStatus.HostReview, ReviewDecision.Reject) => MaterialStatus.Rejected,
        (MaterialStatus.UserReview, ReviewDecision.Adopt) => MaterialStatus.Adopted,
        (MaterialStatus.UserReview, ReviewDecision.Reject) => MaterialStatus.Rejected,
        (MaterialStatus.UserReview, ReviewDecision.Park) => MaterialStatus.Parked,
        (MaterialStatus.Rejected, ReviewDecision.Reopen) => MaterialStatus.Draft,
        (MaterialStatus.Parked, ReviewDecision.Reopen) => MaterialStatus.Draft,
        _ => null,
    };

    /// <summary>权限边界：初审在主持人，终审在用户；主持人不得越过用户直接采纳（§4.2）。</summary>
    private static bool IsAllowedBy(MaterialCard card, ReviewDecision decision, ReviewActorKind actor) => decision switch
    {
        ReviewDecision.Approve => actor == ReviewActorKind.Host,
        ReviewDecision.Adopt => actor == ReviewActorKind.User,
        ReviewDecision.Park => actor == ReviewActorKind.User,
        ReviewDecision.Reject => card.Status == MaterialStatus.HostReview
            ? actor == ReviewActorKind.Host
            : actor == ReviewActorKind.User,
        ReviewDecision.Reopen => true,
        ReviewDecision.Submit => actor is ReviewActorKind.Member or ReviewActorKind.Host,
        _ => false,
    };

    private async Task<(string RelativePath, string Body)> WriteBodyAsync(
        SubmitRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.BodyPath))
        {
            var full = MaterialPathGuard.ResolveWithin(MaterialsDirectory, request.BodyPath);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"正文文件不存在：{request.BodyPath}", full);
            }

            var text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);

            // manifest 里的路径一律用正斜杠：跨平台一致，前端与 URL 也能直接用（§5.5 的示例即如此）。
            return ($"materials/{Path.GetFileName(full)}", text);
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new ArgumentException("正文不能为空（要么给 body，要么给 bodyPath）。", nameof(request));
        }

        var fileName = $"{Slug(request.Title)}-{_manifest.NextMaterialNumber:D3}.md";
        var path = Path.Combine(MaterialsDirectory, fileName);
        await File.WriteAllTextAsync(path, request.Body, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        return ($"materials/{fileName}", request.Body);
    }

    private static async Task SaveManifestAsync(
        MaterialManifest manifest,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var temporary = manifestPath + ".tmp";

        // 先写临时文件再替换：崩溃时不会留下半截 manifest。
        await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, manifestPath, overwrite: true);
    }

    private MaterialManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        var json = File.ReadAllText(ManifestPath);
        return JsonSerializer.Deserialize<MaterialManifest>(json, JsonOptions)
            ?? throw new InvalidDataException($"manifest 解析失败：{ManifestPath}");
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < _manifest.Materials.Count; i++)
        {
            if (string.Equals(_manifest.Materials[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static object Payload(MaterialCard card, int materialVersion, string? note = null) => new
    {
        id = card.Id,
        title = card.Title,
        summary = card.Summary,
        summarySource = card.SummarySource,
        path = card.Path,
        author = card.Author,
        status = card.Status.ToWire(),
        basedOnVersion = card.BasedOnVersion,
        submittedAt = card.SubmittedAt,
        reviewNote = card.ReviewNote,
        reviewer = card.Reviewer,
        materialVersion,
        note,
    };

    private void Publish(string type, object payload) => _bus?.Publish(type, payload);

    /// <summary>把标题变成安全的文件名片段（中文保留，非法字符与空白换成连字符）。</summary>
    private static string Slug(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();

        foreach (var ch in title.Trim())
        {
            builder.Append(char.IsWhiteSpace(ch) || invalid.Contains(ch) ? '-' : ch);
        }

        var slug = builder.ToString().Trim('-', '.');
        if (slug.Length == 0)
        {
            slug = "material";
        }

        return slug.Length > 40 ? slug[..40] : slug;
    }
}
