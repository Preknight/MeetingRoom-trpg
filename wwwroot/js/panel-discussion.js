// 第 2 列上：讨论区（会议）/ 剧情对话流（叙事）。**只显示已提交的正文**——
// 正在流式生成的文本属于"后台记录"，显示在第 5 列（设计文档 §9.1 的分工）。
// 两种模式共用这一列，只换数据源（§11.6）：会议读 messages，叙事读 story（按幕累积）。

import { authorName, clone, empty } from './dom.js';
import { timelineOf } from './store.js';

export function mount(root) {
  let signature = null;

  return {
    render(state) {
      const narrative = state.mode === 'roleplay';
      // 叙事模式把检定卡与骰子卡**按时间内联**进剧情流（F6）；会议模式仍是消息列表。
      const entries = narrative ? timelineOf(state) : state.messages;
      // 模式本身也要进签名：两种模式的数据源不同，内容相等不代表渲染结果相等（空态文案就不一样）。
      const next = JSON.stringify([
        narrative,
        narrative
          ? entries.map((item) => `${item.kind}:${item.at ?? item.entry?.revision}`)
          : entries.map((entry) => [entry.id, entry.text]),
      ]);

      if (next === signature) {
        return;
      }

      signature = next;

      // 用户没有主动上滚时才跟随底部。
      const follow = root.scrollHeight - root.scrollTop - root.clientHeight < 40;

      // 空卡兜底（F7-② 第二道）：没有掷骰、没有被动值、也没有成败的检定卡一律不渲染——
      // 后端已经不再落"被拒的检定"，这一层只防旧数据与将来的新拒绝原因。
      const nodes = entries
        .map((entry) => (narrative ? narrativeNode(state, entry) : messageNode(state, entry, false)))
        .filter(Boolean);

      root.replaceChildren(...(nodes.length > 0 ? nodes : [empty(narrative ? '剧情尚未开始' : '讨论尚未开始')]));

      if (follow) {
        root.scrollTop = root.scrollHeight;
      }
    },
  };
}

/**
 * 叙事流的一条：正文 / 检定卡 / 骰子卡（F6）/ 角色请求（M9b）。
 */
function narrativeNode(state, item) {
  if (item.kind === 'check') {
    return checkNode(state, item.check);
  }

  if (item.kind === 'roll') {
    return rollNode(item.roll);
  }

  if (item.kind === 'request') {
    return requestNode(state, item.request);
  }

  return messageNode(state, item.entry, true);
}

/**
 * 角色请求的一行（M9b）：系统提示性的旁注，不是谁在说话——
 * 所以它**不参与**发言者高亮与 @ 点名，也不带作者色块。
 * 形如：（Ella 请求：对奥秘做一次判定 —— 那扇门上的花纹不对劲）
 */
function requestNode(state, request) {
  const node = document.createElement('div');
  node.className = 'story-request';
  node.dataset.requestKind = request.requestKind ?? 'check';
  // 明示"这不是发言"：高亮 / 点名逻辑按 dataset.author 找人，这里刻意不设。
  node.title = request.requestKind === 'combat_action' ? '角色请求的战斗动作（等说书人处理）' : '角色请求的判定（等说书人处理）';

  const who = actorLabel(state, request.actorId);
  const summary = request.summary || '一次请求';
  const reason = (request.reason ?? '').trim();
  // 摘要里已经含理由时不重复（后端在 reason 为空时会把 summary 填进 reason）。
  const tail = reason && reason !== summary ? ` —— ${reason}` : '';

  node.textContent = `（${who} 请求：${summary}${tail}）`;
  return node;
}

/** 检定卡：who + 技能/豁免 + d20+mod=total vs DC → 成败；暗骰对 DM 照显数值，但加徽标并弱化卡面。 */
function checkNode(state, check) {
  // 空卡（F7-②）：既没掷骰、也没被动值、也没有成败 → 不是一次"检定"，不渲染。
  // 死亡豁免例外：它可能只有成功/失败计数。
  const deathSave = check.kindType === 'death-save';
  const hasOutcome = check.roll?.total != null
    || check.passiveValue != null
    || check.success != null
    || (deathSave && (check.successes != null || check.failures != null));

  if (!hasOutcome) {
    return null;
  }

  const secret = String(check.visibility ?? '').toLowerCase() === 'secret';
  const node = clone('tpl-check');
  node.dataset.secret = String(secret);
  node.dataset.success = check.success === true ? 'true' : check.success === false ? 'false' : 'unknown';

  const who = actorLabel(state, check.who);
  node.querySelector('.check-title').textContent = `${who} · ${checkLabel(check)}`;

  const roll = check.roll ?? {};
  const natural = roll.natural ?? check.natural ?? null;
  const total = roll.total ?? check.total ?? natural;
  const modifier = roll.modifier ?? check.modifier ?? (natural !== null && total !== null ? total - natural : null);

  node.querySelector('.check-formula').textContent = natural === null
    ? `结果 ${total ?? '—'}`
    : `d20 ${natural}${modifier === null || modifier === 0 ? '' : (modifier > 0 ? `+${modifier}` : `${modifier}`)} = ${total}`;

  const dc = node.querySelector('.check-dc');
  const hasDc = Number.isFinite(check.dc) && check.dc > 0;
  dc.textContent = hasDc ? `DC ${check.dc}` : '';
  dc.hidden = !hasDc;

  // DC 来源小标记：规则给的还是 DM 给的（F6 要求可辨认）。
  const source = node.querySelector('.check-source');
  const dcSource = String(check.dcSource ?? '').toLowerCase();
  source.textContent = dcSource === 'rules' ? '规则' : dcSource === 'dm' ? 'DM' : '';
  source.hidden = dcSource !== 'rules' && dcSource !== 'dm';

  const verdict = node.querySelector('.check-verdict');
  // 被动检定不掷骰、也没有 DC：显示"被动值 N"，不是"成功"（那会让人以为掷过骰）。
  verdict.textContent = check.kindType === 'death-save' && check.statusAfter
    ? `死亡豁免 ${check.successes ?? 0}/3 成功 · ${check.failures ?? 0}/3 失败 → ${statusText(check.statusAfter)}`
    : check.passiveValue != null ? `被动值 ${check.passiveValue}`
      : check.success === true ? '成功' : check.success === false ? '失败' : '—';

  // 加值来源（F7-①）：`none` 必须写出来——"无数值表、本次加值 0"和一次正常检定看起来不能一样。
  const modifierNode = node.querySelector('.check-modifier-source');
  const modifierFrom = String(check.modifierSource ?? '').toLowerCase();
  modifierNode.textContent = modifierFrom === 'none' ? '无数值表，本次加值 0'
    : modifierFrom === 'sheet' ? '加值来自角色卡'
      : '';
  modifierNode.hidden = modifierFrom !== 'none' && modifierFrom !== 'sheet';

  node.querySelector('.check-secret').hidden = !secret;
  return node;
}

/** 骰子卡：表达式 + 结果（`dice_roll` 手搓的也照显——战斗内本不该用，但发生了就要看得见）。 */
function rollNode(roll) {
  const node = clone('tpl-roll');
  node.querySelector('.roll-title').textContent = `${actorLabel({}, roll.who)} · 掷骰`;
  node.querySelector('.roll-expression').textContent = roll.expression ?? '';
  node.querySelector('.roll-total').textContent = `共 ${roll.total ?? '—'}`;
  const dc = node.querySelector('.roll-dc');
  dc.textContent = roll.dc ? `DC ${roll.dc}` : '';
  dc.hidden = !roll.dc;
  return node;
}

function checkLabel(check) {
  const kindType = String(check.kindType ?? '').toLowerCase();

  if (kindType === 'death-save') {
    return '死亡豁免';
  }

  if (kindType === 'save') {
    return `豁免 · ${abilityText(check.ability)}`;
  }

  if (check.skill) {
    return `技能 · ${check.skill.split('|')[0]}`;
  }

  return `属性 · ${abilityText(check.ability)}`;
}

function abilityText(ability) {
  const map = { str: '力量', dex: '敏捷', con: '体质', int: '智力', wis: '感知', cha: '魅力' };
  return map[String(ability ?? '').toLowerCase()] ?? (ability ?? '—');
}

function statusText(status) {
  const map = { alive: '存活', dying: '濒死', stable: '已稳定', dead: '死亡' };
  return map[String(status ?? '').toLowerCase()] ?? (status ?? '');
}

function actorLabel(state, id) {
  const found = (state.roleplay?.cast ?? state.participants ?? []).find((actor) => actor.id === id);
  return found?.name ?? id ?? '—';
}

function messageNode(state, message, narrative) {
  const node = clone('tpl-message');

  // 叙事条目自带 name（store 归约时从在场角色里查），会议发言走 authorName。
  node.querySelector('.message-author').textContent =
    (narrative ? message.name : null) ?? authorName(state, message.author);
  node.querySelector('.message-text').textContent = message.text ?? '';

  // 导演自己的旁白/指令：必须和角色正文一眼分得开（淡背景 + 斜体 + 强调色竖线），
  // 否则看起来就像某个角色的台词。
  if (narrative && message.author === 'user') {
    node.classList.add('message--director');
  }

  return node;
}
