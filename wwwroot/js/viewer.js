// 资料正文查看器：点卡片上的"正文"才去拉取（渐进式披露的最后一步，设计文档 §5.3）。
// 正文只在这里展示，绝不进入任何 Agent 的上下文。

const view = { root: null, title: null, path: null, body: null };

export function initViewer() {
  view.root = document.getElementById('material-viewer');
  view.title = view.root.querySelector('.viewer-title');
  view.path = view.root.querySelector('.viewer-path');
  view.body = view.root.querySelector('.viewer-body');

  view.root.querySelector('.viewer-close').addEventListener('click', close);
  view.root.addEventListener('click', (event) => {
    if (event.target === view.root) {
      close();
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      close();
    }
  });

  // 事件委托：所有面板里的"正文"链接都走这里，面板不必各自处理。
  document.addEventListener('click', async (event) => {
    const link = event.target.closest('.card-link');
    if (!link) {
      return;
    }

    event.preventDefault();
    await open(link.dataset.materialId);
  });
}

async function open(id) {
  if (!id) {
    return;
  }

  view.title.textContent = id;
  view.path.textContent = '';
  view.body.textContent = '读取中…';
  view.root.hidden = false;

  const response = await fetch(`/api/materials/${encodeURIComponent(id)}/content`);
  if (!response.ok) {
    view.body.textContent = `读取失败：HTTP ${response.status}`;
    return;
  }

  const payload = await response.json();
  view.title.textContent = payload.title ?? id;
  view.path.textContent = payload.path ?? '';
  view.body.textContent = payload.content ?? '';
}

function close() {
  view.root.hidden = true;
}
