// 「新建叙事」浮层（设计方案 §15.3 / §15.4）。
//
// 填设定、选在场角色；**工作空间在这里选**——已有的直接用，
// 没有就点「选择目录…」新建 / 打开一个叙事型工作空间。
//
// W4 的引导页会接在"工作空间建好之后"这一步（导入资料 / 归档）。

import * as api from './api.js';

const KIND = {
  roleplay: {
    title: '新建叙事',
    hint: '叙事型工作空间只开叙事：填好设定后，开场由说书人直接写。',
  },
};

const KIND_LABEL = { roleplay: '叙事', legacy: '过渡' };

export function mount(options = {}) {
  const root = document.getElementById('session-start');
  const title = document.getElementById('session-start-title');
  const hint = document.getElementById('session-start-hint');
  const close = document.getElementById('session-start-close');
  const form = document.getElementById('session-form');
  const roleplayFields = document.getElementById('session-roleplay-fields');
  const workspaceName = document.getElementById('session-workspace-name');
  const workspacePath = document.getElementById('session-workspace-path');
  const pickPath = document.getElementById('session-pick-path');
  const submit = document.getElementById('session-submit');
  const note = document.getElementById('session-note');

  const worldview = document.getElementById('session-worldview');
  const situation = document.getElementById('session-situation');
  const userRole = document.getElementById('session-user-role');
  const castList = document.getElementById('session-cast-list');
  const castAll = document.getElementById('cast-all');
  const castNone = document.getElementById('cast-none');
  const castNote = document.getElementById('cast-note');
  const narrator = document.getElementById('session-narrator');

  let state = null;
  const kind = 'roleplay';

  /** 勾选的角色 id（空数组 = 全部在场）。 */
  function selectedCastIds() {
    return [...(castList?.querySelectorAll('input[type="checkbox"]:checked') ?? [])]
      .map((input) => input.value);
  }

  /** 拉角色卡清单并渲染勾选列表（r13：只读端点 `GET /api/roles`）。 */
  async function loadCastOptions(workspacePath) {
    if (!castList) {
      return;
    }

    castList.replaceChildren();
    castNote.textContent = '';

    const query = workspacePath ? `?workspace=${encodeURIComponent(workspacePath)}` : '';
    const response = await fetch(`/api/roles${query}`);
    const roles = response.ok ? await response.json() : [];

    if (!Array.isArray(roles) || roles.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'cast-picker-note';
      empty.textContent = '这个工作空间还没有角色卡';
      castList.append(empty);
      return;
    }

    for (const role of roles) {
      const item = document.createElement('label');
      item.className = 'cast-picker-item';

      const box = document.createElement('input');
      box.type = 'checkbox';
      box.value = role.id;

      const text = document.createElement('span');
      text.textContent = role.role === 'host' ? `${role.name}（主持人）` : role.name;

      item.append(box, text);
      castList.append(item);
    }

    castNote.textContent = `共 ${roles.length} 张角色卡；一个都不勾 = 全部在场`;
  }

  castAll?.addEventListener('click', () => {
    for (const box of castList?.querySelectorAll('input[type="checkbox"]') ?? []) {
      box.checked = true;
    }
  });

  castNone?.addEventListener('click', () => {
    for (const box of castList?.querySelectorAll('input[type="checkbox"]') ?? []) {
      box.checked = false;
    }
  });

  const show = (visible) => {
    root.hidden = !visible;
  };

  const currentWorkspace = () =>
    state?.workspaces.find((workspace) => workspace.path === state.currentWorkspace) ?? null;

  /** 工作空间类型与要开的会话类型必须一致（legacy 是过渡形态，两种都收）。 */
  const compatible = () => {
    const workspace = currentWorkspace();
    return Boolean(workspace) && (workspace.kind === 'legacy' || workspace.kind === 'roleplay');
  };

  function renderWorkspaceBlock() {
    const workspace = currentWorkspace();

    workspaceName.textContent = workspace ? (workspace.name ?? workspace.path) : '未选择';
    workspacePath.textContent = workspace ? workspace.path : '还没有工作空间：点「选择目录…」选一个目录。';

    hint.textContent = workspace && !compatible()
      ? `「${workspace.name ?? workspace.path}」是${KIND_LABEL[workspace.kind] ?? workspace.kind}型工作空间，`
        + '开不了叙事——请用「选择目录…」新建或打开一个叙事型工作空间。'
      : KIND.roleplay.hint;

    submit.disabled = !compatible();
  }

  function openFields() {
    title.textContent = KIND.roleplay.title;
    roleplayFields.hidden = false;
    renderWorkspaceBlock();

    // 每次打开都重拉角色卡：换了工作空间、或者刚往 roles/ 里加了卡，都要立刻反映。
    void loadCastOptions(currentWorkspace()?.path);

    show(true);
    worldview.focus();
  }

  pickPath.addEventListener('click', () => options.onPickPath?.(kind));
  close.addEventListener('click', () => show(false));
  root.addEventListener('click', (event) => {
    if (event.target === root) {
      show(false);
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || root.hidden) {
      return;
    }

    // 选目录对话框盖在上面时，Esc 先关它（两个浮层不该一起关掉）。
    if (!document.getElementById('path-picker').hidden) {
      return;
    }

    show(false);
  });

  form.addEventListener('submit', async (event) => {
    event.preventDefault();

    if (!compatible()) {
      note.textContent = '先选一个类型匹配的工作空间。';
      return;
    }

    const world = worldview.value.trim();
    const scene = situation.value.trim();

    if (!world || !scene) {
      note.textContent = '世界观与开场情境都要填。';
      return;
    }

    note.textContent = '正在创建…';

    const { ok, data } = await api.postJson('/api/roleplay/start', {
      worldview: world,
      situation: scene,
      userRole: userRole.value.trim(),
      // 全部不勾 = 全部在场（保持原有语义，不要解释成"没人"）。
      cast: selectedCastIds(),
      // 留空就交给后端用默认值：说书人的角色卡 id 可以由用户自己起名（§11.4 第 5b 项）。
      narratorId: narrator.value.trim(),
    });

    if (ok) {
      note.textContent = '';
      show(false);
      return;
    }

    note.textContent = `创建失败：${data?.error ?? data?.raw ?? '请稍后再试'}`;
  });

  return {
    open() {
      note.textContent = '';
      openFields();
    },

    render(next) {
      state = next;

      if (!root.hidden) {
        renderWorkspaceBlock();
      }
    },
  };
}
