// 战斗条 + 战术板（F1）。
//
// 数据源（§F1-5）：**优先 `roleplay.state.combat` 快照**，事件只负责提示"该重取了"——
// store 收到 `roleplay.combat` 会打一个 `combatStale` 标记，这里看到就拉一次 /api/roleplay/state。
// 这样刷新 / 重连之后战斗条能从快照完整重建（验收硬项），而不是靠事件补出来。
//
// 布局（§F1-2）：战斗开始自动切 `combat` 预设、结束自动回 `default`；预设只折叠列、**不改宽度**。
// 用户手动点「战术板」时以手动为准，直到战斗结束。

import * as api from './api.js';
import { mount as mountBoard } from './board.js';

const LAYOUT_KEY = 'meetingroom.layout';

/** 左右比例（默认 55/45），与列宽折叠共用同一个 localStorage key。 */
const DEFAULT_SPLIT = '55%';

export function mount({ layout } = {}) {
  const bar = document.getElementById('combat-bar');
  const roundText = document.getElementById('combat-round');
  const actorText = document.getElementById('combat-actor');
  const orderText = document.getElementById('combat-order');
  const endButton = document.getElementById('combat-end');
  const toggle = document.getElementById('combat-toggle');
  const board = document.getElementById('tactical-board');
  const boardBody = document.getElementById('tactical-body');
  const split = document.getElementById('main-split');
  const resizer = document.querySelector('[data-main-split]');

  // 战术板（F2）：底图 + 网格 + token，画在右栏顶部；下面仍是战斗者清单。
  const boardHost = document.createElement('div');
  boardHost.className = 'board-host';
  boardBody?.prepend(boardHost);
  const boardView = boardHost ? mountBoard(boardHost) : null;

  // 诊断钩子：浏览器里手动驱动战术板（验收 token / 网格用；没有它就只能靠真实战斗触发）。
  window.__meetingroomBoard = boardView;

  /** 手动切过模式没有：`null` = 跟随战斗状态。 */
  let manual = null;
  let staleRequest = null;

  /**
   * 叙事模式下战术板被用户手动藏起来了吗（持久化，刷新后保留）。
   * 与 `manual` 分开：`manual` 管的是**战斗布局**（preset），这个管的是**战术板可见性**。
   * 两者在非战斗时本来就是一回事，战斗时则不同（战斗一定要看到战术板）。
   */
  let boardHiddenByUser = loadBoardHidden();
  /** 上一次渲染时的模式（`roleplay` / `meeting`）：手动切可见性时要重算。 */
  let lastMode = 'meeting';

  const BOARD_HIDDEN_KEY = 'meetingroom.boardHidden';

  function loadBoardHidden() {
    try {
      return window.localStorage.getItem(BOARD_HIDDEN_KEY) === '1';
    } catch {
      return false;
    }
  }

  function saveBoardHidden(hidden) {
    boardHiddenByUser = hidden;

    try {
      window.localStorage.setItem(BOARD_HIDDEN_KEY, hidden ? '1' : '0');
    } catch {
      // 隐私模式 / 存储被禁：内存里仍然生效，只是刷新后不保留。
    }
  }

  applySplit(loadSplit());

  /**
   * 顶栏「战术板」按钮（main.js 的事件委托调用）。
   * - 战斗中有战斗时：沿用原语义（战斗布局开 / 关）；
   * - 叙事模式非战斗时：切换**战术板可见性**（常驻显示的开关），并持久化。
   */
  function toggleMode() {
    const combatOn = document.body.dataset.combat === 'on';

    if (lastMode === 'roleplay' && !isActive(lastCombat)) {
      saveBoardHidden(!boardHiddenByUser);
      renderBoardFromLastState();
      return;
    }

    manual = combatOn ? 'default' : 'combat';
    renderBoardFromLastState();
  }

  /** 结束战斗（顶栏「结束战斗」按钮）：DM 直接收场，不等模型调工具。 */
  async function endCombat(button) {
    if (button) {
      button.disabled = true;
    }

    try {
      await api.postJson('/api/roleplay/combat/end', {});
    } finally {
      if (button) {
        button.disabled = false;
      }
    }
  }

  bindSplitResizer();

  /** 上一次渲染用的战斗对象：手动切换模式时不用重新拿 state。 */
  let lastCombat = null;
  /** 上一次的地图快照（token 坐标）：手动画板时复用。 */
  let lastVtt = null;
  /** 上一次的地图节点（M4）：战术板的房间/墙/标定都吃这一份，不再自己拉 document。 */
  let lastMap = null;

  function renderBoardFromLastState() {
    // 手动切换时必须把 showCombat 算出来再传：paint 的默认参数会把手动选择盖掉。
    const active = isActive(lastCombat);
    paint(lastCombat, active, active ? manual !== 'default' : manual === 'combat', lastMode);
  }

  function render(state) {
    const combat = state.roleplay?.combat ?? null;
    lastCombat = combat;
    lastVtt = state.roleplay?.vtt ?? null;
    lastMap = state.roleplay?.map ?? null;

    // 事件到了但还没取快照：拉一次（串行，避免连击时并发请求）。
    if (state.roleplay?.combatStale && !staleRequest) {
      staleRequest = api
        .getJson('/api/roleplay/state')
        .then((result) => {
          if (result.ok && result.data) {
            // 直接交给 store：与 SSE 的 roleplay.state 同一形状，重建走同一条路。
            window.dispatchEvent(new CustomEvent('meetingroom:roleplay-state', { detail: result.data }));
          }
        })
        .finally(() => {
          staleRequest = null;
        });
    }

    lastMode = state.mode === 'roleplay' ? 'roleplay' : 'meeting';

    const active = isActive(combat);
    const showCombat = active ? manual !== 'default' : manual === 'combat';

    paint(combat, active, showCombat, lastMode);
  }

  function paint(combat, active = isActive(combat), showCombat = active, mode = 'meeting') {
    /**
     * 战术板可见性（本批）：
     * - **叙事模式**：常驻——非战斗时显示当前地图的玩家视角；用户可用顶栏按钮手动隐藏（持久化）。
     * - **会议模式**：沿用原文——只有战斗布局打开时才显示。
     *   会议里没有地图可看（角色扮演才有 map 快照），常驻一块空板只是占地方。
     */
    const showBoard = mode === 'roleplay' ? !boardHiddenByUser : showCombat;

    // 两个属性各管一件事：`data-board` 决定 main 是单列还是两列，`data-combat` 只管战斗视觉。
    document.body.dataset.board = showBoard ? 'on' : 'off';
    document.body.dataset.combat = showCombat ? 'on' : 'off';

    if (bar) {
      bar.hidden = !active;
    }

    if (toggle) {
      // 叙事模式下这个按钮是"显示 / 隐藏战术板"的开关，任何时候都该在；
      // 会议模式下它只在战斗中才有意义（原行为）。
      toggle.hidden = mode !== 'roleplay' && !active;
      toggle.dataset.mode = showBoard ? 'combat' : 'default';
      toggle.title = mode === 'roleplay'
        ? (showBoard ? '隐藏战术板' : '显示战术板')
        : '手动切换战斗布局（战术板）';
    }

    if (board) {
      board.hidden = !showBoard;
    }

    if (active && combat) {
      if (roundText) {
        roundText.textContent = `第 ${combat.round} 轮`;
      }

      if (actorText) {
        actorText.textContent = `轮到 ${combat.currentActor ?? '—'}`;
      }

      if (orderText) {
        orderText.textContent = (combat.order ?? [])
          .map((entry) => `${entry.actor}(${entry.initiative})`)
          .join(' → ');
      }
    }

    // 预设跟随 `showCombat`：战斗中被手动关掉战术板也要真的回到 default（首版漏了这一支）。
    // 战斗结束则清掉手动状态，让下一场重新自动跟随。
    if (!active) {
      manual = null;
    }

    layout?.setPreset(showCombat ? 'combat' : 'default');

    // 叙事模式下战术板常驻：非战斗时也要渲染（玩家视角看当前地图）。
    // 战斗布局(`showCombat`)只管预设与战斗控件，别拿它当"要不要画板"的判据。
    if (boardBody && showBoard) {
      // board 与清单是兄弟节点：board 常驻（不重建），清单每次重画。
      const list = boardBody.querySelector('.tactical-list, .muted');
      const fresh = renderCombatants(combat);

      if (list) {
        list.replaceWith(...fresh);
      } else {
        boardBody.append(...fresh);
      }

      // map 与快照同源：房间、墙、标定都从这里来（不要再让 board 自己拉一份 document）。
      void boardView?.render({ vtt: lastVtt, combat, map: lastMap });
    }
  }

  /** 战斗者一览：HP 来自快照里的 combatants（后端投影，不缓存），当前行动者高亮。 */
  function renderCombatants(combat) {
    if (!combat || !(combat.combatants ?? []).length) {
      const empty = document.createElement('p');
      empty.className = 'muted';
      empty.textContent = combat ? '这一场没有参战者。' : '没有进行中的战斗。';
      return [empty];
    }

    const list = document.createElement('div');
    list.className = 'tactical-list';

    const budget = combat.budget;

    for (const combatant of combat.combatants) {
      const row = document.createElement('div');
      row.className = 'tactical-row';
      row.dataset.current = String(combatant.id === combat.currentActor);
      row.dataset.status = combatant.status ?? '';

      const name = document.createElement('span');
      name.className = 'tactical-name';
      name.textContent = combatant.cnName || combatant.name || combatant.id;
      if (combatant.id === combat.currentActor && budget) {
        name.title = `Action ${budget.action ? '可用' : '已用'}、Bonus ${budget.bonus ? '可用' : '已用'}、反应 ${budget.reaction ? '可用' : '已用'}、移动剩 ${budget.movementFeet} 尺`;
      }

      const hp = document.createElement('span');
      hp.className = 'tactical-hp';
      hp.textContent = combatant.hp === null || combatant.hp === undefined
        ? 'HP —'
        : `HP ${combatant.hp}/${combatant.maxHp ?? '?'}`;

      const status = document.createElement('span');
      status.className = 'tactical-status';
      status.textContent = statusText(combatant.status);

      row.append(name, hp, status);

      if ((combatant.conditions ?? []).length) {
        const conditions = document.createElement('span');
        conditions.className = 'muted';
        conditions.textContent = combatant.conditions.join('、');
        row.append(conditions);
      }

      list.append(row);
    }

    return [list];
  }

  function isActive(combat) {
    return Boolean(combat) && combat.phase !== 'ended';
  }

  function statusText(status) {
    switch (status) {
      case 'dying': return '濒死';
      case 'stable': return '已稳定';
      case 'dead': return '死亡';
      case 'alive': return '正常';
      default: return status ?? '';
    }
  }

  // ---------- 左右比例 ----------

  function loadSplit() {
    try {
      const saved = JSON.parse(localStorage.getItem(LAYOUT_KEY) ?? 'null');
      if (saved && typeof saved.mainSplit === 'string') {
        return saved.mainSplit;
      }
    } catch {
      // localStorage 坏掉就当没存过。
    }

    return DEFAULT_SPLIT;
  }

  function saveSplit(value) {
    let saved = {};

    try {
      saved = JSON.parse(localStorage.getItem(LAYOUT_KEY) ?? 'null') ?? {};
    } catch {
      saved = {};
    }

    localStorage.setItem(LAYOUT_KEY, JSON.stringify({ ...saved, mainSplit: value }));
  }

  function applySplit(value) {
    split?.style.setProperty('--main-split-left', value);
  }

  function bindSplitResizer() {
    if (!resizer || !split) {
      return;
    }

    resizer.addEventListener('pointerdown', (event) => {
      event.preventDefault();

      try {
        resizer.setPointerCapture(event.pointerId);
      } catch {
        // 合成事件拿不到捕获：不捕获也能拖。
      }

      resizer.dataset.dragging = 'true';

      const move = (moveEvent) => {
        const rect = split.getBoundingClientRect();
        if (rect.width <= 0) {
          return;
        }

        const percent = ((moveEvent.clientX - rect.left) / rect.width) * 100;
        const clamped = Math.min(75, Math.max(30, percent));
        applySplit(`${clamped.toFixed(1)}%`);
      };

      const finish = () => {
        resizer.removeEventListener('pointermove', move);
        resizer.removeEventListener('pointerup', finish);
        resizer.removeEventListener('pointercancel', finish);
        delete resizer.dataset.dragging;

        saveSplit(split.style.getPropertyValue('--main-split-left') || DEFAULT_SPLIT);
      };

      resizer.addEventListener('pointermove', move);
      resizer.addEventListener('pointerup', finish);
      resizer.addEventListener('pointercancel', finish);
    });
  }

  return {
    render,
    toggleMode,
    endCombat,
    /** 列表 → 地图高亮（F3-③）：纯显示层。 */
    setHighlight: (actorId) => boardView?.setHighlight(actorId),
    /** 把战术板实例交给标定浮层（F4）：它要用 getMapInfo / pickPoints / setPreviewCalibration。 */
    getBoard: () => boardView,
    /** 顶栏「战术板」的当前状态（诊断用）。 */
    get preset() {
      return document.body.dataset.combat === 'on' ? 'combat' : 'default';
    },
  };
}
