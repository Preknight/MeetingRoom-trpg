using MeetingRoom.Core.Drafts;
using MeetingRoom.Core.Events;
using MeetingRoom.Core.Logging;
using MeetingRoom.Core.Orchestration;
using MeetingRoom.Core.Roleplay;
using MeetingRoom.Core.Vtt;
using MeetingRoom.Core.Workspaces;
using MeetingRoom.Host.Dev;
using MeetingRoom.Host.Drafts;
using MeetingRoom.Host.Workspaces;

namespace MeetingRoom.Host.Sessions;

/// <summary>
/// 会话生命周期的唯一入口（设计方案 §15.2 / §15.7，W2 落地）。
/// <para>
/// 一次只**活跃**一场会话：单条 SSE 与前端 store 都是"一个世界"的归约（§8.1 / §15.9），
/// 所以"切工作空间 / 切会话"= 释放旧会话、装配新会话。会话自己的数据都在它自己的目录里，
/// 换回来时重新装配即可（会议从 <c>transcript.jsonl</c> + <c>manifest.json</c> 恢复、
/// 叙事从 <c>story.jsonl</c> 重放）。
/// </para>
/// <para>
/// 它同时是两个快照源（<see cref="IMeetingStateSource"/> / <see cref="IRoleplayStateSource"/>）：
/// 真实会话优先，没有会议会话时才退回演示流——这样"没有 provider 也能看界面"仍然成立。
/// </para>
/// </summary>
public sealed class SessionHub : IMeetingStateSource, IRoleplayStateSource, IAsyncDisposable
{
    private readonly WorkspaceRegistry _workspaces;
    private readonly WorkspaceSessionFactory _factory;
    private readonly FileLog _log;
    private readonly DevEventStream _dev;
    private readonly IEventBus _bus;
    private readonly Lock _gate = new();

    private WorkspaceMeetingSession? _meeting;
    private RoleplaySession? _roleplay;
    private SessionDescriptor? _active;

    public SessionHub(
        WorkspaceRegistry workspaces,
        WorkspaceSessionFactory factory,
        DevEventStream dev,
        IEventBus bus,
        FileLog log)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(log);

        _workspaces = workspaces;
        _factory = factory;
        _dev = dev;
        _bus = bus;
        _log = log;
    }

    /// <summary>当前打开的工作空间（没有就是 <c>null</c>）。</summary>
    public WorkspaceDescriptor? Workspace => _workspaces.Current;

    /// <summary>当前活跃的会议会话（未装配或当前是叙事时为 <c>null</c>）。</summary>
    public MeetingSession? Meeting
    {
        get
        {
            lock (_gate)
            {
                return _meeting?.Session;
            }
        }
    }

    /// <summary>当前活跃的叙事会话。</summary>
    public RoleplaySession? Roleplay
    {
        get
        {
            lock (_gate)
            {
                return _roleplay;
            }
        }
    }

    /// <summary>当前活跃会话的元信息。</summary>
    public SessionDescriptor? Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>当前工作空间的待整理区（没有工作空间时为 <c>null</c>）。</summary>
    public DraftStore? Drafts => _workspaces.Current is { } workspace ? new DraftStore(workspace.Layout) : null;

    public WorkspaceDescriptor RequireWorkspace() =>
        _workspaces.Current
        ?? throw new InvalidOperationException("还没有打开工作空间；先 POST /api/workspaces 新建一个，或 POST /api/workspaces/open 打开已有的。");

    /// <summary>当前工作空间里的会话（Legacy 工作空间两种都列）。</summary>
    public IReadOnlyList<SessionDescriptor> ListSessions(WorkspaceDescriptor workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var store = new SessionStore(workspace.Layout);

        if (workspace.Kind == WorkspaceKind.Roleplay)
        {
            return store.List(WorkspaceKind.Roleplay);
        }

        if (workspace.Kind == WorkspaceKind.Meeting)
        {
            return store.List(WorkspaceKind.Meeting);
        }

        return store
            .List(WorkspaceKind.Meeting)
            .Concat(store.List(WorkspaceKind.Roleplay))
            .OrderByDescending(session => session.CreatedAt)
            .ToArray();
    }

    // ── 工作空间 ────────────────────────────────────────────────────────────────

    /// <summary>新建工作空间：注册表建骨架 + 写 <c>workspace.json</c>，然后让它成为当前工作空间。</summary>
    public async Task<WorkspaceDescriptor> CreateWorkspaceAsync(string path, WorkspaceKind kind, string? name)
    {
        var descriptor = _workspaces.Create(path, kind, name);
        _log.Info($"新建工作空间：{descriptor.Name}（{descriptor.KindWire}）→ {descriptor.Path}");
        await OnWorkspaceChangedAsync(descriptor).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>打开已存在的工作空间：已注册的直接切，没注册过的按目录里的 <c>workspace.json</c> 认。</summary>
    public async Task<WorkspaceDescriptor> OpenWorkspaceAsync(string path, WorkspaceKind? fallbackKind)
    {
        var descriptor = _workspaces.Open(path, fallbackKind);
        _log.Info($"打开工作空间：{descriptor.Name}（{descriptor.KindWire}）→ {descriptor.Path}");
        await OnWorkspaceChangedAsync(descriptor).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>确保 <paramref name="workspacePath"/> 就是当前工作空间（不是就先切过去）。</summary>
    public async Task<WorkspaceDescriptor> UseWorkspaceAsync(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        if (_workspaces.Current is { } current && WorkspaceRegistry.SamePath(current.Path, workspacePath))
        {
            return current;
        }

        return await OpenWorkspaceAsync(workspacePath, fallbackKind: null).ConfigureAwait(false);
    }

    /// <summary>从清单移除（只移除登记，不删磁盘）。移除的若是当前工作空间，会话一并退场。</summary>
    public async Task<bool> ForgetWorkspaceAsync(string path)
    {
        if (!_workspaces.Forget(path))
        {
            return false;
        }

        _log.Info($"从清单移除工作空间（磁盘未动）：{path}");
        await ResetAsync().ConfigureAwait(false);
        PublishWorkspaceList();
        return true;
    }

    /// <summary>切了工作空间：旧会话退场（它的数据已落盘），并广播新的清单与展开态。</summary>
    private async Task OnWorkspaceChangedAsync(WorkspaceDescriptor descriptor)
    {
        await ResetAsync().ConfigureAwait(false);

        PublishWorkspaceList();
        PublishWorkspaceOpened(descriptor);
        PublishSessionList(descriptor);
        PublishDrafts();
    }

    // ── 会话 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 开一场会议：点名了 <paramref name="sessionId"/> 就用那一场；没点名而当前正停在一场**还没开跑**的会议上，
    /// 就复用它（界面上"开始会议"该先认当前这场，而不是每点一次都多出一个空会话目录）；否则新开一场。
    /// 会议状态是 <c>Idle</c>——真正开会是端点随后调的 <c>StartAsync</c>。
    /// </summary>
    public async Task<SessionDescriptor> StartMeetingAsync(
        string? topic,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RequireWorkspace();
        EnsureKindAllowed(workspace, WorkspaceKind.Meeting);

        var store = new SessionStore(workspace.Layout);

        if (string.IsNullOrWhiteSpace(sessionId)
            && Active is { Kind: WorkspaceKind.Meeting } active
            && Meeting is null or { Status: MeetingStatus.Idle }
            && store.Find(WorkspaceKind.Meeting, active.Id) is { } current)
        {
            if (!string.IsNullOrWhiteSpace(topic))
            {
                current = current with { Topic = topic };
                store.Save(current);
            }

            await AssembleAsync(workspace, current, cancellationToken).ConfigureAwait(false);
            PublishSessionList(workspace);
            return current;
        }

        return await CreateSessionAsync(
            workspace,
            WorkspaceKind.Meeting,
            new SessionCreateSpec(Id: sessionId, Title: topic, Topic: topic),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在当前工作空间下开一场叙事：设定由调用方给全（世界观 / 情境 / 卡司 / 说书人）。</summary>
    public Task<SessionDescriptor> StartRoleplayAsync(SessionCreateSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return CreateSessionAsync(RequireWorkspace(), WorkspaceKind.Roleplay, spec, cancellationToken);
    }

    /// <summary>
    /// 新建一场会话（<c>POST /api/sessions</c>）：建目录 + <c>session.json</c>、装配并设为活跃，
    /// **不自动开跑**（开跑仍是 <c>/api/meeting/start</c> 与 <c>/api/roleplay/start</c>）。
    /// </summary>
    public async Task<SessionDescriptor> CreateSessionAsync(
        WorkspaceDescriptor workspace,
        WorkspaceKind kind,
        SessionCreateSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(spec);

        EnsureKindAllowed(workspace, kind);

        var store = new SessionStore(workspace.Layout);

        // 显式点了 id、而那一场已经存在：**复用它**（"接着开 / 接着演"），不再另建一场同名的。
        var descriptor = SessionIds.Clean(spec.Id) is { Length: > 0 } wanted && store.Find(kind, wanted) is { } existing
            ? existing
            : store.Create(
                kind,
                spec.Title,
                spec.Id,
                spec.Topic,
                spec.Worldview,
                spec.Situation,
                spec.UserRole,
                spec.Cast,
                spec.NarratorId);

        _log.Info($"打开会话：{descriptor.KindWire}/{descriptor.Id}「{descriptor.Title}」（工作空间 {workspace.Path}）");
        await AssembleAsync(workspace, descriptor, cancellationToken).ConfigureAwait(false);
        PublishSessionList(workspace);
        return descriptor;
    }

    /// <summary>
    /// 切到一场已有会话（侧栏点选 / <c>POST /api/sessions/open</c>）：按其目录重新装配。
    /// <b>不自动开跑</b>——会议停在 <c>Idle</c>，叙事把 <c>story.jsonl</c> 重放进界面。
    /// </summary>
    public async Task<SessionDescriptor> OpenSessionAsync(WorkspaceKind kind, string sessionId, CancellationToken cancellationToken = default)
    {
        var workspace = RequireWorkspace();
        EnsureKindAllowed(workspace, kind);

        var store = new SessionStore(workspace.Layout);
        if (store.Find(kind, sessionId) is not { } descriptor)
        {
            _log.Warn($"会话不存在，拒绝切换：{sessionId}（工作空间 {workspace.Path}）");
            throw new InvalidOperationException($"这个工作空间里没有这场会话：{sessionId}");
        }

        _log.Info($"切到会话：{descriptor.KindWire}/{descriptor.Id}「{descriptor.Title}」（工作空间 {workspace.Path}）");
        await AssembleAsync(workspace, descriptor, cancellationToken).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>
    /// 删掉一场会话（<c>POST /api/sessions/delete</c>，侧栏的「删除」走这里）。
    /// 删的若是**当前活跃会话**，先让它退场（释放文件句柄与后台任务）再删目录；
    /// 删除不可逆，二次确认在前端（§15.3）。
    /// </summary>
    public async Task<bool> DeleteSessionAsync(WorkspaceKind kind, string sessionId, CancellationToken cancellationToken = default)
    {
        var workspace = RequireWorkspace();
        EnsureKindAllowed(workspace, kind);

        var actual = kind == WorkspaceKind.Legacy ? WorkspaceKind.Meeting : kind;
        var safeId = SessionIds.Clean(sessionId);

        if (Active is { } active
            && active.Kind == actual
            && string.Equals(active.Id, safeId, StringComparison.OrdinalIgnoreCase))
        {
            await ResetAsync().ConfigureAwait(false);
        }

        if (!new SessionStore(workspace.Layout).Delete(actual, safeId))
        {
            _log.Warn($"会话不存在，未删除：{actual}/{safeId}（工作空间 {workspace.Path}）");
            return false;
        }

        _log.Info($"删除会话（连同目录）：{actual}/{safeId}（工作空间 {workspace.Path}）");
        PublishSessionList(workspace);
        PublishSnapshot();
        return true;
    }

    /// <summary>让当前会话退场（切工作空间、切会话、工作空间被移除时都走这里）。</summary>
    public async Task ResetAsync()
    {
        WorkspaceMeetingSession? meeting;
        RoleplaySession? roleplay;

        lock (_gate)
        {
            meeting = _meeting;
            roleplay = _roleplay;
            _meeting = null;
            _roleplay = null;
            _active = null;
        }

        if (meeting is not null)
        {
            await meeting.DisposeAsync().ConfigureAwait(false);
        }

        if (roleplay is not null)
        {
            await roleplay.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AssembleAsync(WorkspaceDescriptor workspace, SessionDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.Kind == WorkspaceKind.Roleplay)
        {
            var session = _factory.CreateRoleplay(workspace.Layout, descriptor);
            await ActivateAsync(descriptor, meeting: null, session).ConfigureAwait(false);
        }
        else
        {
            var held = await _factory.CreateMeetingAsync(workspace.Layout, descriptor, cancellationToken).ConfigureAwait(false);
            await ActivateAsync(descriptor, held, roleplay: null).ConfigureAwait(false);
        }

        PublishSnapshot();
    }

    private async Task ActivateAsync(SessionDescriptor descriptor, WorkspaceMeetingSession? meeting, RoleplaySession? roleplay)
    {
        WorkspaceMeetingSession? previousMeeting;
        RoleplaySession? previousRoleplay;

        lock (_gate)
        {
            previousMeeting = _meeting;
            previousRoleplay = _roleplay;
            _meeting = meeting;
            _roleplay = roleplay;
            _active = descriptor;
        }

        if (previousMeeting is not null)
        {
            await previousMeeting.DisposeAsync().ConfigureAwait(false);
        }

        if (previousRoleplay is not null)
        {
            await previousRoleplay.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>工作空间的类型说了算：会议型只开会、叙事型只开叙事（§15.2 的硬规则 1）。Legacy 两种都收。</summary>
    private void EnsureKindAllowed(WorkspaceDescriptor workspace, WorkspaceKind kind)
    {
        if (workspace.Kind is WorkspaceKind.Legacy || workspace.Kind == kind)
        {
            return;
        }

        var message =
            $"「{workspace.Name}」是{(workspace.Kind == WorkspaceKind.Roleplay ? "叙事" : "会议")}型工作空间，"
            + $"不能开{(kind == WorkspaceKind.Roleplay ? "叙事" : "会议")}——工作空间的类型在创建时就定了（§15.2）。";

        _log.Warn($"拒绝：{message}");
        throw new InvalidOperationException(message);
    }

    // ── 快照源 ──────────────────────────────────────────────────────────────────

    /// <summary>会议快照：有真实会议会话就给它，否则退回演示流（阶段 1 的临时物）。</summary>
    object IMeetingStateSource.Snapshot()
    {
        lock (_gate)
        {
            if (_meeting is { } held)
            {
                return held.Session.Snapshot();
            }
        }

        // 没有活跃会议：**只有演示流真的在跑**（`?dev=1` 或 `GET /api/dev/fake`）才用它的数据。
        // 否则返回空会议——之前的实现无条件回落 `_dev.Snapshot()`，而它硬编码了
        // `status = "running"` 与四个默认与会者，于是"什么都没开"的页面显示「进行中」+ 主持人/产品经理/销售经理/工程师（r13）。
        return _dev.IsRunning
            ? _dev.Snapshot()
            : EmptyMeeting;
    }

    /// <summary>叙事快照：没有活跃叙事时给合法空快照（键集合与真实快照一致，前端好归约）。</summary>
    object IRoleplayStateSource.Snapshot()
    {
        lock (_gate)
        {
            return _roleplay?.Snapshot() ?? IdleRoleplay;
        }
    }

    /// <summary>把两份全量快照推给已有连接（切工作空间 / 切会话之后）。</summary>
    public void PublishSnapshot()
    {
        _bus.Publish("meeting.state", ((IMeetingStateSource)this).Snapshot());
        _bus.Publish("roleplay.state", ((IRoleplayStateSource)this).Snapshot());
    }

    /// <summary>广播工作空间清单（重连后侧栏据此重建）。</summary>
    public void PublishWorkspaceList() => _bus.Publish(
        "workspace.list",
        new { workspaces = _workspaces.List().Select(WorkspaceProjection.ToWire).ToArray() });

    /// <summary>当前活跃会话的对外形状（没有活跃会话时是 null）——侧栏据此高亮"我现在在哪一场"。</summary>
    public object? ActiveWire => Active is { } current
        ? new { kind = current.KindWire, id = current.Id, title = current.Title }
        : null;

    /// <summary>广播待整理条目与已归档清单（第 1 列两段据此重建；切工作空间时也会推一次）。</summary>
    public void PublishDrafts()
    {
        if (_workspaces.Current is not { } workspace)
        {
            return;
        }

        var store = new DraftStore(workspace.Layout);

        _bus.Publish(
            "draft.list",
            new
            {
                workspacePath = workspace.Path,
                drafts = store.List().Select(DraftProjection.ToWire).ToArray(),
                archived = store.ListArchived().Select(DraftProjection.ToWire).ToArray(),
            });
    }

    /// <summary>广播"当前打开了哪个工作空间"（含它的会话列表与当前活跃会话）。</summary>
    public void PublishWorkspaceOpened(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        _bus.Publish(
            "workspace.opened",
            new
            {
                path = descriptor.Path,
                kind = descriptor.KindWire,
                name = descriptor.Name,
                sessions = ListSessions(descriptor).Select(WorkspaceProjection.SessionToWire).ToArray(),
                active = ActiveWire,
            });
    }

    private void PublishSessionList(WorkspaceDescriptor workspace) => _bus.Publish(
        "session.list",
        new
        {
            workspacePath = workspace.Path,
            sessions = ListSessions(workspace).Select(WorkspaceProjection.SessionToWire).ToArray(),
            active = ActiveWire,
        });

    /// <summary>空快照：字段与真实快照对齐（前端 store 是纯事件归约，字段缺失会变成两种形状）。</summary>
    /// <summary>
    /// 空会议（r13）：没有任何活跃会议时的合法快照。键集合与真实快照一致，前端好归约；
    /// `participants` 为空数组——"还没有与会者"由前端渲染空态，而不是塞四个假人进来。
    /// </summary>
    private static readonly object EmptyMeeting = new
    {
        status = "idle",
        materialVersion = 0,
        participants = Array.Empty<object>(),
    };

    private static readonly object IdleRoleplay = new
    {
        status = "idle",
        storyId = (string?)null,
        worldview = string.Empty,
        situation = string.Empty,
        userRole = string.Empty,
        revision = 0L,
        worldState = string.Empty,
        cast = Array.Empty<object>(),
        turns = Array.Empty<object>(),
        pendingDecisions = Array.Empty<object>(),
        // 地图（V1）的空形状：与 RoleplaySession.VttStatus() 共用同一份投影。
        vtt = VttStatusView.Idle("还没有叙事会话"),
    };

    public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);
}

/// <summary>
/// 新建一场会话要的设定（端点请求体到领域参数的桥）。会议只看议题，
/// 叙事看世界观 / 情境 / 身份 / 卡司 / 说书人。
/// </summary>
public sealed record SessionCreateSpec(
    string? Id = null,
    string? Title = null,
    string? Topic = null,
    string? Worldview = null,
    string? Situation = null,
    string? UserRole = null,
    IReadOnlyList<string>? Cast = null,
    string? NarratorId = null);
