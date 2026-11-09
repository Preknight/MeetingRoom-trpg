// 按帧批量渲染：participant.delta 是 token 级频率，逐条改 DOM 一定卡（实现步骤 3.3）。
// 同一帧内的多次状态变化只触发一次重绘，且只重绘注册过的面板。

const paints = [];
let scheduled = 0;

/** 注册一个绘制函数（每个面板一个）。 */
export function registerRender(paint) {
  paints.push(paint);
}

/** 标记"需要重绘"，同一帧内重复调用只排一次。 */
export function scheduleRender() {
  if (scheduled) {
    return;
  }

  scheduled = requestAnimationFrame(() => {
    scheduled = 0;
    for (const paint of paints) {
      paint();
    }
  });
}
