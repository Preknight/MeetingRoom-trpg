// 入口：建唯一的一条 SSE、装配侧栏 + 五列面板、绑定指令区（实现步骤 3.3）。
// 双模式：会议与叙事共用一套面板与同一条 SSE，只按 state.mode 切列内语义、列标题与顶栏（设计文档 §11.6）。
// 最左那列是工作空间 / 会话侧栏（设计方案 §15.3）——它没有"首推快照"，连上时主动拉一次 /api/workspaces。

import * as api from './api.js';
import * as render from './render.js';
import * as store from './store.js';
import { connect } from './stream.js';
import { initViewer } from './viewer.js';
import { mount as mountDiscussion } from './panel-discussion.js';
import { mount as mountDrafts } from './panel-drafts.js';
import { mount as mountHostLog } from './panel-host-log.js';
import { mount as mountLayout } from './panel-layout.js';
import { mount as mountMaterials } from './panel-materials.js';
import { mount as mountParticipants } from './panel-participants.js';
import { mount as mountAsk } from './panel-ask.js';
import { mount as mountReview } from './panel-review.js';
import { mount as mountCalibrate } from './panel-calibrate.js';
import { mount as mountCombat } from './panel-combat.js';
import { mount as mountWarnings } from './panel-warnings.js';
import { mount as mountComposer } from './panel-composer.js';
import { mount as mountNewSession } from './panel-new-session.js';
import { mount as mountPathPicker } from './panel-path-picker.js';
import { mount as mountSidebar } from './panel-sidebar.js';
import { mount as mountWorldview } from './panel-worldview.js';

/** 已经引导过的工作空间（localStorage）：引导只在第一次进某个工作空间时自动打开。 */
const GUIDED_KEY = 'meetingroom.guided';

const CONNECTION_LABELS = {
  connecting: '连接中…',
  open: '已连接',
  reconnecting: '重连中…',
};

const MEETING_LABELS = {
  idle: '未开始',
  running: '进行中',
  paused: '已暂停',
  ended: '已结束',
  unknown: '—',
};

/** 列标题：产品只保留叙事模式的语义。 */
const TITLES = {
  col1: '已归档 · 世界观与场景',
  col2: '剧情',
  col2b: '导演指令',
  col3: '待确认事件',
  col3b: '导演记录',
  col4: '在场角色',
  col5: '后台记录（仅显示，Agent 不可读）',
};

/** panel-combat 拉到新快照后广播：与 SSE 的 roleplay.state 走同一条归约路径。 */
window.addEventListener('meetingroom:roleplay-state', (event) => {
  if (event.detail) {
    state = store.apply(state, 'roleplay.state', event.detail);
    render.scheduleRender();
  }
});

let state = store.initialState();

/** 把一条错误摆到顶栏（侧栏的操作失败也走这里，用户才看得见原因）。 */
function showNotice(message) {
  state = { ...state, notice: { code: '', message } };
  render.scheduleRender();
}

/**
 * 第 1 列地图卡的「刷新」按钮：调已有的 GET /api/roleplay/vtt，把结果写回 vtt 切片。
 * 数据源与 SSE 推送同一份（VttStatusView 投影），所以卡片形状不会出现两种。
 */
async function refreshVtt() {
  const result = await api.getJson('/api/roleplay/vtt');

  if (!result.ok) {
    return { ok: false, message: result.data?.error ?? `HTTP ${result.status}` };
  }

  state = { ...state, roleplay: { ...state.roleplay, vtt: result.data ?? null } };
  render.scheduleRender();
  return { ok: true, vtt: result.data ?? null };
}

// 侧栏 → 新建浮层 → 选目录对话框 → 待整理编辑页（引导页）：几层都要能互相叫起来。
const drafts = mountDrafts();
const layout = mountLayout();

// 诊断钩子：验收时手动切预设（与 __meetingroomBoard 同一用途）。
window.__meetingroomLayout = layout;
const combatPanel = mountCombat({ layout });
const calibratePanel = mountCalibrate({ board: combatPanel.getBoard(), layout });

// 战术板实例在 panel-combat 内部：等它挂好后把 board 交给标定浮层。
window.addEventListener('meetingroom:board-calibrate', () => {
  calibratePanel.openCalibration();
});

window.addEventListener('meetingroom:token-dblclick', (event) => {
  calibratePanel.openTokenSheet(event.detail?.actorId, event.detail?.combat);
});
// 列表 ↔ 地图双向高亮（F3-③）：纯显示层，不改任何状态。
function highlightActor(actorId) {
  combatPanel.setHighlight(actorId);

  for (const node of document.querySelectorAll('[data-participant-id]')) {
    node.classList.toggle('rail-cell--highlight', node.dataset.participantId === actorId);
  }
}

window.addEventListener('meetingroom:token-click', (event) => highlightActor(event.detail?.actorId));
window.addEventListener('meetingroom:participant-click', (event) => highlightActor(event.detail?.id));

// 战斗条与警告区的按钮（F1/F2）：挂在 document 上按 id 分派，**必须放在 combatPanel 定义之后**
// （放在前面会踩 TDZ：Cannot access 'combatPanel' before initialization，整个 main.js 崩掉）。
document.addEventListener('click', (event) => {
  const target = event.target;

  if (target?.id === 'combat-toggle') {
    combatPanel.toggleMode();
  } else if (target?.id === 'combat-end') {
    void combatPanel.endCombat(target);
  } else if (target?.id === 'end-session') {
    void endSession();
  }
});

/**
 * 结束当前会话（真机 B，2026-10-29）：叙事与会议各调自己的 `/end`，**带二次确认**。
 * 结束是单向的（这一场不能再继续），所以不接受"点错了"。
 */
async function endSession() {
  const isRoleplay = state.mode === 'roleplay';
  const label = isRoleplay ? '叙事' : '会议';
  const path = isRoleplay ? '/api/roleplay/end' : '/api/meeting/end';

  if (!window.confirm(`结束当前${label}会话？\n\n${label}会停在这里，这一场不能再继续（记录仍留在会话列表里）。`)) {
    return;
  }

  try {
    await api.post(path, {});
    showNotice(`已结束${label}会话。`);
  } catch (error) {
    showNotice(`结束${label}会话失败：${error.message ?? error}`);
  }
}

const warningsPanel = mountWarnings({
  onDismiss: (index) => {
    state = store.dismissWarning(state, index);
    render.scheduleRender();
  },
});
const picker = mountPathPicker({ onWorkspace: openGuideForNewWorkspace });
const newSession = mountNewSession({ onPickPath: (kind) => picker.open(kind) });
const sidebar = mountSidebar(document.getElementById('sidebar-list'), {
  onNew: () => newSession.open(),
  // 「打开…」：选一个磁盘上已有的工作空间；它已经有会话就切到最近一场（见 panel-path-picker）。
  onOpen: () => picker.open(undefined, { mode: 'open' }),
  onError: showNotice,
});

// 侧栏 · 第 2 列 · 第 3 列 · 第 4 列 · 第 5/6 列（+ 两个浮层 + 交互浮层）
// 第 1 列的地图卡要一个「刷新」按钮：给它 main 的刷新函数（结果写回 state.roleplay.vtt）。
const world = mountWorldview(document.getElementById('worldview-list'), { onRefresh: refreshVtt });

const panels = [
  sidebar,
  combatPanel,
  warningsPanel,
  picker,
  newSession,
  drafts,
  layout,
  mountMaterials(document.getElementById('materials-list')),
  world,
  mountDiscussion(document.getElementById('discussion-list')),
  mountComposer(),
  mountReview(document.getElementById('review-list')),
  mountHostLog(document.getElementById('host-log')),
  mountParticipants(document.getElementById('rail-body')),
  mountAsk(),
];

for (const panel of panels) {
  render.registerRender(() => panel.render(state));
}

/**
 * 第一次进某个工作空间：自动打开引导页（设计方案 §15.5）。
 * 引导只是编辑页的第一次打开，所以这里记一份"已经引导过的工作空间"，之后切换不再打扰。
 */
function openGuideForNewWorkspace(data) {
  const path = data?.path;

  if (!path) {
    return;
  }

  let guided = [];

  try {
    guided = JSON.parse(localStorage.getItem(GUIDED_KEY) ?? '[]');
  } catch {
    guided = [];
  }

  if (guided.includes(path)) {
    return;
  }

  guided.push(path);
  localStorage.setItem(GUIDED_KEY, JSON.stringify(guided));
  drafts.openGuide();
}

const el = {
  connection: document.getElementById('connection'),
  workspacePill: document.getElementById('workspace-pill'),
  modePill: document.getElementById('mode-pill'),
  meetingStatus: document.getElementById('meeting-status'),
  materialVersion: document.getElementById('material-version'),
  roleplayStatus: document.getElementById('roleplay-status'),
  storyRevision: document.getElementById('story-revision'),
  waiting: document.getElementById('waiting-pill'),
  notice: document.getElementById('notice'),
  materials: document.getElementById('materials-list'),
  worldview: document.getElementById('worldview-list'),
  titles: {
    col1: document.getElementById('col1-title'),
    col2: document.getElementById('col2-title'),
    col2b: document.getElementById('col2b-title'),
    col3: document.getElementById('col3-title'),
    col3b: document.getElementById('col3b-title'),
    col4: document.getElementById('col4-title'),
    col5: document.getElementById('col5-title'),
  },
};

/** 顶栏那一格回答"我在哪"：工作空间 · 当前那一场。 */
function workspaceLabel(current) {
  const workspace = current.workspaces.find((item) => item.path === current.currentWorkspace);

  if (!workspace) {
    return '未打开工作空间';
  }

  const name = workspace.name ?? workspace.path;
  const active = current.activeSession;

  if (!active) {
    return name;
  }

  const session = current.sessions.find((item) => item.kind === active.kind && item.id === active.id);

  return `${name} · ${session?.title ?? active.title ?? active.id}`;
}

render.registerRender(() => {
  const titles = TITLES;

  el.connection.dataset.state = state.connection;
  el.connection.textContent = CONNECTION_LABELS[state.connection] ?? state.connection;

  el.workspacePill.textContent = workspaceLabel(state);
  el.workspacePill.title = state.currentWorkspace ?? '还没有打开工作空间';

  // 产品只保留叙事模式：会议那两个 pill 恒隐藏（后端会议端点仍在，供旧数据与测试使用）。
  el.modePill.dataset.mode = 'roleplay';
  el.modePill.textContent = '叙事模式';
  el.meetingStatus.hidden = true;
  el.materialVersion.hidden = true;
  el.roleplayStatus.hidden = false;
  el.storyRevision.hidden = false;

  el.roleplayStatus.textContent = MEETING_LABELS[state.roleplay.status] ?? state.roleplay.status;
  el.storyRevision.textContent = `剧情进度 ${state.roleplay.revision}`;

  el.titles.col1.textContent = titles.col1;
  el.titles.col2.textContent = titles.col2;
  el.titles.col2b.textContent = titles.col2b;
  el.titles.col3.textContent = titles.col3;
  el.titles.col3b.textContent = titles.col3b;
  el.titles.col4.textContent = titles.col4;
  el.titles.col5.textContent = titles.col5;

  // 第 2 列只显示叙事的世界观 / 场景卡。
  el.materials.hidden = true;
  el.worldview.hidden = false;

  // 等答复期间给一个明确的提示：浮层可能还没渲染出来，这一行不会缺席。
  el.waiting.hidden = state.roleplay.status !== 'awaitingInput';

  el.notice.hidden = !state.notice;
  el.notice.textContent = state.notice ? `${state.notice.code ?? ''} ${state.notice.message ?? ''}`.trim() : '';
});

/** 侧栏的三份状态不在 SSE 首推里（只有 meeting.state / roleplay.state 会），所以连上时主动拉一次。 */
async function refreshWorkspaces() {
  const { ok, data } = await api.getJson('/api/workspaces');

  if (!ok || !data) {
    return;
  }

  state = store.apply(state, 'workspace.list', data);
  state = store.apply(state, 'workspace.opened', {
    path: data.current,
    sessions: data.sessions ?? [],
  });

  // 待整理 / 已归档同样不在 SSE 首推里（只在动作之后广播）：连上时一起拉。
  const drafts = await api.getJson('/api/drafts');

  if (drafts.ok && drafts.data) {
    state = store.apply(state, 'draft.list', drafts.data);
  }

  render.scheduleRender();
}

connect({
  onEvent(type, payload) {
    state = store.apply(state, type, payload);
    render.scheduleRender();
  },
  onConnectionChange(status) {
    state = { ...state, connection: status };
    render.scheduleRender();

    if (status === 'open') {
      refreshWorkspaces();
    }
  },
});

// 资料正文查看器（点击卡片上的"正文"才拉取）。
initViewer();

// 阶段 1 的演示入口：地址带 ?dev=1 时自动开假事件流。
if (new URLSearchParams(location.search).get('dev') === '1') {
  api.get('/api/dev/fake');
}

render.scheduleRender();
