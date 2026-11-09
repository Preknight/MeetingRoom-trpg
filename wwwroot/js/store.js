// 事件归约：纯函数、无副作用、可单步重放（实现步骤 3.3）。
// 未知事件类型一律忽略、不抛错。
//
// 产品只保留叙事模式：state.mode 恒为 'roleplay'（会议端点仍在后端，但前端不再切换语义）。
//   mode = 'roleplay' → 第 1/3/4 列读 roleplay.cast / story / pending / roleplayLog

export function initialState() {
  return {
    connection: 'connecting',
    mode: 'roleplay',
    meeting: { status: 'unknown', materialVersion: 0 },
    roleplay: {
      status: 'idle',
      storyId: null,
      worldview: '',
      situation: '',
      userRole: '',
      revision: 0,
      worldState: '',
      cast: [],
      // 地图（《枭熊地图接入实现方案》V1）：只在 roleplay.state 快照里出现，形状见 VttStatusView。
      vtt: null,
      // 战斗投影（F1，§5.2）：无战斗时为 null；刷新 / 重连后靠快照重建战斗条。
      combat: null,
      // roleplay.combat 事件到了但还没重新取快照：main.js 见到就拉一次 /api/roleplay/state。
      combatStale: false,
      // 检定与骰子（F6）：快照直接给记录，事件追加——**字段照记录类，不另造形状**。
      checks: [],
      rolls: [],
    },
    // 后端 roleplay.warning 的原文（不加工，直接显示 message）：可逐条关闭。
    warnings: [],
    participants: [], // [{id, name, role, status}]
    materials: [],    // [{id, title, summary, status, author, summarySource, path, basedOnVersion}]
    messages: [],     // [{id, author, kind, text}]
    story: [],        // 剧情对话流：[{author, name, text, revision}]（导演的旁白/指令 author='user'、name='导演'）
    requests: [],     // 角色请求（M9b）：与 checks / rolls 并列的独立切片，渲染时内联进剧情流
    streaming: {},    // participantId -> 正在流式生成的正文
    backstage: {},    // participantId -> [{kind, text}]（思考 / 工具 / 打断，仅显示）
    hostLog: [],      // 会议：主持人记录 [{action, target, reason}]
    roleplayLog: [],  // 叙事：导演记录 [{action, reason}]
    pending: [],      // 待确认事件 [{kind, text, options}]（说书人的提议；阶段 9 的提问也走这里）
    diagnostics: {},  // participantId -> {prefix, cacheHitTokens, cacheMissTokens, lastMissReason}
    // 工作空间与会话（设计方案 §15.7）：侧栏读这三份。
    workspaces: [],        // [{path, kind, name, sessionCount}]
    currentWorkspace: null,// 当前工作空间的 path
    sessions: [],          // 当前工作空间下的会话 [{id, kind, title, createdAt, topic, worldview}]
    activeSession: null,   // 当前活跃会话 [{kind, id}]（一次只活跃一场，§8.1）
    // 第 1 列：上段「已归档」与下段「待整理」（设计方案 §15.6）。
    drafts: [],            // [{id, kind, title, createdAt}]
    archived: [],          // [{id, kind, title, path, bytes}]
    notice: null,
  };
}

/** 后台记录每个与会者最多保留的条数。 */
const BACKSTAGE_LIMIT = 200;

/** 记录类列表（主持人记录 / 导演记录）最多保留的条数。 */
const LOG_LIMIT = 300;

/** 关掉一条警告（index 来自 state.warnings 的顺序）。 */
export function dismissWarning(state, index) {
  return { ...state, warnings: state.warnings.filter((_, i) => i !== index) };
}

/** 事件里的检定是扁平字段，快照里是记录形状：统一成记录形状（多一个 `at` 也没有坏处）。 */
/**
 * 角色请求（M9b）归一化：事件与快照字段名一致，但事件可能缺 `at`，这里补一个。
 */
function normalizeRequest(payload) {
  return {
    actorId: payload.actorId ?? payload.actor ?? '',
    requestKind: payload.requestKind ?? payload.request_type ?? 'check',
    summary: payload.summary ?? '',
    reason: payload.reason ?? '',
    at: payload.at ?? new Date().toISOString(),
  };
}

/**
 * 按 (actorId, requestKind, summary, at) 去重合并：`next` 里的条目覆盖同 key 的旧条目。
 * 时间相同、内容相同的两次请求**只会出现一次**——这是"实时一条 + 快照再一条"的防线。
 */
function mergeRequests(next, previous) {
  const merged = new Map();

  for (const request of [...(previous ?? []), ...(next ?? [])]) {
    const key = `${request.actorId}|${request.requestKind}|${request.summary}|${request.at}`;
    merged.set(key, request);
  }

  return [...merged.values()].sort((left, right) => String(left.at).localeCompare(String(right.at)));
}

function normalizeCheck(payload) {
  const roll = payload.roll ?? {
    natural: payload.natural ?? null,
    total: payload.total ?? null,
    success: payload.success ?? null,
    modifier: payload.modifier ?? null,
  };

  return {
    checkId: payload.checkId ?? null,
    who: payload.who ?? payload.actor ?? '',
    kind: payload.kind ?? '',
    kindType: payload.kindType ?? null,
    purpose: payload.purpose ?? '',
    skill: payload.skill ?? null,
    ability: payload.ability ?? null,
    dc: payload.dc ?? 0,
    dcSource: payload.dcSource ?? 'dm',
    dcAdjusted: payload.dcAdjusted ?? false,
    modifierSource: payload.modifierSource ?? payload.ModifierSource ?? 'request',
    visibility: String(payload.visibility ?? 'public').toLowerCase(),
    success: payload.success ?? roll.success ?? null,
    passiveValue: payload.passiveValue ?? null,
    applied: payload.applied ?? true,
    stakes: payload.stakes ?? null,
    successes: payload.successes ?? null,
    failures: payload.failures ?? null,
    statusAfter: payload.statusAfter ?? null,
    roll,
    at: payload.at ?? new Date().toISOString(),
  };
}

/** 骰子记录：事件里少 `at`，补上即可。 */
function normalizeRoll(payload) {
  return {
    who: payload.who ?? '',
    expression: payload.expression ?? '',
    mode: payload.mode ?? 'normal',
    reason: payload.reason ?? '',
    attacks: payload.attacks ?? [],
    total: payload.total ?? 0,
    dc: payload.dc ?? null,
    success: payload.success ?? null,
    at: payload.at ?? new Date().toISOString(),
  };
}

/**
 * 剧情流 + 检定 + 骰子，按时间合并（F6 要求内联、按时间序）。
 * `at` 是 ISO 字符串，字典序就是时间序；缺 `at` 的老条目排在所在 turn 的位置。
 */
export function timelineOf(state) {
  const roleplay = state.mode === 'roleplay';
  const story = (state.story ?? []).map((entry) => ({ kind: 'line', at: entry.at ?? '', entry }));
  const checks = roleplay ? (state.roleplay?.checks ?? []).map((check) => ({ kind: 'check', at: check.at ?? '', check })) : [];
  const rolls = roleplay ? (state.roleplay?.rolls ?? []).map((roll) => ({ kind: 'roll', at: roll.at ?? '', roll })) : [];
  // 请求行（M9b）：与检定卡 / 骰子卡同样按时间内联——它不是正文，所以走独立 kind。
  const requests = roleplay
    ? (state.roleplay?.requests ?? []).map((request) => ({ kind: 'request', at: request.at ?? '', request }))
    : [];

  return [...story, ...checks, ...rolls, ...requests]
    .sort((left, right) => String(left.at).localeCompare(String(right.at)));
}

export function apply(state, type, payload) {
  switch (type) {
    case 'meeting.state':
      // 全量快照：重连后据此无歧义重建（设计文档 §8.4）。
      return {
        ...state,
        meeting: {
          status: payload.status ?? 'unknown',
          materialVersion: payload.materialVersion ?? 0,
        },
        participants: payload.participants ?? [],
        materials: payload.materials ?? [],
        messages: payload.messages ?? state.messages,
        hostLog: payload.hostLog ?? state.hostLog,
      };

    // ---------- 角色扮演（叙事）模式 ----------

    case 'roleplay.state': {
      const roleplay = {
        status: payload.status ?? 'idle',
        storyId: payload.storyId ?? null,
        worldview: payload.worldview ?? '',
        situation: payload.situation ?? '',
        userRole: payload.userRole ?? '',
        revision: payload.revision ?? 0,
        worldState: payload.worldState ?? '',
        cast: payload.cast ?? [],
        // 地图状态（V1）：前端第 1 列读它；旧后端没有这个字段时保持 null，面板按"未启用"处理。
        vtt: payload.vtt ?? null,
        // 地图节点（M4）：战术板的底图/网格/房间/墙都吃这一份；**漏了这个字段，
        // 真实页面里 map 恒为 null，M4 等于从未生效过**（d8af7eb 的回归）。
        map: payload.map ?? null,
        // 战斗投影（F1）：快照是权威源，事件只负责提示"该重取了"。
        combat: payload.combat ?? null,
        combatStale: false,
        // 快照里的记录形状与事件略有差异（success 在 roll 内），同样过一遍归一化——
        // 否则刷新后的卡片会丢「成功/失败」。
        checks: (payload.checks ?? []).map(normalizeCheck),
        rolls: (payload.rolls ?? []).map(normalizeRoll),
        // 请求（M9b）：快照是权威源；但实时事件可能先到、落盘后快照才带上它，
        // 所以按 key 合并去重而不是直接替换——否则"实时一条 + 重放再一条"。
        // 换了一场戏就**不保留**旧请求（否则侧栏切会话会串台）。
        requests: mergeRequests(
          (payload.requests ?? []).map(normalizeRequest),
          payload.storyId && payload.storyId === state.roleplay?.storyId ? (state.roleplay?.requests ?? []) : [],
        ),
      };

      // r13：模式要看"这场是什么"，不是"它是不是在跑"。
      // 重启后打开旧叙事，后端给的是 status=idle（没有运行态恢复）——但 storyId 在。
      // mode 恒为 roleplay；active 只决定叙事切片是否清空。
      const active = Boolean(payload.storyId) || roleplay.status !== 'idle';

      // 没有活跃叙事就把叙事切片清空——否则上一场戏的剧情会残留在界面里。
      return {
        ...state,
        mode: 'roleplay',
        roleplay,
        // 快照带最近若干幕，据它重建剧情流：刷新 / 重连后剧情不该凭空消失。
        story: active ? rebuildStory(payload.turns, roleplay.cast) : [],
        roleplayLog: active ? state.roleplayLog : [],
        // 还挂着没答复的待决事项：刷新 / 重连后浮层与第 3 列据此重建（§14.3）。
        pending: active ? (payload.pendingDecisions ?? state.pending) : [],
        notice: null,
      };
    }

    case 'roleplay.started':
      return {
        ...state,
        mode: 'roleplay',
        roleplayLog: log(
          state.roleplayLog,
          '开场',
          `在场：${(payload.cast ?? []).map((actor) => actor.name).join('、') || '（无）'}`,
        ),
      };

    case 'roleplay.ooc':
      // 出戏指令是元指令：只留痕，不进剧情对话流（§11.4-9）。
      return {
        ...state,
        roleplayLog: log(state.roleplayLog, '出戏指令', `${(payload.directives ?? []).join('；')}（未写进剧情）`),
      };

    case 'roleplay.line': {
      // 一条正文已提交：收掉它的流式草稿（第 5 列）；剧情流本身由 roleplay.turn 一次补齐。
      const streaming = { ...state.streaming };
      delete streaming[payload.author];
      return { ...state, streaming };
    }

    case 'roleplay.turn': {
      const lines = (payload.lines ?? []).map((line) => ({
        author: line.author,
        name: nameOf(state.roleplay.cast, line.author),
        text: line.text,
      }));
      const directorInput = (payload.directorInput ?? '').trim();
      const isOpening = directorInput.length === 0;

      return {
        ...state,
        roleplay: {
          ...state.roleplay,
          revision: payload.revision ?? state.roleplay.revision,
        },
        story: [
          ...state.story,
          ...(directorInput ? [{ author: 'user', name: '导演', text: directorInput, revision: payload.revision }] : []),
          ...lines.map((line) => ({ ...line, revision: payload.revision })),
        ],
        streaming: {},
        roleplayLog: log(
          state.roleplayLog,
          isOpening ? '开场正文' : `第 ${payload.revision} 幕`,
          isOpening ? `${lines.length} 段开场正文` : `导演指令 → ${lines.length} 个角色接话`,
        ),
      };
    }

    case 'roleplay.ended':
      return {
        ...state,
        streaming: {},
        notice: null,
        roleplayLog: log(state.roleplayLog, '本场结束', `停在 revision ${payload.revision ?? 0}`),
      };

    case 'roleplay.world':
      // 世界状态变更（说书人提议 → 导演拍板）：这里只留痕，
      // 现场本身由紧随其后的 roleplay.state 快照重建（后端落盘后必推快照）。
      return {
        ...state,
        roleplayLog: log(
          state.roleplayLog,
          payload.applied ? '世界状态变更' : '提议未采纳',
          describeWorld(payload),
        ),
      };

    case 'roleplay.combat':
      // 战斗增量（F1）：事件载荷只说明"战斗有变化"，真正的投影以快照为准——
      // 标一个 combatStale，由 main.js 拉一次 /api/roleplay/state 重建（刷新/重连走同一条路）。
      return {
        ...state,
        roleplay: {
          ...state.roleplay,
          combat: payload.ended ? null : state.roleplay.combat,
          combatStale: true,
        },
      };

    case 'roleplay.warning':
      // 原样显示后端 message，不自创文案（§F1-6）；最多留 10 条，免得刷屏。
      return {
        ...state,
        warnings: [
          ...state.warnings,
          { code: payload.code ?? 'warning', message: payload.message ?? '', at: Date.now() },
        ].slice(-10),
      };

    case 'roleplay.check':
      // 检定（F6）：事件与快照记录形状略有差异，这里统一成记录形状再进列表。
      return { ...state, roleplay: { ...state.roleplay, checks: [...state.roleplay.checks, normalizeCheck(payload)] } };

    case 'roleplay.roll':
      return { ...state, roleplay: { ...state.roleplay, rolls: [...state.roleplay.rolls, normalizeRoll(payload)] } };

    case 'roleplay.request':
      // 角色请求（M9b）：只落一行提示，不改任何世界状态。同步去重——
      // 紧随其后的 roleplay.state 快照里也有这条，key 相同就不会出现两条。
      return {
        ...state,
        roleplay: {
          ...state.roleplay,
          requests: mergeRequests(state.roleplay.requests, [normalizeRequest(payload)]),
        },
      };

    case 'roleplay.error':
      return { ...state, notice: { code: payload.code, message: payload.message } };

    // ---------- 交互模式（会议与叙事共用，§14） ----------

    case 'interaction.request':
      // 待人拍板的一件事：主持人提问与说书人提议走同一条流，靠 kind 区分。
      return {
        ...state,
        pending: [...state.pending.filter((item) => item.id !== payload.id), payload],
      };

    case 'interaction.resolved':
      return {
        ...state,
        pending: state.pending.filter((item) => item.id !== payload.id),
        ...logDecision(state, describeResolution(payload)),
      };

    case 'interaction.skipped':
      // 主持人 / 说书人自己判断"这轮没什么可问的"：也要留痕，否则"连着三轮没问我"看不见。
      return { ...state, ...logDecision(state, `本轮未提问：${payload.reason ?? ''}`) };

    // ---------- 会议模式 ----------

    case 'participant.status':
      return { ...state, participants: upsert(state.participants, payload) };

    case 'participant.delta': {
      const previous = state.streaming[payload.id] ?? '';
      return { ...state, streaming: { ...state.streaming, [payload.id]: previous + (payload.text ?? '') } };
    }

    case 'message.committed': {
      const streaming = { ...state.streaming };
      delete streaming[payload.author];
      return { ...state, streaming, messages: [...state.messages, payload] };
    }

    case 'participant.thought':
      return { ...state, backstage: append(state.backstage, payload.id, 'thought', payload.text ?? '') };

    case 'tool.call':
      return { ...state, backstage: append(state.backstage, payload.id, 'tool', `调用 ${payload.name}`) };

    case 'tool.result':
      return {
        ...state,
        backstage: append(state.backstage, payload.id, 'tool', `${payload.name} → ${text(payload.result)}`),
      };

    case 'interrupt.applied':
      return {
        ...state,
        backstage: append(state.backstage, payload.participantId, 'interrupt', `${payload.kind}：${payload.reason}`),
      };

    case 'host.decision': {
      // 主持人的产出走"主持人记录"而不是讨论区，所以这里要顺手收掉它的流式草稿。
      const streaming = { ...state.streaming };
      if (payload.participantId) {
        delete streaming[payload.participantId];
      }

      return { ...state, streaming, hostLog: [...state.hostLog, payload] };
    }

    case 'material.submitted':
    case 'material.reviewed':
      return {
        ...state,
        meeting: {
          ...state.meeting,
          materialVersion: payload.materialVersion ?? state.meeting.materialVersion,
        },
        materials: upsert(state.materials, payload),
      };

    case 'cache.diagnostics':
      return { ...state, diagnostics: { ...state.diagnostics, [payload.id]: payload } };

    case 'meeting.ended':
      // 会议结束：把可能残留的"正在发言"草稿一并收掉（终止时 turn 可能停在流式中间）。
      return { ...state, streaming: {}, notice: null };

    case 'meeting.error':
      return { ...state, notice: payload };

    // ---------- 工作空间与会话（设计方案 §15.7） ----------

    case 'workspace.list':
      return { ...state, workspaces: payload.workspaces ?? [] };

    case 'workspace.opened':
      return {
        ...state,
        currentWorkspace: payload.path ?? null,
        sessions: payload.sessions ?? [],
        activeSession: payload.active ?? null,
      };

    case 'session.list': {
      // 切工作空间时会先后收到两份 session.list（旧的与新的）：只认当前工作空间那一份，
      // 否则侧栏会被上一份带偏。
      if (payload.workspacePath && payload.workspacePath !== state.currentWorkspace) {
        return state;
      }

      return {
        ...state,
        sessions: payload.sessions ?? [],
        activeSession: payload.active ?? state.activeSession,
      };
    }

    // ---------- 待整理 / 已归档（设计方案 §15.6） ----------

    case 'draft.list':
      // 与 session.list 同理：切工作空间时会先后收到两份，只认当前那一份。
      if (payload.workspacePath && payload.workspacePath !== state.currentWorkspace) {
        return state;
      }

      return { ...state, drafts: payload.drafts ?? [], archived: payload.archived ?? [] };

    default:
      return state;
  }
}

/** 世界状态变更的一句话描述（时间 / 地点 / 在场 + 怎么定下来的）。 */
function describeWorld(payload) {
  const bits = [];

  if (payload.time) {
    bits.push(`时间 ${payload.time}`);
  }

  if (payload.place) {
    bits.push(`地点 ${payload.place}`);
  }

  if ((payload.present ?? []).length > 0) {
    bits.push(`在场 ${payload.present.join('、')}`);
  }

  const errors = payload.errors ?? [];
  const how = payload.applied
    ? payload.timedOut
      ? '（超时未答复，按采用收口）'
      : payload.confirmed
        ? ''
        : '（未提问，直接生效）'
    : errors.length > 0
      ? `（结算失败：${errors.join('；')}）`
      : '（说书人提议，导演未采纳）';

  return `v${payload.version ?? 0}：${bits.join('；') || '无具体变化'}${how}——${payload.reason ?? ''}`;
}

/** 收口结果的一句话描述（超时 / 跳过 / 答复三种走向都要写清楚，§14.5）。 */
function describeResolution(payload) {
  const summary = (payload.answers ?? [])
    .map((answer) => `${answer.questionId} → ${(answer.selected ?? []).join('、') || '（未选）'}`)
    .join('；') || '（无问题）';

  if (payload.timedOut) {
    return `超时自动选择：${summary}`;
  }

  if (payload.skipped) {
    return `跳过（${payload.skipReason ?? '用户跳过'}）：${summary}`;
  }

  return `已答复：${summary}`;
}

/** 把一条决策记进当前模式的那一列（会议 = 主持人记录，叙事 = 导演记录）。 */
function logDecision(state, reason) {
  return state.mode === 'roleplay'
    ? { roleplayLog: log(state.roleplayLog, '待确认事件', reason) }
    : { hostLog: log(state.hostLog, '待确认事件', reason) };
}

/** 从快照的 turns 重建剧情流（刷新 / 重连后剧情不该消失）。 */
function rebuildStory(turns, cast) {
  const entries = [];

  for (const turn of turns ?? []) {
    const directorInput = (turn.directorInput ?? '').trim();

    if (directorInput) {
      entries.push({ author: 'user', name: '导演', text: directorInput, revision: turn.revision, at: turn.at ?? '' });
    }

    for (const line of turn.lines ?? []) {
      entries.push({
        author: line.author,
        name: nameOf(cast, line.author),
        text: line.text,
        revision: turn.revision,
        at: turn.at ?? '',
      });
    }
  }

  return entries;
}

/** 叙事模式下的显示名：先查在场角色，再回落到 id。 */
function nameOf(cast, id) {
  if (id === 'user') {
    return '你';
  }

  const actor = (cast ?? []).find((item) => item.id === id);
  return actor?.name ?? id;
}

function log(entries, action, reason) {
  const next = [...entries, { action, reason }];
  return next.length > LOG_LIMIT ? next.slice(-LOG_LIMIT) : next;
}

function upsert(list, payload) {
  const index = list.findIndex((item) => item.id === payload.id);
  if (index < 0) {
    return [...list, { name: payload.id, role: 'member', ...payload }];
  }

  const next = list.slice();
  next[index] = { ...list[index], ...payload };
  return next;
}

function append(map, id, kind, value) {
  if (!id || !value) {
    return map;
  }

  const entries = [...(map[id] ?? []), { kind, text: value }];
  return { ...map, [id]: entries.length > BACKSTAGE_LIMIT ? entries.slice(-BACKSTAGE_LIMIT) : entries };
}

function text(value) {
  if (value === null || value === undefined) {
    return '';
  }

  return typeof value === 'string' ? value : JSON.stringify(value);
}
