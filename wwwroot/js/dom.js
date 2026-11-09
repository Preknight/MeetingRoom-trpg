// 各面板共用的小工具与中文标签（术语与设计文档保持一致）。

export function clone(templateId) {
  return document.getElementById(templateId).content.firstElementChild.cloneNode(true);
}

export function empty(message) {
  const node = document.createElement('p');
  node.className = 'empty';
  node.textContent = message;
  return node;
}

// 与会者状态（设计文档 §4.3）。
const PARTICIPANT_STATUS = {
  idle: '空闲',
  queued: '排队中',
  thinking: '思考中',
  reading: '读取资料',
  writing: '撰写中',
  waiting_approval: '等待审批',
  interrupted: '被打断',
  failed: '失败',
};

// 资料六态（设计文档 §4.2）。
const MATERIAL_STATUS = {
  draft: '草稿',
  host_review: '主持人审批中',
  user_review: '待你终审',
  adopted: '已采纳',
  rejected: '已驳回',
  parked: '已搁置',
};

export function statusLabel(status) {
  return PARTICIPANT_STATUS[status] ?? status ?? '';
}

export function materialStatusLabel(status) {
  return MATERIAL_STATUS[status] ?? status ?? '';
}

// 角色标签：会议模式是主持人 / 与会者，叙事模式是说书人 / 角色（§11.6 的列内语义切换）。
// 注意：「导演」这个词留给用户（DM），那个 role: host 的 Agent 叫说书人。
export function roleLabel(role) {
  switch (role) {
    case 'host':
      return '主持人';
    case 'narrator':
      return '说书人';
    case 'member':
      return '与会者';
    case 'actor':
      return '角色';
    default:
      return role ?? '';
  }
}

export function authorName(state, id) {
  if (!id) {
    return '';
  }

  if (id === 'user') {
    return '你';
  }

  return state.participants.find((p) => p.id === id)?.name ?? id;
}
