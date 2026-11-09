// 标定浮层（F4）+ token 摘要浮层（共用同一套壳子）。
//
// 标定取点：在图上点两点（对齐两条实际墙体/网格线），输入"跨了几格"，反算：
//   dpi = 两点像素距离 / 格数
//   offsetX = 第一点 x mod dpi、offsetY = 第一点 y mod dpi（让格线落到点上）
// 换算只用几何，不引入任何"默认 100 猜"的兜底（§4.2 / F3 口径）。
//
// token 摘要（F4 顺手做）：**只读** `/api/roleplay/state` 的 `combat.combatants`，不开新端点、不缓存 HP。

import * as api from './api.js';

export function mount({ board, layout } = {}) {
  const shell = element('div', 'overlay-shell');
  shell.hidden = true;
  document.body.append(shell);

  const card = element('div', 'overlay-card');
  shell.append(card);

  let mode = null;

  shell.addEventListener('click', (event) => {
    if (event.target === shell) {
      close();
    }
  });

  /** 打开标定面板。 */
  function openCalibration() {
    const map = board?.getMapInfo?.();

    if (!map) {
      return;
    }

    mode = 'calibration';
    const calibration = map.calibration ?? { dpi: 100, offsetX: 0, offsetY: 0, gridType: 'square', scale: 5 };

    card.replaceChildren(
      heading('标定底图'),
      hint(`底图 ${map.width}×${map.height}。点两次「图上取点」：**请点在网格线上，不要点在格中心**`
        + `（点在格中心会让 offset 偏半格，而预览看起来"还行"——这是最隐的错）。`),
      fieldRow('dpi', numberInput('dpi', calibration.dpi, 1)),
      fieldRow('offsetX', numberInput('offsetX', calibration.offsetX, 1)),
      fieldRow('offsetY', numberInput('offsetY', calibration.offsetY, 1)),
      fieldRow('scale（一格雷尺数）', numberInput('scale', calibration.scale ?? 5, 1)),
      fieldRow('跨了几格', numberInput('cells', 5, 1)),
      buttonRow([
        button('图上取点', async () => {
          if (!board?.pickPoints) {
            return;
          }

          status('点两个点：先左边/上边的墙线，再右边/下边的墙线。');
          // F7-②：取点时遮罩必须让开——否则第一次点击就命中遮罩，浮层关掉、Promise 永不 resolve。
          shell.style.pointerEvents = 'none';
          shell.style.background = 'transparent';
          let points = [];

          try {
            points = await board.pickPoints(2);
          } finally {
            shell.style.pointerEvents = '';
            shell.style.background = '';
          }

          if (!points) {
            status('已取消取点（Esc）。');
            return;
          }
          const dialog = card.querySelector('[data-status]');

          if (dialog) {
            dialog.textContent = `已取两点（${points[0].x.toFixed(0)},${points[0].y.toFixed(0)}）→（${points[1].x.toFixed(0)},${points[1].y.toFixed(0)}）`;
          }

          applyPickedPoints(points);
        }),
        button('−1 dpi', () => nudge('dpi', -1)),
        button('+1 dpi', () => nudge('dpi', 1)),
        button('−1 offsetX', () => nudge('offsetX', -1)),
        button('+1 offsetX', () => nudge('offsetX', 1)),
        button('−1 offsetY', () => nudge('offsetY', -1)),
        button('+1 offsetY', () => nudge('offsetY', 1)),
      ]),
      hint('', 'status'),
      buttonRow([
        button('保存', async () => {
          const value = readInputs();

          if (!value) {
            return;
          }

          const result = await api.request(`/api/maps/${map.id}/calibration`, {
            method: 'PUT',
            body: value,
          });

          if (!result.ok) {
            status(result.data?.error ?? `保存失败（HTTP ${result.status}）`);
            return;
          }

          board?.setPreviewCalibration?.(null);
          await board?.render?.({});
          close();
        }, 'primary'),
        button('取消', () => {
          board?.setPreviewCalibration?.(null);
          void board?.render?.({});
          close();
        }),
      ]),
    );

    shell.hidden = false;
    bindPreview();

    // 打开时先按当前已存值预览（没有就按 100）。
    void board.setPreviewCalibration?.(calibration);

    /** 取点后反算 dpi 与 offset。 */
    function applyPickedPoints(points) {
      const cellsInput = card.querySelector('[name="cells"]');
      const cells = Math.max(1, Number(cellsInput?.value ?? 5));
      const dx = Math.abs(points[1].x - points[0].x);
      const dy = Math.abs(points[1].y - points[0].y);

      // 取横向更准的那一轴：两点主要沿哪边走，就用哪边算 dpi。
      const span = dx >= dy ? dx : dy;

      if (span <= 0) {
        return;
      }

      const dpi = span / cells;
      setInput('dpi', dpi.toFixed(2));
      setInput('offsetX', (points[0].x % dpi).toFixed(2));
      setInput('offsetY', (points[0].y % dpi).toFixed(2));
      bindPreviewValues();
      status(`反算：dpi=${dpi.toFixed(2)}（${span.toFixed(1)}px / ${cells} 格）`);
    }

    function setInput(name, value) {
      const input = card.querySelector(`[name="${name}"]`);

      if (input) {
        input.value = value;
      }
    }

    function nudge(name, delta) {
      const input = card.querySelector(`[name="${name}"]`);

      if (input) {
        input.value = String(Number(input.value || 0) + delta);
        bindPreviewValues();
      }
    }

    function bindPreview() {
      for (const input of card.querySelectorAll('input')) {
        input.addEventListener('change', bindPreviewValues);
      }

      bindPreviewValues();
    }

    /** 改任何数字都立刻重画网格（预览，不落盘）。 */
    function bindPreviewValues() {
      const value = readInputs();

      if (!value) {
        return;
      }

      // 非整数 dpi 往往意味着这张图被重采样过：50 格宽会累计漂移，边缘对不齐。
      // 不是错误，但要提示——别让它默默存在。
      const dpi = value.dpi;

      if (Math.abs(dpi - Math.round(dpi)) > 0.001) {
        status(`提示：dpi=${dpi} 不是整数，该图可能被重采样过；格数多时边缘会漂移，必要时用 ±1 局部校正。`);
      }

      void board?.setPreviewCalibration?.(value);
    }

    function readInputs() {
      const dpi = Number(card.querySelector('[name="dpi"]')?.value ?? 0);
      const offsetX = Number(card.querySelector('[name="offsetX"]')?.value ?? 0);
      const offsetY = Number(card.querySelector('[name="offsetY"]')?.value ?? 0);
      const scale = Number(card.querySelector('[name="scale"]')?.value ?? 5);

      if (!Number.isFinite(dpi) || dpi <= 0) {
        status('dpi 必须大于 0。');
        return null;
      }

      return { dpi, offsetX, offsetY, gridType: 'square', scale: scale > 0 ? scale : 5 };
    }
  }

  /** 双击 token：看这个战斗者的 Sheet 摘要（只读，数据来自快照的 combat.combatants）。 */
  function openTokenSheet(actorId, combat) {
    // 只比 id，**忽略大小写**：token 的 characterId 可能来自桥（小写），角色 id 可能是 `Ella`。
    // 不要再加名字匹配——两套口径就是上次 look_around 的坑。
    const key = String(actorId ?? '').toLowerCase();
    const combatant = (combat?.combatants ?? [])
      .find((item) => String(item.id ?? '').toLowerCase() === key);

    if (!combatant) {
      // 不静默：用户点了就得看到反馈（浮层能关掉，不挡事）。
      mode = 'sheet';
      card.replaceChildren(
        heading(actorId || '这个 token'),
        hint('该 token 不在本场战斗中，没有可显示的数值摘要。'),
        buttonRow([button('关掉', close, 'primary')]),
      );
      shell.hidden = false;
      return;
    }

    mode = 'sheet';
    card.replaceChildren(
      heading(combatant.cnName || combatant.name || combatant.id),
      hint('只读摘要（数据来自 roleplay.state.combat.combatants；HP 的唯一真相仍在 SheetStore）。'),
      fieldRow('HP', text(`${combatant.hp ?? '—'} / ${combatant.maxHp ?? '—'}`)),
      fieldRow('状态', text(statusText(combatant.status))),
      fieldRow('状态效果', text((combatant.conditions ?? []).join('、') || '无')),
      buttonRow([button('关掉', close, 'primary')]),
    );

    shell.hidden = false;
  }

  function close() {
    mode = null;
    shell.hidden = true;
    card.replaceChildren();
  }

  function status(message, slot = 'status') {
    const node = card.querySelector(`[data-${slot}]`) ?? element('p', 'overlay-status', { 'data-status': '' });
    node.textContent = message;

    if (!node.isConnected) {
      card.append(node);
    }
  }

  return {
    openCalibration,
    openTokenSheet,
    close,
    get mode() {
      return mode;
    },
  };
}

// ---------- 小工具 ----------

function element(tag, className, dataset = {}) {
  const node = document.createElement(tag);
  node.className = className;

  for (const [key, value] of Object.entries(dataset)) {
    node.setAttribute(key, value);
  }

  return node;
}

function heading(text) {
  const node = element('h2', 'overlay-title');
  node.textContent = text;
  return node;
}

function hint(text, slot) {
  const node = element('p', 'overlay-hint');

  if (slot) {
    node.dataset[slot] = '';
  }

  node.textContent = text;
  return node;
}

function fieldRow(label, control) {
  const row = element('label', 'overlay-field');
  const name = element('span', 'overlay-label');
  name.textContent = label;
  row.append(name, control);
  return row;
}

function numberInput(name, value, step) {
  const input = document.createElement('input');
  input.type = 'number';
  input.name = name;
  input.step = String(step);
  input.value = String(value);
  return input;
}

function text(value) {
  const node = element('span', 'overlay-value');
  node.textContent = value;
  return node;
}

function buttonRow(buttons) {
  const row = element('div', 'overlay-actions');
  row.append(...buttons);
  return row;
}

function button(label, onClick, variant) {
  const node = document.createElement('button');
  node.type = 'button';
  node.className = variant === 'primary' ? 'overlay-button overlay-button--primary' : 'overlay-button';
  node.textContent = label;
  node.addEventListener('click', onClick);
  return node;
}

function statusText(status) {
  switch (status) {
    case 'dying': return '濒死';
    case 'stable': return '已稳定';
    case 'dead': return '死亡';
    case 'alive': return '正常';
    default: return status ?? '—';
  }
}
