// 交互浮层（设计文档 §14.3）：待用户拍板的事贴在指令区上方，不遮挡五列。
//
// 多问题分步展示：一次只问一个，收集完再一次性提交——后端是按"条目"一次收口的。
// 回答只有两种形状：点某个选项，或"直接输入"一句自己的话；两者都写进 selected 里（§14.2）。

import * as api from './api.js';

export function mount() {
  const root = document.getElementById('ask-panel');
  const kind = document.getElementById('ask-kind');
  const step = document.getElementById('ask-step');
  const prompt = document.getElementById('ask-prompt');
  const options = document.getElementById('ask-options');
  const prev = document.getElementById('ask-prev');
  const next = document.getElementById('ask-next');
  const submit = document.getElementById('ask-submit');
  const skip = document.getElementById('ask-skip');
  const free = document.getElementById('ask-free');
  const freeInput = document.getElementById('ask-free-input');

  let itemId = null;
  let index = 0;
  let picked = new Map(); // questionId -> selected[]
  let current = null;

  const questions = () => current?.questions ?? [];
  const question = () => questions()[index] ?? null;
  const total = () => questions().length;
  const isLast = () => index >= total() - 1;

  prev.addEventListener('click', () => {
    index = Math.max(0, index - 1);
    paint();
  });

  next.addEventListener('click', () => {
    index = Math.min(total() - 1, index + 1);
    paint();
  });

  skip.addEventListener('click', async () => {
    skip.disabled = true;
    // 跳过 = 按默认选项收口：不让"没想好"变成卡住。
    await api.post(`/api/interactions/${encodeURIComponent(itemId)}/skip`, {});
  });

  submit.addEventListener('click', () => send());

  free.addEventListener('submit', async (event) => {
    event.preventDefault();

    const text = freeInput.value.trim();
    const asked = question();

    if (!text || !asked) {
      return;
    }

    // 自由输入也是 selected 里的一条：与点选项走同一条路径（§14.2）。
    picked.set(asked.id, [text]);
    freeInput.value = '';
    paint();

    if (isLast()) {
      await send();
    } else {
      index += 1;
      paint();
    }
  });

  async function send() {
    const answers = questions().map((asked) => ({
      questionId: asked.id,
      selected: picked.get(asked.id) ?? [],
    }));

    submit.disabled = true;
    // 收口之后条目会从 state.pending 里消失，下一次 render 自己把浮层收起来。
    await api.post(`/api/interactions/${encodeURIComponent(itemId)}`, { answers });
  }

  function paint() {
    const asked = question();

    if (!current || !asked) {
      root.hidden = true;
      return;
    }

    root.hidden = false;
    kind.textContent = decisionKindLabel(current.kind);
    step.textContent = total() > 1 ? `第 ${index + 1} / ${total()} 个问题` : '';
    prompt.textContent = asked.prompt ?? '';

    const chosen = picked.get(asked.id) ?? [];
    options.replaceChildren(...(asked.options ?? []).map((option) => optionButton(asked, option, chosen)));

    prev.hidden = index === 0;
    next.hidden = isLast();
    submit.hidden = !isLast();
    submit.disabled = false;
    submit.textContent = total() > 1 ? '提交答复' : '就这么定';
    skip.disabled = false;
  }

  function optionButton(asked, option, chosen) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'ask-option';
    button.dataset.selected = chosen.includes(option.id) ? 'true' : 'false';

    const label = document.createElement('span');
    label.className = 'ask-option-label';
    label.textContent = option.label ?? option.id;

    const description = document.createElement('span');
    description.className = 'ask-option-desc';
    description.textContent = option.description ?? '';

    button.append(label, description);
    button.addEventListener('click', () => {
      picked.set(asked.id, [option.id]);
      paint();
    });

    return button;
  }

  return {
    render(state) {
      const item = (state.pending ?? [])[0] ?? null;

      if (!item) {
        // 没有了就收起来，并把这一件的临时状态清干净。
        current = null;
        itemId = null;
        picked = new Map();
        index = 0;
        root.hidden = true;
        return;
      }

      if (item.id !== itemId) {
        itemId = item.id;
        picked = new Map();
        index = 0;
      }

      current = item;

      if (index >= total()) {
        index = 0;
      }

      paint();
    },
  };
}

/**
 * 待决事项的标题（P1）：迷雾提议独立成一种，不再与世界状态提议混在一起。
 * 两者的默认选项语义相反——世界状态超时 = 按提议生效，迷雾超时 = 不揭示，
 * 所以界面上必须分得清。
 */
export function decisionKindLabel(kind) {
  if (kind === 'world_state') {
    return '说书人的提议';
  }

  if (kind === 'fog') {
    return '说书人提议揭示迷雾';
  }

  if (kind === 'dm_note') {
    // B5：导演投的提醒——它不是"要拍板"的事，只是旁注，三种回应都不改剧情。
    return '导演提醒';
  }

  return '需要你拍板';
}
