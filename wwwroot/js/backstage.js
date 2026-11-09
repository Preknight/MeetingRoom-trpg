// 后台观察页（《叙事模式后台与上下文设计》§3.2）。
//
// 三栏：左=前台剧情（turn 的 lines）/ 中=后台动态（backstage.jsonl 的 thought|tool）/ 右=当前状态。
// 数据全部走只读 HTTP：GET /api/roleplay/state（前台 + 世界状态）与
// GET /api/roleplay/backstage（后台，按幕升序）。**不写任何东西**——本批没有提醒输入框（那是 B5）。
//
// 为什么轮询而不是 SSE：本页是观察面，允许落后一两秒；SSE 那条通道留给主界面（只开一条）。

const pollMs = 1500;

const el = {
  status: document.getElementById('bs-status'),
  follow: document.getElementById('bs-follow'),
  who: document.getElementById('bs-who'),
  kind: document.getElementById('bs-kind'),
  jump: document.getElementById('bs-jump'),
  jumpGo: document.getElementById('bs-jump-go'),
  clear: document.getElementById('bs-clear'),
  rev: document.getElementById('bs-rev'),
  story: document.getElementById('bs-story'),
  events: document.getElementById('bs-events'),
  state: document.getElementById('bs-state'),
};

let names = new Map(); // participantId → 名字（把 backstage 的 who 换成能读的）
let pinnedRevision = null; // 非 null = 用户在"跳到幕"，不自动跟到最新
let lastSignature = '';

function text(value) {
  return typeof value === 'string' ? value : value == null ? '' : String(value);
}

function nameOf(id) {
  return names.get(id) ?? id;
}

function trim(value, max) {
  const flat = text(value).replace(/\s+/g, ' ').trim();
  return flat.length <= max ? flat : `${flat.slice(0, max)}…`;
}

async function fetchJson(url) {
  const response = await fetch(url, { cache: 'no-store' });

  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }

  return response.json();
}

export function renderStory(state) {
  const turns = Array.isArray(state?.turns) ? state.turns : [];
  const rows = [];

  for (const turn of turns) {
    const lines = Array.isArray(turn.lines) ? turn.lines : [];

    if (lines.length === 0 && !turn.directorInput) {
      continue;
    }

    rows.push(`<div class="bs-turn" data-rev="${turn.revision}">
      <div class="bs-turn-head">第 ${turn.revision} 幕${turn.directorInput ? ` · 导演：${escapeHtml(trim(turn.directorInput, 80))}` : ''}</div>
      ${lines.map((line) => `<div class="bs-line"><span class="bs-who">${escapeHtml(nameOf(line.author))}</span>${escapeHtml(text(line.text))}</div>`).join('')}
    </div>`);
  }

  el.story.innerHTML = rows.length > 0 ? rows.join('') : '<p class="bs-empty">还没有剧情。</p>';
}

export function renderEvents(payload) {
  const events = Array.isArray(payload?.events) ? payload.events : [];
  const who = el.who.value;
  const kind = el.kind.value;

  const visible = events.filter((event) =>
    (!who || event.who === who)
    && (!kind || event.kind === kind)
    && (pinnedRevision == null || Number(event.revision) <= pinnedRevision));

  if (visible.length === 0) {
    el.events.innerHTML = '<p class="bs-empty">没有符合条件的后台记录。</p>';
    return;
  }

  el.events.innerHTML = visible
    .map((event) => {
      const head = `<span class="bs-rev-badge">第 ${event.revision} 幕</span><span class="bs-who">${escapeHtml(nameOf(event.who))}</span>`;

      if (event.kind === 'tool') {
        const args = event.args ? `<code class="bs-args">${escapeHtml(trim(formatArgs(event.args), 240))}</code>` : '';
        const result = event.result ? `<div class="bs-result">→ ${escapeHtml(trim(event.result, 240))}</div>` : '';
        return `<div class="bs-event bs-event--tool">${head}<span class="bs-tool">${escapeHtml(text(event.name))}</span>${args}${result}</div>`;
      }

      return `<div class="bs-event bs-event--thought">${head}<div class="bs-thought">${escapeHtml(trim(event.text, 400))}</div></div>`;
    })
    .join('');
}

export function renderState(state, payload) {
  const world = state?.worldState ?? {};
  const cast = Array.isArray(state?.cast) ? state.cast : [];
  const parts = [];

  parts.push(`<div class="bs-kv"><span>故事</span><b>${escapeHtml(text(state?.storyId))}</b></div>`);
  parts.push(`<div class="bs-kv"><span>进度</span><b>第 ${text(state?.revision ?? payload?.revision ?? 0)} 幕</b></div>`);
  parts.push(`<div class="bs-kv"><span>后台记录</span><b>${Array.isArray(payload?.events) ? payload.events.length : 0} 条</b></div>`);

  // 上下文指标（E）：验证时最想看的就是这三项——L2 多长、大概多少 token、压过没有。
  const context = state?.context;

  if (context) {
    const compress = Number(context.compressionCount ?? 0);
    parts.push(`<div class="bs-kv"><span>剧情消息</span><b>${Number(context.historyCount ?? 0)} 条</b></div>`);
    parts.push(`<div class="bs-kv"><span>估算 token</span><b>${Number(context.estimatedTokens ?? 0)}</b></div>`);
    parts.push(`<div class="bs-kv"><span>压缩次数</span><b>${compress}${compress > 0 ? ` · 摘要 ${Number(context.summaryChars ?? 0)} 字` : ''}</b></div>`);
  }

  if (world && typeof world === 'object') {
    for (const key of ['time', 'place']) {
      if (world[key]) {
        parts.push(`<div class="bs-kv"><span>${key === 'time' ? '时间' : '地点'}</span><b>${escapeHtml(text(world[key]))}</b></div>`);
      }
    }
  }

  if (cast.length > 0) {
    parts.push('<div class="bs-cast">');
    for (const member of cast) {
      const mark = member.present ? '在场' : '离场';
      parts.push(`<div class="bs-kv"><span>${escapeHtml(text(member.name))}</span><b>${mark} · ${escapeHtml(text(member.status))}</b></div>`);
    }
    parts.push('</div>');
  }

  el.state.innerHTML = parts.join('');
  el.rev.textContent = `（第 ${text(state?.revision ?? 0)} 幕）`;
}

/**
 * 工具参数的可读化（C，2026-10-29）：`args` 是 JSON **文本**，直接 textContent 会原样显示
 * `\u5B88\u591C\u4EBA` 这种转义。解析后重新序列化，中文就出来了；
 * 解析失败（截断 / 非 JSON）就原样返回——不能把参数吞掉。
 */
function formatArgs(raw) {
  const text = typeof raw === 'string' ? raw : String(raw ?? '');

  if (text.trim().length === 0) {
    return '';
  }

  try {
    return JSON.stringify(JSON.parse(text), null, 1);
  } catch {
    return text;
  }
}

function escapeHtml(value) {
  return text(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function scrollToBottom() {
  if (!el.follow.checked) {
    return;
  }

  for (const node of [el.story, el.events]) {
    node.scrollTop = node.scrollHeight;
  }
}

function refreshWhoOptions(payload, state) {
  const ids = new Set();

  for (const event of Array.isArray(payload?.events) ? payload.events : []) {
    ids.add(event.who);
  }

  for (const member of Array.isArray(state?.cast) ? state.cast : []) {
    names.set(member.id, member.name);
  }

  const current = el.who.value;
  const options = ['<option value="">全部</option>'];

  for (const id of [...ids].filter(Boolean).sort()) {
    options.push(`<option value="${escapeHtml(id)}">${escapeHtml(nameOf(id))}</option>`);
  }

  const html = options.join('');

  if (el.who.dataset.html !== html) {
    el.who.dataset.html = html;
    el.who.innerHTML = html;
    el.who.value = current;
  }
}

async function tick() {
  try {
    const [state, payload] = await Promise.all([
      fetchJson('/api/roleplay/state'),
      fetchJson('/api/roleplay/backstage?since=0&limit=500'),
    ]);

    const signature = JSON.stringify([
      payload?.events?.length,
      state?.turns?.length,
      state?.revision,
      el.who.value,
      el.kind.value,
      pinnedRevision,
    ]);

    if (signature !== lastSignature) {
      lastSignature = signature;
      refreshWhoOptions(payload, state);
      renderStory(state);
      renderEvents(payload);
      renderState(state, payload);
      scrollToBottom();
    }

    el.status.textContent = `已连接 · 第 ${text(state?.revision ?? 0)} 幕 · 后台 ${Array.isArray(payload?.events) ? payload.events.length : 0} 条`;
    el.status.dataset.state = 'ok';
  } catch (error) {
    el.status.textContent = `连接失败：${error.message}（还没有叙事会话时也会这样）`;
    el.status.dataset.state = 'error';
  }
}

el.who.addEventListener('change', () => {
  lastSignature = '';
  tick();
});

el.kind.addEventListener('change', () => {
  lastSignature = '';
  tick();
});

el.follow.addEventListener('change', () => {
  if (el.follow.checked) {
    pinnedRevision = null;
    lastSignature = '';
    tick();
    scrollToBottom();
  }
});

el.jumpGo.addEventListener('click', () => {
  const value = Number(el.jump.value);

  if (!Number.isFinite(value) || value <= 0) {
    return;
  }

  pinnedRevision = value;
  el.follow.checked = false;
  lastSignature = '';
  tick();
});

el.clear.addEventListener('click', () => {
  pinnedRevision = null;
  el.follow.checked = true;
  lastSignature = '';
  tick();
  scrollToBottom();
});

// ——— 投一条提醒（§3.3，B5）———
//
// 只发 POST /api/roleplay/dm-note，不改任何本地状态：卡片由第五列（主界面）显示与回应。
// 三种回应（知道了 / 记进设定 / 忽略）都在主界面那张卡片上，本页不重复实现。

const noteForm = document.getElementById('bs-note');
const noteText = document.getElementById('bs-note-text');
const noteSeverity = document.getElementById('bs-note-severity');
const noteSend = document.getElementById('bs-note-send');
const noteStatus = document.getElementById('bs-note-status');

noteForm?.addEventListener('submit', async (event) => {
  event.preventDefault();

  const text = noteText.value.trim();

  if (text.length === 0) {
    noteStatus.textContent = '先写一句要提醒的事。';
    noteStatus.dataset.state = 'error';
    return;
  }

  noteSend.disabled = true;
  noteStatus.dataset.state = '';
  noteStatus.textContent = '发送中…';

  try {
    const response = await fetch('/api/roleplay/dm-note', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ severity: noteSeverity.value, text }),
    });

    const payload = await response.json();

    if (!response.ok) {
      noteStatus.textContent = payload?.error ?? `发送失败：${response.status}`;
      noteStatus.dataset.state = 'error';
      return;
    }

    noteStatus.textContent = '已投到第五列「待确认事件」。';
    noteStatus.dataset.state = 'ok';
    noteText.value = '';
    lastSignature = '';
    tick();
  } catch (error) {
    noteStatus.textContent = `发送失败：${error.message}`;
    noteStatus.dataset.state = 'error';
  } finally {
    noteSend.disabled = false;
  }
});

// 导出渲染函数：`--probe`/验收时可以在控制台直接喂数据，确认三栏与筛选都对
//（页面自身的行为完全不变——它只调用 tick）。
// ——— 三栏宽度可拖（批 2） ——————————————————————————————————————————————
// 宽度存 localStorage（刷新保留）；第三栏吃剩余宽度，所以只存前两栏的百分比。
const SPLIT_KEY = 'meetingroom.backstage.split';
const SPLIT_DEFAULT = { w1: 24, w2: 46 };
const SPLIT_MIN = { w1: 12, w2: 18 };
const SPLIT_MAX = { w1: 48, w2: 62 };
// 两栏加起来要留一截给右栏，否则拖到底右栏会挤成一条缝。
const SPLIT_RESERVE = 20;

function clampSplit(split) {
  const w1 = Math.min(SPLIT_MAX.w1, Math.max(SPLIT_MIN.w1, split.w1));
  const w2 = Math.min(SPLIT_MAX.w2, Math.max(SPLIT_MIN.w2, split.w2));

  // 超了就压后一栏（拖第一根时用户的意图是"这一栏多宽"，第二栏让一步更符合预期）。
  return { w1, w2: Math.min(w2, 100 - w1 - SPLIT_RESERVE) };
}

function readSplit() {
  try {
    const raw = localStorage.getItem(SPLIT_KEY);

    if (!raw) {
      return { ...SPLIT_DEFAULT };
    }

    const parsed = JSON.parse(raw);
    const w1 = Number(parsed?.w1);
    const w2 = Number(parsed?.w2);

    if (!Number.isFinite(w1) || !Number.isFinite(w2)) {
      return { ...SPLIT_DEFAULT };
    }

    return clampSplit({ w1, w2 });
  } catch {
    return { ...SPLIT_DEFAULT };
  }
}

function applySplit(split) {
  const grid = document.querySelector('.backstage-grid');

  if (!grid) {
    return;
  }

  grid.style.setProperty('--bs-w1', `${split.w1}%`);
  grid.style.setProperty('--bs-w2', `${split.w2}%`);
}

function saveSplit(split) {
  try {
    localStorage.setItem(SPLIT_KEY, JSON.stringify(split));
  } catch {
    // 存不进去（隐私模式 / 配额满）不影响本次拖动，只是刷新后回到默认。
  }
}

function mountSplitters() {
  const grid = document.querySelector('.backstage-grid');

  if (!grid) {
    return;
  }

  applySplit(readSplit());

  for (const handle of document.querySelectorAll('.bs-splitter')) {
    const which = handle.dataset.split === '2' ? 2 : 1;

    handle.addEventListener('pointerdown', (event) => {
      const rect = grid.getBoundingClientRect();
      const startX = event.clientX;
      const base = readSplit();

      // 捕获指针：拖到栏外也不丢事件。合成事件（自动化测试）没有真实指针，会抛，忽略即可。
      try {
        handle.setPointerCapture(event.pointerId);
      } catch {
        // 不影响拖动本身。
      }

      handle.dataset.dragging = 'true';

      const move = (moveEvent) => {
        const deltaPct = ((moveEvent.clientX - startX) / rect.width) * 100;

        const next = which === 1
          ? { w1: base.w1 + deltaPct, w2: base.w2 }
          : { w1: base.w1, w2: base.w2 + deltaPct };

        applySplit(clampSplit(next));
      };

      const up = () => {
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', up);
        handle.removeEventListener('pointercancel', up);
        delete handle.dataset.dragging;

        // 以**实际生效**的百分比落盘（钳制后的值），刷新回来才不会漂。
        const w1 = parseFloat(grid.style.getPropertyValue('--bs-w1')) || SPLIT_DEFAULT.w1;
        const w2 = parseFloat(grid.style.getPropertyValue('--bs-w2')) || SPLIT_DEFAULT.w2;

        saveSplit(clampSplit({ w1, w2 }));
      };

      handle.addEventListener('pointermove', move);
      handle.addEventListener('pointerup', up);
      handle.addEventListener('pointercancel', up);
    });
  }
}

mountSplitters();

export { tick };

tick();
setInterval(tick, pollMs);
