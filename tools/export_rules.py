#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
export_rules.py —— 在已有 V3 规则包基础上把「怪物模板」补全、并新增「职业」导出（V4）。

为什么是增量导出：
    V3 包（`rules-data.json`, schema `meetingroom-rules/3`）里 `monsterTemplates`
    只有 11 个元数据字段（id/name/cnName/type/size/alignment/cr/habitat/fluff），
    没有 hp / ac / 六属性 / 特性 / 攻击 —— 因为当初的导出脚本只取了这些。
    怪物模板的用途是「按模板现放怪、现场设数值调控强度」，缺了这些等于空壳。

    而 spells / items / conditions / actions / skills 在 V3 里已经完整，不需要重导。
    所以这里只做两件事：
      1. 用 5etools 原始 bestiary 把 `monsterTemplates` 补全
      2. 从 5etools class/*.json 导出 `classes`（V3 没有）
    其余顶层键**原样搬过来**，避免重导引入新差异。

语言策略（与 V3 一致）：
    * 描述文本一律英文原文 —— 5etools 的中文翻译有不准的地方，原文不易歧义。
    * 中文只用于 cnName + keywords（检索用）。
    * 5etools 的 {@tag} 标记统一清洗成纯文本（`{@hit 4}` → `+4`）。

用法：
    python export_rules.py ^
        --en   "<5etools-en-data>" ^
        --cn   "<5etools-cn-data>" ^
        --base "<previous-rules-data.json>" ^
        --out  "src\\MeetingRoom.Host\\data\\rules"

输出：
    <out>/rules-data.json      完整 V4 包
    <out>/export-report.json   计数与缺口
"""

import argparse
import json
import os
import re
import sys

TAG_RE = re.compile(r"\{@(\w+)(?:\s+([^{}]*))?\}")
HIT_RE = re.compile(r"\{@hit\s+\+?(-?\d+)")
DAMAGE_RE = re.compile(r"\{@damage\s+([^}|]+)")

# 主源集合。V3 的 10 本基础上补 EFA（艾伯伦：工匠熔炉，2024 规则）——
# 否则 Artificer 会出现「职业来自 TCE、子职来自 EFA」的孤儿关系。
DEFAULT_SOURCES = {"PHB", "XPHB", "DMG", "XDMG", "MM", "XMM", "TCE", "XGE", "SKT", "VEoR", "EFA"}

# 2024 规则的书。不靠 "X 前缀" 猜：EFA 不是 X 开头但同样是 2024 规则。
RULESET_2024 = {"XPHB", "XMM", "XDMG", "EFA"}

# 这些不是玩家可用职业，导出会污染检索（Sidekick 的名字是 "Expert Sidekick" 这类，用后缀判断）。
CLASS_SKIP = {"Mystic"}
CLASS_SKIP_SUFFIX = "Sidekick"


# ---------------------------------------------------------------- 文本清洗

def render(text):
    """把 5etools 的 {@tag ...} 压成可读纯文本（与 V3 导出脚本同一套规则）。"""
    if not isinstance(text, str):
        return ""

    def repl(m):
        tag, args = m.group(1), m.group(2) or ""
        parts = [p.strip() for p in args.split("|")]
        if tag in ("h", "atk", "atkr", "comic", "i", "b", "note"):
            if tag == "atkr" and parts and parts[0]:
                return "Melee/Ranged Attack" if parts[0] in ("mw", "rw") else parts[0]
            return ""
        if tag == "hit" and parts and parts[0]:
            return "+" + parts[0].lstrip("+")
        if tag == "scaledamage" and len(parts) >= 3:
            return parts[0]
        return parts[0] if parts and parts[0] else ""

    return TAG_RE.sub(repl, text)


def flat_text(entries):
    """递归拼接 entries / items / entriesHigherLevel，并清洗标记。"""
    out = []

    def walk(node):
        if isinstance(node, str):
            out.append(render(node))
        elif isinstance(node, list):
            for item in node:
                walk(item)
        elif isinstance(node, dict):
            node_type = str(node.get("type") or "")
            if node_type.startswith("ref"):
                # 引用节点（refOptionalfeature / refClassFeature / refSpell …）：
                # 内容不在本节点里，输出可读占位，真正的清单由 references 字段与顶层 optionalFeatures 提供。
                # 键名是去掉 "ref" 前缀并把首字母小写（refOptionalfeature → optionalfeature，refSpell → spell）。
                ref_id = node.get(node_type) or node.get(ref_key(node_type)) or node.get("uid") or ""
                if ref_id:
                    out.append(f"[{ref_id}]")
                return
            if "name" in node and ("entries" in node or "items" in node):
                out.append(render(str(node["name"])))
            for key in ("entries", "items", "entriesHigherLevel", "entry"):
                if key in node:
                    walk(node[key])

    walk(entries)
    return "\n".join(t for t in out if t).strip()


def flat_raw(entries):
    """保留原始标签拼接——只用来从原文里抽攻击加值与伤害骰。"""
    out = []

    def walk(node):
        if isinstance(node, str):
            out.append(node)
        elif isinstance(node, list):
            for item in node:
                walk(item)
        elif isinstance(node, dict):
            for key in ("entries", "items", "entriesHigherLevel", "entry"):
                if key in node:
                    walk(node[key])

    walk(entries)
    return "\n".join(t for t in out if t).strip()


def named_blocks(raw):
    """把 trait / action / reaction / legendary 数组转成 [{name, text}]，顺带抽数值。"""
    if not isinstance(raw, list):
        return []
    out = []
    for item in raw:
        if not isinstance(item, dict):
            continue
        name = render(str(item.get("name") or "")).strip()
        text = flat_text(item.get("entries") or item.get("items") or [])
        if not name and not text:
            continue
        block = {"name": name, "text": text}
        # 攻击加值 / 伤害骰：战斗引擎要用，从原文抽比让模型再读文本稳。
        raws = flat_raw(item.get("entries") or [])
        m = HIT_RE.search(raws)
        if m:
            block["attackBonus"] = int(m.group(1))
        dm = DAMAGE_RE.search(raws)
        if dm:
            expr = dm.group(1).strip()
            if re.search(r"\d*d\d+", expr):
                block["damageDice"] = expr
        out.append(block)
    return out


def load(path):
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


# ---------------------------------------------------------------- 索引

def build_bestiary_index(en_dir):
    """(name, source) -> 原始 monster 条目。"""
    index = {}
    folder = os.path.join(en_dir, "bestiary")
    if not os.path.isdir(folder):
        return index
    for fn in sorted(os.listdir(folder)):
        if not fn.endswith(".json") or fn.startswith(("fluff-", "foundry-")):
            continue
        try:
            data = load(os.path.join(folder, fn))
        except Exception as exc:  # 单个文件坏掉不该让整次导出失败
            print(f"  跳过 {fn}: {exc}", file=sys.stderr)
            continue
        for m in data.get("monster", []) or []:
            name, source = m.get("name"), m.get("source")
            if name and source:
                index[(name, source)] = m
    return index


def build_class_cn_index(cn_dir):
    """英文职业名 / 英文特性名 / 英文子职全名 -> 中文名。"""
    class_cn, feature_cn, subclass_cn = {}, {}, {}
    folder = os.path.join(cn_dir, "class")
    if not os.path.isdir(folder):
        return class_cn, feature_cn, subclass_cn
    for fn in sorted(os.listdir(folder)):
        if not fn.endswith(".json") or fn.startswith(("fluff-", "foundry-")):
            continue
        try:
            data = load(os.path.join(folder, fn))
        except Exception:
            continue
        for c in data.get("class", []) or []:
            eng, cn = c.get("ENG_name") or c.get("name"), c.get("name")
            if eng and cn and eng != cn:
                class_cn[eng] = cn
        for f in data.get("classFeature", []) or []:
            eng, cn = f.get("ENG_name"), f.get("name")
            if eng and cn:
                # 只按特性英文名索引：中文数据里 className 是中文（“法师”），
                # 用它拼 key 对不上英文职业名。“Spellcasting” 这类跨职业重名的中文名一致，覆盖无害。
                feature_cn[eng] = cn
        for s in data.get("subclass", []) or []:
            eng, cn = s.get("ENG_name"), s.get("name")
            # 用「英文全名 + 来源」做 key：中文包的 ENG_name 是 "Way of the Drunken Master"，
            # 而英文包的 shortName 是 "Drunken Master"，两者对不上，只能拿全名匹配。
            if eng and cn:
                subclass_cn[(eng, s.get("source"))] = cn
    return class_cn, feature_cn, subclass_cn


# ---------------------------------------------------------------- 怪物补全

def resolve_copy(monster, index, depth=0):
    """解析 5etools 的 `_copy` 继承。

    原始怪物里有 1144 条用 `_copy`（SKT 的命名 NPC 几乎全是），hp/ac/cr 都在基础条目上。
    实测本项目数据里 `_mod` 为 0 条，所以只做「基础条目递归解析 + 自身字段覆盖」。
    """
    if not isinstance(monster, dict) or depth > 6:
        return monster
    copy = monster.get("_copy")
    if not isinstance(copy, dict):
        return monster
    base = index.get((copy.get("name"), copy.get("source")))
    if base is None:
        return monster
    merged = dict(resolve_copy(base, index, depth + 1))
    for key, value in monster.items():
        if key == "_copy":
            continue
        merged[key] = value
    return merged


def enhance_monster(template, raw):
    """把 11 字段的模板补成可用于放怪的完整模板。"""
    out = dict(template)

    # 模板里 type/size/alignment/cr 为 null 的（多半是 _copy 派生的命名 NPC），从解析后的原始条目补。
    for key in ("alignment", "cr"):
        if out.get(key) in (None, "") and raw.get(key) not in (None, ""):
            out[key] = raw[key]
    if out.get("type") in (None, ""):
        raw_type = raw.get("type")
        out["type"] = raw_type if isinstance(raw_type, str) else (raw_type.get("type") if isinstance(raw_type, dict) else "")
    if out.get("size") in (None, ""):
        raw_size = raw.get("size")
        out["size"] = (raw_size or [None])[0] if isinstance(raw_size, list) else raw_size

    hp = raw.get("hp") or {}
    if isinstance(hp, dict):
        out["hp"] = {"average": hp.get("average"), "formula": hp.get("formula")}

    ac = raw.get("ac")
    if isinstance(ac, list):
        normalized = []
        for item in ac:
            if isinstance(item, dict):
                normalized.append({
                    "value": item.get("ac"),
                    "from": [render(x) for x in (item.get("from") or [])],
                })
            elif isinstance(item, int):
                normalized.append({"value": item, "from": []})
        out["ac"] = normalized
    elif isinstance(ac, int):
        out["ac"] = [{"value": ac, "from": []}]

    if raw.get("speed"):
        out["speed"] = raw["speed"]

    abilities = {k: raw.get(k) for k in ("str", "dex", "con", "int", "wis", "cha") if raw.get(k) is not None}
    if abilities:
        out["abilities"] = abilities

    for key, dest in (
        ("save", "saves"),
        ("skill", "skills"),
        ("resist", "resist"),
        ("immune", "immune"),
        ("vulnerable", "vulnerable"),
        ("conditionImmune", "conditionImmune"),
        ("senses", "senses"),
        ("languages", "languages"),
    ):
        value = raw.get(key)
        if not value:
            continue
        if isinstance(value, dict):
            # save / skill 是字典（{"stealth": "+6"}）；值尽量转成 int，转不了的保留清洗后的文本。
            normalized = {}
            for name, bonus in value.items():
                text = render(str(bonus)).strip()
                normalized[name] = int(text) if re.fullmatch(r"[+-]?\d+", text) else text
            out[dest] = normalized
        elif isinstance(value, list):
            out[dest] = [render(x) for x in value]
        else:
            out[dest] = render(str(value))

    if raw.get("passive") is not None:
        out["passive"] = raw["passive"]

    for key, dest in (
        ("trait", "traits"),
        ("action", "actions"),
        ("bonus", "bonusActions"),
        ("reaction", "reactions"),
        ("legendary", "legendary"),
        ("mythic", "mythic"),
        ("spellcasting", "spellcasting"),
    ):
        blocks = named_blocks(raw.get(key))
        if blocks:
            out[dest] = blocks

    return out


# ---------------------------------------------------------------- 职业

def ruleset_of(source):
    """2024 规则的书按 RULESET_2024 判断；其余按 2014 处理。"""
    return "2024" if source in RULESET_2024 else "2014"


def ref_key(node_type):
    """refOptionalfeature → optionalfeature（去掉 ref 前缀 + 首字母小写）。"""
    stripped = node_type[3:] if node_type.startswith("ref") else node_type
    return stripped[:1].lower() + stripped[1:]


def collect_refs(entries, found=None):
    """递归收集 ref* 引用节点 → [{"type": "refOptionalfeature", "id": "Agonizing Blast|XPHB"}]。"""
    if found is None:
        found = []
    if isinstance(entries, list):
        for item in entries:
            collect_refs(item, found)
    elif isinstance(entries, dict):
        node_type = str(entries.get("type") or "")
        if node_type.startswith("ref"):
            ref_id = entries.get(node_type) or entries.get(ref_key(node_type)) or entries.get("uid")
            if ref_id and not any(r["type"] == node_type and r["id"] == ref_id for r in found):
                found.append({"type": node_type, "id": ref_id})
        for key in ("entries", "items", "entriesHigherLevel", "entry"):
            if key in entries:
                collect_refs(entries[key], found)
    return found


def feature_blocks(feature_list, feature_cn):
    """把 classFeature / subclassFeature 列表整理成 [{level,name,cnName,text}]（同 level+name 去重）。"""
    merged = {}
    for f in feature_list:
        level = f.get("level")
        fname = render(str(f.get("name") or "")).strip()
        if not level or not fname:
            continue
        key = (level, fname)
        if key in merged:
            continue
        text = flat_text(f.get("entries") or [])
        if not text:
            continue
        block = {
            "level": level,
            "name": fname,
            "cnName": feature_cn.get(fname, ""),
            "text": text,
        }
        refs = collect_refs(f.get("entries") or [])
        if refs:
            block["references"] = refs
        merged[key] = block
    return [merged[k] for k in sorted(merged)]


def build_classes(en_dir, cn_dir, problems):
    """导出全部职业版本 + 全部子职。

    为什么保留多版本：5etools 里同一职业常有 2014（PHB）与 2024（XPHB）两版，特性不同；
    且同一子职会同时挂在两个职业版本下（如 XGE 的醉拳既挂 Monk PHB 也挂 Monk XPHB），
    但它自己往往只有 2014 版特性。只留一版会静默丢内容，所以两版都导出、各自带 ruleset，
    由查询方自己选；子职特性匹配不到时回退到 2014 版并标 featuresInheritedFrom。
    """
    class_cn, feature_cn, subclass_cn = build_class_cn_index(cn_dir)
    folder = os.path.join(en_dir, "class")
    if not os.path.isdir(folder):
        problems.append("没有找到 5etools 的 class 目录，职业与子职未导出")
        return [], []

    classes_raw = {}         # (name, source) -> class
    subclasses_raw = {}      # (className, classSource, shortName, source) -> subclass
    class_features = {}      # (className, classSource) -> [feature]
    subclass_features = {}   # (className, classSource, subclassShortName, subclassSource) -> [feature]

    for fn in sorted(os.listdir(folder)):
        if not fn.endswith(".json") or fn.startswith(("fluff-", "foundry-")):
            continue
        try:
            data = load(os.path.join(folder, fn))
        except Exception as exc:
            problems.append(f"职业文件 {fn} 读取失败：{exc}")
            continue

        for c in data.get("class", []) or []:
            name, source = c.get("name"), c.get("source")
            if not name or source not in DEFAULT_SOURCES:
                continue
            if name in CLASS_SKIP or name.endswith(CLASS_SKIP_SUFFIX):
                continue
            classes_raw[(name, source)] = c

        for s in data.get("subclass", []) or []:
            short = s.get("shortName")
            sub_source = s.get("source")
            class_name = s.get("className")
            class_source = s.get("classSource")
            if not (short and sub_source and class_name):
                continue
            if class_name in CLASS_SKIP or class_name.endswith(CLASS_SKIP_SUFFIX):
                continue
            if class_source not in DEFAULT_SOURCES:
                continue
            subclasses_raw[(class_name, class_source, short, sub_source)] = s

        for f in data.get("classFeature", []) or []:
            key = (f.get("className"), f.get("classSource"))
            if key[0] and key[1] in DEFAULT_SOURCES:
                class_features.setdefault(key, []).append(f)

        for f in data.get("subclassFeature", []) or []:
            key = (f.get("className"), f.get("classSource"), f.get("subclassShortName"), f.get("subclassSource"))
            if key[0] and key[2] and key[3]:
                subclass_features.setdefault(key, []).append(f)

    # ---- 职业（每个版本一条）
    out_classes = []
    for (name, source), c in sorted(classes_raw.items()):
        hd = c.get("hd") or {}
        attached = sorted(
            {f"{k[3]}|{k[2]}" for k in subclasses_raw if k[0] == name and k[1] == source}
        )
        entry = {
            "id": f"{name}|{source}",
            "name": name,
            "cnName": class_cn.get(name, ""),
            "keywords": [x for x in (name, class_cn.get(name)) if x],
            "source": source,
            "ruleset": ruleset_of(source),
            "hitDie": hd.get("faces"),
            "hitDieCount": hd.get("number", 1),
            "saves": [x.upper() for x in (c.get("proficiency") or [])],
            "spellcastingAbility": c.get("spellcastingAbility"),
            "subclassTitle": c.get("subclassTitle"),
            "subclasses": attached,
        }

        skills = ((c.get("startingProficiencies") or {}).get("skills") or [])
        choices = []
        for s in skills:
            if isinstance(s, dict) and "choose" in s:
                choose = s["choose"]
                choices.append({
                    "from": [render(x) for x in (choose.get("from") or [])],
                    "count": choose.get("count", 1),
                })
        if choices:
            entry["skillChoices"] = choices

        multiclass = (c.get("multiclassing") or {}).get("requirements") or {}
        if multiclass:
            entry["multiclassRequirement"] = multiclass

        feats = feature_blocks(class_features.get((name, source), []), feature_cn)
        if not feats:
            fallback = [f for (cn, _cs), lst in class_features.items() if cn == name for f in lst]
            feats = feature_blocks(fallback, feature_cn)
            if feats:
                entry["featuresInherited"] = True
        if feats:
            entry["features"] = feats

        out_classes.append(entry)

    # ---- 子职（每个「职业版本 × 子职」一条）
    out_subclasses = []
    for (class_name, class_source, short, sub_source), s in sorted(subclasses_raw.items()):
        feats = feature_blocks(
            subclass_features.get((class_name, class_source, short, sub_source), []),
            feature_cn,
        )
        inherited = None
        if not feats:
            for (cn, cs, sn, ss), lst in subclass_features.items():
                if cn == class_name and sn == short and ss == sub_source and cs != class_source:
                    candidate = feature_blocks(lst, feature_cn)
                    if candidate:
                        feats = candidate
                        inherited = cs
                        break
        display = f"{class_name} - {short}"
        sub_cn = subclass_cn.get((s.get("name"), sub_source), "")
        entry = {
            "id": f"{display}|{sub_source}|{class_source}",
            "name": display,
            "cnName": sub_cn,
            "keywords": [x for x in (short, display, s.get("name"), sub_cn) if x],
            "source": sub_source,
            "className": class_name,
            "classSource": class_source,
            "classRuleset": ruleset_of(class_source),
            "shortName": short,
        }
        if inherited:
            entry["featuresInheritedFrom"] = inherited
        if feats:
            entry["features"] = feats
        out_subclasses.append(entry)

    # 2024 版**完全覆盖** 2014 版（用户 2026-10-29 定的口径）：同名职业只保留规则版本最高的那条。
    # 子职只保留挂在这些职业版本下的；某子职若在保留版本下完全没有挂载，则保留它唯一的旧版并标注。
    best_by_name = {}
    for e in out_classes:
        current = best_by_name.get(e["name"])
        if current is None or (e["ruleset"] == "2024" and current["ruleset"] != "2024"):
            best_by_name[e["name"]] = e
    out_classes = list(best_by_name.values())
    kept_versions = {(e["name"], e["source"]) for e in out_classes}

    kept_subclasses, covered = [], set()
    for s in out_subclasses:
        if (s["className"], s["classSource"]) in kept_versions:
            kept_subclasses.append(s)
            covered.add((s["shortName"], s["source"]))
    for s in out_subclasses:
        key = (s["shortName"], s["source"])
        if key not in covered:
            s["onlyLegacyVersion"] = True
            kept_subclasses.append(s)
            covered.add(key)

    return out_classes, kept_subclasses


# ---------------------------------------------------------------- 专长 / 动作（同样受重印影响）

def reprint_targets(entry):
    """把 reprintedAs 解析成 [(name, source)]。

    5etools 的目标有三种形态：
        法术/物品/专长/动作      'Fireball|XPHB'                          → ('Fireball', 'XPHB')
        子职                    'Shadow|Monk|XPHB|XPHB'                  → ('Shadow', 'XPHB')（首段短名 + 末段源）
        注法（TCE→EFA）         {'uid': 'Boots of ...|EFA', 'tag': 'item'} → 取 uid
    """
    out = []
    for target in entry.get("reprintedAs") or []:
        if isinstance(target, dict):
            target = target.get("uid") or ""
        parts = str(target).split("|")
        if len(parts) >= 2:
            out.append((parts[0], parts[-1]))
    return out


def drop_reprinted(entries, key_of):
    """丢掉「已被本包内更高版本取代」的旧条目；返回 (保留, 丢弃数)。

    与 V3 行为一致：只有当重印目标**确实在包内**时才丢，避免把目标书没导出时误删。
    """
    pool = {key_of(e) for e in entries}
    kept, dropped = [], 0
    for entry in entries:
        if any(target in pool for target in reprint_targets(entry)):
            dropped += 1
        else:
            kept.append(entry)
    return kept, dropped


def build_cn_simple_index(cn_dir, filename, folder_key):
    """按 ENG_name 对齐中文名（feats.json / actions.json 都是单文件）。"""
    index = {}
    path = os.path.join(cn_dir, filename)
    if not os.path.exists(path):
        return index
    try:
        data = load(path)
    except Exception:
        return index
    for item in data.get(folder_key, []) or []:
        eng, cn = item.get("ENG_name"), item.get("name")
        if eng and cn:
            index[eng] = cn
    return index


def build_feats(en_dir, cn_dir, problems):
    """专长：category（O 起源 / G 通用 / FS 战斗风格 / EB 史诗恩惠 / D 龙纹 …）+ 先决条件 + 文本。"""
    path = os.path.join(en_dir, "feats.json")
    if not os.path.exists(path):
        problems.append("没有找到 feats.json，专长未导出")
        return []

    raw = [f for f in load(path).get("feat", []) or [] if f.get("source") in DEFAULT_SOURCES]
    kept, dropped = drop_reprinted(raw, lambda e: (e.get("name"), e.get("source")))
    cn_index = build_cn_simple_index(cn_dir, "feats.json", "feat")

    out = []
    for f in kept:
        name, source = f.get("name"), f.get("source")
        if not name:
            continue
        cn = cn_index.get(name, "")
        same_name = sorted({x.get("source") for x in kept if x.get("name") == name})
        entry = {
            "id": f"{name}|{source}",
            "name": name,
            "cnName": cn,
            "keywords": [x for x in (name, cn) if x],
            "source": source,
            "ruleset": ruleset_of(source),
            "category": f.get("category"),
            "text": flat_text(f.get("entries") or []),
        }
        prereq = f.get("prerequisite")
        if prereq:
            entry["prerequisite"] = [flat_text([p]) if isinstance(p, (dict, list)) else render(str(p)) for p in prereq]
        if len(same_name) > 1:
            entry["sameNameSources"] = same_name
        out.append(entry)

    print(f"  专长：原始 {len(raw)}，重印丢弃 {dropped}，保留 {len(out)}")
    return out


def build_actions(en_dir, cn_dir, problems):
    """动作：V3 里没做重印过滤（48 条里有 19 条被重印），这里统一按重印规则重导。"""
    path = os.path.join(en_dir, "actions.json")
    if not os.path.exists(path):
        problems.append("没有找到 actions.json，动作沿用 V3 结果")
        return None

    raw = [a for a in load(path).get("action", []) or [] if a.get("source") in DEFAULT_SOURCES]
    kept, dropped = drop_reprinted(raw, lambda e: (e.get("name"), e.get("source")))
    cn_index = build_cn_simple_index(cn_dir, "actions.json", "action")

    out = []
    for a in kept:
        name, source = a.get("name"), a.get("source")
        if not name:
            continue
        cn = cn_index.get(name, "")
        out.append({
            "id": f"{name}|{source}",
            "name": name,
            "cnName": cn,
            "keywords": [x for x in (name, cn) if x],
            "source": source,
            "ruleset": ruleset_of(source),
            "time": a.get("time"),
            "text": flat_text(a.get("entries") or []),
        })

    print(f"  动作：原始 {len(raw)}，重印丢弃 {dropped}，保留 {len(out)}")
    return out


# ---------------------------------------------------------------- 可选特性（祈唤 / 注法 / 战技 / 超魔法 / 战斗风格 / 契约恩赐）

# featureType 的含义（5etools 定义）：
#   EI 魔能祈唤（Warlock）· AI 注法（Artificer）· MV:B 战技（Battle Master）
#   MM 超魔法（Sorcerer）· FS:F/P/R/B 战斗风格（Fighter/Paladin/Ranger/Bard）
#   PB 契约恩赐（Warlock）· ED 元素戒律（四象宗）· AS 奥术射击 · RN 符文 · RP 其他
def build_optional_features(en_dir, cn_dir, problems):
    """这些是**职业表单引用、但不内嵌**的数据：不导出就等于职业特性里少了一大块。"""
    path = os.path.join(en_dir, "optionalfeatures.json")
    if not os.path.exists(path):
        problems.append("没有找到 optionalfeatures.json，可选特性未导出")
        return []

    raw = [x for x in load(path).get("optionalfeature", []) or [] if x.get("source") in DEFAULT_SOURCES]
    kept, dropped = drop_reprinted(raw, lambda e: (e.get("name"), e.get("source")))

    cn_index = {}
    cn_path = os.path.join(cn_dir, "optionalfeatures.json")
    if os.path.exists(cn_path):
        for item in load(cn_path).get("optionalfeature", []) or []:
            eng, cn = item.get("ENG_name"), item.get("name")
            if eng and cn:
                cn_index[eng] = cn

    out = []
    for x in kept:
        name, source = x.get("name"), x.get("source")
        if not name:
            continue
        cn = cn_index.get(name, "")
        types = x.get("featureType")
        types = types if isinstance(types, list) else ([types] if types else [])
        entry = {
            "id": f"{name}|{source}",
            "name": name,
            "cnName": cn,
            "keywords": [k for k in (name, cn) if k],
            "source": source,
            "ruleset": ruleset_of(source),
            "featureTypes": types,
            "text": flat_text(x.get("entries") or []),
        }
        prereq = x.get("prerequisite")
        if prereq:
            entry["prerequisite"] = [flat_text([p]) if isinstance(p, (dict, list)) else render(str(p)) for p in prereq]
        out.append(entry)

    print(f"  可选特性：原始 {len(raw)}，重印丢弃 {dropped}，保留 {len(out)}")
    return out


# ---------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--en", required=True, help="5etools-v2.39.0/data")
    ap.add_argument("--cn", required=True, help="5etools-cn-2.0/data")
    ap.add_argument("--base", required=True, help="已有的 rules-data.json（V3）")
    ap.add_argument("--out", required=True, help="输出目录")
    args = ap.parse_args()

    base = load(args.base)
    problems = []
    report = {"schema": "export-report/4", "base": os.path.basename(args.base)}

    # --- 怪物补全
    index = build_bestiary_index(args.en)
    report["bestiaryIndex"] = len(index)

    enhanced, missing = [], []
    for template in base.get("monsterTemplates", []):
        raw = index.get((template.get("name"), template.get("source")))
        if raw is None:
            missing.append(template.get("id"))
            enhanced.append(template)
        else:
            enhanced.append(enhance_monster(template, resolve_copy(raw, index)))

    with_hp = sum(1 for m in enhanced if (m.get("hp") or {}).get("average") is not None)
    with_traits = sum(1 for m in enhanced if m.get("traits"))
    with_actions = sum(1 for m in enhanced if m.get("actions"))
    with_cr = sum(1 for m in enhanced if m.get("cr") not in (None, ""))
    report["monsterTemplates"] = {
        "total": len(enhanced),
        "withHp": with_hp,
        "withCr": with_cr,
        "withTraits": with_traits,
        "withActions": with_actions,
        "missingRaw": len(missing),
    }
    if missing:
        report["missingRaw"] = missing[:20]
        problems.append(f"{len(missing)} 条模板在原始 bestiary 里没找到")

    # --- 职业与子职
    classes, subclasses = build_classes(args.en, args.cn, problems)
    report["classes"] = {
        "total": len(classes),
        "multiVersion": sorted({c["name"] for c in classes if sum(1 for x in classes if x["name"] == c["name"]) > 1}),
        "withFeatures": sum(1 for c in classes if c.get("features")),
        "withCn": sum(1 for c in classes if c.get("cnName")),
        "featureCount": sum(len(c.get("features") or []) for c in classes),
    }
    report["subclasses"] = {
        "total": len(subclasses),
        "withFeatures": sum(1 for s in subclasses if s.get("features")),
        "inherited": sum(1 for s in subclasses if s.get("featuresInheritedFrom")),
        "legacyOnly": sum(1 for s in subclasses if s.get("onlyLegacyVersion")),
        "featureCount": sum(len(s.get("features") or []) for s in subclasses),
    }

    # --- 专长 / 动作（都受重印影响：2024 覆盖 2014，未被覆盖的扩展书版本必须保住）
    feats = build_feats(args.en, args.cn, problems)
    report["feats"] = {
        "total": len(feats),
        "byRuleset": {
            "2014": sum(1 for f in feats if f["ruleset"] == "2014"),
            "2024": sum(1 for f in feats if f["ruleset"] == "2024"),
        },
        "withCn": sum(1 for f in feats if f.get("cnName")),
    }
    optional_features = build_optional_features(args.en, args.cn, problems)
    by_type = {}
    for x in optional_features:
        for t in x["featureTypes"]:
            by_type[t] = by_type.get(t, 0) + 1
    report["optionalFeatures"] = {"total": len(optional_features), "byType": by_type}

    actions = build_actions(args.en, args.cn, problems)
    if actions is None:
        actions = base.get("actions")
    else:
        report["actions"] = {
            "total": len(actions),
            "byRuleset": {
                "2014": sum(1 for x in actions if x["ruleset"] == "2014"),
                "2024": sum(1 for x in actions if x["ruleset"] == "2024"),
            },
        }

    payload = {
        "schema": "meetingroom-rules/4",
        "ruleset": base.get("ruleset"),
        "strategy": base.get("strategy"),
        "sources": base.get("sources"),
        "monsterTemplates": enhanced,
        "classes": classes,
        "subclasses": subclasses,
        "feats": feats,
        "optionalFeatures": optional_features,
        # spells / items / conditions / skills 在 V3 里已按重印规则导出过（2024 覆盖 2014、
        # 未被覆盖的扩展书版本保留），原样搬过来；actions 需要重导是因为 V3 漏了重印过滤。
        "spells": base.get("spells"),
        "items": base.get("items"),
        "conditions": base.get("conditions"),
        "actions": actions,
        "skills": base.get("skills"),
    }

    os.makedirs(args.out, exist_ok=True)
    out_json = os.path.join(args.out, "rules-data.json")
    with open(out_json, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=1)

    report["problems"] = problems
    with open(os.path.join(args.out, "export-report.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)

    print(json.dumps(report, ensure_ascii=False, indent=2))
    print(f"\n输出：{out_json}（{os.path.getsize(out_json) / 1024 / 1024:.2f} MB）")
    return 0 if not problems else 2


if __name__ == "__main__":
    sys.exit(main())
