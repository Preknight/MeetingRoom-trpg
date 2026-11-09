// 命令通道：POST/GET + 启动时的一次性 token（设计文档 §10.4）。
// token 从界面地址的 ?t= 取出，随请求头带回；端点侧的校验在后续阶段启用。

const token = new URLSearchParams(location.search).get('t') ?? '';

function headers(extra) {
  return token ? { ...extra, 'X-Meeting-Token': token } : { ...extra };
}

/**
 * 发一个请求并拿回响应体。侧栏（工作空间 / 会话）与选目录对话框要读返回的数据，
 * 也在失败时要把后端说的原因显示出来，所以它们用这个；只关心成败的旧面板仍可用 post / get。
 */
export async function request(path, { method = 'GET', body } = {}) {
  const response = await fetch(path, {
    method,
    headers: headers(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await response.text();
  let data = null;

  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { raw: text };
    }
  }

  return { ok: response.ok, status: response.status, data };
}

export function postJson(path, body) {
  return request(path, { method: 'POST', body: body ?? {} });
}

export function getJson(path) {
  return request(path);
}

export async function post(path, body) {
  const response = await fetch(path, {
    method: 'POST',
    headers: headers({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(body ?? {}),
  });

  return response.ok;
}

export async function get(path) {
  const response = await fetch(path, { headers: headers({}) });
  return response.ok;
}

/** 事件流地址（诊断面板/手工调试用）。 */
export const eventStreamPath = '/api/events';
