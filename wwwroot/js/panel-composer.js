// 第 2 列下：指令区（会议：用户发言 + @ 点名、补料、开始/暂停）/ 导演指令（叙事）。
// 两种模式共用这一条输入框与同一个"暂停 / 继续"按钮，只换端点与按钮显隐（设计文档 §11.6）。
// 叙事的一条输入里可以同时有旁白与括号里的元指令——拆分由服务端 OocChannel 做（§11.4-9），
// 前端不解析，免得两处规则各写一遍、慢慢分叉。

import * as api from './api.js';

/** 从文本里提取 @ 点名（与会者 id 用连字符，如 @product-manager）。 */
const MENTION = /@([\w-]+)/g;

export function mount() {
  const form = document.getElementById('composer');
  const input = document.getElementById('composer-input');
  const broadcast = document.getElementById('composer-broadcast');
  const start = document.getElementById('composer-start');
  const pause = document.getElementById('composer-pause');

  let mode = 'meeting';
  let status = 'idle';
  let narrativeStatus = 'idle';

  form.addEventListener('submit', async (event) => {
    event.preventDefault();

    const text = input.value.trim();
    if (!text) {
      return;
    }

    input.value = '';

    if (mode === 'roleplay') {
      if (narrativeStatus === 'idle') {
        return;
      }

      await api.post('/api/roleplay/say', { text });
      return;
    }

    // 会议还没开始：把这句话当议题，直接开局。
    if (status === 'idle') {
      await api.post('/api/meeting/start', { topic: text });
      return;
    }

    const mentions = [...text.matchAll(MENTION)].map((match) => match[1]);
    await api.post('/api/meeting/say', { text, mentions });
  });

  broadcast.addEventListener('click', async () => {
    const text = input.value.trim();
    if (!text) {
      return;
    }

    input.value = '';
    await api.post('/api/meeting/broadcast', { text });
  });

  start.addEventListener('click', async () => {
    const topic = input.value.trim() || '待定议题';
    input.value = '';
    await api.post('/api/meeting/start', { topic });
  });

  pause.addEventListener('click', async () => {
    if (mode === 'roleplay') {
      await api.post(narrativeStatus === 'paused' ? '/api/roleplay/resume' : '/api/roleplay/pause');
      return;
    }

    await api.post(status === 'paused' ? '/api/meeting/resume' : '/api/meeting/pause');
  });

  return {
    render(state) {
      mode = state.mode;
      status = state.meeting.status;
      narrativeStatus = state.roleplay.status;

      const narrative = mode === 'roleplay';
      const active = narrative ? narrativeStatus : status;
      const running = active === 'running' || active === 'paused';

      // "补料 / 开始会议"是会议专有的；叙事模式只留输入框与暂停。
      broadcast.hidden = narrative;
      start.hidden = narrative;
      start.disabled = !narrative && running;
      pause.disabled = !running;
      pause.textContent = active === 'paused' ? '继续' : '暂停';

      input.placeholder = narrative
        ? '下指令或写旁白；括号里是不进剧情的元指令，如（让艾拉出场）'
        : '对与会者发言，@ 点名…';
    },
  };
}
