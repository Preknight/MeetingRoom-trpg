// 选目录对话框（设计方案 §15.4）：目录浏览为主，"手输路径"是逃生口（网络盘 / 超长路径 /
// 从资源管理器复制粘贴）。「新建子目录并选中」直接把那个目录建成工作空间——后端会建骨架并写
// workspace.json（已存在的直接打开、不覆盖）。
// 目录浏览端点只返回目录名与文件摘要，**不返回任何文件内容**。

import * as api from './api.js';
import { clone, empty } from './dom.js';

export function mount(options = {}) {
  const root = document.getElementById('path-picker');
  const close = document.getElementById('picker-close');
  const goForm = document.getElementById('picker-go');
  const pathInput = document.getElementById('picker-path');
  const up = document.getElementById('picker-up');
  const list = document.getElementById('picker-list');
  const note = document.getElementById('picker-note');
  const newName = document.getElementById('picker-new-name');
  const create = document.getElementById('picker-create');
  const select = document.getElementById('picker-select');

  let kind = 'roleplay';
  // create：按类型新建（目录不存在就建骨架）；open：打开已有的（按目录里的 workspace.json 认类型）。
  let mode = 'create';
  let current = null;
  let parent = null;

  const show = (visible) => {
    root.hidden = !visible;
  };

  function renderList(data) {
    const nodes = (data.directories ?? []).map((directory) => {
      const node = clone('tpl-dir');

      node.querySelector('.picker-dir-name').textContent = directory.name;
      node.querySelector('.picker-dir-tag').textContent = directory.isWorkspace ? '已是工作空间' : '';
      node.dataset.workspace = String(Boolean(directory.isWorkspace));
      node.addEventListener('click', () => browse(directory.path));

      return node;
    });

    list.replaceChildren(...(nodes.length ? nodes : [empty('这个目录下没有子目录')]));
  }

  async function browse(path) {
    note.textContent = '读取中…';

    const query = path ? `?path=${encodeURIComponent(path)}` : '';
    const { ok, data } = await api.getJson(`/api/workspaces/browse${query}`);

    if (!ok) {
      note.textContent = data?.error ?? data?.raw ?? '读不了这个目录';
      return;
    }

    current = data.path;
    parent = data.parent;
    pathInput.value = data.path;

    note.textContent = data.isWorkspace
      ? '这个目录已经是工作空间：选中即打开它（里面的东西一个都不覆盖）。'
      : '这个目录还不是工作空间：选中会建出骨架（roles / materials / worldbook / drafts / meetings / stories）。';

    renderList(data);
  }

  async function use(path) {
    note.textContent = '正在打开…';

    const { ok, data } = mode === 'open'
      ? await api.postJson('/api/workspaces/open', { path })
      : await api.postJson('/api/workspaces', { path, kind });

    if (!ok) {
      note.textContent = data?.error ?? data?.raw ?? '打开工作空间失败';
      return;
    }

    if (mode === 'open') {
      await openLatestSession();
    }

    note.textContent = '';
    show(false);
    options.onWorkspace?.(data);
  }

  /** 「打开…」的收尾：那个工作空间已经有会话，就直接切到最近一场——省得用户再点一次。 */
  async function openLatestSession() {
    const { ok, data } = await api.getJson('/api/sessions');

    if (!ok || !(data?.sessions ?? []).length) {
      return;
    }

    const latest = data.sessions[0];
    await api.postJson('/api/sessions/open', { kind: latest.kind, id: latest.id });
  }

  /** 在当前目录下拼一个子目录：分隔符跟着当前路径走（Windows 上是 `\`）。 */
  function childPath(name) {
    const separator = current.includes('\\') ? '\\' : '/';
    return `${current.replace(/[\\/]+$/, '')}${separator}${name}`;
  }

  goForm.addEventListener('submit', (event) => {
    event.preventDefault();

    const value = pathInput.value.trim();
    if (value) {
      browse(value);
    }
  });

  up.addEventListener('click', () => {
    if (parent) {
      browse(parent);
    }
  });

  select.addEventListener('click', () => {
    if (current) {
      use(current);
    }
  });

  create.addEventListener('click', () => {
    const name = newName.value.trim();

    if (!current) {
      note.textContent = '先浏览到一个目录。';
      return;
    }

    if (!name) {
      note.textContent = '填一个子目录名。';
      return;
    }

    use(childPath(name));
  });

  // 系统原生对话框：网页里的 file input 拿不到绝对路径，所以由服务进程弹一次（§15.4 的逃生口再往前一步）。
  document.getElementById('picker-system').addEventListener('click', async () => {
    note.textContent = '等系统对话框…';

    const { ok, data } = await api.postJson('/api/system/pick-folder', { title: '选择一个目录作为工作空间', initialPath: current ?? '' });

    if (!ok) {
      note.textContent = data?.error ?? data?.raw ?? '调系统对话框失败';
      return;
    }

    if (data.cancelled || !data.path) {
      note.textContent = '（系统对话框里取消了）';
      return;
    }

    // 它就是"我选这个目录"的意思：直接用它。
    await use(data.path);
  });

  close.addEventListener('click', () => show(false));
  root.addEventListener('click', (event) => {
    if (event.target === root) {
      show(false);
    }
  });

  return {
    open(nextKind, options = {}) {
      kind = nextKind ?? 'roleplay';
      mode = options.mode ?? 'create';
      newName.value = '';
      show(true);
      browse(options.startPath);
    },

    // 目录内容由服务端在每次浏览时给出，没有需要从 store 归约的状态。
    render() {},
  };
}
