// 第 1 列：会议桌（资料卡片：标题 + 摘要 + 链接 + 状态色）。

import { clone, empty, materialStatusLabel } from './dom.js';

export function mount(root) {
  let signature = null;

  return {
    render(state) {
      const next = JSON.stringify(state.materials.map((m) => [m.id, m.status, m.title, m.summary, m.author]));
      if (next === signature) {
        return;
      }

      signature = next;

      if (state.materials.length === 0) {
        root.replaceChildren(empty('还没有资料'));
        return;
      }

      root.replaceChildren(...state.materials.map((material) => card(material)));
    },
  };
}

function card(material) {
  const node = clone('tpl-material');
  node.dataset.status = material.status ?? '';
  node.querySelector('.card-title').textContent = material.title ?? material.id;

  const summary = node.querySelector('.card-summary');
  summary.textContent = material.summary ?? '';
  // 列表按容器宽度截断，完整摘要放进 title（设计文档 §5.3）。
  summary.title = material.summary ?? '';

  node.querySelector('.card-meta').textContent =
    `${material.author ?? ''} · ${materialStatusLabel(material.status)}`;

  // 正文只在侧栏展示，不进任何 Agent 上下文（§5.3）；点击由 viewer.js 统一接管。
  const link = node.querySelector('.card-link');
  link.dataset.materialId = material.id;
  link.href = `/api/materials/${encodeURIComponent(material.id)}/content`;
  return node;
}
