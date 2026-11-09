// 第 3 列上：待审批（会议）/ 待确认事件（叙事）。
// 会议：主持人审批中 + 待用户终审的资料卡片。
// 叙事：需要用户拍板的事——说书人的世界状态提议，以及（阶段 9 起）它的提问。
// 两者是同一种机制（待决条目），所以都走 /api/interactions/{id}（设计文档 §14.3、§11.4 第 6b 项）。

import * as api from './api.js';
import { clone, empty, materialStatusLabel } from './dom.js';
import { decisionKindLabel } from './panel-ask.js';

const PENDING = new Set(['draft', 'host_review', 'user_review']);

export function mount(root) {
  let signature = null;

  return {
    render(state) {
      const narrative = state.mode === 'roleplay';
      const items = narrative ? state.pending : state.materials.filter((material) => PENDING.has(material.status));
      const next = JSON.stringify([
        state.mode,
        items.map((item) => (narrative
          ? [
              item.id,
              item.kind,
              (item.questions ?? []).map((question) => [
                question.id,
                question.prompt,
                (question.options ?? []).map((option) => option.id),
              ]),
            ]
          : [item.id, item.status, item.title, item.summary])),
      ]);

      if (next === signature) {
        return;
      }

      signature = next;

      if (items.length === 0) {
        root.replaceChildren(empty(narrative ? '暂无待确认事件' : '没有待审批资料'));
        return;
      }

      root.replaceChildren(...items.map((item) => (narrative ? pendingCard(item) : card(item))));
    },
  };
}

/**
 * 待确认事件卡片：说书人提议了什么 + 每个问题的选项按钮。
 * 点下去才答复（POST /api/interactions/{id}）——世界状态等在这一次答复之后才落盘。
 */
function pendingCard(item) {
  const node = clone('tpl-decision');
  const questions = item.questions ?? [];

  // 来源区分（批 2）：`dm_note` 是**人**写的（导演提醒），其余是模型提的（说书人提议）。
  // 不能只靠颜色——色弱用户看不出，所以同时给文字徽标 + 不同的边线线型（见 app.css）。
  const origin = item.kind === 'dm_note' ? 'human' : 'ai';

  node.dataset.origin = origin;
  node.querySelector('.decision-origin').textContent = origin === 'human' ? '人' : 'AI';
  node.querySelector('.decision-origin').title =
    origin === 'human' ? '人写的：导演提醒（只进观察面）' : '模型提的：说书人';
  node.querySelector('.decision-action').textContent = decisionKindLabel(item.kind);
  node.querySelector('.decision-reason').textContent =
    questions.map((question) => question.prompt).join('\n\n') || '（没有说明）';

  const actions = node.querySelector('.decision-actions');
  const buttons = [];

  for (const question of questions) {
    for (const option of question.options ?? []) {
      buttons.push(optionButton(item, question, option));
    }
  }

  // 跳过 = 按默认选项收口（世界状态的默认选项就是"采用"）：不想逐条看的人也有出路。
  buttons.push(skipButton(item));

  actions.append(...buttons);
  return node;
}

function optionButton(item, question, option) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = option.label ?? option.id;

  if (option.description) {
    button.title = option.description;
  }

  button.addEventListener('click', async () => {
    button.disabled = true;
    await api.post(`/api/interactions/${encodeURIComponent(item.id)}`, {
      answers: [{ questionId: question.id, selected: [option.id] }],
    });
  });

  return button;
}

function skipButton(item) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'decision-skip';
  button.textContent = '跳过';
  button.title = '按默认选项收口（世界状态提议的默认是"采用"）';

  button.addEventListener('click', async () => {
    button.disabled = true;
    await api.post(`/api/interactions/${encodeURIComponent(item.id)}/skip`, {});
  });

  return button;
}

function card(material) {
  const node = clone('tpl-material');
  node.dataset.status = material.status ?? '';
  node.querySelector('.card-title').textContent = material.title ?? material.id;

  const summary = node.querySelector('.card-summary');
  summary.textContent = material.summary ?? '';
  summary.title = material.summary ?? '';

  node.querySelector('.card-meta').textContent =
    `${materialStatusLabel(material.status)} · 基于版本 ${material.basedOnVersion ?? 0}`;

  const link = node.querySelector('.card-link');
  link.dataset.materialId = material.id;
  link.href = `/api/materials/${encodeURIComponent(material.id)}/content`;

  // 用户终审的入口：只出现在"待你终审"的卡片上（§8.2）。
  if (material.status === 'user_review') {
    node.querySelector('.card-actions').append(...decisionButtons(material.id));
  }

  return node;
}

/** 采纳 / 打回 / 搁置：状态变化通过 SSE 回来，面板会自动重绘。 */
function decisionButtons(id) {
  return [
    ['采纳', 'adopt'],
    ['打回', 'reject'],
    ['搁置', 'park'],
  ].map(([label, decision]) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.addEventListener('click', async () => {
      button.disabled = true;
      await api.post(`/api/materials/${encodeURIComponent(id)}/review`, { decision });
    });
    return button;
  });
}
