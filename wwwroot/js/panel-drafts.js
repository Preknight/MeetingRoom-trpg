// 第 1 列下段「待整理」+ 引导页 / 编辑页（设计方案 §15.5 / §15.6 / §15.11）。
//
// 一个页面两种形态：**引导态**（新建 / 打开工作空间后自动打开，文案更详细、带「跳过」）与**编辑态**
// （从「编辑」按钮打开）——引导只是它的第一次打开，两者共用同一套控件。
//
// 手写的落待整理（可改、可归档）；**导入直接进归档区**（外部已编辑好的成品，界面不提供编辑，§15.6）。
// 角色卡的 frontmatter 由后端渲染 / 解析（§15.11），这一层只负责表单与列表。

import * as api from './api.js';
import { confirmAction } from './dialog.js';
import { clone, empty } from './dom.js';

const KIND_LABEL = { role: '角色卡', material: '资料', worldbook: '世界书条目' };

/** 导入的落点与"待整理"的类型一一对应。 */
const TARGET = { role: 'roles', material: 'materials', worldbook: 'worldbook' };

export function mount() {
  const root = document.getElementById('drafts-editor');
  const editorTitle = document.getElementById('editor-title');
  const editorHint = document.getElementById('editor-hint');
  const closeButton = document.getElementById('editor-close');
  const skipButton = document.getElementById('editor-skip');
  const draftsList = document.getElementById('editor-drafts');
  const archivedList = document.getElementById('editor-archived');
  const form = document.getElementById('editor-form');
  const cardFields = document.getElementById('card-fields');
  const plainFields = document.getElementById('plain-fields');
  const plainTitle = document.getElementById('editor-title-input');
  const bodyInput = document.getElementById('editor-body');
  const saveButton = document.getElementById('editor-save');
  const archiveButton = document.getElementById('editor-archive');
  const deleteButton = document.getElementById('editor-delete');
  const note = document.getElementById('editor-note');
  const newButtons = {
    role: document.getElementById('editor-new-role'),
    material: document.getElementById('editor-new-material'),
    worldbook: document.getElementById('editor-new-worldbook'),
  };
  const importToggle = document.getElementById('editor-import-toggle');
  const importPanel = document.getElementById('editor-import-panel');
  const importPath = document.getElementById('editor-import-path');
  const importKind = document.getElementById('editor-import-kind');
  const importRun = document.getElementById('editor-import-run');
  const importNote = document.getElementById('editor-import-note');
  const sidebarList = document.getElementById('drafts-list');
  const archivedColumn = document.getElementById('archived-list');
  const sidebarEdit = document.getElementById('drafts-edit');
  const sidebarAdd = document.getElementById('drafts-add');

  const card = {
    id: document.getElementById('card-id'),
    name: document.getElementById('card-name'),
    title: document.getElementById('card-title'),
    summary: document.getElementById('card-summary'),
    role: document.getElementById('card-role'),
    model: document.getElementById('card-model'),
    effort: document.getElementById('card-effort'),
    allowedTools: document.getElementById('card-tools'),
    readOnly: document.getElementById('card-readonly'),
  };

  let state = null;
  let kind = 'material';
  let editingId = null;
  let signature = null;

  const isOpen = () => !root.hidden;
  const setVisible = (visible) => {
    root.hidden = !visible;
  };

  // ---------- 侧栏下段（第 1 列） ----------

  function sidebarItem(entry) {
    const node = clone('tpl-draft');
    node.dataset.kind = entry.kind;
    node.querySelector('.draft-title').textContent = entry.title;
    node.querySelector('.draft-kind').textContent = KIND_LABEL[entry.kind] ?? entry.kind;
    node.querySelector('.draft-open').addEventListener('click', () => openEditor(false, entry.id));
    node.querySelector('.draft-archive').addEventListener('click', () => archive(entry.id));
    return node;
  }

  function renderSidebar() {
    const drafts = state?.drafts ?? [];
    sidebarList.replaceChildren(
      ...(drafts.length ? drafts.map(sidebarItem) : [empty('还没有待整理的东西：点「＋ 添加」把资料放进来，整理好再归档。')]),
    );
  }

  /** 第 1 列上段的归档区（工作空间级：角色卡 / 资料 / 世界书条目，跨会话共享）。 */
  function renderArchivedColumn() {
    const archived = state?.archived ?? [];

    // 空的时候不留一行提示——上半段本来就是资料卡的地盘，安静就好。
    archivedColumn.replaceChildren(...(archived.length ? archived.map(archivedItem) : []));
  }

  // ---------- 编辑页里的两个列表 ----------

  function editorItem(entry) {
    const node = clone('tpl-draft');
    node.dataset.kind = entry.kind;
    node.dataset.active = String(entry.id === editingId);
    node.querySelector('.draft-title').textContent = entry.title;
    node.querySelector('.draft-kind').textContent = KIND_LABEL[entry.kind] ?? entry.kind;
    node.querySelector('.draft-open').addEventListener('click', () => loadInto(entry.id));
    node.querySelector('.draft-archive').addEventListener('click', () => archive(entry.id));
    return node;
  }

  function archivedItem(item) {
    const node = clone('tpl-archived');
    node.dataset.kind = item.kind;
    node.querySelector('.archived-title').textContent = item.title;
    node.querySelector('.archived-kind').textContent = KIND_LABEL[item.kind] ?? item.kind;

    // 正文只在点击时拉取（与资料卡同一条规矩：正文不进任何 Agent 的上下文）。
    const link = node.querySelector('.archived-view');
    link.href = `/api/archived/content?path=${encodeURIComponent(item.path)}`;

    node.querySelector('.archived-unarchive').addEventListener('click', () => unarchive(item));
    return node;
  }

  function renderLists() {
    if (!isOpen()) {
      return;
    }

    const drafts = state?.drafts ?? [];
    const archived = state?.archived ?? [];

    draftsList.replaceChildren(...(drafts.length ? drafts.map(editorItem) : [empty('（待整理是空的）')]));
    archivedList.replaceChildren(...(archived.length ? archived.map(archivedItem) : [empty('（还没有归档的东西）')]));
  }

  // ---------- 表单 ----------

  function setKind(next) {
    kind = next;
    cardFields.hidden = kind !== 'role';
    plainFields.hidden = kind === 'role';
  }

  function clearForm() {
    editingId = null;
    setKind(kind);
    card.id.value = '';
    card.id.readOnly = false;
    card.name.value = '';
    card.title.value = '';
    card.summary.value = '';
    card.role.value = 'member';
    card.model.value = '';
    card.effort.value = '';
    card.allowedTools.value = '';
    card.readOnly.checked = false;
    plainTitle.value = '';
    bodyInput.value = '';
    note.textContent = '';
    deleteButton.disabled = true;
  }

  function cardPayload() {
    return {
      name: card.name.value.trim(),
      title: card.title.value.trim(),
      summary: card.summary.value.trim(),
      role: card.role.value,
      model: card.model.value.trim(),
      effort: card.effort.value.trim(),
      allowedTools: card.allowedTools.value.split(',').map((tool) => tool.trim()).filter(Boolean),
      readOnly: card.readOnly.checked,
    };
  }

  function payload() {
    return kind === 'role'
      ? { id: editingId, kind, title: card.name.value.trim(), body: bodyInput.value, card: cardPayload() }
      : { id: editingId, kind, title: plainTitle.value.trim(), body: bodyInput.value };
  }

  /** 把一条待整理条目拉回来填进表单（角色卡的字段由后端解析，前端不碰 frontmatter）。 */
  async function loadInto(id) {
    const { ok, data } = await api.getJson(`/api/drafts/content?id=${encodeURIComponent(id)}`);

    if (!ok || !data) {
      note.textContent = data?.error ?? '读不到这条待整理条目';
      return;
    }

    editingId = data.id;
    setKind(data.kind);
    bodyInput.value = data.body ?? '';

    if (data.kind === 'role') {
      const fields = data.card ?? {};

      card.id.value = data.id;
      card.id.readOnly = true; // 已有条目：改 id 会变成另一份，容易困惑
      card.name.value = fields.name ?? data.title ?? '';
      card.title.value = fields.title ?? '';
      card.summary.value = fields.summary ?? '';
      card.role.value = fields.role ?? 'member';
      card.model.value = fields.model ?? '';
      card.effort.value = fields.effort ?? '';
      card.allowedTools.value = (fields.allowedTools ?? []).join(', ');
      card.readOnly.checked = Boolean(fields.readOnly);
    } else {
      card.id.value = '';
      card.id.readOnly = false;
      plainTitle.value = data.title ?? '';
    }

    note.textContent = '';
    deleteButton.disabled = false;
    signature = null;
    renderLists();
  }

  async function save() {
    const body = payload();

    if (kind === 'role' && !body.title) {
      note.textContent = '角色卡的 name 必填。';
      return null;
    }

    note.textContent = '正在保存…';

    const { ok, data } = await api.postJson('/api/drafts', body);

    if (!ok) {
      note.textContent = data?.error ?? data?.raw ?? '保存失败';
      return null;
    }

    editingId = data.entry.id;
    note.textContent = '已保存到待整理';
    return editingId;
  }

  async function archive(id) {
    const confirmed = await confirmAction('归档这份？归档后进「已归档」，界面上不再可改（要改先撤销归档）。', {
      title: '归档',
      okText: '归档',
    });

    if (!confirmed) {
      return;
    }

    const { ok, data } = await api.postJson('/api/drafts/archive', { id });

    if (!ok) {
      note.textContent = data?.error ?? '归档失败';
      return;
    }

    if (editingId === id) {
      clearForm();
    }

    note.textContent = '已归档';
  }

  async function unarchive(item) {
    const { ok, data } = await api.postJson('/api/drafts/unarchive', { kind: item.kind, path: item.path });

    if (!ok) {
      note.textContent = data?.error ?? '撤销归档失败';
      return;
    }

    note.textContent = '已撤销归档，回到待整理';
  }

  async function remove() {
    if (!editingId) {
      return;
    }

    const confirmed = await confirmAction('删掉这条待整理条目？它还没归档，删了就没了。', {
      title: '删除',
      okText: '删除',
      danger: true,
    });

    if (!confirmed) {
      return;
    }

    const { ok, data } = await api.postJson('/api/drafts/delete', { id: editingId });

    if (!ok) {
      note.textContent = data?.error ?? '删除失败';
      return;
    }

    clearForm();
  }

  async function runImport() {
    const sources = importPath.value
      .split(';')
      .map((value) => value.trim())
      .filter(Boolean);

    if (sources.length === 0) {
      importNote.textContent = '填一个文件或目录的路径（多个用分号隔开）。';
      return;
    }

    const target = TARGET[importKind.value] ?? 'materials';
    importNote.textContent = '正在复制…';

    const { ok, data } = await api.postJson('/api/workspaces/import', { sources, target });

    if (!ok) {
      importNote.textContent = data?.error ?? data?.raw ?? '导入失败';
      return;
    }

    const skipped = (data.skipped ?? []).length;
    importNote.textContent = `已把 ${data.copied.length} 个文件复制进 ${target}/`
      + `${skipped > 0 ? `（跳过 ${skipped} 个）` : ''}——导入即归档，界面上不提供编辑。`;
  }

  // ---------- 打开 / 关闭 ----------

  function openEditor(guided, entryId) {
    setVisible(true);
    editorTitle.textContent = guided ? '先把资料放对位置' : '整理资料';
    editorHint.textContent = guided
      ? '这里就是「待整理」区：手写一条（角色卡 / 资料 / 世界书条目）→ 归档；'
        + '也可以导入磁盘上已经编辑好的文件（导入即归档）。没想好就跳过，随时点「编辑」回来。'
      : '待整理里的一切都可以改；归档之后进「已归档」，界面上不再可改（要改先撤销归档）。';
    skipButton.hidden = !guided;
    importPanel.hidden = true;
    importNote.textContent = '';

    if (entryId) {
      loadInto(entryId);
    } else if (!editingId) {
      clearForm();
    }

    signature = null;
    renderLists();
  }

  function closeEditor() {
    setVisible(false);
  }

  // ---------- 绑定 ----------

  sidebarEdit.addEventListener('click', () => openEditor(false));
  sidebarAdd.addEventListener('click', () => {
    openEditor(false);
    clearForm();
    setKind('material');
    plainTitle.focus();
  });

  closeButton.addEventListener('click', closeEditor);
  skipButton.addEventListener('click', closeEditor);
  root.addEventListener('click', (event) => {
    if (event.target === root) {
      closeEditor();
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && isOpen() && document.getElementById('path-picker').hidden) {
      closeEditor();
    }
  });

  for (const [nextKind, button] of Object.entries(newButtons)) {
    button.addEventListener('click', () => {
      clearForm();
      setKind(nextKind);
      (nextKind === 'role' ? card.name : plainTitle).focus();
    });
  }

  // 系统原生对话框选文件（可多选）：填回路径框，用户再点「导入」。
  document.getElementById('editor-import-system').addEventListener('click', async () => {
    importNote.textContent = '等系统对话框…';

    const { ok, data } = await api.postJson('/api/system/pick-files', {
      title: '选择要导入的文件（导入即归档，界面不提供编辑）',
      filter: 'Markdown (*.md)|*.md|文本 (*.txt)|*.txt|所有文件 (*.*)|*.*',
    });

    if (!ok) {
      importNote.textContent = data?.error ?? data?.raw ?? '调系统对话框失败';
      return;
    }

    if (data.cancelled || !(data.paths ?? []).length) {
      importNote.textContent = '（系统对话框里取消了）';
      return;
    }

    importPath.value = data.paths.join('; ');
    importNote.textContent = `选了 ${data.paths.length} 个文件，点「导入」把它们复制进来。`;
  });

  importToggle.addEventListener('click', () => {
    importPanel.hidden = !importPanel.hidden;
    importKind.value = kind;
  });
  importRun.addEventListener('click', runImport);

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    await save();
  });

  archiveButton.addEventListener('click', async () => {
    const id = await save();

    if (id) {
      await archive(id);
    }
  });

  deleteButton.addEventListener('click', remove);

  return {
    /** 新建 / 打开工作空间之后自动打开的引导态（§15.5）。 */
    openGuide() {
      openEditor(true);
    },

    render(next) {
      state = next;

      const key = JSON.stringify([next.drafts ?? [], next.archived ?? [], editingId]);
      const changed = key !== signature;
      signature = key;

      if (changed) {
        renderSidebar();
        renderArchivedColumn();
        renderLists();
      }
    },
  };
}
