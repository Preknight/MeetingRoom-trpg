// 第 5 列：后台记录（思考 / 工具调用 / 打断 / 正在生成的正文；仅显示，Agent 不可读）。
// 正在流式生成的正文也在这里——讨论区只保留最终发言（设计文档 §9.1）。
// 注意：这一列不是独立面板——它由 panel-participants.js 逐行成对渲染，
// 两列才可能行高一致、滚动同步（实现步骤 3.3 的验收项）。

import { clone } from './dom.js';

/** 每个与会者最多显示的后台记录条数（旧记录仍在落盘的 transcript 里，阶段 3 提供）。 */
const VISIBLE_LIMIT = 20;

export function backstageSignature(state) {
  return JSON.stringify([state.backstage, state.streaming]);
}

export function backstageCell(state, participantId) {
  const node = clone('tpl-rail-backstage');
  const entries = state.backstage[participantId] ?? [];
  const nodes = entries.slice(-VISIBLE_LIMIT).map((entry) => entryNode(entry));

  // 正在流式生成的正文：跟着后台记录走，提交（message.committed）后自动消失。
  const streaming = state.streaming[participantId];
  if (streaming) {
    nodes.push(streamingNode(streaming));
  }

  node.replaceChildren(...nodes);
  return node;
}

function streamingNode(text) {
  const node = document.createElement('span');
  node.className = 'backstage-entry backstage-entry--streaming';
  node.textContent = text;
  return node;
}

function entryNode(entry) {
  const node = document.createElement('span');
  node.className = `backstage-entry backstage-entry--${entry.kind ?? 'thought'}`;
  node.textContent = entry.text ?? '';
  return node;
}
