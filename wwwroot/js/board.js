// 战术板（F2 底图 + F3 交互）：底图 + SVG 网格 + token + 先攻徽标 + 缩放/平移/测距/视角。
//
// 坐标约定：**SVG 的 viewBox 就是图片的像素尺寸**（`0 0 width height`），
// 所以 token 的像素坐标（`roleplay.state.vtt.tokens[].x/y`）可以直接用，不用换算。
// 底图用 `object-fit: contain`，SVG 用 `preserveAspectRatio="xMidYMid meet"`——两者等价，叠在一起不会错位。
//
// 缩放/平移只改**视口层**的 CSS transform（`.board-viewport`），底图与 SVG 一起动、严格同步；
// 网格已与 token 分层，缩放时**不重建**网格（用户要求：只改 transform，不重绘）。
//
// 测距（F3-②）：换算只用 `dpi × scale`（§4.2），前端不自创算法；无标定时**禁用并说明原因**。

import * as api from './api.js';

const SVG_NS = 'http://www.w3.org/2000/svg';

/** 缩放范围（F3-①）：0.5×–3×。 */
const MIN_SCALE = 0.5;
const MAX_SCALE = 3;

/**
 * @param {HTMLElement} container
 * @param {{ view?: 'dm' | 'player' }} [options] 初始视角，默认 player（叙事页给玩家看）；
 *   编辑器传 `{ view: 'dm' }`，**首次渲染就是 DM**，不必等后面再 setView。
 */
export function mount(container, options = {}) {
  const wrap = document.createElement('div');
  wrap.className = 'board';

  const viewport = document.createElement('div');
  viewport.className = 'board-viewport';

  const image = document.createElement('img');
  image.className = 'board-map';
  image.alt = '战术板底图';
  // HTML5 原生拖拽会在按下时抢走 pointer 事件，表现为"图片跟鼠标飞"、画布不动。
  // CSS 的 `-webkit-user-drag: none` 只有 Chromium/WebKit 认，Firefox 必须靠这个属性。
  image.draggable = false;
  image.setAttribute('draggable', 'false');

  const overlay = document.createElementNS(SVG_NS, 'svg');
  overlay.classList.add('board-overlay');

  viewport.append(image, overlay);

  const toolbar = document.createElement('div');
  toolbar.className = 'board-toolbar';

  const measureButton = toolButton('测距', '点两点量距离（换算用 dpi × scale）；Esc 清除');
  const viewButton = toolButton('视角：DM', '切换 DM / 玩家视角。前端开关只是模拟——真正的过滤在后端（§4.4）');
  // 文案稍后由 setView 统一同步，这里只是建按钮时占位。
  const calibrateButton = toolButton('标定', '标定底图：图上取两点 + 填跨几格，反算 dpi / offset（F4）');
  toolbar.append(measureButton, viewButton, calibrateButton);

  const hint = document.createElement('p');
  hint.className = 'board-hint';
  hint.hidden = true;

  wrap.append(viewport, toolbar, hint);
  container.append(wrap);

  /** 已加载的地图（含标定）；null = 还没有底图。 */
  let current = null;

  /** 地图文档里的墙与房间（M2）：叙事战术板也要画，所以由 board 自己按地图 id 拉。 */
  let lastDocument = null;
  let lastDocumentMapId = null;
  /** 文档版本号：编辑器改一次就 +1，用来让图层重建（不能只看长度，拖顶点不改点数）。 */
  let documentRevision = 0;

  /** 快照带来的标定（`undefined` = 没提供，才回落到清单里的 calibration）。 */
  let snapshotCalibration;

  /**
   * 房间 / 墙是否接收指针事件（M2 顶点编辑的前提）。
   * 默认关：战术板点房间没有意义，而且开着会让"绘制/取点"的落点被房间吃掉。
   */
  let shapeHit = false;

  function setShapeHit(enabled) {
    shapeHit = enabled === true;
    wrap.dataset.hit = shapeHit ? 'on' : 'off';
  }

  /** 取点 / 测量期间临时让形状让开，结束后恢复调用方的设置。 */
  function pauseShapeHit() {
    if (shapeHit) {
      wrap.dataset.hit = 'off';
    }
  }

  function resumeShapeHit() {
    wrap.dataset.hit = shapeHit ? 'on' : 'off';
  }

  /** 编辑器推送文档（它自己有最新的一份，不必再拉一次）。 */
  function setDocument(nextDocument) {
    // 参数名避开 `document`：遮蔽全局 DOM 对象是 F7 踩过的同一个坑。
    lastDocument = nextDocument;
    snapshotCalibration = undefined;
    documentRevision++;
    // 直接作废图层缓存：编辑器每改一次就必须重建（否则新画的墙/房间不出现）。
    lastGridState = '';
  }
  let lastGridState = '';
  let lastCombat = null;
  let lastVtt = null;

  /** 视口变换：translate(tx,ty) scale(k)，作用于 `.board-viewport`。 */
  let scale = 1;
  let tx = 0;
  let ty = 0;

  /**
   * 视角：`dm` 全部（未登记虚线、迷雾半透明提示）；`player` 按玩家能看到的画（§4.4）。
   * 默认 **player**：这块板在叙事页是给玩家看的（M6a 起）；编辑器用 `{ view: 'dm' }` 覆盖。
   */
  let view = options.view === 'dm' ? 'dm' : 'player';

  /**
   * 切视角：**赋值 / 同步按钮文案 / 重绘**一次做完。
   *
   * <p>以前这里只做第一件事，文案与重绘留在按钮的 click 里——于是任何"从外面调 setView"的地方
   * 都会进入一个自相矛盾的状态：内部是 dm、画面还是 player、按钮写着 DM。
   * 编辑器的表现就是"要点两下才到 DM"（第一下把 dm 切成 player，画面没动；第二下才回来）。</p>
   */
  function setView(next) {
    view = next === 'player' ? 'player' : 'dm';
    viewButton.textContent = `视角：${view === 'dm' ? 'DM' : '玩家'}`;
    applyViewport();
    void render({});
  }

  // 建好按钮就把文案对齐当前视角（避免初始状态文案与 view 不一致）。
  viewButton.textContent = `视角：${view === 'dm' ? 'DM' : '玩家'}`;

  /** 测距模式与已取的点（0/1/2）。 */
  let measuring = false;
  let measurePoints = [];

  /** 当前高亮的 actor（列表 ↔ 地图双向高亮，纯显示层）。 */
  let highlight = null;

  /** 标定取点（F4）：`{ count, points, resolve }`，满 count 后 resolve。 */
  let picking = null;

  /** 预览标定（F4）：浮层里调 dpi/offset 时先看效果，保存前不落盘。 */
  let previewCalibration = null;

  applyViewport();

  /** 编辑器指定要看哪张图（M1）；不设时沿用"第一张已标定的"。 */
  let preferredMapId = null;

  /** 编辑器用：拖 token 的回调 `(actorId, phase, point)`；不设时战术板行为完全不变。 */
  let tokenDragHandler = null;
  let tokenDragging = null;

  function setMapId(id) {
    preferredMapId = id;
  }

  /** 拖拽时的修饰键（编辑器据此决定是否吸附）。 */
  function modifiersOf(event) {
    return { altKey: event.altKey === true, shiftKey: event.shiftKey === true };
  }

  function setTokenDragHandler(handler) {
    tokenDragHandler = handler;
  }

  async function loadMap() {
    const result = await api.getJson('/api/maps');

    if (!result.ok || !Array.isArray(result.data)) {
      return null;
    }

    const maps = result.data;

    if (preferredMapId) {
      return maps.find((map) => map.id === preferredMapId) ?? null;
    }

    return maps.find((map) => map.calibrated) ?? maps[0] ?? null;
  }

  /** 渲染一次：`vtt` 来自 roleplay.state.vtt，`combat` 来自 roleplay.state.combat。 */
  async function render({ vtt, combat, calibrations, document: pushedDocument, map: pushedMap } = {}) {
    if (vtt !== undefined) {
      lastVtt = vtt;
    }

    if (combat !== undefined) {
      lastCombat = combat;
    }

    if (pushedDocument !== undefined) {
      setDocument(pushedDocument);
    }

    if (pushedMap !== undefined) {
      // 快照的地图节点（M4）：与编辑器推的 document 同形状，**标定也从它派生**，
      // 这样网格、墙、房间只有一份数据源（F6 的教训：两路供给同一 UI 必须同口径）。
      lastDocument = pushedMap;
      snapshotCalibration = pushedMap?.grid
        ? {
            dpi: pushedMap.grid.dpi,
            offsetX: pushedMap.grid.offsetX,
            offsetY: pushedMap.grid.offsetY,
            gridType: 'square',
            scale: pushedMap.grid.feetPerCell,
          }
        : null;
      documentRevision++;
      lastGridState = '';
    }

    const summary = await loadMap();

    if (!summary) {
      current = null;
      image.removeAttribute('src');
      overlay.replaceChildren();
      hint.hidden = false;
      hint.textContent = '还没有底图：用 POST /api/maps/upload 传一张 webp / png。';
      return;
    }

    // 标定：快照带了 map 就用它（同一份数据），否则用调用方缓存 / 清单里的 calibration。
    const saved = snapshotCalibration !== undefined
      ? snapshotCalibration
      : (calibrations?.get(summary.id) ?? summary.calibration ?? null);
    // 预览优先：标定浮层里调数值时立刻看效果，保存前不改后端。
    const calibration = previewCalibration ?? saved;

    if (!current || current.id !== summary.id) {
      current = { ...summary, calibration };

      // M5：sketch（手绘、无底图）不能设 src——否则会去请求不存在的 /content。
      // 不设 src 时 <img> 不占内容，CSS 的深色底就是"纯色底"。
      if (summary.mode === 'sketch') {
        image.removeAttribute('src');
        // 没有 src 时浏览器会画一个破图图标 + alt 文本：直接隐藏。
        image.hidden = true;
      } else {
        image.hidden = false;
        image.src = `/api/maps/${summary.id}/content`;
      }
    } else {
      current = { ...current, ...summary, calibration };
    }

    const width = summary.width;
    const height = summary.height;

    overlay.setAttribute('viewBox', `0 0 ${width} ${height}`);
    overlay.setAttribute('preserveAspectRatio', 'xMidYMid meet');

    // 叙事战术板：按当前地图 id 拉一次文档（墙 / 房间）。编辑器推过就不用再拉。
    if (pushedDocument === undefined && pushedMap === undefined && lastDocumentMapId !== summary.id) {
      await loadDocumentFor(summary.id);
    }

    // sketch / image 决定配色（深底上的墙要亮一些才看得见）。
    wrap.dataset.mode = summary.mode ?? 'image';

    const gridKey = `${width}x${height}|${JSON.stringify(calibration)}|${documentRevision}`;

    if (gridKey !== lastGridState) {
      lastGridState = gridKey;
      overlay.replaceChildren(...buildLayer(width, height, calibration));
    }

    const zonesLayer = overlay.querySelector('.board-zones');

    if (zonesLayer) {
      zonesLayer.replaceChildren(...buildZones(lastDocument?.zones, calibration, width));
    }

    const fogLayer = overlay.querySelector('.board-fog');

    if (fogLayer) {
      fogLayer.replaceChildren(...buildFog(lastDocument?.fog, calibration, width, height));
    }

    const tokensLayer = overlay.querySelector('.board-tokens');

    if (tokensLayer) {
      tokensLayer.replaceChildren(...buildTokens(lastVtt, lastCombat, calibration, width, height));
    }

    paintMeasure();
    updateHint();
  }

  function updateHint() {
    if (current && !current.calibration) {
      hint.hidden = false;
      hint.textContent = '未标定：网格、测距与距离暂不可用（标定后自动出现）。';
      return;
    }

    hint.hidden = true;
    hint.textContent = '';
  }

  /** 网格层 + token 层 + 测距层（三层分开：网格只在标定/尺寸变化时重建）。 */
  function buildLayer(width, height, calibration) {
    const grid = document.createElementNS(SVG_NS, 'g');
    grid.classList.add('board-grid');

    if (calibration && calibration.dpi > 0) {
      const { dpi, offsetX, offsetY } = calibration;

      for (let x = offsetX; x <= width; x += dpi) {
        grid.append(line(x, 0, x, height));
      }

      for (let y = offsetY; y <= height; y += dpi) {
        grid.append(line(0, y, width, y));
      }
    }

    // 区域层（M7）：在网格之上、迷雾之下——迷雾要能遮住区域，token 不会被遮住。
    const zones = document.createElementNS(SVG_NS, 'g');
    zones.classList.add('board-zones');
    zones.setAttribute('pointer-events', 'none');

    // 迷雾层（M6a）：在网格之上、房间与墙之下——玩家视角的"未探索"要盖住网格与房间，
    // 而 DM 视角只是半透明提示，墙/房间仍看得清。
    const fog = document.createElementNS(SVG_NS, 'g');
    fog.classList.add('board-fog');
    fog.setAttribute('pointer-events', 'none');

    // 房间填充在网格之上、墙与 token 之下：填充必须很淡（fill-opacity ≤ .12），不能盖住底图细节。
    const rooms = document.createElementNS(SVG_NS, 'g');
    rooms.classList.add('board-rooms');
    rooms.append(...buildRooms(lastDocument?.rooms, calibration, width));

    const walls = document.createElementNS(SVG_NS, 'g');
    walls.classList.add('board-walls');
    walls.append(...buildWalls(lastDocument?.walls, width));

    const tokens = document.createElementNS(SVG_NS, 'g');
    tokens.classList.add('board-tokens');

    const measure = document.createElementNS(SVG_NS, 'g');
    measure.classList.add('board-measure');

    const picks = document.createElementNS(SVG_NS, 'g');
    picks.classList.add('board-picks');

    // 顺序（设计 §4）：底图/网格 → 区域 → 迷雾 → 房间/墙 → token → 测距 → 取点。
    // 迷雾要能遮住区域，而 token 不能被遮住。
    return [grid, zones, fog, rooms, walls, tokens, measure, picks];
  }

  /** 屏幕像素 → 图片像素的换算（字号、线宽不跟图缩放走，F7-⑦ 同一套理由）。 */
  function screenPerImageAt(imageWidth) {
    const viewportWidth = overlay.getBoundingClientRect().width || 0;
    return viewportWidth > 0 ? viewportWidth / imageWidth : 1 / Math.max(imageWidth, 1);
  }

  function buildRooms(rooms, calibration, imageWidth) {
    if (!Array.isArray(rooms) || rooms.length === 0) {
      return [];
    }

    const nodes = [];
    const perImage = screenPerImageAt(imageWidth);

    for (const room of rooms) {
      const points = (room.polygon ?? []).map((point) => point.join(',')).join(' ');

      if (!points) {
        continue;
      }

      const polygon = document.createElementNS(SVG_NS, 'polygon');
      polygon.setAttribute('points', points);
      polygon.classList.add('board-room');
      polygon.dataset.roomId = room.id ?? '';

      // 名称放在多边形重心（顶点平均足够，房间都接近凸形）。
      const centroid = (room.polygon ?? []).reduce(
        (sum, point) => ({ x: sum.x + point[0] / room.polygon.length, y: sum.y + point[1] / room.polygon.length }),
        { x: 0, y: 0 });

      const label = document.createElementNS(SVG_NS, 'text');
      label.setAttribute('x', String(centroid.x));
      label.setAttribute('y', String(centroid.y));
      label.setAttribute('text-anchor', 'middle');
      label.setAttribute('font-size', String(12 / Math.max(perImage, 1e-6)));
      label.classList.add('board-room-label');
      label.textContent = room.name ?? '';
      nodes.push(polygon, label);
    }

    return nodes;
  }

  function buildWalls(walls) {
    if (!Array.isArray(walls) || walls.length === 0) {
      return [];
    }

    return walls
      .filter((wall) => Array.isArray(wall.points) && wall.points.length >= 2)
      .map((wall) => {
        const polyline = document.createElementNS(SVG_NS, 'polyline');
        polyline.setAttribute('points', wall.points.map((point) => point.join(',')).join(' '));
        polyline.classList.add('board-wall', `board-wall--${wall.kind ?? 'wall'}`);
        polyline.dataset.wallId = wall.id ?? '';

        if (wall.kind === 'door') {
          polyline.dataset.open = String(wall.open === true);
        }

        return polyline;
      });
  }

  /**
   * 迷雾（M6a）：三态按格渲染。用**单个 `<path>`** 拼所有格子——每格一个 `<rect>`
   * 在大图上会有上千个节点（4096 宽 ≈ 1700 格），节点太多会拖慢每次重绘。
   *
   * 两种视角（设计 §4）：
   * - DM：未探索 = 黑 35% + 边界线；已探索 = 不绘制；可见 = 细边框；
   * - 玩家：未探索 = 纯黑；已探索 = 黑 65%；可见 = 不绘制。
   */
  function buildFog(fog, calibration, width, height) {
    if (!fog?.enabled || !calibration || !(calibration.dpi > 0) || width <= 0 || height <= 0) {
      return [];
    }

    const { dpi, offsetX = 0, offsetY = 0 } = calibration;
    const columns = Math.ceil(Math.max(0, width - offsetX) / dpi);
    const rows = Math.ceil(Math.max(0, height - offsetY) / dpi);

    if (columns <= 0 || rows <= 0) {
      return [];
    }

    const key = (x, y) => `${x},${y}`;
    const explored = new Set((fog.explored ?? []).map((cell) => key(cell[0], cell[1])));
    const visible = new Set((fog.visible ?? []).map((cell) => key(cell[0], cell[1])));

    const rect = (x, y) => {
      const left = offsetX + (x * dpi);
      const top = offsetY + (y * dpi);
      return `M${left} ${top}h${dpi}v${dpi}h${-dpi}z`;
    };

    const unexplored = [];
    const seen = [];

    for (let y = 0; y < rows; y += 1) {
      for (let x = 0; x < columns; x += 1) {
        if (!explored.has(key(x, y))) {
          unexplored.push(rect(x, y));
        } else if (view !== 'player' || !visible.has(key(x, y))) {
          seen.push(rect(x, y));
        }
      }
    }

    const nodes = [];

    if (unexplored.length > 0) {
      const path = document.createElementNS(SVG_NS, 'path');
      path.setAttribute('d', unexplored.join(''));

      if (view === 'player') {
        path.classList.add('board-fog-unexplored');
        path.setAttribute('fill', '#000');
        path.setAttribute('fill-opacity', '1');
      } else {
        path.classList.add('board-fog-unexplored');
        path.setAttribute('fill', '#000');
        path.setAttribute('fill-opacity', '0.35');

        // DM 视角额外描边：大块未探索区域里一条细线不显眼，边界能帮它一眼看出"这块没揭"。
        path.setAttribute('stroke', 'rgba(255, 209, 102, .55)');
        path.setAttribute('stroke-width', '1');
        path.setAttribute('vector-effect', 'non-scaling-stroke');
      }

      nodes.push(path);
    }

    // 玩家视角：已探索但不在当前视野 → 黑 65%（能看出地形，看不清细节）。
    if (view === 'player' && seen.length > 0) {
      const path = document.createElementNS(SVG_NS, 'path');
      path.classList.add('board-fog-explored');
      path.setAttribute('d', seen.join(''));
      path.setAttribute('fill', '#000');
      path.setAttribute('fill-opacity', '0.65');
      nodes.push(path);
    }

    // DM 视角：当前视野画细边框（玩家视角不画，可见格就是原图）。
    if (view !== 'player' && visible.size > 0) {
      const out = [];

      for (const cell of visible) {
        const [x, y] = cell.split(',').map(Number);
        out.push(rect(x, y));
      }

      const path = document.createElementNS(SVG_NS, 'path');
      path.classList.add('board-fog-visible');
      path.setAttribute('d', out.join(''));
      path.setAttribute('fill', 'none');
      path.setAttribute('stroke', 'rgba(120, 220, 180, .8)');
      path.setAttribute('stroke-width', '1.5');
      path.setAttribute('vector-effect', 'non-scaling-stroke');
      nodes.push(path);
    }

    return nodes;
  }

  /**
   * 特殊区域（M7）：圆 / 多边形 + 名称标签。
   * <c>visible=false</c> 只在 DM 视角画（虚线 + 「(隐藏)」后缀），玩家视角完全不渲染。
   */
  function buildZones(zones, calibration, imageWidth) {
    if (!Array.isArray(zones) || zones.length === 0) {
      return [];
    }

    const nodes = [];
    const perImage = screenPerImageAt(imageWidth);
    const dpi = calibration?.dpi > 0 ? calibration.dpi : 40;

    for (const zone of zones) {
      const hidden = zone.visible === false;

      if (hidden && view === 'player') {
        continue;
      }

      const shape = zone.shape ?? {};
      let element = null;
      let labelAt = null;

      if (shape.type === 'polygon' && Array.isArray(shape.points) && shape.points.length >= 3) {
        element = document.createElementNS(SVG_NS, 'polygon');
        element.setAttribute('points', shape.points.map((point) => point.join(',')).join(' '));

        const centroid = shape.points.reduce(
          (sum, point) => ({ x: sum.x + point[0] / shape.points.length, y: sum.y + point[1] / shape.points.length }),
          { x: 0, y: 0 });
        labelAt = centroid;
      } else if (shape.type === 'circle' && Array.isArray(shape.center)) {
        element = document.createElementNS(SVG_NS, 'circle');
        element.setAttribute('cx', String(shape.center[0] ?? 0));
        element.setAttribute('cy', String(shape.center[1] ?? 0));
        element.setAttribute('r', String((shape.radiusCells ?? 2) * dpi));
        labelAt = { x: shape.center[0] ?? 0, y: shape.center[1] ?? 0 };
      }

      if (!element) {
        continue;
      }

      element.classList.add('board-zone');
      element.dataset.zoneId = zone.id ?? '';
      element.dataset.kind = zone.kind ?? 'custom';
      element.setAttribute('fill', zone.color ?? '#c9a227');
      element.setAttribute('fill-opacity', hidden ? '0.1' : '0.18');
      element.setAttribute('stroke', zone.color ?? '#c9a227');
      element.setAttribute('stroke-width', '1.5');
      element.setAttribute('vector-effect', 'non-scaling-stroke');

      if (hidden) {
        element.setAttribute('stroke-dasharray', '6 4');
      }

      nodes.push(element);

      if (labelAt && zone.name) {
        const label = document.createElementNS(SVG_NS, 'text');
        label.setAttribute('x', String(labelAt.x));
        label.setAttribute('y', String(labelAt.y));
        label.setAttribute('text-anchor', 'middle');
        label.setAttribute('font-size', String(11 / Math.max(perImage, 1e-6)));
        label.classList.add('board-zone-label');
        label.textContent = hidden ? `${zone.name}（隐藏）` : zone.name;
        nodes.push(label);
      }
    }

    return nodes;
  }

  async function loadDocumentFor(id) {
    lastDocumentMapId = id;

    try {
      const response = await fetch(`/api/maps/${id}/document`);

      if (!response.ok) {
        lastDocument = null;
        documentRevision++;
        return;
      }

      lastDocument = await response.json();
    } catch {
      lastDocument = null;
    }

    documentRevision++;
  }

  function line(x1, y1, x2, y2) {
    const element = document.createElementNS(SVG_NS, 'line');
    element.setAttribute('x1', String(x1));
    element.setAttribute('y1', String(y1));
    element.setAttribute('x2', String(x2));
    element.setAttribute('y2', String(y2));
    // 缩放后粗细不变：4096 像素宽的图缩到半屏也看得清网格。
    element.setAttribute('vector-effect', 'non-scaling-stroke');
    return element;
  }

  /** token：像素坐标直接画；视角决定画不画（`player` 只画 visible，`dm` 全部 + 未登记虚线）。 */
  function buildTokens(vtt, combat, calibration, width, height) {
    const tokens = vtt?.tokens ?? [];
    // F7-⑦：token 半径按**屏幕像素**定（约 11px），再反算成图片坐标——
    // 跟 dpi 走会在 4096 宽图 + 窄板时缩成 ~4.7 屏幕 px，徽标读不出来。
    const viewportWidth = overlay.getBoundingClientRect().width || 0;
    const screenPerImage = viewportWidth > 0 ? viewportWidth / width : 1 / Math.max(width, 1);
    const radius = 11 / Math.max(screenPerImage, 1e-6);
    const initiativeOf = new Map((combat?.order ?? []).map((entry) => [entry.actor, entry.initiative]));
    const currentActor = combat?.currentActor ?? null;
    const nodes = [];

    for (const token of tokens) {
      const actorId = token.characterId ?? token.id ?? '';
      const visible = token.visible !== false;

      if (view === 'player' && !visible) {
        continue;
      }

      const group = document.createElementNS(SVG_NS, 'g');
      group.classList.add('board-token');
      group.dataset.tokenId = token.id ?? '';
      group.dataset.actorId = actorId;
      group.dataset.unlisted = String(!visible);
      group.setAttribute('transform', `translate(${token.x ?? 0} ${token.y ?? 0})`);

      if (highlight && actorId === highlight) {
        group.classList.add('board-token--highlight');
      }

      const circle = document.createElementNS(SVG_NS, 'circle');
      circle.setAttribute('r', String(radius));
      circle.setAttribute('vector-effect', 'non-scaling-stroke');
      group.append(circle);

      // 当前行动者：加一圈高亮环（不改变命中区域）。
      if (actorId && actorId === currentActor) {
        const ring = document.createElementNS(SVG_NS, 'circle');
        ring.classList.add('board-token-ring');
        ring.setAttribute('r', String(radius * 1.45));
        ring.setAttribute('vector-effect', 'non-scaling-stroke');
        group.append(ring);
      }

      const initiative = actorId ? initiativeOf.get(actorId) : undefined;

      if (initiative !== undefined) {
        const badge = document.createElementNS(SVG_NS, 'text');
        badge.classList.add('board-token-badge');

        // 徽标字号固定屏幕 10px（换算回图片像素），保证缩放后仍可读。
        const badgeFont = 10 / Math.max(screenPerImage, 1e-6);
        badge.setAttribute('font-size', String(badgeFont));
        badge.setAttribute('text-anchor', 'middle');
        badge.setAttribute('dominant-baseline', 'middle');
        badge.textContent = String(initiative);

        // 沿 45° 推到**环外**，且要保证两者的**外接矩形**分离（不只是圆与矩形不相交）：
        // 45° 时轴向分量 = distance·cos45，所以按“轴向要够远”反推距离。
        const halfWidth = badgeFont * 0.32 * String(initiative).length;
        const halfHeight = badgeFont * 0.55;
        const clearance = 2 / Math.max(screenPerImage, 1e-6);
        // 环描边 2.5（non-scaling）会让外接矩形再大一截，用 clearance 吸收。
        const ringRadius = (radius * 1.45) + (1.5 / Math.max(screenPerImage, 1e-6));
        const axialNeeded = ringRadius + Math.max(halfWidth, halfHeight) + clearance;
        const distance = axialNeeded / Math.cos(Math.PI / 4);
        const angle = -Math.PI / 4;

        badge.setAttribute('x', String(Math.cos(angle) * distance));
        badge.setAttribute('y', String(Math.sin(angle) * distance));
        group.append(badge);
      }

      if (token.name) {
        const label = document.createElementNS(SVG_NS, 'text');
        label.classList.add('board-token-label');
        label.setAttribute('y', String(radius * 2.1));
        label.setAttribute('text-anchor', 'middle');
        label.setAttribute('font-size', String(9 / Math.max(screenPerImage, 1e-6)));
        label.textContent = token.name;
        group.append(label);
      }

      nodes.push(group);
    }

    return nodes;
  }

  // ---------- 测距（F3-②） ----------

  function paintMeasure() {
    const layer = overlay.querySelector('.board-measure');

    if (!layer) {
      return;
    }

    layer.replaceChildren();

    if (measurePoints.length === 0) {
      return;
    }

    const first = measurePoints[0];
    const second = measurePoints[1];
    const dotRadius = current?.calibration?.dpi ? current.calibration.dpi * 0.2 : 12;

    if (second) {
      layer.append(line(first.x, first.y, second.x, second.y));
    }

    for (const point of measurePoints) {
      const dot = document.createElementNS(SVG_NS, 'circle');
      dot.setAttribute('cx', String(point.x));
      dot.setAttribute('cy', String(point.y));
      dot.setAttribute('r', String(dotRadius));
      dot.setAttribute('vector-effect', 'non-scaling-stroke');
      layer.append(dot);
    }

    if (second) {
      const text = document.createElementNS(SVG_NS, 'text');
      text.classList.add('board-measure-label');
      text.setAttribute('x', String((first.x + second.x) / 2));
      text.setAttribute('y', String((first.y + second.y) / 2));
      text.setAttribute('text-anchor', 'middle');
      text.setAttribute('font-size', String(current?.calibration?.dpi ? current.calibration.dpi * 0.55 : 24));
      text.textContent = formatDistance(first, second);
      layer.append(text);
    }
  }

  /** 距离换算：**只用 dpi × scale**（§4.2），不引入任何前端自创公式。 */
  function formatDistance(first, second) {
    const calibration = current?.calibration;

    if (!calibration || calibration.dpi <= 0) {
      return '未标定：无法换算距离';
    }

    const pixels = Math.hypot(second.x - first.x, second.y - first.y);
    const cells = pixels / calibration.dpi;
    const feet = cells * (calibration.scale > 0 ? calibration.scale : 5);

    return `${cells.toFixed(1)} 格 · ${feet.toFixed(0)} 尺`;
  }

  // ---------- 缩放 / 平移（F3-①） ----------

  function applyViewport() {
    // 缩得太小时房间名会糊成一团：交给 CSS 隐掉（墙与填充仍在）。
    wrap.dataset.zoom = scale < 0.55 ? 'out' : 'in';
    viewport.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`;
    viewport.dataset.scale = scale.toFixed(2);
  }

  /** 屏幕坐标 → 图片像素坐标（逆变换）：测距取点用。 */
  function toImagePoint(clientX, clientY) {
    const rect = viewport.getBoundingClientRect();
    const naturalWidth = image.naturalWidth || current?.width || 1;
    const naturalHeight = image.naturalHeight || current?.height || 1;

    // `.board-map` 是 object-fit: contain：先算贴图在视口里的实际框。
    const boxRatio = rect.width / rect.height;
    const imageRatio = naturalWidth / naturalHeight;
    let drawWidth = rect.width;
    let drawHeight = rect.height;
    let offsetX = 0;
    let offsetY = 0;

    if (imageRatio > boxRatio) {
      drawHeight = rect.width / imageRatio;
      offsetY = (rect.height - drawHeight) / 2;
    } else {
      drawWidth = rect.height * imageRatio;
      offsetX = (rect.width - drawWidth) / 2;
    }

    const x = ((clientX - rect.left - offsetX) / drawWidth) * naturalWidth;
    const y = ((clientY - rect.top - offsetY) / drawHeight) * naturalHeight;
    return { x, y };
  }

  wrap.addEventListener('wheel', (event) => {
    if (!current) {
      return;
    }

    event.preventDefault();

    const rect = wrap.getBoundingClientRect();
    const anchorX = event.clientX - rect.left;
    const anchorY = event.clientY - rect.top;

    const next = clamp(scale * (event.deltaY < 0 ? 1.1 : 1 / 1.1), MIN_SCALE, MAX_SCALE);

    // 以鼠标位置为锚：缩放前后让锚点停在同一处。
    tx = anchorX - ((anchorX - tx) * next) / scale;
    ty = anchorY - ((anchorY - ty) * next) / scale;
    scale = next;

    applyViewport();
  }, { passive: false });

  let dragging = null;

  wrap.addEventListener('pointerdown', (event) => {
    if (!current || event.button !== 0) {
      return;
    }

    // 工具条上的按钮不参与拖视图：否则下面的 setPointerCapture 会把 click 的目标改成 wrap，
    // 按钮的 click 永远不触发（测距 / 视角 / 标定三个按钮全都点不动）。
    if (event.target.closest('.board-toolbar')) {
      return;
    }

    const tokenElement = event.target.closest('.board-token');

    if (tokenElement) {
      // 编辑器里拖 token（M1）：没有回调时保持原行为（点一下只是高亮）。
      const actorId = tokenElement.dataset.actorId;

      if (tokenDragHandler && actorId && !picking && !measuring) {
        tokenDragging = { actorId };
        tokenDragHandler(actorId, 'start', toImagePoint(event.clientX, event.clientY), modifiersOf(event));

        try {
          wrap.setPointerCapture(event.pointerId);
        } catch {
          // 合成事件拿不到捕获：不捕获也能拖。
        }
      }

      return;
    }

    // 命中房间 / 墙且开着形状命中（选择态）：**不要再抢指针捕获**，
    // 否则 pointerup 的 target 变成 wrap，click 落不到 polygon 上，编辑器永远选不中
    // （r4 复验里"点房间毫无反应"的真实原因）。
    // 区域（M7）也要在这份"别抢指针"的名单里：只排 room/wall 的话，
    // 点在区域上会被 setPointerCapture 抢走指针，pointerup 的 target 变成 wrap，
    // click 永远落不到 polygon/circle 上——表现就是"区域选不中"。
    if (shapeHit && !picking && !measuring
      && event.target.closest('.board-room, .board-wall, .board-zone')) {
      return;
    }

    if (picking) {
      const point = toImagePoint(event.clientX, event.clientY);
      picking.points.push(point);
      paintPicks();

      if (picking.points.length >= picking.count) {
        const done = picking;
        picking = null;
        resumeShapeHit();
        paintPicks();
        done.resolve(done.points);
      }

      return;
    }

    if (measuring) {
      if (!current.calibration) {
        hint.hidden = false;
        hint.textContent = '未标定：测距不可用（禁止用默认 100 猜）。';
        return;
      }

      const point = toImagePoint(event.clientX, event.clientY);
      measurePoints = measurePoints.length >= 2 ? [point] : [...measurePoints, point];
      paintMeasure();
      return;
    }

    dragging = { x: event.clientX - tx, y: event.clientY - ty };
    wrap.dataset.dragging = 'true';

    try {
      wrap.setPointerCapture(event.pointerId);
    } catch {
      // 合成事件拿不到捕获：不捕获也能拖。
    }
  });

  wrap.addEventListener('pointermove', (event) => {
    if (tokenDragging) {
      tokenDragHandler?.(tokenDragging.actorId, 'move', toImagePoint(event.clientX, event.clientY), modifiersOf(event));
      return;
    }

    if (!dragging) {
      return;
    }

    tx = event.clientX - dragging.x;
    ty = event.clientY - dragging.y;
    applyViewport();
  });

  for (const type of ['pointerup', 'pointercancel']) {
    wrap.addEventListener(type, (event) => {
      if (tokenDragging) {
        const finished = tokenDragging;
        tokenDragging = null;
        tokenDragHandler?.(finished.actorId, 'end', toImagePoint(event.clientX, event.clientY), modifiersOf(event));
      }

      dragging = null;
      delete wrap.dataset.dragging;
    });
  }

  wrap.addEventListener('click', (event) => {
    const token = event.target.closest('.board-token');

    if (!token) {
      return;
    }

    const actorId = token.dataset.actorId;

    if (actorId) {
      // 点 token → 广播，交给 main.js 去高亮「与会者」行（§4.5）。
      window.dispatchEvent(new CustomEvent('meetingroom:token-click', { detail: { actorId } }));
    }
  });

  calibrateButton.addEventListener('click', () => {
    window.dispatchEvent(new CustomEvent('meetingroom:board-calibrate'));
  });

  // 双击 token：交给浮层显示 Sheet 摘要（只读，F4）。
  wrap.addEventListener('dblclick', (event) => {
    const token = event.target.closest('.board-token');

    if (!token?.dataset.actorId) {
      return;
    }

    window.dispatchEvent(
      new CustomEvent('meetingroom:token-dblclick', {
        detail: { actorId: token.dataset.actorId, combat: lastCombat },
      }),
    );
  });

  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') {
      return;
    }

    // F7-④：取点也必须能取消——否则只能凑满两点或刷新页面。
    if (picking) {
      const pending = picking;
      picking = null;
      resumeShapeHit();
      paintPicks();
      pending.resolve(null);
    }

    measurePoints = [];
    paintMeasure();
  });

  measureButton.addEventListener('click', () => {
    measuring = !measuring;
    measureButton.dataset.on = String(measuring);
    measurePoints = [];
    paintMeasure();

    if (measuring && !current?.calibration) {
      hint.hidden = false;
      hint.textContent = '未标定：测距不可用（禁止用默认 100 猜）。';
    } else {
      updateHint();
    }
  });

  viewButton.addEventListener('click', () => {
    // 前端只是模拟；真正的过滤在后端（§4.4）。
    setView(view === 'dm' ? 'player' : 'dm');
  });

  /** 列表 → 地图：把某个 actor 的 token 高亮（纯显示层，不改状态）。 */
  function setHighlight(actorId) {
    highlight = actorId;
    void render({});
  }

  /** 取点标记：与测距分开一层，免得互相清掉。 */
  function paintPicks() {
    const layer = overlay.querySelector('.board-picks');

    if (!layer) {
      return;
    }

    layer.replaceChildren();

    for (const point of picking?.points ?? []) {
      const dot = document.createElementNS(SVG_NS, 'circle');
      dot.setAttribute('cx', String(point.x));
      dot.setAttribute('cy', String(point.y));
      dot.setAttribute('r', '10');
      dot.setAttribute('vector-effect', 'non-scaling-stroke');
      layer.append(dot);
    }
  }

  /** 让用户在图点若干个点（标定取点用）：返回 Promise<{x,y}[]>。 */
  /** 让用户在图点若干个点：满 `count` 返回点数组，**取消（Esc）返回 null**。 */
  function pickPoints(count) {
    return new Promise((resolve) => {
      picking = { count, points: [], resolve };
      pauseShapeHit();
      paintPicks();
    });
  }

  /** 预览一组标定值（F4）：不落盘，只重画网格；传 null 恢复已存值。 */
  function setPreviewCalibration(calibration) {
    previewCalibration = calibration;
    return render({});
  }

  function getMapInfo() {
    return current
      ? {
          id: current.id,
          width: current.width,
          height: current.height,
          calibration: current.calibration,
        }
      : null;
  }

  function toolButton(text, title) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'board-tool';
    button.textContent = text;
    button.title = title;
    return button;
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  return {
    render,
    setHighlight,
    pickPoints,
    setPreviewCalibration,
    getMapInfo,
    setMapId,
    setTokenDragHandler,
    setDocument,
    setShapeHit,
    setView,
    toImagePoint,
    /** 诊断：当前文档层状态（排查"改了没重绘"用）。 */
    debug() {
      return {
        revision: documentRevision,
        gridKey: lastGridState,
        documentMapId: lastDocumentMapId,
        walls: lastDocument?.walls?.length ?? -1,
        rooms: lastDocument?.rooms?.length ?? -1,
      };
    },
    get scale() {
      return scale;
    },
    get view() {
      return view;
    },
  };
}
