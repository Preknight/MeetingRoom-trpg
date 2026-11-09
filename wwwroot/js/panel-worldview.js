// 第 1 列（叙事模式）：世界观 / 场景资料卡（设计文档 §11.6）。
// 产品只保留叙事模式；会议资料面板仍在 DOM 里但恒隐藏（后端会议端点保留给旧数据与测试）。
// 这里展示的是 scene 与当前世界状态（只读）：正文与状态由后端算好，前端只呈现。
//
// 地图（本批改造）：**不再显示「地图 · 枭熊」**——那是 OBR 桥的旧通道（vtt.enabled=false 已停用）。
// 现在列的是地图编辑器里的地图（GET /api/maps）：名称 + 尺寸 + 最后修改时间；
// 点一项把它载入战术板（board.setMapId），另有「在编辑器中打开」跳到 /map-editor.html?map=<id>。
// 「世界观 / 场景」等卡片保持原样，只换掉地图那一块。

import { clone, empty } from './dom.js';

export function mount(root, options = {}) {
  let signature = null;
  let lastState = null;
  let busy = false;
  let error = '';

  /** 地图列表（GET /api/maps）。null = 还没拿到。 */
  let maps = null;
  let mapsError = '';
  /** 当前载入战术板的地图 id（点了哪张）。 */
  let loadedMapId = null;

  // 首屏就拿一次地图列表：它不经过 SSE，等不到"有人推"。
  // 注意必须写在 `return { render };` **之前**——mount 一旦 return，后面的语句就是死代码。
  void loadMaps();

  async function loadMaps() {
    try {
      const response = await fetch('/api/maps');

      if (!response.ok) {
        mapsError = `地图列表拉取失败（HTTP ${response.status}）`;
        maps = maps ?? [];
      } else {
        maps = await response.json();
        mapsError = '';
      }
    } catch (ex) {
      mapsError = `地图列表拉取失败：${ex?.message ?? ex}`;
      maps = maps ?? [];
    }

    repaint();
  }

  return {
    render(state) {
      lastState = state;

      const roleplay = state.roleplay;
      const next = JSON.stringify([
        roleplay.status,
        roleplay.storyId,
        roleplay.worldview,
        roleplay.situation,
        roleplay.userRole,
        roleplay.worldState,
        roleplay.vtt ?? null,
        // 地图列表（本批）：加载完 / 点选后都要重绘。
        maps ? maps.map((item) => `${item.id}:${item.updatedAt ?? 0}:${item.name}`) : null,
        mapsError,
        loadedMapId,
        // 刷新按钮的两个瞬时状态：变了就得重绘，否则"刷新中…"永远看不到。
        busy,
        error,
      ]);

      if (next === signature) {
        return;
      }

      signature = next;
      paint(state);
    },
  };

  function paint(state) {
    const roleplay = state.roleplay;

    // 判据是"这场戏有没有设定"（storyId），不是"跑没跑起来"。
    if (!roleplay.storyId) {
      root.replaceChildren(empty('还没有叙事：点顶栏的「新建叙事」'));
      return;
    }

    const cards = [
      ['世界观', roleplay.worldview],
      ['开场情境', roleplay.situation],
      ['我的身份', roleplay.userRole],
      ['当前世界状态', roleplay.worldState],
    ].filter(([, text]) => (text ?? '').length > 0);

    // 地图卡放最前：它是"此刻的作战环境"，比背景设定更需要一眼看到。
    root.replaceChildren(mapsCard(), ...cards.map(([title, text]) => card(title, text)));
  }

  /** 跳过 signature 短路强制重绘：按钮的进行中 / 失败状态不经过 SSE，得自己触发。 */
  function repaint() {
    if (!lastState) {
      return;
    }

    signature = null;
    paint(lastState);
  }

  /** 刷新按钮：请求中禁用；失败原因留在卡片上（不弹 toast、不挡别的列）。 */
  async function refresh() {
    if (busy || typeof options.onRefresh !== 'function') {
      return;
    }

    busy = true;
    error = '';
    repaint();

    try {
      const result = await options.onRefresh();

      if (result?.ok) {
        // main.js 会把结果写回 store；这里也更新一份，保证这一帧就显示新数据。
        if (lastState) {
          lastState = {
            ...lastState,
            roleplay: { ...lastState.roleplay, vtt: result.vtt ?? null },
          };
        }
      } else {
        error = result?.message || '刷新失败（原因未知）';
      }
    } catch (ex) {
      error = ex?.message ?? String(ex);
    } finally {
      busy = false;
      repaint();
    }
  }

  /**
   * 地图列表卡（本批）：替换原来的「地图 · 枭熊」。
   * 每行：名称 · 尺寸 · 最后修改时间；点行 = 载入战术板，右侧「编辑」跳地图编辑器。
   */
  function mapsCard() {
    const node = clone('tpl-material');
    node.dataset.status = 'ready';

    const title = node.querySelector('.card-title');
    title.textContent = '地图';

    const summary = node.querySelector('.card-summary');
    summary.classList.add('card-summary--lines');
    // 场景卡没有"可查看的正文"：去掉会议桌卡片上的正文链接。
    node.querySelector('.card-link').remove();

    const meta = node.querySelector('.card-meta');
    const actions = node.querySelector('.card-actions');
    actions.replaceChildren();

    if (maps === null) {
      summary.textContent = '正在读取地图列表…';
      meta.textContent = '';
      return node;
    }

    if (maps.length === 0) {
      summary.textContent = mapsError || '还没有地图。用「新建地图」或在地图编辑器里导入一张底图。';
      meta.textContent = '';
      return node;
    }

    // 列表本身放在 summary 里（它是这块卡片唯一的多行区域）。
    summary.replaceChildren(
      ...maps.map((item) => {
        const row = document.createElement('button');
        row.type = 'button';
        row.className = 'map-row';
        row.dataset.mapId = item.id;
        row.dataset.loaded = item.id === loadedMapId ? 'true' : 'false';
        row.title = '点一下把它载入战术板';

        const name = document.createElement('span');
        name.className = 'map-row-name';
        name.textContent = item.name || item.id;

        const detail = document.createElement('span');
        detail.className = 'map-row-detail';
        detail.textContent = [
          `${item.width}×${item.height}`,
          item.mode === 'sketch' ? '手绘' : '底图',
          formatTime(item.updatedAt),
        ].filter(Boolean).join(' · ');

        const edit = document.createElement('span');
        edit.className = 'map-row-edit';
        edit.textContent = '编辑';
        edit.title = '在地图编辑器里打开';
        edit.addEventListener('click', (event) => {
          event.stopPropagation();
          window.open(`/map-editor.html?map=${encodeURIComponent(item.id)}`, '_blank', 'noopener');
        });

        row.append(name, detail, edit);
        row.addEventListener('click', () => selectMap(item));
        return row;
      }),
    );

    meta.textContent = mapsError ? mapsError : `${maps.length} 张`;

    const refreshButton = document.createElement('button');
    refreshButton.type = 'button';
    refreshButton.textContent = '刷新列表';
    refreshButton.addEventListener('click', () => void loadMaps());
    actions.append(refreshButton);

    return node;
  }

  /** 点一行：把它载入战术板（战术板是全局的，用 main.js 暴露的 board 视图）。 */
  function selectMap(item) {
    const board = window.__meetingroomBoard;

    if (!board?.setMapId) {
      // 战术板还没挂上（会议模式 / 面板未渲染）：不要假装成功。
      mapsError = '战术板不在当前视图，先切到叙事模式。';
      repaint();
      return;
    }

    loadedMapId = item.id;
    board.setMapId(item.id);
    void board.render({});
    repaint();
  }

  function formatTime(value) {
    const stamp = Number(value);

    if (!Number.isFinite(stamp) || stamp <= 0) {
      // 旧后端不带 updatedAt（或从没保存过）：不要编一个时间出来。
      return '修改时间未知';
    }

    const date = new Date(stamp);
    const pad = (n) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  function mapCard(vtt) {
    const info = describeVtt(vtt, error);
    const node = clone('tpl-material');

    node.dataset.status = info.status;
    node.querySelector('.card-title').textContent = '地图 · 枭熊';

    const summary = node.querySelector('.card-summary');
    summary.classList.add('card-summary--lines');
    summary.textContent = info.lines.join('\n');
    summary.title = info.lines.join('\n');

    node.querySelector('.card-meta').textContent = info.meta;
    // 场景卡没有"可查看的正文"：去掉会议桌卡片上的正文链接。
    node.querySelector('.card-link').remove();

    const actions = node.querySelector('.card-actions');
    actions.replaceChildren();

    if (typeof options.onRefresh === 'function') {
      const button = document.createElement('button');
      button.type = 'button';
      button.dataset.action = 'vtt-refresh';
      button.textContent = busy ? '刷新中…' : '刷新';
      button.disabled = busy;
      button.title = '重新拉一次枭熊地图状态（GET /api/roleplay/vtt）';
      button.addEventListener('click', refresh);
      actions.append(button);
    }

    return node;
  }
}

function describeVtt(vtt, error) {
  const info = baseInfo(vtt);

  if (error) {
    // 失败原因就写在这张卡上：用户看的是这一列，不该只留一条会消失的提示。
    info.lines.push(`刷新失败：${error}`);
  }

  return info;
}

function baseInfo(vtt) {
  // 没有这个字段（旧后端 / 还没推过快照）与"未启用"是同一个展示：至少不空白、不报错。
  if (!vtt || vtt.enabled !== true) {
    return {
      status: 'draft',
      meta: '未启用',
      lines: [
        '地图未启用：角色只按文字场景行动，距离与位置不会出现在上下文里。',
        '启用方式：meetingroom.json 里设 vtt.enabled = true，并启动本机的 obr_bridge。',
      ],
    };
  }

  if (vtt.available !== true) {
    const reason = (vtt.reason ?? '').trim();
    return {
      status: 'parked',
      meta: '未连接',
      lines: [
        reason ? `地图未连接：${reason}` : '地图未连接：还没有收到枭熊的快照。',
        '这一轮按没有地图处理——不会自己估算距离。',
      ],
    };
  }

  const grid = vtt.grid ?? {};
  // 相对时间（"3 分钟前"）比绝对时间更能一眼看出"桥是不是卡住了"。
  const lines = [`已连接 · 最后更新 ${formatTime(vtt.lastUpdated)}${formatAge(vtt.lastUpdated)}`];
  lines.push(`网格 ${grid.dpi} dpi · ${grid.measurement} · ${grid.multiplier} ${grid.unit}/格`);

  const rulers = (vtt.rulers ?? []).map((ruler) => ruler.measurement).filter((text) => (text ?? '').length > 0);

  if (rulers.length > 0) {
    // 多条标尺逐条列出并标出总数：只写"标尺 20 ft"会让人以为场上只有一条。
    const count = rulers.length > 1 ? `（共 ${rulers.length} 条）` : '';
    lines.push(`标尺 ${rulers.join('、')}${count}`);
  }

  lines.push(
    vtt.includeHidden
      ? '含隐藏项：DM 关掉的 token 也会交给模型（会剧透）'
      : '不含隐藏项：隐藏的 token 一个字都不进上下文',
  );

  return { status: 'adopted', meta: '已连接', lines };
}

/** 最后更新时间：桥给的是 UTC 毫秒（receivedAt）投影出的 ISO 串，这里按本地时区显示。 */
function formatTime(value) {
  if (!value) {
    return '未知';
  }

  const at = new Date(value);

  return Number.isNaN(at.getTime()) ? '未知' : at.toLocaleString('zh-CN', { hour12: false });
}

/** 距今多久："3 分钟前"。算不出来就返回空串——别在卡片上写"Invalid Date"。 */
function formatAge(value) {
  if (!value) {
    return '';
  }

  const at = new Date(value);

  if (Number.isNaN(at.getTime())) {
    return '';
  }

  const seconds = Math.max(0, Math.round((Date.now() - at.getTime()) / 1000));

  if (seconds < 5) {
    return '（刚刚）';
  }

  if (seconds < 60) {
    return `（${seconds} 秒前）`;
  }

  const minutes = Math.floor(seconds / 60);

  if (minutes < 60) {
    return `（${minutes} 分钟前）`;
  }

  const hours = Math.floor(minutes / 60);

  return hours < 24 ? `（${hours} 小时前）` : `（${Math.floor(hours / 24)} 天前）`;
}

function card(title, text) {
  const node = clone('tpl-material');
  node.dataset.status = 'adopted';
  node.querySelector('.card-title').textContent = title;

  const summary = node.querySelector('.card-summary');
  summary.textContent = text;
  summary.title = text;

  node.querySelector('.card-meta').textContent = '常驻';
  // 场景卡没有"可查看的正文"：去掉会议桌卡片上的正文链接。
  node.querySelector('.card-link').remove();
  return node;
}
