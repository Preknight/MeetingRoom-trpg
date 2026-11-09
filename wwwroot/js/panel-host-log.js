// 第 3 列下：主持人记录（会议）/ 导演记录（叙事）。
// 都是"仅显示、Agent 不可读"的编排留痕（设计文档 §9.1、§11.6），所以共用一列、只换数据源。

import { clone, empty } from './dom.js';

export function mount(root) {
  let signature = null;

  return {
    render(state) {
      const narrative = state.mode === 'roleplay';
      const entries = narrative ? state.roleplayLog : state.hostLog;
      const next = JSON.stringify([state.mode, entries]);

      if (next === signature) {
        return;
      }

      signature = next;
      root.replaceChildren(
        ...(entries.length > 0
          ? entries.map((entry) => decision(entry))
          : [empty(narrative ? '暂无记录' : '主持人暂无动作')]),
      );
      root.scrollTop = root.scrollHeight;
    },
  };
}

function decision(entry) {
  const node = clone('tpl-decision');
  node.querySelector('.decision-action').textContent =
    entry.target ? `${entry.action ?? ''} · ${entry.target}` : (entry.action ?? '');
  node.querySelector('.decision-reason').textContent = entry.reason ?? '';
  return node;
}
