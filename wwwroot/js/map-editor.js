import { mount as mountBoard } from './board.js';
import { askFields, confirmAction } from './dialog.js';

/**
 * 地图编辑器（M1，设计文档 §3）。
 *
 * 刻意**不复用** `panel-combat.js` 那一套：那个模块管的是战术板在会议界面里的挂载。
 * 这里只依赖 `board.js` 的底图/网格/标定/测距，以及两个新入口
 * （`setMapId` 指定看哪张图、`setTokenDragHandler` 拖 token）。
 */

const boardHost = document.getElementById('editor-board');
const mapSelect = document.getElementById('map-select');
const uploadInput = document.getElementById('map-upload');
const saveButton = document.getElementById('save-button');
const statusNode = document.getElementById('editor-status');
const castList = document.getElementById('cast-list');
const tokenList = document.getElementById('token-list');

const board = mountBoard(boardHost, { view: 'dm' });

/** 当前地图与文档（tokens 就地修改；walls/rooms 原样带回，M1 不编辑它们）。 */
let mapId = null;
let mapDoc = null;

/** 正在等待落点的角色（点了左侧列表之后）。 */
let pendingActor = null;

/** 拖动中的 token id（拖动期间只改内存，松手才写回）。 */
let draggingTokenId = null;

let saveTimer = null;
let renderQueued = false;

function status(text, tone) {
  statusNode.textContent = text ?? '';
  statusNode.dataset.tone = tone ?? '';
}

// ---------------------------------------------------------------- 读取

async function loadMaps() {
  const response = await fetch('/api/maps');
  const maps = response.ok ? await response.json() : [];

  mapSelect.replaceChildren(
    ...maps.map((map) => {
      const option = document.createElement('option');
      option.value = map.id;
      option.textContent = `${map.name || map.id}（${map.width}×${map.height}）`;
      return option;
    }));

  if (maps.length === 0) {
    status('还没有底图：先用上面的「上传底图」传一张 webp / png。', 'warn');
    return [];
  }

  const next = mapId && maps.some((map) => map.id === mapId) ? mapId : (maps[0]?.id ?? null);
  mapSelect.value = next;
  return maps;
}

async function loadCast() {
  const response = await fetch('/api/roleplay/state');
  const state = response.ok ? await response.json() : {};
  const cast = Array.isArray(state.cast) ? state.cast : [];

  castList.replaceChildren(...cast.map((actor) => {
    const item = document.createElement('li');
    item.className = 'editor-cast-item';
    item.dataset.actorId = actor.id;
    item.textContent = actor.present === false ? `${actor.name}（不在场）` : actor.name;
    item.title = '点一下，然后在地图上点位置';
    item.addEventListener('click', () => pickActor(actor));
    return item;
  }));

  if (cast.length === 0) {
    castList.append(hint('当前没有在场角色：先起一场叙事，角色才会出现在这里。token 的 id 必须是角色 id。'));
  }

  return cast;
}

async function loadDocument(id) {
  const response = await fetch(`/api/maps/${id}/document`);

  if (!response.ok) {
    status(`读不到地图文档：${id}`, 'error');
    return null;
  }

  return response.json();
}

// ---------------------------------------------------------------- 渲染

function toVttTokens() {
  return (mapDoc?.tokens ?? []).map((token) => ({
    id: token.id,
    // board.js 用 characterId 作为 actorId（点 token 时广播它）。
    characterId: token.id,
    name: token.name,
    x: token.x,
    y: token.y,
    visible: token.visible !== false,
  }));
}

async function refreshBoard() {
  // 显式推文档：board 自己只在"按 id 首次加载"时拉一次，编辑器改完必须主动告诉它。
  board.setDocument(mapDoc);
  await board.render({ vtt: { tokens: toVttTokens() }, combat: null });
  renderTokenList();
  renderOverlay();
}

function renderTokenList() {
  const tokens = mapDoc?.tokens ?? [];

  tokenList.replaceChildren(...tokens.map((token) => {
    const item = document.createElement('li');
    item.className = 'editor-token-item';

    const label = document.createElement('span');
    label.textContent = `${token.name || token.id} · ${Math.round(token.x)},${Math.round(token.y)}`;

    const visible = document.createElement('input');
    visible.type = 'checkbox';
    visible.checked = token.visible !== false;
    visible.title = '对玩家可见（隐藏的 token 不进上下文）';
    visible.addEventListener('change', () => {
      token.visible = visible.checked;
      scheduleRender();
      scheduleSave();
    });

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.textContent = '×';
    remove.title = '从地图上移除（不影响角色本身）';
    remove.addEventListener('click', () => {
      mapDoc.tokens = mapDoc.tokens.filter((other) => other.id !== token.id);
      scheduleRender();
      scheduleSave();
    });

    item.append(label, visible, remove);
    return item;
  }));

  if (tokens.length === 0) {
    tokenList.append(hint('还没有 token。'));
  }
}

function hint(text) {
  const node = document.createElement('li');
  node.className = 'editor-hint';
  node.textContent = text;
  return node;
}

function scheduleRender() {
  if (renderQueued) {
    return;
  }

  renderQueued = true;
  // 不用 requestAnimationFrame：窗口不可见 / 后台标签页时 rAF 会被节流甚至完全不触发，
  // 表现为"数据改了但界面没变"，非常难查。定时器在任何情况下都会跑。
  setTimeout(() => {
    renderQueued = false;
    void refreshBoard();
  }, 16);
}

// ---------------------------------------------------------------- 编辑

async function pickActor(actor) {
  if (!mapDoc) {
    status('先选一张地图，再放 token。', 'warn');
    return;
  }

  pendingActor = actor;
  status(`在地图上点一下，放下 ${actor.name}（Esc 取消）。`);

  const points = await board.pickPoints(1);
  pendingActor = null;

  if (!points || points.length === 0) {
    status('已取消放置。');
    return;
  }

  const { x, y } = points[0];

  if (mapDoc.tokens.some((token) => token.id === actor.id)) {
    // 同一个角色只放一个 token：再点就是移动它。
    const existing = mapDoc.tokens.find((token) => token.id === actor.id);
    existing.x = x;
    existing.y = y;
  } else {
    mapDoc.tokens.push({
      id: actor.id,
      name: actor.name,
      x,
      y,
      visible: true,
      kind: 'character',
    });
  }

  await refreshBoard();
  scheduleSave();
  status(`${actor.name} 已放在 ${Math.round(x)},${Math.round(y)}。`);
}

board.setTokenDragHandler((actorId, phase, point, modifiers) => {
  if (!mapDoc || !point) {
    return;
  }

  // 拖 token 与画墙用同一套吸附（按住 Alt 关闭），否则"落点吸附正确"只对绘制成立。
  const snapped = snapPoint(point, modifiers?.altKey === true);

  const token = mapDoc.tokens.find((candidate) => candidate.id === actorId);

  if (!token) {
    return;
  }

  if (phase === 'start') {
    draggingTokenId = actorId;
    return;
  }

  token.x = snapped.x;
  token.y = snapped.y;

  if (phase === 'move') {
    scheduleRender();
    return;
  }

  // phase === 'end'：松手才写回（防抖），拖动过程中不发请求。
  draggingTokenId = null;
  scheduleRender();
  scheduleSave(0);
  status(`${token.name} → ${Math.round(token.x)},${Math.round(token.y)}（正在保存…）`);
});

/**
 * 迷雾的"有未保存改动"标记。迷雾**不自动保存**——它是信息闸门，一次误推给玩家影响很大，
 * 所以只改本地 + 进撤销栈，等用户按「保存」。其余绘制（墙 / 房间 / 区域 / token）仍自动保存。
 */
let fogDirty = false;

function markFogDirty(dirty) {
  fogDirty = dirty;
  const button = document.getElementById('save-button');

  if (button) {
    button.classList.toggle('is-dirty', dirty);
    button.title = dirty ? '迷雾有未保存改动' : '';
  }
}

function scheduleSave(delay = 500) {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => void save(), delay);
}

async function save() {
  if (!mapId || !mapDoc) {
    return;
  }

  // 自愈：旧数据里可能已经存了越界格（用户那张图就是因为这个每次保存都 400）。
  const pruned = pruneFogCells();

  if (pruned > 0) {
    status(`已清理 ${pruned} 个超出可绘制范围的雾格，正在保存…`, 'warn');
    scheduleRender();
  }

  const response = await fetch(`/api/maps/${mapId}/document`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      walls: mapDoc.walls ?? [],
      rooms: mapDoc.rooms ?? [],
      tokens: mapDoc.tokens ?? [],
      // M6a：迷雾必须一起提交——否则刷完"已保存"但服务端还是旧的。
      fog: mapDoc.fog ?? { enabled: false, explored: [], visible: [] },
      // M7：区域同理。
      zones: mapDoc.zones ?? [],
    }),
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    status(`保存失败：${payload.error ?? response.status}`, 'error');
    return;
  }

  markFogDirty(false);
  status(`已保存（${new Date().toLocaleTimeString()}）。`, 'ok');
}

// ---------------------------------------------------------------- 上传 / 切换 / 标定

uploadInput.addEventListener('change', async () => {
  const file = uploadInput.files?.[0];

  if (!file) {
    return;
  }

  const form = new FormData();
  form.append('file', file);
  status(`上传 ${file.name}…`);

  const response = await fetch('/api/maps/upload', { method: 'POST', body: form });
  const payload = await response.json().catch(() => ({}));

  if (!response.ok) {
    status(`上传失败：${payload.error ?? response.status}`, 'error');
    return;
  }

  mapId = payload.id;
  await loadMaps();
  mapSelect.value = mapId;
  await openMap(mapId);
  status(payload.reused ? '这张图已经在库里了，直接打开。' : '上传完成。用「标定」调一下网格，再放 token。', 'ok');
  uploadInput.value = '';
});

document.getElementById('map-blank').addEventListener('click', async () => {
  const values = await askFields({
    title: '新建空白地图',
    hint: '没有底图：纯色底 + 网格，墙 / 房间 / token 照常画。标定后就能按格绘制。',
    fields: [{ name: 'name', label: '地图名', value: '未命名地图' }],
    validate: (input) => (input.name.trim().length === 0 ? '给这张地图起个名字。' : null),
    okText: '新建',
  });

  if (!values) {
    return;
  }

  const response = await fetch('/api/maps/blank', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: values.name.trim() }),
  });

  const payload = await response.json().catch(() => ({}));

  if (!response.ok) {
    status(`新建失败：${payload.error ?? response.status}`, 'error');
    return;
  }

  mapId = payload.id;
  await loadMaps();
  mapSelect.value = mapId;
  await openMap(mapId);
  status(`已新建空白地图「${payload.name}」：先标定，再画墙 / 房间。`, 'ok');
});

mapSelect.addEventListener('change', () => void openMap(mapSelect.value));

async function openMap(id) {
  if (!id) {
    return;
  }

  mapId = id;
  board.setMapId(id);
  mapDoc = await loadDocument(id);

  if (!mapDoc) {
    return;
  }

  // 旧文件可能没有这三个字段（F2/F4）——loader 已按空数组返回，这里再兜一层。
  mapDoc.walls ??= [];
  mapDoc.rooms ??= [];
  mapDoc.tokens ??= [];

  // 加载即自愈：坏数据（越界雾格）会让后续每次保存都 400，先剔掉再说话。
  const pruned = pruneFogCells();

  if (pruned > 0) {
    markFogDirty(true);
  }

  await refreshBoard();

  if (pruned > 0) {
    status(`已清理 ${pruned} 个超出可绘制范围的雾格——确认无误后点「保存」。`, 'warn');
    return;
  }

  if (!mapDoc.grid || !(mapDoc.grid.dpi > 0)) {
    status(
      mapDoc.image
        ? '这张图还没标定：点「标定」，在图上点两个点并填跨了几格。'
        : '空白地图（手绘）：先点「标定」定下格宽，再画墙 / 房间。',
      'warn');
  } else {
    status(`已打开 ${mapDoc.name || id}（${mapDoc.tokens.length} 个 token）。`);
  }
}

// 标定复用 board 自带的按钮（它会发这个事件），这里负责算 dpi / offset 并写回。
window.addEventListener('meetingroom:board-calibrate', async () => {
  if (!mapId || !mapDoc) {
    return;
  }

  status('标定：在图上点两个点（同一条格线的两端）。Esc 取消。');
  boardHost.querySelector('.overlay-shell')?.style.setProperty('pointer-events', 'none');

  let points = [];

  try {
    points = await board.pickPoints(2);
  } finally {
    boardHost.querySelector('.overlay-shell')?.style.removeProperty('pointer-events');
  }

  if (!points || points.length < 2) {
    status('已取消标定。');
    return;
  }

  const [first, second] = points;
  const distance = Math.hypot(second.x - first.x, second.y - first.y);

  // 一次表单收两个字段（原来两次 原生 prompt：会阻塞主线程，自动化环境直接卡死）。
  const values = await askFields({
    title: '标定网格',
    hint: `量得的像素距离：${distance.toFixed(1)} px。填这两点跨了几格，反算每格多少像素。`,
    fields: [
      { name: 'cells', label: '两点跨', type: 'number', value: '10', suffix: '格' },
      {
        name: 'feet',
        label: '每格',
        type: 'number',
        value: String(mapDoc.grid?.scale ?? 5),
        suffix: '尺',
      },
    ],
    validate: (input) => {
      const cells = Number.parseFloat(input.cells);
      const feet = Number.parseFloat(input.feet);

      if (!Number.isFinite(cells) || cells <= 0) {
        return '跨格数必须是大于 0 的数字。';
      }

      if (!Number.isFinite(feet) || feet <= 0) {
        return '每格尺数必须是大于 0 的数字。';
      }

      return null;
    },
    okText: '写入标定',
  });

  if (!values) {
    status('已取消标定。');
    return;
  }

  const cells = Number.parseFloat(values.cells);
  const scale = Number.parseFloat(values.feet);
  const dpi = distance / cells;
  const response = await fetch(`/api/maps/${mapId}/calibration`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      dpi,
      offsetX: first.x % dpi,
      offsetY: first.y % dpi,
      gridType: 'square',
      scale,
    }),
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    status(`标定失败：${payload.error ?? response.status}`, 'error');
    return;
  }

  mapDoc = await loadDocument(mapId);
  await refreshBoard();
  status(`标定完成：dpi ${dpi.toFixed(2)}，每格 ${scale} 尺。`, 'ok');
});

saveButton.addEventListener('click', () => void save());

// ---------------------------------------------------------------- M2：绘制 / 选中 / 顶点 / 撤销

const overlayLayer = document.getElementById('editor-overlay');
const wallKindSelect = document.getElementById('wall-kind');
const snapButton = document.getElementById('snap-toggle');
const undoButton = document.getElementById('undo-button');
const redoButton = document.getElementById('redo-button');
const deleteButton = document.getElementById('delete-button');

const fogToolButton = document.getElementById('fog-tool');
const fogPens = document.querySelector('.fog-pens');
const FOG_PENS = ['reveal', 'sense', 'hide'];

const HISTORY_LIMIT = 30;

/** 区域形状（polygon / circle）。 */
let zoneShape = 'polygon';

/** 当前迷雾笔：null 表示没在刷迷雾。 */
let fogPen = null;
/** 本次拖动已经刷过的格子（按格去重：同一格拖过就不重复写）。 */
let fogPainted = null;

/** 当前工具：`select` / `wall` / `room`。 */
let tool = 'select';
let wallKind = 'wall';
/** 吸附粒度：`corner` 格点 / `center` 格心；按住 Alt 临时关闭。 */
let snapGranularity = 'corner';
/** 正在画的对象：`{ type, points: [{x,y}] }`，`type` 为 null 表示没在画。 */
let draft = { type: null, points: [] };
/** 选中的墙 / 房间：`{ type: 'wall'|'room', id }`。 */
let selected = null;

const history = [];
const futureHistory = [];

function snapshot() {
  return JSON.stringify({
    walls: mapDoc?.walls ?? [],
    rooms: mapDoc?.rooms ?? [],
    tokens: mapDoc?.tokens ?? [],
    // 迷雾也要进撤销栈：否则刷错了只能靠"再刷一遍回去"，而刷错往往是大片。
    fog: mapDoc?.fog ?? null,
    // 区域同理：Delete 删掉一个区域后撤销救不回来（快照里没有 zones 就恢复不出它）。
    zones: mapDoc?.zones ?? [],
  });
}

function applySnapshot(json) {
  if (!mapDoc) {
    return;
  }

  const data = JSON.parse(json);
  const fogChanged = data.fog !== undefined
    && JSON.stringify(data.fog) !== JSON.stringify(mapDoc.fog ?? null);
  const objectsBefore = JSON.stringify([mapDoc.walls, mapDoc.rooms, mapDoc.tokens]);

  mapDoc.walls = data.walls;
  mapDoc.rooms = data.rooms;
  mapDoc.tokens = data.tokens;

  if (data.fog !== undefined) {
    mapDoc.fog = data.fog;
  }

  if (Array.isArray(data.zones)) {
    mapDoc.zones = data.zones;
  }

  selected = null;
  scheduleRender();
  renderOverlay();

  const objectsChanged = JSON.stringify([mapDoc.walls, mapDoc.rooms, mapDoc.tokens]) !== objectsBefore;

  if (objectsChanged) {
    scheduleSave(0);
  }

  if (fogChanged) {
    // 迷雾是手动保存的（见 setFogPen / 松手处）：撤销只回本地，不偷偷提交。
    markFogDirty(true);
  }
}

/** 每次改动**之前**调用：记下"改之前长什么样"，撤销就是把它放回去。 */
function pushHistory() {
  const current = snapshot();

  if (history.at(-1) === current) {
    return;
  }

  history.push(current);

  if (history.length > HISTORY_LIMIT) {
    history.shift();
  }

  futureHistory.length = 0;
}

function undo() {
  if (history.length === 0) {
    status('没有可撤销的操作。');
    return;
  }

  futureHistory.push(snapshot());
  applySnapshot(history.pop());
  status(`已撤销（还能撤 ${history.length} 步）。`);
}

function redo() {
  if (futureHistory.length === 0) {
    status('没有可重做的操作。');
    return;
  }

  history.push(snapshot());
  applySnapshot(futureHistory.pop());
  status(`已重做（还能重做 ${futureHistory.length} 步）。`);
}

/** 吸附：格点 = 网格交点，格心 = 每格中心；按住 Alt 关闭。 */
function snapPoint(raw, altKey) {
  const grid = mapDoc?.grid;

  if (altKey || !grid || !(grid.dpi > 0)) {
    return { x: raw.x, y: raw.y };
  }

  const dpi = grid.dpi;
  const offsetX = grid.offsetX ?? 0;
  const offsetY = grid.offsetY ?? 0;
  const shift = snapGranularity === 'center' ? dpi / 2 : 0;

  return {
    x: offsetX + Math.round((raw.x - offsetX - shift) / dpi) * dpi + shift,
    y: offsetY + Math.round((raw.y - offsetY - shift) / dpi) * dpi + shift,
  };
}

function nextId(prefix) {
  return `${prefix}${Math.random().toString(36).slice(2, 7)}`;
}

function round2(value) {
  return Math.round(value * 100) / 100;
}

/**
 * 迷雾三支笔（M6a）：点「迷雾」进入；再点在同一按钮循环 揭示 → 视野 → 遮黑。
 * 未标定（dpi <= 0）时禁用并说明——没有格就没有格索引。
 */
function cycleFogPen() {
  const dpi = mapDoc?.grid?.dpi ?? 0;

  if (!(dpi > 0)) {
    status('还没标定：迷雾按格刷，先点「标定」定下格宽。', 'warn');
    return;
  }

  if (!mapDoc.fog) {
    mapDoc.fog = { enabled: true, explored: [], visible: [] };
  }

  mapDoc.fog.enabled = true;

  if (fogPen === null) {
    fogPen = FOG_PENS[0];
  } else {
    const index = FOG_PENS.indexOf(fogPen);
    fogPen = index >= FOG_PENS.length - 1 ? null : FOG_PENS[index + 1];
  }

  applyFogPenUi();
}

/**
 * 收起迷雾笔，**只改 UI 与状态、不碰 tool**。
 * 必须在 `setTool` 里用它而不是 `applyFogPenUi()`——后者在 fogPen 非空时会把 tool 拉回 select，
 * 从 setTool 里调就会递归。
 */
function clearFogPen() {
  fogPen = null;
  fogPainted = null;
  fogPens.hidden = true;

  for (const button of fogPens.querySelectorAll('[data-fog]')) {
    button.classList.remove('is-active');
  }

  fogToolButton.classList.remove('is-active');
}

function applyFogPenUi() {
  fogPens.hidden = true;

  for (const button of fogPens.querySelectorAll('[data-fog]')) {
    button.classList.toggle('is-active', button.dataset.fog === fogPen);
    button.disabled = !(mapDoc?.grid?.dpi > 0);
  }

  if (fogPen === null) {
    fogToolButton.classList.remove('is-active');
    status('迷雾笔已收起。');
    return;
  }

  fogPens.hidden = false;
  fogToolButton.classList.add('is-active');

  // 离开其它绘制工具：迷雾与画墙/画房间互斥。
  if (tool !== 'select') {
    setTool('select');
  }

  const label = fogPen === 'reveal' ? '揭示（黑 → 半透明）' : fogPen === 'sense' ? '视野（→ 全亮）' : '遮黑（→ 黑）';
  status(`迷雾：${label}。按住拖动按格刷；刷完点「保存」才写进地图。`);
}

/** 把一格按当前笔写成目标状态（三态：都不在 = 未探索）。 */
function paintFogCell(cell) {
  const fog = mapDoc.fog;
  const key = `${cell.x},${cell.y}`;
  const explored = new Set(fog.explored.map((c) => `${c[0]},${c[1]}`));
  const visible = new Set(fog.visible.map((c) => `${c[0]},${c[1]}`));

  if (fogPen === 'reveal') {
    explored.add(key);
    visible.delete(key);
  } else if (fogPen === 'sense') {
    explored.add(key);
    visible.add(key);
  } else {
    explored.delete(key);
    visible.delete(key);
  }

  const toPairs = (set) => [...set].map((item) => item.split(',').map(Number));
  fog.explored = toPairs(explored);
  fog.visible = toPairs(visible);
}

/**
 * 迷雾的边缘冗余格数（与 `MapDocumentValidator.FogMarginCells` 必须一致）。
 *
 * <p>迷雾**允许比底图大**：底图常有白边、图外空白、贴边的东西，要靠雾遮住。
 * 所以合法范围是 `[-margin, cols-1+margin]`，不是 `[0, cols)`——
 * 早先按"格索引非负"卡，用户想遮边距就被 400 挡住（`迷雾格 [3,-3] 超出画布`）。</p>
 */
const FOG_MARGIN_CELLS = 3;

/**
 * 屏幕坐标 → 格索引（`gx = floor((x - offsetX) / dpi)`）。
 * 超出"网格 + 冗余"才返回 null（那才是画布之外太远的地方）。
 */
function fogCellAt(clientX, clientY) {
  const grid = mapDoc?.grid;

  if (!grid || !(grid.dpi > 0)) {
    return null;
  }

  const point = board.toImagePoint(clientX, clientY);
  const offsetX = grid.offsetX ?? 0;
  const offsetY = grid.offsetY ?? 0;
  const x = Math.floor((point.x - offsetX) / grid.dpi);
  const y = Math.floor((point.y - offsetY) / grid.dpi);
  const { cols, rows } = fogGridSize();

  if (x < -FOG_MARGIN_CELLS || y < -FOG_MARGIN_CELLS
    || x > cols - 1 + FOG_MARGIN_CELLS || y > rows - 1 + FOG_MARGIN_CELLS) {
    return null;
  }

  return { x, y };
}

/** 网格的列数 / 行数：由底图逻辑尺寸与标定（dpi / 边距）换算出来。 */
function fogGridSize() {
  const grid = mapDoc?.grid ?? {};
  const dpi = grid.dpi > 0 ? grid.dpi : 0;

  if (!dpi) {
    return { cols: 0, rows: 0 };
  }

  const width = mapDoc?.image?.width || 2000;
  const height = mapDoc?.image?.height || 1500;

  return {
    cols: Math.max(0, Math.floor((width - (grid.offsetX ?? 0)) / dpi)),
    rows: Math.max(0, Math.floor((height - (grid.offsetY ?? 0)) / dpi)),
  };
}

/**
 * 剔除越界雾格（自愈）。用户那张图里已经存了负格，所以加载后每次保存都 400——
 * 光在前面拦住新刷不够，得把旧数据也清掉，否则他永远保存不了。
 * 返回被剔除的格数。
 */
function pruneFogCells() {
  const fog = mapDoc?.fog;

  if (!fog) {
    return 0;
  }

  const { cols, rows } = fogGridSize();

  if (!cols || !rows) {
    return 0;
  }

  // 冗余内的负数格是**合法数据**（遮边用），不能剔——只剔真正的脏数据。
  const inside = (cell) => Array.isArray(cell)
    && cell[0] >= -FOG_MARGIN_CELLS && cell[1] >= -FOG_MARGIN_CELLS
    && cell[0] <= cols - 1 + FOG_MARGIN_CELLS && cell[1] <= rows - 1 + FOG_MARGIN_CELLS;

  const exploredBefore = (fog.explored ?? []).length;
  const visibleBefore = (fog.visible ?? []).length;
  fog.explored = (fog.explored ?? []).filter(inside);
  fog.visible = (fog.visible ?? []).filter(inside);

  return (exploredBefore - fog.explored.length) + (visibleBefore - fog.visible.length);
}

const ZONE_KIND_WIRE = { spell: 'spell', terrain: 'terrain', portal: 'portal', hazard: 'hazard', custom: 'custom' };

function setTool(next) {
  tool = next;
  draft = { type: null, points: [] };
  selected = null;

  // 离开迷雾笔：迷雾借的是 select 态，所以切到任何**别的**工具都要把它收起来。
  // 以前这里只重置 draft / selected，导致切到画墙后 fogPen 还在，
  // pointerdown 仍走迷雾分支（点上没反应、或者反而刷了一格雾）。
  if (next !== 'select' && fogPen !== null) {
    clearFogPen();
  }

  for (const button of document.querySelectorAll('.editor-tools [data-tool]')) {
    button.classList.toggle('is-active', button.dataset.tool === next);
  }

  // 形状命中只在"选择"状态打开：绘制时点房间内部/墙线必须仍然是落点（不能丢点）。
  board.setShapeHit(next === 'select');

  renderOverlay();
  status(next === 'select'
    ? '选择：点墙 / 房间 / 区域可选中；拖手柄改形状；Delete 删除。'
    : next === 'pan'
      // 平移交给 board 自己做（它本来就能拖视图）——编辑器这边不拦事件即可。
      ? '移动：左键拖动画布，滚轮缩放。按住空格可随时临时切换。'
      : next === 'wall'
        ? '画墙：点击加点，双击或 Esc 结束；按住 Shift 锁定水平/垂直。'
        : '画房间：点击加点，点回起点闭合。');
}

function addDraftPoint(event) {
  if (!mapDoc) {
    return;
  }

  let point = board.toImagePoint(event.clientX, event.clientY);
  point = snapPoint(point, event.altKey);

  const last = draft.points.at(-1);

  // Shift：相对上一点锁定水平 / 垂直。
  if (event.shiftKey && last) {
    point = Math.abs(point.x - last.x) >= Math.abs(point.y - last.y)
      ? { x: point.x, y: last.y }
      : { x: last.x, y: point.y };
  }

  // 圆形区域：点一下就是圆心，直接弹表单（半径在里面填）。
  if (draft.type === 'zone' && zoneShape === 'circle') {
    void finishZone({ type: 'circle', center: [round2(point.x), round2(point.y)], radiusCells: 2 });
    return;
  }

  // 房间 / 多边形区域：点回起点就闭合（阈值按格宽给，避免缩得很小时点不中）。
  if ((draft.type === 'room' || draft.type === 'zone') && draft.points.length >= 3) {
    const first = draft.points[0];
    const tolerance = (mapDoc.grid?.dpi ?? 40) * 0.4;

    if (Math.hypot(point.x - first.x, point.y - first.y) <= tolerance) {
      if (draft.type === 'zone') {
        void finishZone({
          type: 'polygon',
          points: draft.points.map((item) => [round2(item.x), round2(item.y)]),
        });
      } else {
        finishRoom();
      }

      return;
    }
  }

  draft.points.push(point);
  renderOverlay();
  status(`${draft.type === 'wall' ? '墙' : '房间'}：${draft.points.length} 个点。`);
}

function finishWall() {
  if (!mapDoc || draft.points.length < 2) {
    draft = { type: null, points: [] };
    renderOverlay();
    status('墙至少要两个点。');
    return;
  }

  pushHistory();
  mapDoc.walls.push({
    id: nextId('w'),
    points: draft.points.map((point) => [round2(point.x), round2(point.y)]),
    kind: wallKind,
    open: false,
  });

  draft = { type: null, points: [] };
  scheduleRender();
  renderOverlay();
  scheduleSave(0);
  status(`已加一段${wallKind === 'door' ? '门' : wallKind === 'window' ? '窗' : '墙'}（共 ${mapDoc.walls.length} 段）。`);
}

async function finishRoom() {
  if (!mapDoc || draft.points.length < 3) {
    draft = { type: null, points: [] };
    renderOverlay();
    status('房间至少要三个点。');
    return;
  }

  const polygon = draft.points.map((point) => [round2(point.x), round2(point.y)]);
  const attributes = await askRoomAttributes();

  if (!attributes) {
    status('已取消画房间。');
    draft = { type: null, points: [] };
    renderOverlay();
    return;
  }

  pushHistory();
  mapDoc.rooms.push({
    id: nextId('r'),
    name: attributes.name,
    polygon,
    light: attributes.light,
    note: '',
  });

  draft = { type: null, points: [] };
  scheduleRender();
  renderOverlay();
  scheduleSave(0);
  status(`已加房间「${attributes.name}」（共 ${mapDoc.rooms.length} 个）。`);
}

/** 弹页面内表单填名称与光照；取消返回 `null`。 */
function askRoomAttributes() {
  const form = document.getElementById('room-form');
  const nameInput = document.getElementById('room-name');
  const lightSelect = document.getElementById('room-light');

  nameInput.value = '房间';
  lightSelect.value = 'dim';
  form.hidden = false;
  nameInput.focus();
  nameInput.select();

  return new Promise((resolve) => {
    const cleanup = () => {
      form.hidden = true;
      document.getElementById('room-ok').removeEventListener('click', onOk);
      document.getElementById('room-cancel').removeEventListener('click', onCancel);
      nameInput.removeEventListener('keydown', onKey);
    };

    const onOk = () => {
      const name = nameInput.value.trim() || '房间';
      const light = lightSelect.value;
      cleanup();
      resolve({ name, light: ['bright', 'dim', 'dark'].includes(light) ? light : 'dim' });
    };

    const onCancel = () => {
      cleanup();
      resolve(null);
    };

    const onKey = (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        onOk();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        onCancel();
      }
    };

    document.getElementById('room-ok').addEventListener('click', onOk);
    document.getElementById('room-cancel').addEventListener('click', onCancel);
    nameInput.addEventListener('keydown', onKey);
  });
}

/**
 * 区域属性表单（M7）：形状落定后弹出来。取消返回 null。
 */
function askZoneAttributes(shapeType = 'polygon', initial = null) {
  const form = document.getElementById('zone-form');
  const name = document.getElementById('zone-name');
  const kind = document.getElementById('zone-kind');
  const note = document.getElementById('zone-note');
  const color = document.getElementById('zone-color');
  const visible = document.getElementById('zone-visible');
  const radiusRow = document.getElementById('zone-radius-row');
  const radius = document.getElementById('zone-radius');

  // 选中已有区域再编辑时预填当前值；新建时用默认值。
  name.value = initial?.name ?? '区域';
  kind.value = initial?.kind ?? 'spell';
  note.value = initial?.note ?? '';
  color.value = initial?.color ?? '#c9a227';
  visible.checked = initial?.visible ?? true;
  radius.value = String(initial?.radiusCells ?? 2);
  // 半径只有圆形才用得上。
  radiusRow.hidden = shapeType !== 'circle';
  form.hidden = false;
  if (shapeType === 'circle') {
    radius.focus();
    radius.select();
  } else {
    name.focus();
    name.select();
  }

  return new Promise((resolve) => {
    const cleanup = () => {
      form.hidden = true;
      document.getElementById('zone-ok').removeEventListener('click', onOk);
      document.getElementById('zone-cancel').removeEventListener('click', onCancel);
      name.removeEventListener('keydown', onKey);
    };

    const onOk = () => {
      if (shapeType === 'circle') {
        const cells = Number.parseFloat(radius.value);

        if (!Number.isFinite(cells) || cells < 1 || cells > 12) {
          // 不静默：半径不合规就把错误写在提示里并留在表单内。
          status('半径要 1–12 格。', 'error');
          radius.focus();
          return;
        }
      }

      const values = {
        name: name.value.trim() || '区域',
        kind: ZONE_KIND_WIRE[kind.value] ?? 'custom',
        note: note.value.trim(),
        color: color.value,
        visible: visible.checked,
        radiusCells: Number.parseFloat(radius.value) || 2,
      };
      cleanup();
      resolve(values);
    };

    const onCancel = () => {
      cleanup();
      resolve(null);
    };

    const onKey = (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        onOk();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        onCancel();
      }
    };

    document.getElementById('zone-ok').addEventListener('click', onOk);
    document.getElementById('zone-cancel').addEventListener('click', onCancel);
    name.addEventListener('keydown', onKey);
  });
}

/** 形状落定 → 弹属性表单 → 写进 zones（自动保存）。 */
async function finishZone(shape) {
  if (!mapDoc) {
    return;
  }

  const attributes = await askZoneAttributes(shape.type);

  if (!attributes) {
    draft = { type: null, points: [] };
    renderOverlay();
    status('已取消画区域。');
    return;
  }

  pushHistory();
  mapDoc.zones = mapDoc.zones ?? [];

  if (shape.type === 'circle') {
    shape.radiusCells = attributes.radiusCells ?? shape.radiusCells ?? 2;
  }

  mapDoc.zones.push({
    id: nextId('z'),
    name: attributes.name,
    kind: attributes.kind,
    shape,
    color: attributes.color,
    note: attributes.note,
    visible: attributes.visible,
  });

  draft = { type: null, points: [] };
  scheduleRender();
  renderOverlay();
  scheduleSave(0);
  status(`已加区域「${attributes.name}」（共 ${mapDoc.zones.length} 个）。`);
}

/** 选中对象的点集（图片像素坐标）。 */
/**
 * 选中对象上可拖的点（M7/P1 之后墙、房间、区域共用同一套手柄）。
 * 返回 `{ points, set }`：`points` 只用来画手柄，写回一律走 `set`——
 * 因为区域的点藏在 `shape.points` / `shape.center` 里，圆的半径还要换算，形状各不相同。
 */
function pointsOfSelection() {
  if (!mapDoc || !selected) {
    return null;
  }

  if (selected.type === 'wall') {
    const wall = mapDoc.walls.find((item) => item.id === selected.id);

    if (!wall) {
      return null;
    }

    return {
      points: wall.points,
      set: (index, point) => {
        wall.points[index] = [point.x, point.y];
      },
    };
  }

  if (selected.type === 'zone') {
    const zone = (mapDoc.zones ?? []).find((item) => item.id === selected.id);
    const shape = zone?.shape;

    if (!shape) {
      return null;
    }

    if (shape.type === 'polygon') {
      const points = shape.points ?? [];
      return {
        points,
        set: (index, point) => {
          points[index] = [point.x, point.y];
        },
      };
    }

    // 圆形：0 号手柄是圆心，1 号是半径手柄（放在圆心 +x 方向上，拖动改 radiusCells）。
    const center = toXY(shape.center) ?? { x: 0, y: 0 };
    const perCell = cellSize();
    const radius = Math.max(1, shape.radiusCells ?? 1) * perCell;

    return {
      points: [center, { x: center.x + radius, y: center.y }],
      set: (index, point) => {
        if (index === 0) {
          shape.center = [point.x, point.y];
          return;
        }

        // 半径手柄：按"到圆心的距离"折回格数，钳在 1–12（与 MapDocumentValidator 一致）。
        const anchor = toXY(shape.center) ?? center;
        const cells = Math.hypot(point.x - anchor.x, point.y - anchor.y) / perCell;
        shape.radiusCells = Math.min(12, Math.max(1, Math.round(cells)));
      },
    };
  }

  const room = mapDoc.rooms.find((item) => item.id === selected.id);

  if (!room) {
    return null;
  }

  return {
    points: room.polygon,
    set: (index, point) => {
      room.polygon[index] = [point.x, point.y];
    },
  };
}

/** `[x, y]` 或 `{x, y}` 都收（存档里是数组，内存里可能已被规范成对象）。 */
function toXY(raw) {
  if (!raw) {
    return null;
  }

  return Array.isArray(raw) ? { x: raw[0], y: raw[1] } : { x: raw.x, y: raw.y };
}

/** 一格的像素边长（区域半径以"格"为单位，换算成像素要用它）。 */
function cellSize() {
  const dpi = Number(mapDoc?.grid?.dpi ?? 0);
  return dpi > 0 ? dpi : 100;
}

/** 顶点层用 HTML 定位：直接跟 `<img>` 的实际显示框走，不复制 board 的视图变换。 */
function renderOverlay() {
  if (!overlayLayer) {
    return;
  }

  for (const element of boardHost.querySelectorAll('.board-wall--selected, .board-room--selected, .board-zone--selected')) {
    element.classList.remove('board-wall--selected', 'board-room--selected', 'board-zone--selected');
  }

  if (selected && mapDoc) {
    const element = boardHost.querySelector(`[data-${selected.type}-id="${selected.id}"]`);
    const className = selected.type === 'wall'
      ? 'board-wall--selected'
      : selected.type === 'zone' ? 'board-zone--selected' : 'board-room--selected';
    element?.classList.add(className);
  }

  const nodes = [];
  const image = boardHost.querySelector('.board-map');

  if (!image || !mapDoc) {
    overlayLayer.replaceChildren();
    return;
  }

  // sketch（无底图）时 `<img>` 是 hidden 的，getBoundingClientRect 全 0 —— 直接拿它算会把手柄
  // 全堆到一个点上（M7 的墙 / 房间顶点就这样）。没有底图时贴图框就是视口框，逻辑尺寸按
  // 空白地图的约定 2000×1500（MapStore.BlankWidth / BlankHeight）。
  const sketch = !mapDoc.image;
  const boxElement = sketch ? boardHost.querySelector('.board-viewport') ?? image : image;
  const imageRect = boxElement.getBoundingClientRect();
  const hostRect = boardHost.getBoundingClientRect();
  const width = mapDoc.image?.width || image.naturalWidth || (sketch ? 2000 : 1);
  const height = mapDoc.image?.height || image.naturalHeight || (sketch ? 1500 : 1);

  // `.board-map` 是 object-fit: contain：元素框不等于贴图框，必须像 board.toImagePoint 那样
  // 先算实际贴图尺寸与居中偏移，否则顶点会整体错位（尤其宽高比与容器不一致时）。
  const boxRatio = imageRect.width / imageRect.height;
  const imageRatio = width / height;
  let drawWidth = imageRect.width;
  let drawHeight = imageRect.height;
  let offsetX = 0;
  let offsetY = 0;

  if (imageRatio > boxRatio) {
    drawHeight = imageRect.width / imageRatio;
    offsetY = (imageRect.height - drawHeight) / 2;
  } else {
    drawWidth = imageRect.height * imageRatio;
    offsetX = (imageRect.width - drawWidth) / 2;
  }

  // 有效缩放补偿：`getBoundingClientRect()` 返回的是**视觉**像素，而 `style.left` 写在 overlay 的
  // CSS 坐标里。浏览器缩放（Ctrl +/-）不改 CSS zoom，两者一比一；但页面里若用 CSS `zoom`
  // （包括调试时模拟 125%），差值会被再放大一次，手柄就会随距离越来越偏。除一下两种情况都对。
  const zoom = Number.parseFloat(getComputedStyle(document.documentElement).zoom) || 1;
  const ratio = (drawWidth / width) / zoom;
  const originLeft = (imageRect.left - hostRect.left + offsetX) / zoom;
  const originTop = (imageRect.top - hostRect.top + offsetY) / zoom;

  const toScreen = (point) => ({
    left: originLeft + point.x * ratio,
    top: originTop + point.y * ratio,
  });

  const selection = pointsOfSelection();
  const groups = [];

  if (selection?.points) {
    groups.push({ target: selected.type, id: selected.id, points: selection.points });
  }

  if (draft.type) {
    groups.push({ target: 'draft', id: '', points: draft.points });
  }

  for (const group of groups) {
    group.points.forEach((rawPoint, index) => {
      const point = Array.isArray(rawPoint) ? { x: rawPoint[0], y: rawPoint[1] } : rawPoint;
      const screen = toScreen(point);
      const handle = document.createElement('div');
      handle.className = 'editor-vertex';
      handle.style.left = `${screen.left}px`;
      handle.style.top = `${screen.top}px`;
      handle.dataset.target = group.target;
      handle.dataset.objectId = group.id;
      handle.dataset.index = String(index);
      nodes.push(handle);
    });
  }

  overlayLayer.replaceChildren(...nodes);
  renderDraftPreview();
}

/** 正在画的折线：虚线提示（还没写进文档，不能画成"已完成"的样子）。 */
function renderDraftPreview() {
  boardHost.querySelector('.board-draft')?.remove();

  if (!draft.type || draft.points.length < 2) {
    return;
  }

  const polyline = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
  polyline.setAttribute('points', draft.points.map((point) => `${point.x},${point.y}`).join(' '));
  polyline.classList.add('board-draft');

  // 放进 board 的 SVG（那里的坐标就是图片像素，不用再换算）。
  boardHost.querySelector('.board-viewport svg')?.append(polyline);
}

function deleteSelected() {
  if (!mapDoc || !selected) {
    status('先选中一堵墙或一个房间。');
    return;
  }

  pushHistory();
  const removing = selected;

  if (removing.type === 'wall') {
    mapDoc.walls = mapDoc.walls.filter((wall) => wall.id !== removing.id);
  } else if (removing.type === 'zone') {
    mapDoc.zones = (mapDoc.zones ?? []).filter((zone) => zone.id !== removing.id);
  } else {
    mapDoc.rooms = mapDoc.rooms.filter((room) => room.id !== removing.id);
  }

  selected = null;
  scheduleRender();
  renderOverlay();
  scheduleSave(0);
  status('已删除。');
}

// ---- 顶点拖拽：capture 阶段先于 board 自己的 pointerdown，避免被当成拖视图 ----

let draggingVertex = null;

boardHost.addEventListener('pointerdown', (event) => {
  if (event.button !== 0 || !mapDoc) {
    return;
  }

  // 移动工具（或按住空格临时平移）：完全不拦事件，交给 board 自己拖视图。
  // 顺序上排在迷雾笔**之前**——平移时不该刷雾（否则一拖一手雾格）。
  if (tool === 'pan' || spacePanning) {
    return;
  }

  // 迷雾笔（M6a）：按住拖动按格刷，按格去重；松手才标"未保存"。
  if (fogPen) {
    event.stopPropagation();
    event.preventDefault();

    pushHistory();
    fogPainted = new Set();
    const cell = fogCellAt(event.clientX, event.clientY);

    if (cell) {
      fogPainted.add(`${cell.x},${cell.y}`);
      paintFogCell(cell);
      scheduleRender();
    }

    return;
  }

  const handle = event.target.closest('.editor-vertex');

  if (handle && handle.dataset.target !== 'draft') {
    pushHistory();
    draggingVertex = {
      target: handle.dataset.target,
      objectId: handle.dataset.objectId,
      index: Number(handle.dataset.index),
      moved: false,
    };
    handle.dataset.active = 'true';
    event.stopPropagation();
    event.preventDefault();
    return;
  }

  if (tool === 'select') {
    return;
  }

  // 绘制模式：吃掉事件，board 就不会把它当成"取点 / 拖视图"。
  event.stopPropagation();
  event.preventDefault();

  if (draft.type === null) {
    draft = { type: tool, points: [] };
  }

  addDraftPoint(event);
}, true);

boardHost.addEventListener('pointermove', (event) => {
  if (fogPainted && mapDoc) {
    const cell = fogCellAt(event.clientX, event.clientY);

    if (cell && !fogPainted.has(`${cell.x},${cell.y}`)) {
      fogPainted.add(`${cell.x},${cell.y}`);
      paintFogCell(cell);
      scheduleRender();
    }

    return;
  }

  if (!draggingVertex || !mapDoc) {
    return;
  }

  const pointsRef = pointsOfSelection();

  if (!pointsRef?.set) {
    return;
  }

  const raw = board.toImagePoint(event.clientX, event.clientY);
  const point = snapPoint(raw, event.altKey);
  // 写回交给适配器：墙 / 房间 / 多边形是数组元素，圆形要按"到圆心的距离"折回格数。
  pointsRef.set(draggingVertex.index, { x: round2(point.x), y: round2(point.y) });
  draggingVertex.moved = true;
  scheduleRender();
  renderOverlay();
}, true);

for (const type of ['pointerup', 'pointercancel']) {
  boardHost.addEventListener(type, () => {
    if (fogPainted) {
      const painted = fogPainted.size;
      fogPainted = null;

      if (painted > 0) {
        // 迷雾**不自动保存**：只改本地并标记未保存，等用户按「保存」。
        markFogDirty(true);
        status(`迷雾已刷 ${painted} 格（未保存）——改完点右上角「保存」。`, 'warn');
      }

      return;
    }

    if (!draggingVertex) {
      return;
    }

    const finished = draggingVertex;
    draggingVertex = null;

    if (finished.moved) {
      scheduleSave(0);
      status('顶点已移动，正在保存…');
    } else {
      // 没动过：撤销刚才那次 pushHistory，免得空操作占一格撤销栈。
      history.pop();
    }
  }, true);
}

// ---- 选中 / 双击结束 / 键盘 ----

boardHost.addEventListener('click', (event) => {
  if (tool !== 'select' || !mapDoc) {
    return;
  }

  const wallElement = event.target.closest('.board-wall');
  const roomElement = event.target.closest('.board-room');
  const zoneElement = event.target.closest('.board-zone');

  if (wallElement) {
    selected = { type: 'wall', id: wallElement.dataset.wallId };
  } else if (roomElement) {
    selected = { type: 'room', id: roomElement.dataset.roomId };
  } else if (zoneElement) {
    selected = { type: 'zone', id: zoneElement.dataset.zoneId };
  } else {
    selected = null;
  }

  renderOverlay();

  if (selected) {
    const what = selected.type === 'wall' ? '墙' : selected.type === 'zone' ? '区域' : '房间';
    status(`已选中${what} ${selected.id}；拖${selected.type === 'zone' ? '手柄' : '顶点'}改形状，Delete 删除。`);
  }
}, true);

boardHost.addEventListener('dblclick', async (event) => {
  // 双击已有区域 = 改属性（名称 / 类型 / 备注 / 颜色 / 可见性，圆形还能改半径）。
  // 属性表单原来只在新画区域时出现，选中之后没有入口——用户只能删了重画。
  const zoneElement = event.target.closest('.board-zone');

  if (zoneElement && tool === 'select') {
    const zone = (mapDoc?.zones ?? []).find((item) => item.id === zoneElement.dataset.zoneId);

    if (!zone?.shape) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    const attributes = await askZoneAttributes(zone.shape.type === 'circle' ? 'circle' : 'polygon', {
      name: zone.name,
      kind: zone.kind,
      note: zone.note,
      color: zone.color,
      visible: zone.visible,
      radiusCells: zone.shape.radiusCells,
    });

    if (!attributes) {
      return;
    }

    pushHistory();
    zone.name = attributes.name;
    zone.kind = attributes.kind;
    zone.note = attributes.note;
    zone.color = attributes.color;
    zone.visible = attributes.visible;

    if (zone.shape.type === 'circle' && attributes.radiusCells) {
      zone.shape.radiusCells = attributes.radiusCells;
    }

    scheduleRender();
    renderOverlay();
    scheduleSave(0);
    status(`已更新区域「${zone.name}」。`, 'ok');
    return;
  }

  if (draft.type !== 'wall') {
    return;
  }

  event.preventDefault();

  // 双击会先产生两次 pointerdown（多加了重合的点）：去掉与上一个重合的那个。
  const points = draft.points;

  while (points.length >= 2
    && Math.hypot(points.at(-1).x - points.at(-2).x, points.at(-1).y - points.at(-2).y) < 1) {
    points.pop();
  }

  finishWall();
}, true);

/**
 * 按住空格临时平移（绘图软件惯例）：按下记住原工具，松开还原。
 * 空格只在"不是输入框"时生效——与 Enter 走同一处排除（见下面的 keydown 入口）。
 */
let spacePanning = false;
let toolBeforeSpace = null;

document.addEventListener('keydown', (event) => {
  // 输入类元素里不接管键盘：Enter 在表单里只做「确定」，Esc 归对话框自己处理。
  if (event.target instanceof HTMLInputElement
    || event.target instanceof HTMLSelectElement
    || event.target instanceof HTMLTextAreaElement) {
    return;
  }

  // 空格 = 临时平移。`event.repeat` 要挡住：按住不放会连发 keydown，
  // 那样每次都会把"原工具"记成 pan，松开后就回不去了。
  if (event.code === 'Space' && !event.repeat) {
    if (!spacePanning) {
      spacePanning = true;
      toolBeforeSpace = tool;
      setTool('pan');
    }

    event.preventDefault();
    return;
  }

  if (event.key === 'Escape') {
    if (draft.type) {
      draft = { type: null, points: [] };
      renderOverlay();
      status('已取消绘制。');
    } else if (selected) {
      selected = null;
      renderOverlay();
      status('已取消选中。');
    }

    return;
  }

  if ((event.key === 'Delete' || event.key === 'Backspace') && selected) {
    event.preventDefault();
    deleteSelected();
    return;
  }

  if (event.key === 'Enter') {
    // M5：Enter 等价于"结束当前绘制"。表单里的 Enter 只做「确定」，所以上面已经把
    // INPUT/TEXTAREA 排除掉了（那个判断在事件入口）。
    if (draft.type === 'wall') {
      event.preventDefault();
      finishWall();
      return;
    }

    if (draft.type === 'room' || (draft.type === 'zone' && zoneShape === 'polygon')) {
      event.preventDefault();

      if (draft.points.length < 3) {
        status(draft.type === 'room' ? '房间至少要三个点。' : '区域多边形至少要三个点。');
        return;
      }

      if (draft.type === 'zone') {
        void finishZone({
          type: 'polygon',
          points: draft.points.map((item) => [round2(item.x), round2(item.y)]),
        });
      } else {
        void finishRoom();
      }

      return;
    }
  }

  // 数字键 4 = 迷雾（再按循环三支笔）。输入类元素已在入口排除。
  if (event.key === '4' && !event.ctrlKey && !event.metaKey) {
    event.preventDefault();
    cycleFogPen();
    return;
  }

  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault();

    if (event.shiftKey) {
      redo();
    } else {
      undo();
    }
  }
});

fogToolButton.addEventListener('click', () => cycleFogPen());

// ---- 一键全部揭示 / 全部遮黑（地图编辑器的收尾批） ----

/**
 * 覆盖范围内的全部格：网格区域 ± `FOG_MARGIN_CELLS`（与 `fogCellAt` 同一个常量、同一口径）。
 * 迷雾允许比底图大（遮边距），所以这里也要把冗余算进去，否则"全部揭示"会留下边缘一圈黑。
 */
function allFogCells() {
  const { cols, rows } = fogGridSize();
  const cells = [];

  for (let y = -FOG_MARGIN_CELLS; y <= rows - 1 + FOG_MARGIN_CELLS; y += 1) {
    for (let x = -FOG_MARGIN_CELLS; x <= cols - 1 + FOG_MARGIN_CELLS; x += 1) {
      cells.push([x, y]);
    }
  }

  return cells;
}

/** 全部揭示：只写 explored，**不动 visible**——"当前视野"不能一键伪造。 */
function revealAllFog() {
  if (!mapDoc?.fog || !(mapDoc.grid?.dpi > 0)) {
    status('还没标定：迷雾按格算，先点「标定」定下格宽。', 'warn');
    return;
  }

  const started = performance.now();
  pushHistory();
  const cells = allFogCells();
  // 保留原有的 visible（它必须是 explored 的子集，加了 explored 之后仍然成立）。
  mapDoc.fog.explored = cells;
  mapDoc.fog.enabled = true;
  const elapsed = performance.now() - started;
  markFogDirty(true);
  scheduleRender();
  status(`全部揭示：${cells.length} 格（生成 ${elapsed.toFixed(1)} ms，未保存）。visible 未改动。`, 'ok');
}

/** 全部遮黑：清空 explored 与 visible。会丢掉所有探索记录，所以先确认。 */
async function hideAllFog() {
  if (!mapDoc) {
    return;
  }

  const confirmed = await confirmAction('会清掉所有探索记录（包括已揭示与当前视野），无法恢复除撤销之外。', {
    title: '全部遮黑',
    okText: '全部遮黑',
    danger: true,
  });

  if (!confirmed) {
    status('已取消：迷雾没有改动。');
    return;
  }

  const started = performance.now();
  pushHistory();

  if (!mapDoc.fog) {
    mapDoc.fog = { enabled: true, explored: [], visible: [] };
  }

  mapDoc.fog.explored = [];
  mapDoc.fog.visible = [];
  mapDoc.fog.enabled = true;
  const elapsed = performance.now() - started;
  markFogDirty(true);
  scheduleRender();
  status(`全部遮黑：已清空探索记录（${elapsed.toFixed(1)} ms，未保存）。`, 'ok');
}

document.getElementById('fog-reveal-all')?.addEventListener('click', () => revealAllFog());
document.getElementById('fog-hide-all')?.addEventListener('click', () => void hideAllFog());

for (const button of fogPens.querySelectorAll('[data-fog]')) {
  button.addEventListener('click', () => {
    fogPen = button.dataset.fog;
    applyFogPenUi();
  });
}

document.getElementById('zone-shape').addEventListener('change', (event) => {
  zoneShape = event.target.value;
  status(`区域形状：${zoneShape === 'circle' ? '圆形（点一下定圆心）' : '多边形（点回起点或 Enter 闭合）'}。`);
});

wallKindSelect.addEventListener('change', () => {
  wallKind = wallKindSelect.value;
  status(`墙的种类：${wallKindSelect.selectedOptions[0]?.textContent ?? wallKind}`);
});

snapButton.addEventListener('click', () => {
  snapGranularity = snapGranularity === 'corner' ? 'center' : 'corner';
  snapButton.textContent = `吸附：${snapGranularity === 'corner' ? '格点' : '格心'}`;
});

undoButton.addEventListener('click', () => undo());
redoButton.addEventListener('click', () => redo());
deleteButton.addEventListener('click', () => deleteSelected());

for (const button of document.querySelectorAll('.editor-tools [data-tool]')) {
  button.addEventListener('click', () => setTool(button.dataset.tool));
}

// 缩放 / 平移之后顶点要跟着走（board 的 applyViewport 是内部的，这里用事件兜住）。
boardHost.addEventListener('wheel', () => setTimeout(renderOverlay, 16), { passive: true });
boardHost.addEventListener('pointermove', () => {
  if (tool === 'select' && !draggingVertex) {
    setTimeout(renderOverlay, 16);
  }
}, { passive: true });
window.addEventListener('resize', () => setTimeout(renderOverlay, 16));

// ---------------------------------------------------------------- 启动

async function start() {
  const maps = await loadMaps();

  if (maps.length > 0) {
    await openMap(mapSelect.value);
  }

  board.setShapeHit(true);
  // 编辑器是 DM 视图（能看全图；迷雾只做半透明提示，不遮死）。
  // 初始视角已经在 mount 时给定（首次渲染就是 DM），这里不必再 setView——
  // 真要再调也无害：setView 现在负责重绘与文案。

  await loadCast();

  // 让编辑器在浏览器里可被断言（与战术板的 window.__meetingroomBoard 同一套思路）。
  window.__meetingroomMapEditor = {
    get mapId() {
      return mapId;
    },
    get document() {
      return mapDoc;
    },
    openMap,
    save,
    // 调试/断言用：直接拿到 board 实例与编辑状态。
    board,
    get tool() {
      return tool;
    },
    get selected() {
      return selected;
    },
    get history() {
      return history.length;
    },
    get dragging() {
      return draggingTokenId;
    },
  };
}

void start();

// 空格松开 → 回到按住之前的工具（本来就在「移动」上就保持不动）。
document.addEventListener('keyup', (event) => {
  if (event.code !== 'Space' || !spacePanning) {
    return;
  }

  if (event.target instanceof HTMLInputElement
    || event.target instanceof HTMLSelectElement
    || event.target instanceof HTMLTextAreaElement) {
    return;
  }

  spacePanning = false;
  const restore = toolBeforeSpace ?? 'select';
  toolBeforeSpace = null;

  if (tool !== restore) {
    setTool(restore);
  }
});

// 焦点丢了（切窗口）时 keyup 收不到，兜底清掉，免得"空格一直按着"。
window.addEventListener('blur', () => {
  if (!spacePanning) {
    return;
  }

  spacePanning = false;
  const restore = toolBeforeSpace ?? 'select';
  toolBeforeSpace = null;

  if (tool !== restore) {
    setTool(restore);
  }
});
