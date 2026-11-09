/**
 * 页面内对话框（通用）：确认与表单。
 *
 * 为什么不直接用 `原生 confirm` / `原生 prompt`：
 * 1. 原生对话框**阻塞页面主线程**——自动化环境（WebView2 / CDP）里会直接卡死，
 *    后续 evaluate 全部超时，只能重建窗口（r6 现场）；
 * 2. 原生输入框一次只能问一个字段，没有内联校验、回显与取消语义；
 * 3. 样式不可控，和现有浮层风格对不上。
 *
 * 这里只用原生 DOM：建一个遮罩 + 卡片，Promise 结束时一并移除，**不留全局状态**。
 * 变量名避开 `document / window / name / top / location / history`（本项目已踩过两次）。
 */

const OVERLAY_CLASS = 'app-dialog-scrim';
const CARD_CLASS = 'app-dialog';

/** 当前打开的那个对话框（同一时刻只允许一个）。 */
let active = null;

function ensureHost() {
  return document.body;
}

function buildScrim() {
  const scrim = document.createElement('div');
  scrim.className = OVERLAY_CLASS;
  scrim.setAttribute('role', 'presentation');
  return scrim;
}

/**
 * 确认框：返回 `true` / `false`。Esc 与「取消」都返回 `false`。
 * <paramref name="options.okText"/> 可换按钮文案（删除类用「删除」）。
 */
export function confirmAction(message, options = {}) {
  return open({
    title: options.title ?? '确认',
    hint: message,
    fields: [],
    okText: options.okText ?? '确定',
    danger: options.danger === true,
  }).then((result) => result !== null);
}

/**
 * 表单：一次收多个字段。
 * `fields` 每项：`{ name, label, type?, value?, placeholder?, hint?, suffix? }`。
 * `validate(values)` 返回错误信息字符串（或 `null` 表示通过）——**校验失败不关窗**。
 *
 * 返回 `{ [name]: string }`；取消 / Esc 返回 `null`。
 */
export function askFields({ title, hint = '', fields = [], validate, okText = '确定' }) {
  return open({ title, hint, fields, validate, okText });
}

function open({ title, hint, fields, validate, okText, danger }) {
  closeActive();

  return new Promise((resolve) => {
    const scrim = buildScrim();
    const card = document.createElement('form');
    card.className = CARD_CLASS;
    card.setAttribute('novalidate', '');

    const headingText = document.createElement('h3');
    headingText.textContent = title;
    card.append(headingText);

    if (hint) {
      const hintNode = document.createElement('p');
      hintNode.className = 'app-dialog-hint';
      hintNode.textContent = hint;
      card.append(hintNode);
    }

    const inputs = new Map();

    for (const field of fields) {
      const row = document.createElement('label');
      row.className = 'app-dialog-row';

      const labelText = document.createElement('span');
      labelText.textContent = field.label;
      row.append(labelText);

      const input = document.createElement('input');
      input.type = field.type ?? 'text';
      input.value = field.value ?? '';
      input.name = field.name;
      input.autocomplete = 'off';

      if (field.type === 'number') {
        input.step = 'any';
        input.inputMode = 'decimal';
      }

      if (field.placeholder) {
        input.placeholder = field.placeholder;
      }

      row.append(input);

      if (field.suffix) {
        const suffix = document.createElement('span');
        suffix.className = 'app-dialog-suffix';
        suffix.textContent = field.suffix;
        row.append(suffix);
      }

      inputs.set(field.name, input);
      card.append(row);

      if (field.note) {
        const note = document.createElement('p');
        note.className = 'app-dialog-note';
        note.textContent = field.note;
        card.append(note);
      }
    }

    const error = document.createElement('p');
    error.className = 'app-dialog-error';
    error.hidden = true;
    card.append(error);

    const actions = document.createElement('div');
    actions.className = 'app-dialog-actions';

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.textContent = '取消';

    const ok = document.createElement('button');
    ok.type = 'submit';
    ok.className = danger ? 'app-dialog-danger' : 'app-dialog-primary';
    ok.textContent = okText;

    actions.append(ok, cancel);
    card.append(actions);
    scrim.append(card);
    ensureHost().append(scrim);

    const readValues = () => Object.fromEntries([...inputs].map(([key, input]) => [key, input.value.trim()]));

    const finish = (result) => {
      scrim.remove();
      active = null;
      resolve(result);
    };

    const submit = () => {
      const values = readValues();
      const problem = validate ? validate(values) : null;

      if (problem) {
        // 内联提示、不关窗、焦点回到第一个出错的输入。
        error.hidden = false;
        error.textContent = problem;
        const [firstName] = inputs.keys();
        inputs.get(firstName)?.focus();
        return;
      }

      finish(values);
    };

    card.addEventListener('submit', (event) => {
      event.preventDefault();
      submit();
    });

    cancel.addEventListener('click', () => finish(null));
    scrim.addEventListener('click', (event) => {
      if (event.target === scrim) {
        finish(null);
      }
    });

    scrim.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        finish(null);
      }
    });

    active = { finish };

    // 焦点落在第一个输入（没有输入就是确认按钮）。**必须延后一帧**：
    // 元素刚 append 时浏览器会忽略 focus()，实测 activeElement 仍停在 body。
    const [firstInput] = inputs.values();
    const focusTarget = firstInput ?? ok;
    setTimeout(() => focusTarget.focus(), 0);
  });
}

function closeActive() {
  active?.finish(null);
  active = null;
}
