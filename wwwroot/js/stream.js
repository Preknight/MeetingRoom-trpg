// 唯一的 SSE 连接（实现步骤 3.2 / 设计文档 §8.1）。
// 事件类型写进 SSE 的 event: 字段，这里按类型精确订阅；EventSource 自带断线重连，
// 重连时会自动带上 Last-Event-ID，服务端则会先推一份 meeting.state 与 roleplay.state 全量快照。
//
// 契约提醒：事件表（设计文档 §8.3）是前后端唯一契约，改它必须同时改这里——
// 前端没有类型检查（实施进度与待办 风险 6），漏一个类型不会构建报错，只会表现为"界面没反应"。

const EVENT_TYPES = [
  'meeting.state',
  'meeting.error',
  'participant.status',
  'participant.delta',
  'participant.thought',
  'tool.call',
  'tool.result',
  'message.committed',
  'host.decision',
  'material.submitted',
  'material.reviewed',
  'interrupt.applied',
  'cache.diagnostics',

  // 交互模式（设计文档 §8.3 / §14）：待用户拍板的事项——主持人提问与说书人提议共用同一条流，
  // 所以这三个类型不带模式前缀（会议与叙事都会发）。
  'interaction.request',
  'interaction.resolved',
  'interaction.skipped',

  // 角色扮演（叙事）模式（设计文档 §11.6）：与会议事件并列，靠 roleplay.state 的 status 决定界面语义。
  'roleplay.state',
  'roleplay.error',
  'roleplay.started',
  'roleplay.ooc',
  'roleplay.line',
  'roleplay.turn',
  'roleplay.ended',
  'roleplay.world',

  // 战斗与警告（F1）：战斗条靠 roleplay.combat 提示"该重取快照"，
  // 警告区直接显示 roleplay.warning 的原文——漏这两个类型不会报错，只会表现为"界面没反应"。
  'roleplay.combat',
  'roleplay.warning',
  // 检定与骰子（F6）：内联进剧情流。
  'roleplay.check',
  'roleplay.roll',

  // 工作空间与会话（设计方案 §15.7）：侧栏据此重建；切工作空间 / 切会话 / 新建会话都从这三个来。
  'workspace.list',
  'workspace.opened',
  'session.list',

  // 待整理 / 已归档（设计方案 §15.6）：第 1 列两段据此重建。
  'draft.list',
];

export function connect({ onEvent, onConnectionChange }) {
  const source = new EventSource('/api/events');

  source.addEventListener('open', () => onConnectionChange('open'));
  // EventSource 在错误后会自动重连；这里只反映连接状态，不做手工重连。
  source.addEventListener('error', () => onConnectionChange('reconnecting'));

  for (const type of EVENT_TYPES) {
    source.addEventListener(type, (event) => {
      onEvent(type, parse(event.data));
    });
  }

  return source;
}

function parse(data) {
  try {
    return JSON.parse(data);
  } catch {
    return { raw: data };
  }
}
