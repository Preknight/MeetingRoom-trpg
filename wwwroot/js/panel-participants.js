// 第 4 列：与会者（会议）/ 在场角色（叙事）。
// 与第 5 列在同一个 grid 里逐行成对渲染——行高由同一行决定，滚动由同一个容器同步（实现步骤 3.3）。
// 两种模式共用这一列，只换数据源（设计文档 §11.6）；行对齐的机制完全不变。

import { backstageCell, backstageSignature } from './panel-backstage.js';
import { clone, empty, roleLabel, statusLabel } from './dom.js';

export function mount(root) {
  let signature = null;

  return {
    render(state) {
      const narrative = state.mode === 'roleplay';

      // 叙事模式只列在场角色：不在场的角色不该出现在"在场角色"列表里（也不该发言）。
      const roster = narrative ? state.roleplay.cast.filter((actor) => actor.present) : state.participants;
      const next = JSON.stringify([state.mode, roster, backstageSignature(state), state.diagnostics]);

      if (next === signature) {
        return;
      }

      signature = next;

      if (roster.length === 0) {
        root.replaceChildren(empty(narrative ? '还没有在场角色' : '还没有与会者'));
        return;
      }

      root.replaceChildren(
        ...roster.flatMap((actor) => [participantCell(actor, state.diagnostics), backstageCell(state, actor.id)]),
      );

      // 节点插入后再把每格滚到底（插入前 scrollHeight 还是 0）。
      for (const cell of root.querySelectorAll('.rail-cell--backstage')) {
        cell.scrollTop = cell.scrollHeight;
      }
    },
  };
}

function participantCell(participant, diagnostics) {
  const node = clone('tpl-rail-participant');
  node.dataset.status = participant.status ?? '';
  // F3-③：列表行 ↔ 地图 token 双向高亮靠这个 id 对上。
  node.dataset.participantId = participant.id;
  node.addEventListener('click', () => {
    window.dispatchEvent(new CustomEvent('meetingroom:participant-click', { detail: { id: participant.id } }));
  });
  node.querySelector('.rail-name-text').textContent = participant.name ?? participant.id;
  node.querySelector('.rail-desc').textContent = `${roleLabel(participant.role)} · ${statusLabel(participant.status)}`;

  // 缓存诊断（设计文档 §7.4）：让"省 token"从愿望变成指标。叙事模式同样适用——
  // 两种模式共用同一套会话与同一份诊断。
  const report = diagnostics?.[participant.id];
  const cache = node.querySelector('.rail-cache');

  if (report) {
    const reason = report.lastMissReason ? ` · miss: ${report.lastMissReason}` : '';
    cache.textContent = `缓存 ${tokens(report.cacheHitTokens)}/${tokens(report.cacheMissTokens)}${reason}`;
    cache.title = `前缀 ${report.prefix ?? ''}`;
  }

  return node;
}

function tokens(value) {
  const count = Number(value ?? 0);
  return count >= 1000 ? `${(count / 1000).toFixed(1)}k` : String(count);
}
