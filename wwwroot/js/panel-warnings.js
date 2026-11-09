// 后端警告提示区（F1）：原样显示 `roleplay.warning` 的 message，可逐条关闭。
//
// 不自创文案（§F1-6）：坏 sheet、战斗中被拒的收束这类信息，后端已经写好给人看的话，前端只负责摆出来。

export function mount({ onDismiss } = {}) {
  const area = document.getElementById('warning-area');

  function render(state) {
    if (!area) {
      return;
    }

    const warnings = state.warnings ?? [];

    if (warnings.length === 0) {
      area.hidden = true;
      area.replaceChildren();
      return;
    }

    area.hidden = false;
    area.replaceChildren(
      ...warnings.map((warning, index) => {
        const item = document.createElement('div');
        item.className = 'warning-item';
        item.dataset.code = warning.code ?? '';

        const text = document.createElement('span');
        text.className = 'warning-text';
        text.textContent = warning.message ?? '';

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'warning-close';
        close.title = '关掉这条提示（不影响日志）';
        close.textContent = '×';
        close.addEventListener('click', () => onDismiss?.(index));

        item.append(text, close);
        return item;
      }),
    );
  }

  return { render };
}
