# Meeting Room

[![build](https://github.com/Preknight/MeetingRoom-trpg/actions/workflows/build.yml/badge.svg)](https://github.com/Preknight/MeetingRoom-trpg/actions/workflows/build.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

多角色 TRPG 叙事引擎：单进程 **.NET 10** 后端 + 原生 HTML/CSS/JS 前端（**无构建步骤**）。

**你当导演（DM），AI 当说书人和角色。** 说书人负责控场、转写与节奏，角色 Agent 多人群聊推进剧情；
战斗、检定、地图与规则查询由程序结算，模型不能自己编数值。

> 当前状态：可运行的单机原型。产品只提供叙事模式；会议模式的后端端点仍在仓库里，用于兼容旧数据与测试，界面不提供入口。

## 特性

### 叙事

- 用户当**导演**，说书人（narrator）转写与控场，角色 Agent 按在场顺序接话；
- **世界状态只能提议**：`propose_world_state` 先问导演，同意或超时才落盘；
- 场景收束由 `declare_scene_end` 提出建议，导演拍板；
- 新建叙事时可**勾选在场角色**（列表来自工作空间的 `roles/`，一个都不勾 = 全部在场），并单独指定说书人卡；
- **上下文分三层**：世界观 / 情境 / 早期剧情摘要在**前缀**里，只有压缩时才变一次；剧情发言只追加、不滚动；
  「此刻的现场」与剧情进度在每轮的**尾部**——不压缩的轮次前缀缓存能一直命中；
- `recall_story`：角色与说书人都能回忆**落盘记录**（关键词 / 作者 / 类型 / 从第几幕起 / 最近 N 条），一轮一次、只读；
- 上下文超过 80 条（或估算 token 超 8000）时把前半段压成摘要，摘要落在
  `data/stories/<id>/context-summary.json`，重启后仍在。

### 程序化结算

- 检定走 `request_check`（属性 / 技能 / 对抗 / 被动、暗骰、Stakes），骰值一律由程序产生；
- **同一轮只允许一个判定**（引擎强制，第二个起拒绝并点名上一个）——避免一次行动被拆成三次运气；
- 战斗工具面：`start_combat` / `next_turn` / `combat_action` / `end_combat`，另有 `dm_override` 供导演强制改数值；
- HP 的唯一真相在数值表（`SheetStore`），战斗事件只落记录；先攻、命中、伤害、死亡豁免都由引擎掷；
- **开战必须带 HP**：有角色卡的走旁路数值表，没有卡的敌人要在 `start_combat` 里给 `hp / maxHp / ac`；
- **胜负要被校验**：`victory` 要求所有敌方 HP ≤ 0，`defeat` 要求我方全倒，否则拒绝并点名谁还站着；
- `dm_end`（导演收场）要求本轮导演发过话；`dm_override` 不能把 HP 改成 0。

### 地图、战术板与迷雾

- 自带地图数据，不依赖外部 VTT；旧的 Owlbear Rodeo 只读桥保留但默认关闭（`vtt.enabled = false`）；
- 地图编辑器 `/map-editor`（顶栏「地图」新标签打开）：上传底图、两点标定网格、画房间 / 墙 / 门 / 特殊区域、摆 token；
- 迷雾三态（揭示 / 视野 / 遮黑），迷雾改动**手动保存**——它是信息闸门，误操作会直接推到玩家视角；
- 战术板默认**玩家视角**：未揭示的 token 从 DOM 里整个移除，不靠颜色暗示；需要核对全局时切 DM 视角；
- 地图结构进说书人上下文（房间、相对位置、距离、墙体），距离以【地图】为准。

### 规则查询

规则包（`src/MeetingRoom.Host/data/rules/rules-data.json`，schema `meetingroom-rules/4`）随程序加载；
找不到就按"没有规则包"运行，相关工具会**明确回执"规则包未加载"**，不会假装成功。

| 工具 | 谁能用 | 查什么 |
| --- | --- | --- |
| `lookup_rule` | 说书人 **和** 角色 | 法术 / 装备 / 状态 / 动作 / 技能 / 专长 / 职业 / 子职 / 可选特性 |
| `lookup_monster` | **只有说书人** | 怪物模板：CR / 类型 / 体型 / HP / AC / 六属性 / 特性与动作原文 |

- **权限靠工具表结构保证**：`lookup_monster` 不写进角色工具表，不是靠运行时判断"你是不是说书人"；
- 检索顺序（命中即止）：全等（id / 英文名 / 中文名）→ `keywords` 子串 → 指定 `kind` 时搜正文；命中多条列候选；
- 返回：默认紧凑摘要；`detail=true` 只给 1 条并附英文原文，超过 4000 字符截断；
- **一轮最多查 3 次**，且**只读**——查到原文也不能自己结算；
- 路径覆盖：配置 `rules.dataPath` → 命令行 `--rules <path>` → 环境变量 `MEETINGROOM_RULES_DATA`。

## 快速开始

依赖 **.NET 10 SDK**。模型服务默认走 DeepSeek，需要在启动前设置环境变量：

```bash
# Windows（PowerShell）
$env:DEEPSEEK_API_KEY = "sk-..."

dotnet build
dotnet run --project src/MeetingRoom.Host          # 起服务并自动打开浏览器
```

- 默认监听 `http://127.0.0.1:8787`（可在 `meetingroom.json` 的 `server.port` 改）；
- 不想自动开浏览器：加 `--no-browser`；
- 换端口：`dotnet run --project src/MeetingRoom.Host -- --urls http://127.0.0.1:8799`；
- 前端是**无构建**的物理目录：改了 `wwwroot/` 必须重新 `dotnet build`（服务读的是 `bin/.../wwwroot` 的副本），
  然后浏览器强制刷新（`Ctrl+F5`）。`dotnet run --no-build` **不会**刷新副本。

## 工作空间与角色卡

一个工作空间就是一套目录，叙事、角色、地图、图片都放在里面：

```
<workspace>/
  workspace.json
  roles/           角色卡 <id>.md + 旁路数值表 <id>.sheet.json
  data/stories/    叙事会话（story.jsonl 剧情时间线）
  data/maps/       地图文档（<id>.json + 底图）
  data/materials/  素材
  data/drafts/     草稿箱（待整理 / 已归档）
  data/images/     图片库（out/ 生成、refs/ 参考图）
```

角色卡是极简 frontmatter + Markdown 正文：

```markdown
---
id: ella
name: Ella
title: 幸存者
summary: 醒在地窖里的幸存者，只想弄清周围有什么
role: member          # member = 角色；narrator.md 用 host
---

你是 Ella：刚从地窖里醒过来，手里只有一盏快灭的灯。
（正文就是这个人设与写作要求）
```

- **`roles/` 目录即名单**：开会话时目录里每张卡都会成为在场角色（只扫顶层 `*.md`，子目录不扫）；
- `roles/<id>.sheet.json` 是旁路数值表（`currentHp` / `maxHp` / `ac` / 属性 / 技能），战斗与检定从这里取数；
- `narrator.md` 是说书人，**不能删**——缺了它（且没有 `role: host` 的卡）会导致说书人解析为 null、工具面全空；
- 开发期建议只留当前需要的卡，其余移到 `roles/_unused-<时间戳>/` 归档。

字段与校验规则的完整说明见 [`docs/角色卡与数值表模板.md`](docs/角色卡与数值表模板.md)。

## 主界面

顶栏左边是状态（连接 / 当前工作空间 / 进行中或未开始 / 剧情进度），右边是**列开关**与「地图」。

| 列 | 内容 |
| --- | --- |
| 第 1 列 | 工作空间（＋ 新建叙事 / 打开…，下面是会话清单） |
| 第 2 列 | 已归档 · 世界观与场景（下半是「待整理」草稿箱） |
| 第 3 列 | 剧情 |
| 第 3 列下半 | 导演指令 |
| 第 4 列 | 待确认事件 |
| 第 4 列下半 | 导演记录 |
| 第 5 列 | 在场角色 |
| 第 6 列 | 后台记录（思考 / 工具 / 打断，只显示，Agent 读不到） |

战斗进行时右侧会出现**战术板**；非战斗时也可以点顶栏「战术板」手动打开。

一次从开演到战斗的典型流程：

1. 「＋ 新建叙事」→ 选工作空间、填世界观与开场情境、勾选在场角色、指定说书人 → 开始；
2. 在「导演指令」里说话，说书人成文、角色依次接话；
3. 「剧情」里会出现正文、**检定卡**、**骰子卡**、**请求行**（角色提出"想做某件事"的结构化请求，由说书人决定是否真的掷骰）；
4. 说书人要改世界状态、或提议揭示迷雾时，「待确认事件」会出现卡片——「跳过」等于不采纳；
5. 开战后右侧出现战术板，「结束战斗」在顶栏。

## 配置

配置文件 `meetingroom.json` 与 exe 同级（开发期在 `src/MeetingRoom.Host/bin/<Config>/net10.0/`），
**改后重启生效**，不做热重载；非法值会拒绝启动并说明原因。

| 段 | 关键项 | 说明 |
| --- | --- | --- |
| `server` | `port`、`bind`、`heartbeatSeconds` | 监听端口与 SSE 心跳 |
| `provider` | `name`、`baseUrl`、`model`、`apiKeyEnv` | 模型服务；`apiKeyEnv` 指向环境变量名 |
| `limits` | `maxStepsPerTurn`、`maxConcurrentTurns`、`steerMergeWindowMs` | 每轮步数上限、并发闸门、打断合并窗口 |
| `summaries` | `thoughtSummaryMaxChars` 等 | 思考过程摘要的截断与风格（思考全文落盘、默认不进上下文） |
| `context` | `alwaysMaterialMaxCount`、`triggeredTokenBudget`、`triggeredCooldownRounds` | 上下文里材料的常驻与触发预算 |
| `roleplay` | `context.*` | 叙事前台上下文：L2 上限、压缩比例、单条截断 |
| `images` | `apiKeyEnv`、`model`、`outDir` | 图片生成（OpenRouter） |
| `vtt` | `enabled`（默认 `false`） | 旧的 Owlbear Rodeo 只读桥，非必需 |

## 诊断探针

启动参数里带 `--probe-*` 可做离线自检（不监听端口、不起浏览器），用于回归与排查：

| 探针 | 作用 |
| --- | --- |
| `--probe-tools` | 打印工具清单、工具面指纹与系统提示前缀指纹（改提示词 / 工具后必看） |
| `--probe-rules` | 校验规则数据包（5etools 转换产物） |
| `--probe-sheets` | 校验 `roles/*.sheet.json` 的加载与合法性 |
| `--probe-combat` | 跑战斗探针（以 `-<case>.json` 结尾的用例文件） |
| `--probe-combat-guard` | 胜负守卫、`dm_end` 导演发话、`override` 禁改 0、开战 HP 必填 |
| `--probe-check-spam` | 判定防刷（同目标同理由第 2 次 DC+5、第 3 次拒绝）与同轮单判定 |
| `--probe-events` | 事件总线 / SSE 契约 |
| `--probe-launcher` | 全屏启动器的命令行探测 |
| `--probe-map-context` | 地图段进说书人上下文的生成结果 |
| `--probe-chat` / `--probe-image` | 真实模型 / 图片服务的连通性 |

改提示词或工具后，工具面与系统提示前缀指纹都会变——这是一次预期的前缀缓存 miss，需要连同
`Narrator.PromptPrefixVersion` 一起更新。叙事模式的前缀指纹只覆盖**共享节 + 工具签名**，不含逐轮追加的剧情消息。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `src/MeetingRoom.Agent/` | step 循环、provider 接入、打断与思考摘要 |
| `src/MeetingRoom.Core/` | 领域模型、编排、叙事、战斗、规则、地图、工作空间 |
| `src/MeetingRoom.Host/` | Minimal API 端点、SSE、启动与探针 |
| `tests/MeetingRoom.Tests/` | xUnit 测试 |
| `wwwroot/` | 前端（无构建，服务读 exe 同级副本） |
| `docs/` | 设计与规范文档 |
| `tools/` | 规则数据导出脚本 |

## 文档

| 文档 | 用途 |
| --- | --- |
| [`docs/Agent 会议（Meeting Room）设计方案 - .NET 10 版.md`](docs/Agent%20会议（Meeting%20Room）设计方案%20-%20.NET%2010%20版.md) | 架构、领域模型、可中断运行时、SSE 契约 |
| [`docs/叙事模式后台与上下文设计.md`](docs/叙事模式后台与上下文设计.md) | 前台三层上下文、后台观察页、DM 提醒通道 |
| [`docs/战斗托管设计.md`](docs/战斗托管设计.md) | 战斗规则、工具面、胜负守卫 |
| [`docs/地图编辑器设计.md`](docs/地图编辑器设计.md) | 自带地图数据、编辑器与战术板 |
| [`docs/地图视野与特殊区域设计.md`](docs/地图视野与特殊区域设计.md) | 迷雾三态、特殊区域、角色视角、多模态接入 |
| [`docs/规则数据导出方法.md`](docs/规则数据导出方法.md) | 规则包字段契约、重印口径与重新导出步骤 |
| [`docs/角色卡与数值表模板.md`](docs/角色卡与数值表模板.md) | 角色卡与数值表的完整字段、校验规则、示例 |
| [`docs/图片生成模块设计方案.md`](docs/图片生成模块设计方案.md) | 图片生成附加功能 |
| [`docs/前端重设计（含战术板）.md`](docs/前端重设计（含战术板）.md) | 布局与战术板的前端设计 |
| [`docs/模型级看门狗与异步任务设计.md`](docs/模型级看门狗与异步任务设计.md) | 未实施的草案：watchdog 与异步任务骨架 |
| [`AGENTS.md`](AGENTS.md) | 工程约定与硬约束（改前必读） |

## 已知限制

- 前端无类型检查：契约变更时前后端可能脱节，事件表是唯一契约；
- 提示词层的约束（例如"只禁机制性结论"的受伤描写）只有提示词手段，行为验证表明它并不可靠；
- 临时怪用 inline HP 建的最小表会落进会话的 `sheets.json`，目前没有清理机制；
- 两个特殊区域重叠时标签会叠在一起（每个区域各自在圆心附近画名字，没有做避让）；
- 测试覆盖集中在核心引擎（规则、战斗、上下文、工作空间）；UI 与真实 provider 的行为靠 `--probe-*` 探针与手工验证。

## 规则数据与许可

`src/MeetingRoom.Host/data/rules/rules-data.json` 由 [5etools](https://github.com/5etools-mirror-org/5etools-mirror-org.github.io)
数据转换而来，用于本地规则查询。规则文本的版权归原作者所有，使用与再分发前请自行确认 5etools 源数据与相关内容的许可条款；
**MIT 许可证不覆盖这部分数据**。

本项目以 [MIT 许可证](LICENSE)发布。

