// 第 1 列：侧栏（工作空间 / 会话，设计方案 §15.2 / §15.3）。
//
// 一次只活跃一场会话（单条 SSE 的归约前提，§8.1），所以"切"就是让后端重新装配那一场；
// "删"不可逆，这里做二次确认。会话的删除只作用于工作空间自己的会话目录（后端会复核路径）。
// 展开状态是纯 UI 状态（不进 store）：切到哪个工作空间就展开哪个，用户手动收起的不会被强行展开。

import * as api from './api.js';
import { confirmAction } from './dialog.js';
import { clone, empty } from './dom.js';

const KIND_LABEL = { meeting: '会议', roleplay: '叙事', legacy: '过渡' };

export function mount(root, options = {}) {
  const newRoleplay = document.getElementById('new-roleplay');

  let state = null;
  let signature = null;
  let paintedCurrent = null;
  const expanded = new Set();

  const openWorkspace = document.getElementById('open-workspace');

  newRoleplay.addEventListener('click', () => options.onNew?.('roleplay'));
  openWorkspace.addEventListener('click', () => options.onOpen?.());

  async function call(path, body, fallback) {
    const { ok, data } = await api.postJson(path, body);

    if (!ok) {
      options.onError?.(data?.error ?? data?.raw ?? fallback);
      return false;
    }

    return true;
  }

  function sessionMeta(session) {
    const kind = KIND_LABEL[session.kind] ?? session.kind;
    const text = (session.topic ?? session.worldview ?? '').trim();

    return text ? `${kind} · ${text}` : kind;
  }

  function sessionNode(session) {
    const node = clone('tpl-session');
    const active = state.activeSession?.kind === session.kind && state.activeSession?.id === session.id;
    const label = session.title ?? session.id;

    node.dataset.active = String(active);
    node.querySelector('.sess-title').textContent = label;
    node.querySelector('.sess-title').title = label;
    node.querySelector('.sess-meta').textContent = sessionMeta(session);

    node.querySelector('.sess-open').addEventListener('click', () => {
      call('/api/sessions/open', { kind: session.kind, id: session.id }, '切换会话失败');
    });

    node.querySelector('.sess-delete').addEventListener('click', async () => {
      const confirmed = await confirmAction(`删掉「${label}」这场会话？它的目录与内容会一起删掉，不可恢复。`, {
        title: '删除会话',
        okText: '删除',
        danger: true,
      });

      if (!confirmed) {
        return;
      }

      await call('/api/sessions/delete', { kind: session.kind, id: session.id }, '删除会话失败');
    });

    return node;
  }

  function workspaceNode(workspace) {
    const node = clone('tpl-workspace');
    const isCurrent = workspace.path === state.currentWorkspace;
    const isOpen = expanded.has(workspace.path);

    node.dataset.active = String(isCurrent);
    node.querySelector('.ws-caret').textContent = isOpen ? '▾' : '▸';
    node.querySelector('.ws-name').textContent = workspace.name ?? workspace.path;
    node.querySelector('.ws-name').title = workspace.path;
    node.querySelector('.ws-kind').textContent = KIND_LABEL[workspace.kind] ?? workspace.kind;
    node.querySelector('.ws-path').textContent = workspace.path;
    node.querySelector('.ws-path').title = workspace.path;
    node.querySelector('.ws-count').textContent = `${workspace.sessionCount ?? 0} 场`;

    node.querySelector('.ws-head').addEventListener('click', () => {
      if (isCurrent) {
        // 已经是它：这一下只折叠 / 展开。
        if (isOpen) {
          expanded.delete(workspace.path);
        } else {
          expanded.add(workspace.path);
        }

        signature = null;
        paint();
        return;
      }

      // 不是它：展开并切过去（切过去之后后端才会给出它的会话列表）。
      expanded.add(workspace.path);
      call('/api/workspaces/open', { path: workspace.path }, '打开工作空间失败');
    });

    node.querySelector('.ws-forget').addEventListener('click', async () => {
      const confirmed = await confirmAction(`把「${workspace.name ?? workspace.path}」从清单里移除？磁盘上的东西一个都不动。`, {
        title: '移出清单',
        okText: '移除',
      });

      if (!confirmed) {
        return;
      }

      await call('/api/workspaces/forget', { path: workspace.path }, '移除失败');
    });

    const sessions = node.querySelector('.ws-sessions');
    // 只有当前工作空间才有会话列表（会话是按当前工作空间装配的）。
    const items = isCurrent ? state.sessions : [];

    sessions.hidden = !(isCurrent && isOpen);
    sessions.replaceChildren(
      ...(items.length
        ? items.map(sessionNode)
        : [empty('这个工作空间还没有会话：点上面的「＋ 新建叙事」开一场。')]),
    );

    return node;
  }

  function paint() {
    if (!state) {
      return;
    }

    // 切了工作空间就把新的那个展开（用户随后手动收起的不会被再次强行展开）。
    if (state.currentWorkspace && state.currentWorkspace !== paintedCurrent) {
      paintedCurrent = state.currentWorkspace;
      expanded.add(state.currentWorkspace);
    }

    if (state.workspaces.length === 0) {
      root.replaceChildren(empty('还没有工作空间。点上面的「＋ 新建叙事」，选一个目录开始。'));
      return;
    }

    root.replaceChildren(...state.workspaces.map(workspaceNode));
  }

  return {
    render(next) {
      state = next;

      const key = JSON.stringify([
        next.workspaces.map((workspace) => [workspace.path, workspace.name, workspace.kind, workspace.sessionCount]),
        next.currentWorkspace,
        next.sessions.map((session) => [session.kind, session.id, session.title]),
        next.activeSession,
        [...expanded].sort(),
      ]);

      if (key === signature) {
        return;
      }

      signature = key;
      paint();
    },
  };
}
