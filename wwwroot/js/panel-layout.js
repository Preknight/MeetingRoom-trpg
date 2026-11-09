// 列宽与折叠（用户 2026-09 要求）：拖分隔条改宽度、顶栏开关折叠 / 显示，状态存 localStorage。
//
// 宽度直接算成 grid-template-columns（而不是 CSS 变量）：折叠一列时要把它**从模板里去掉**，
// 否则会留下一条零宽列 + 一个 gap。主列（讨论 / 剧情）保持 1fr——拖别的列就是在给它让宽度。
//
// 没动过（localStorage 里什么都没有）时不写 inline 样式：让 app.css 的响应式档位继续生效；
// 一旦用户拖过或折叠过，就以"他调过的"为准。

const STORAGE_KEY = 'meetingroom.layout';

/** 主列（讨论 / 剧情）的最小宽度：拖动时给它留出来，别被两侧挤没。 */
const MAIN_MIN = 220;

/** 可调的列。main 是弹性列，宽度不记；rail-l / rail-r 是 rail 内部的两列。 */
const COLUMNS = [
  { key: 'sidebar', label: '侧栏', title: '工作空间 / 会话', width: 240, min: 140, max: 420 },
  { key: 'archived', label: '已归档', title: '已归档 + 待整理', width: 240, min: 160, max: 520 },
  { key: 'main', label: '剧情', title: '剧情 / 导演指令', width: 0, min: 0, max: 0 },
  { key: 'review', label: '待确认', title: '待确认事件 + 记录', width: 260, min: 160, max: 520 },
  { key: 'rail-l', label: '在场角色', title: '在场角色', width: 200, min: 120, max: 420 },
  { key: 'rail-r', label: '后台', title: '后台记录', width: 200, min: 120, max: 600 },
];

/** 折叠的粒度：rail 已拆成两列，各自可折（§3.1）。 */
const GROUPS = [
  { key: 'sidebar', label: '侧栏', title: '工作空间 / 会话' },
  { key: 'archived', label: '已归档', title: '已归档 + 待整理' },
  { key: 'main', label: '剧情', title: '剧情 / 导演指令' },
  { key: 'review', label: '待确认', title: '待确认事件 + 记录' },
  { key: 'rail-l', label: '在场角色', title: '在场角色' },
  { key: 'rail-r', label: '后台', title: '后台记录' },
];

/**
 * 布局预设（F1）：**只叠加折叠，不动宽度**——用户拖过的列宽在任何预设下都原样保留。
 * `hidden` 存的是用户自己的意图，预设只是临时叠加一层，所以切回 default 就能完整还原。
 */
const PRESETS = {
  default: [],
  combat: ['sidebar', 'review', 'rail-r'],
};

/** grid 里列的先后顺序（量宽度、算可用空间都用它）。 */
const ORDER = ['sidebar', 'archived', 'main', 'review', 'rail-l', 'rail-r'];

/** rail 里两个列标题的 id：`rail-l` / `rail-r` 没有独立列元素，折叠时标题得单独收（§26.8 B）。 */
const RAIL_TITLES = [
  ['rail-l', 'col4-title'],
  ['rail-r', 'col5-title'],
];

export function mount() {
  const workspace = document.querySelector('.workspace');
  const rail = document.querySelector('[data-col="rail"]');
  const railHead = rail?.querySelector('.rail-head');
  const railBody = rail?.querySelector('.rail-body');
  const toggles = document.getElementById('col-toggles');

  const widths = {};
  const hidden = new Set();

  /** 用户动过没有（拖过 / 折叠过）：动过才写 inline 样式，否则把排版交回 CSS 的响应式档位。 */
  let custom = false;

  /** 当前预设（`default` / `combat`）：只叠加折叠，**不改 width**（§F1-2）。 */
  let preset = 'default';

  const columnOf = (key) => COLUMNS.find((item) => item.key === key);
  const px = (key) => `${Math.round(widths[key])}px`;

  /** 生效的折叠 = 用户自己的 + 预设叠加的那几条。 */
  const shows = (key) => hidden.has(key) ? false : !(PRESETS[preset] ?? []).includes(key);
  // 排除把手：`.col-resizer` 也带 `data-col`（拖拽用它认列），直接 querySelector 会命中把手，
  // 于是 `rail-l` / `rail-r` 这类"没有独立 DOM 元素"的列会被误判成"有元素"。
  const elementOf = (key) => document.querySelector(`[data-col="${key}"]:not(.col-resizer)`);

  load();

  if (!custom) {
    measure();
  }

  function load() {
    for (const column of COLUMNS) {
      widths[column.key] = column.width;
    }

    let saved = null;

    try {
      saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null');
    } catch {
      saved = null;
    }

    if (!saved) {
      return;
    }

    custom = true;

    for (const [key, value] of Object.entries(saved.widths ?? {})) {
      if (columnOf(key) && Number.isFinite(value)) {
        widths[key] = Math.min(columnOf(key).max, Math.max(columnOf(key).min, value));
      }
    }

    for (const key of saved.hidden ?? []) {
      hidden.add(key);
    }

    if (typeof saved.preset === 'string' && saved.preset in PRESETS) {
      preset = saved.preset;
    }
  }

  /**
   * 没记录时，以**当前渲染出来的**宽度作为拖动起点。
   * （否则第一次拖动会把两侧弹回硬编码的默认值、顺手把主列挤成 0——真机验证时就是这么发现的。）
   */
  function measure() {
    const parts = getComputedStyle(workspace)
      .gridTemplateColumns
      .split(/\s+/)
      .map((value) => Math.round(parseFloat(value)));

    ORDER.forEach((key, index) => {
      if (Number.isFinite(parts[index])) {
        widths[key] = parts[index];
      }
    });
  }

  function save() {
    // 合并写：`mainSplit`（战斗比例）与列宽共用同一个 key，不能互相覆盖。
    let saved = {};

    try {
      saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null') ?? {};
    } catch {
      saved = {};
    }

    localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...saved, widths, hidden: [...hidden], preset }));
  }

  /** 这一列最多能多宽：给主列留出 MAIN_MIN，拖到边界就停。 */
  function limitOf(column) {
    const others = COLUMNS
      .filter((item) => item.key !== column.key && item.key !== 'main' && shows(item.key))
      .reduce((sum, item) => sum + widths[item.key], 0);

    let visible = 0;

    for (const key of ['sidebar', 'archived', 'main', 'review']) {
      if (shows(key)) {
        visible += 1;
      }
    }

    // rail 已拆成两列：可见列要**逐列**数，否则折掉 rail-r 后宽度会错位（§F1-3）。
    if (shows('rail-l')) {
      visible += 1;
    }

    if (shows('rail-r')) {
      visible += 1;
    }

    const overhead = 16 + Math.max(0, visible - 1) * 8;
    const room = workspace.clientWidth - overhead - others - (shows('main') ? MAIN_MIN : 0);

    return Math.max(column.min, Math.min(column.max, room));
  }

  function apply() {
    for (const group of GROUPS) {
      const element = elementOf(group.key);

      if (!element) {
        continue;
      }

      element.hidden = !shows(group.key);
    }

    // 「与会者 / 后台记录」没有独立的列元素：内容在 `.rail-body` 里交替挂着两种格子
    // （`.rail-cell--participant` / `.rail-cell--backstage`），标题在 `.rail-head` 的两个 h2 里，
    // 所以上面那圈 `hidden` 管不到它们。折叠哪一列 → 收该列标题 + 该类格子（格子由 CSS 按
    // rail 的 data 属性收）；两列都折 → 整个 rail 不显示（`.rail` 是 display:flex，
    // 得显式写 `.rail[hidden] { display: none }`，UA 的 `[hidden]` 压不住它）。
    const railShowsL = shows('rail-l');
    const railShowsR = shows('rail-r');

    if (rail) {
      rail.hidden = !(railShowsL || railShowsR);
      rail.dataset.railL = railShowsL ? 'on' : 'off';
      rail.dataset.railR = railShowsR ? 'on' : 'off';
    }

    for (const [key, id] of RAIL_TITLES) {
      const title = document.getElementById(id);

      if (title) {
        title.hidden = !shows(key);
      }
    }

    // 没动过就交回给 CSS（响应式档位还在）。
    if (!custom) {
      workspace.style.gridTemplateColumns = '';
      rail?.style.removeProperty('--w-rail-l');

      if (railHead && railBody) {
        // 两列都显示 → 交回 CSS 的响应式档位；只剩一列时（战斗预设会折掉 rail-r）
        // 必须给单列，否则那一类格子的显示被藏了、剩下一类仍被挤在 200px 里，右边空一块。
        const columns = railShowsL && railShowsR ? '' : 'minmax(0, 1fr)';
        railHead.style.gridTemplateColumns = columns;
        railBody.style.gridTemplateColumns = columns;
      }

      return;
    }

    const parts = [];

    if (shows('sidebar')) {
      parts.push(px('sidebar'));
    }

    if (shows('archived')) {
      parts.push(px('archived'));
    }

    if (shows('main')) {
      parts.push(`minmax(${MAIN_MIN}px, 1fr)`);
    }

    if (shows('review')) {
      parts.push(px('review'));
    }

    if (shows('rail-l')) {
      parts.push(px('rail-l'));
    }

    if (shows('rail-r')) {
      parts.push(px('rail-r'));
    }

    // 全折叠时至少留一条空列，否则模板为空、网格恢复默认（所有列又都回来了）。
    workspace.style.gridTemplateColumns = parts.length > 0 ? parts.join(' ') : 'minmax(0, 1fr)';

    // 折叠掉的列，把手一起藏起来：悬在空处的把手拖了没有对应列，只会让人困惑。
    for (const handle of document.querySelectorAll('.col-resizer')) {
      const key = handle.dataset.col;
      handle.hidden = !shows(key);
    }

    // 「与会者 / 后台记录」的格子由 `rail.dataset.railL / railR` + CSS 收（见 apply 开头），
    // 这里只管 rail 的两列模板。
    if (rail && railHead && railBody) {
      // rail 占几列：两列各自可折，span 必须跟着**实际可见列数**走（§F1-3 的另一半）。
      const spans = (railShowsL ? 1 : 0) + (railShowsR ? 1 : 0);
      rail.style.gridColumn = `span ${Math.max(1, spans)}`;
      rail.style.setProperty('--w-rail-l', px('rail-l'));

      // 与会者列与后台记录列：与外层对应列同宽，逐行才对得齐。
      // F7-③：第二列用 `minmax(0, 1fr)` 而不是固定 px —— 内部 grid 有 8px gap，
      // 外层 rail 宽度是两列之和（不含 gap），写死 px 会稳定溢出 8px（实测 right=1922.8）。
      // 只剩一列时同理：宽度已经由外层那一格决定，写死 px 会再溢出 rail 的左右 padding（20px）。
      const railColumns = spans === 2 ? `${px('rail-l')} minmax(0, 1fr)` : 'minmax(0, 1fr)';

      railHead.style.gridTemplateColumns = railColumns;
      railBody.style.gridTemplateColumns = railColumns;
    }
  }

  function renderToggles() {
    if (!toggles) {
      return;
    }

    toggles.replaceChildren(
      ...GROUPS.map((group) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'col-toggle';
        button.dataset.on = String(shows(group.key));
        button.textContent = group.label;
        button.title = `${shows(group.key) ? '折叠' : '显示'}「${group.title}」`;
        button.addEventListener('click', () => {
          if (hidden.has(group.key)) {
            hidden.delete(group.key);
          } else {
            hidden.add(group.key);
          }

          custom = true;
          apply();
          renderToggles();
          save();
        });

        return button;
      }),
    );
  }

  /**
   * 第 2 列的横向分隔条（本批）：改「已归档 / 待整理」的高度比例。
   * 比例存**独立的 key**（不塞进 `meetingroom.layout`）——那个 key 的 `save()` 是合并写，
   * 多一个字段就要动它的读写，容易把列宽一起写坏。
   */
  const ROW_SPLIT_KEY = 'meetingroom.archivedSplit';
  const ROW_MIN_PX = 120;

  function bindRowResizer() {
    const handle = document.querySelector('.row-resizer[data-row="archived"]');
    const column = document.querySelector('.col[data-col="archived"]');
    const top = column?.querySelector('.panel--archived');

    if (!handle || !column || !top) {
      return;
    }

    /**
     * 上段能占多高：给下段留 ROW_MIN_PX，再留一点缝隙。
     * `clientHeight` 为 0 时（还没布局）**不封顶**——否则恢复存档会被钳到最小值，
     * 表现为"刷新后比例丢了、上段塌成 120px"（第一次就是这么踩的）。
     */
    const clamp = (px) => {
      const room = column.clientHeight - ROW_MIN_PX - 20;

      if (!(column.clientHeight > 0)) {
        return Math.max(ROW_MIN_PX, px);
      }

      return Math.min(Math.max(ROW_MIN_PX, room), Math.max(ROW_MIN_PX, px));
    };

    const apply = (px) => {
      const height = clamp(px);
      top.style.flex = `0 0 ${height}px`;
      column.dataset.split = 'custom';
      return height;
    };

    const saveHeight = (height) => {
      try {
        localStorage.setItem(ROW_SPLIT_KEY, String(Math.round(height)));
      } catch {
        // 存不了就只在本页生效。
      }
    };

    // 恢复放在下一帧：bindRowResizer 在 mount 早期跑，此时第 2 列可能还没量出高度，
    // clamp 会把它钳成最小值（真机踩过：刷新后 403px 变回 120px）。
    const restore = () => {
      try {
        const saved = Number(localStorage.getItem(ROW_SPLIT_KEY));

        if (Number.isFinite(saved) && saved > 0) {
          // 恢复时**不做上限钳制**：上限是给拖动用的（别把下段挤没），
          // 恢复时列高可能还没量出来，钳一下就会把存档压成最小值（真机踩过：403 → 120）。
          const height = Math.max(ROW_MIN_PX, saved);
          top.style.flex = `0 0 ${height}px`;
          column.dataset.split = 'custom';
        }
      } catch {
        // localStorage 被禁：用默认比例。
      }
    };

    // 双帧：第一帧布局、第二帧再量。单帧时第 2 列的高度仍是初始值。
    requestAnimationFrame(() => requestAnimationFrame(restore));

    handle.addEventListener('pointerdown', (event) => {
      event.preventDefault();

      try {
        handle.setPointerCapture(event.pointerId);
      } catch {
        // 合成事件拿不到捕获：不捕获也能拖。
      }

      handle.dataset.dragging = 'true';
      const startY = event.clientY;
      const startHeight = top.getBoundingClientRect().height;

      const move = (moveEvent) => {
        apply(startHeight + (moveEvent.clientY - startY));
      };

      const finish = () => {
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', finish);
        handle.removeEventListener('pointercancel', finish);
        delete handle.dataset.dragging;
        saveHeight(top.getBoundingClientRect().height);
      };

      handle.addEventListener('pointermove', move);
      handle.addEventListener('pointerup', finish);
      handle.addEventListener('pointercancel', finish);
    });
  }

  function bindResizers() {
    for (const handle of document.querySelectorAll('.col-resizer')) {
      const column = columnOf(handle.dataset.col);

      if (!column || column.max <= column.min) {
        continue;
      }

      handle.addEventListener('pointerdown', (event) => {
        event.preventDefault();

        try {
          handle.setPointerCapture(event.pointerId);
        } catch {
          // 合成事件 / 已失效的 pointerId 拿不到捕获：不捕获也照样拖动（事件仍在 handle 上）。
        }

        handle.dataset.dragging = 'true';

        const startX = event.clientX;
        const startWidth = widths[column.key];
        /**
         * 拖动方向（本批）：把手在**左边缘**时向左拖 = 那一列变宽，所以 Δ 取反。
         * 统一语义是"把手远离被调列 → 该列变大"，两种摆位都满足它。
         */
        const sign = handle.dataset.edge === 'left' ? -1 : 1;

        const move = (moveEvent) => {
          const next = startWidth + (sign * (moveEvent.clientX - startX));
          widths[column.key] = Math.min(limitOf(column), Math.max(column.min, next));
          // 先标记"动过"，否则第一次拖动会走"交回 CSS"那条分支，界面上看不到任何变化。
          custom = true;
          apply();
        };

        const finish = () => {
          handle.removeEventListener('pointermove', move);
          handle.removeEventListener('pointerup', finish);
          handle.removeEventListener('pointercancel', finish);
          delete handle.dataset.dragging;

          custom = true;
          save();
        };

        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', finish);
        handle.addEventListener('pointercancel', finish);
      });
    }
  }

  apply();
  renderToggles();
  bindResizers();
  bindRowResizer();

  /**
   * 切布局预设（F1）：战斗开始时 `combat`，结束时回 `default`。
   * 只改折叠叠加层，**不碰 widths**——拖过的宽度原样保留。
   */
  function setPreset(name) {
    if (!(name in PRESETS) || preset === name) {
      return preset;
    }

    preset = name;
    custom = true;
    apply();
    renderToggles();
    save();
    return preset;
  }

  return {
    // 列宽与折叠是纯本地状态（不进 store），没有要从事件归约的东西。
    render() {},
    setPreset,
    getPreset: () => preset,
  };
}
