# Meeting Room · .NET 10 设计方案

> 文档状态：设计稿 v6（.NET 10 版）
> 技术栈：.NET 10 + ASP.NET Core（后端，单进程）+ 原生 HTML/CSS/JS（前端，**无构建步骤**）
> v6 变更：发布/分发方式定稿为 **SelfContained + zip 压缩包**（§10.3）——解压即用、`exe` 与 `wwwroot/`、`data/`、`meetingroom.json` 同级，完全符合原则 1；**7z SFX 自解压与 NativeAOT 一并暂缓**（前者默认"解压到临时目录后运行"，会破坏数据持久性；后者反射受限），理由与将来启用时的验证点都记在 §10.3。
> v5 变更：**§10.2 由实测改写**——在 SelfContained 非单文件形态下，`Environment.ProcessPath`、`AppContext.BaseDirectory`、`AppDomain.BaseDirectory`、`Assembly.Location` 等五个 API 都能定位 exe 同级目录，此前"必须用 `Environment.ProcessPath`"的推断依据（`AppContext.BaseDirectory` 会指向临时解压目录）**对非单文件形态不成立**；推荐值不变，但理由已更正，并新增 §10.2 的复现方式。
> v3 变更：并入第三轮 3 项确认（发布形态两阶段、资料摘要 300 汉字、思考总结 500 汉字），新增 §10.5 配置文件与可调参数，把散落在各节的 N / 预算 / 冷却 / 合并窗口统一参数化；同时解决了上一轮 `PublishSingleFile` 的术语冲突。
> v2 变更：并入首轮 6 项确认（五列、思考落盘策略、主持人 Agent、会中不增减角色、暂不支持异构、摘要由工具给出）；新增 §11 编排策略（会议 + 角色扮演双模式）。

---

## 参考说明

本项目在实现过程中参考了两个公开的开源项目：

- **DeepSeek-Reasonix**（Go）：两级中途注入（`Controller.TrySteer` / `Steer`）与它在 step 边界消费注入的位置（§6.2）。本项目沿用其语义，其余不照搬。
- **dsh-tavern**（Node.js）：世界书与人物卡的**数据结构**（条目字段、格式约束、导入形式，见 §7.2 与 §五）。
  它的一对一对话设计（用户当玩家 ↔ 单个 NPC 逐轮对话，连带重写当前回合、回退、候选切换）**不是**本项目的设计依据——
  Meeting Room 的角色扮演是角色 Agent 多人群聊推进剧情、用户当导演（DM）（§11.2、§11.4-4）。

---

## 〇、TL;DR

**一个单进程 .NET 10 程序，用系统默认浏览器打开；后端并行运行 N 个可随时被打断的与会者 Agent（其中之一是主持人 Agent），用一条 SSE 连接把全部状态推到前端五列界面。**

| 决策点 | 选择 | 理由 |
| --- | --- | --- |
| 后端 | .NET 10 + ASP.NET Core Minimal API | 单进程、自包含发布后目标机器无需装运行时（原则 1） |
| 前端 | 原生 HTML/CSS/JS + ES modules，**无构建步骤** | 不引入 node/npm；dsh-tavern 已验证同等 UI 规模可行 |
| 界面载体 | 系统默认浏览器打开 `http://127.0.0.1:<port>` | 比 WebView2 / Electron 更便携，不多一层运行时 |
| 实时通道 | **单条** SSE（服务端→客户端）+ POST（客户端→服务端） | SSE 是浏览器原生 API；单连接避开 HTTP/1.1 同源 6 连接上限 |
| 持久化 | 程序目录下的 JSON / JSONL 文件 | 便携式；无数据库、无注册表 |
| 打断模型 | 两级：软注入（step 边界）+ 硬打断（CancellationToken），版本号兜底 | 照搬 Reasonix 已实证的语义，避免无谓重烧 token |
| LLM 抽象 | 自建薄封装 + **手写 step 循环** | 保留取消点；自动 function-invoking 封装会把打断点藏起来 |
| 主持 | **主持人 Agent**（与用户分开显示），用户不输入时自主推进会议 | 会议有默认推进力，用户只做否决与补料 |
| 编排 | 内核 + 可插拔编排策略；会议与角色扮演共用内核（§11） | 避免后期接入叙事群聊时重构内核 |
| 发布 | 调试期 `FrameworkDependent`；发布期 `SelfContained` + **zip 压缩包**（§10.3） | 迭代速度与"目标机零依赖"分开取舍 |
| 可调参数 | 全部走 `meetingroom.json`，本期不做设置 UI（§10.5） | 摘要长度等需频繁试调 |

---

## 一、四条设计原则的落地

| 原则 | 落在哪一层 | 具体机制 | 验收点 |
| --- | --- | --- | --- |
| 1 便携式优先 | 部署层 | exe 同级目录承载配置、资料、日志；调试期 `FrameworkDependent`、发布期 `SelfContained` + zip 压缩包；`wwwroot/` 与 exe 同级；不写 `%APPDATA%`、不依赖注册表/数据库 | 把发布目录整个复制到一台干净机器，仍能双击启动并打开界面 |
| 2 全局资料省 token | 上下文层 | 资料卡片只下发**标题 + 摘要 + 路径**，正文由 Agent 用工具按需读取；常驻/触发式二分；共享字节稳定前缀；思考过程不进上下文（§4.4） | 一次典型会议的 input token 中，未被读取的资料正文与思考过程占比为 0（诊断面板可见） |
| 3 读写异步且可打断 | Agent 运行时 | 每会话独立 `CancellationTokenSource` + step 边界 steer 消费点 + 资料版本校验 | 资料被采纳后，仍在思考的与会者要么在本 step 边界收到注入，要么被取消重开，且最终产出必然基于最新版本 |
| 4 用户随时暂停/补料 | 编排层 | 会议状态机（Running / Paused）+ 广播指令通道 + 直连点名指令 | 暂停后不再发起新 turn；补料后所有运行中会话按原则 3 处理 |

---

## 二、技术栈决策记录

### 2.1 "不使用 node/npm" 的确切含义

前端只用浏览器原生能力：ES modules（`<script type="module">`）、`fetch`、`EventSource`、`WebSocket`、`<template>`、CSS Grid。**没有 bundler、没有转译、没有 `node_modules`、没有构建步骤**——改完刷新即可。

### 2.2 先例：dsh-tavern 的前端就是原生 JS

实证（本项目已拉取的两个仓库之一）：

- `dsh-tavern/tavern-plugin/src/client/` 下 **33 个手写 `.js` 文件**，没有 React / Vue / Vite / Webpack；
- 它的"构建"只是 `bin/build-tavern-client.mjs`：扫描 `// @include <相对路径>` 指令把片段拼成单文件，并把 CSS 内联进去，约 40 行；
- 也就是说，**它用 node 唯一在做的事就是一个字符串拼接器**——本方案用 .NET 侧同样能做，甚至不拼（多个 `<script type="module">` 直接加载）。

反面对照：`DeepSeek-Reasonix/desktop/frontend/` 是 TSX + Vite + pnpm + tsx + eslint + Playwright，几十条 test 脚本——正是本方案要避开的负担。而 Reasonix 的 `internal/serve/events_http.go` 用标准库 HTTP 就实现了 `text/event-stream`（SSE），`broadcaster.go` 做事件扇出——**说明实时通道本身不需要 node**。

### 2.3 代价与应对

| 失去 | 影响 | 应对 |
| --- | --- | --- |
| 打包 / TypeScript | 不能写 TS、无 bundler | 原生 ES modules；关键结构用 JSDoc 标注类型；后端 DTO 作单一真源 |
| npm 生态 | 无 React / Vue / Tailwind / monaco / marked / highlight.js | 自己写模板渲染（`<template>` + `cloneNode`）；Markdown 交后端渲染；编辑器先用 `<textarea>` |
| 前端单元测试 / 浏览器自动化 | 没有 vitest / jsdom / Playwright | 用 **Microsoft.Playwright 的 .NET 绑定**（不需要 node），或先靠 API 层测试 |
| HMR / 类型检查 | 改完手动刷新 | 开发期后端对静态文件禁缓存 |

### 2.4 被否掉的备选

| 备选 | 否决理由 |
| --- | --- |
| Blazor Server | 全 C# 不写 JS 是优点，但 SignalR diff 在高频流式更新下要节流、断线后 UI 状态在服务端易丢、且与 NativeAOT/minimal 发布不搭 |
| Blazor WebAssembly | 首屏要下载 .NET wasm 运行时，本地工具不划算 |
| WebView2 / Electron 壳 | 多一层运行时依赖，直接违背原则 1 |
| 原生 JS + 后端 HTTP/SSE | **选定** |

---

## 三、整体架构

```
┌─ MeetingRoom.Host（ASP.NET Core，单进程）───────────────────────────┐
│  Minimal API 端点 · SSE 广播 · 静态资源(wwwroot 同行) · 单实例守卫   │
│  Config        meetingroom.json 的读取 / 校验 / 默认值（§10.5）      │
├─ MeetingRoom.Core ─────────────────────────────────────────────────┤
│  Orchestrator（策略可插拔）  会议状态机、轮次调度、资料广播、打断决策  │
│  Participant               与会者运行时（1 个 = 1 个 AgentSession）  │
│  MaterialStore             manifest + 文件 + 版本号 + 写锁          │
│  ContextComposer           稳定前缀装配（system / tools / 共享节）   │
│  Visibility                资料可见性判定（会议全公开 / 叙事按角色）  │
│  EventBus                  事件扇出 → 单条 SSE                      │
├─ MeetingRoom.Agent ────────────────────────────────────────────────┤
│  AgentSession              step 循环、steer 队列、取消、工具调度      │
│  ChatClientPool            Provider 抽象（OpenAI 兼容 / Anthropic）  │
│  Tools                     read_material / list_materials / ...     │
└────────────────────────────────────────────────────────────────────┘
                                    │
                        data/（exe 同级，可整目录拷走）
```

数据流：

```
与会者产出 ──工具调用──▶ MaterialStore（版本 +1）
                              │
                              ▼
                     EventBus ──▶ 单条 SSE ──▶ 前端 store ──▶ 各面板
                              │
                              ▼
           Orchestrator：对仍在思考的会话执行 软注入 / 硬打断
```

`Orchestrator` 的行为由**编排策略**决定（§11）。会议模式是本稿的实现目标；角色扮演模式共用同一内核。

---

## 四、领域模型

### 4.1 实体

| 实体 | 关键字段 |
| --- | --- |
| Meeting | `id`、`topic`、`status(Running/Paused/Ended)`、`materialVersion`、`participants[]`、`goalState` |
| Participant | `id`、`role(host/member)`、角色卡 id、`AgentSession`、`status` |
| Material | `id`、`title`、`summary`、`summarySource(tool/fellback)`、`path`、`author`、`status`、`basedOnVersion`、审核字段 |
| Thought | 模型直接输出的思考过程：`participantId`、`turnId`、`text`、`at`（**落盘，默认不进上下文**，§4.4） |
| Speech | 讨论区一条消息：`id`、`author`、`kind(speech/decision/system)`、`text`、`mentions[]`、`at` |
| Round | 一次讨论轮次：参与人、起止、触发的资料变更 |

**主持人 Agent 是 `role=host` 的 Participant**：它与其他与会者共用同一套会话运行时，但额外持有"审核资料""点名发言""推进轮次""宣告目标达成"的权限（§6.5）。

### 4.2 资料状态机（六态，两级审批）

初版 UI 列出的六态对应"主持人 Agent 先审、用户终审"的两级流转：

```
draft（未审批，与会者刚提交）
  │ 提交
  ▼
host_review（主持人 Agent 审批）
  ├─ 驳回 ──▶ rejected（打回作者修改）
  └─ 通过
       ▼
   user_review（用户审批）
     ├─ 采纳 ──▶ adopted          ← materialVersion +1，向所有运行中会话广播
     ├─ 打回 ──▶ rejected         ← 可修改后重提，回到 draft
     └─ 搁置 ──▶ parked           ← 保留在列表中，可重新激活，回到 draft

adopted ──（作者提交新版）──▶ 新 draft（旧版留档，不原地覆盖）
```

约束：

- 主持人 Agent 只能"驳回/通过"（不能越过用户直接采纳）；最终采纳权在用户；
- 与会者不能审核他人资料；
- `adopted` 集合 + `materialVersion` 构成"模型可见投影"的版本基准，是原则 3 判定的唯一依据。

### 4.3 与会者状态（供第四列状态灯使用）

`idle` / `queued` / `thinking` / `reading` / `writing` / `waiting_approval` / `interrupted` / `failed`

### 4.4 思考过程的落盘策略（原则 2 的一部分）

**已确认：模型直接输出的思考过程一律落盘，默认不进入上下文；需要时由程序做简化总结（500 汉字以内，参考论文摘要结构）后进入上下文。**

| 层 | 处置 | 理由 |
| --- | --- | --- |
| 前端展示 | 原样显示在第三列（主持人记录）与第五列（各与会者后台记录） | 用户要看到"为什么这么想" |
| 落盘 | `Thought` 记录进 `transcript.jsonl`（append-only） | 可回看、可审计、崩溃后可重建 |
| Agent 上下文 | **默认不注入** | 思考过程体量大且高度冗余，注入会迅速吃掉 token 预算 |
| 例外：简化总结 | 由**程序**（非模型）在该 turn 结束时生成一份有界摘要，随该 turn 的产出一起进私有历史 | 保留"我上一轮为什么这么判断"的连贯性，而不拖入全文 |

**总结模板（参考论文摘要）**：摘要文体按"目的 → 做法 → 结论"三段压缩，正好对应 Agent 一轮的三个必要信息：

| 段 | 取什么 | 上限 |
| --- | --- | --- |
| 目的 | 本轮要解决/回答什么（取自本轮任务与 turn 首句） | — |
| 做法 | 实际做了什么（工具调用的要点、读过的资料 id） | — |
| 结论 | 得出什么判断 / 下一步打算 | — |
| 全文 | — | **500 汉字**（可配 `summaries.thoughtSummaryMaxChars`，§10.5） |

硬约束：

- 这份总结**只进产生它的那个会话的私有历史**，绝不进共享前缀（否则所有会话的缓存前缀都被同一份思考污染）；
- 总结由程序按固定模板生成，不额外调用模型（不增加 token 与延迟）；
- 超限时按"结论优先保留、做法次之、目的最后"的顺序截断，而不是简单截尾；
- 原思考全文永远可从落盘记录取回（供用户查阅，或作为"深度回溯"时的显式注入源）。

> 这条设计与 §7.1 的前缀结构一致：思考属于"私有历史"，不属于"会议公共节"。

---

## 五、角色卡与资料卡（人物卡格式参考 dsh-tavern）

### 5.1 控制面与叙事面分流

dsh-tavern 的人物卡格式笔记（`docs/design/dsh-native-character-card.md`）指出：酒馆卡的根本限制是"把叙事正文同时当作状态协议"，模型一掉格式就互相污染。本项目自己的解法是**控制逻辑分流**——叙事面只生成正文，控制面通过带 Schema 的工具提交结构化结果，由宿主校验后原子写入。

本方案照搬：

- **叙事面**：与会者只负责发言（讨论区正文）；
- **控制面**：待审批资料、状态变更、结论提交，一律走**带 Schema 的工具调用**，不由模型在正文里填格式；
- 程序负责加载、校验、版本与提交；正文永远不被格式错误污染。

> 角色扮演模式（§11）会进一步收紧叙事面：NPC 完全不带工具，连结构化提交的机会都不给。

### 5.2 混合上下文供给（Hybrid Context Supply）

本项目的划分原则是"程序负责高确定性、低成本、不可遗漏的上下文；Agent 负责开放、长尾、难以预判的上下文"。会议场景的划分：

| 供给方式 | 内容 | 为什么 |
| --- | --- | --- |
| 程序 Push | 议题、用户纠偏、常驻资料索引、被 `@` 的上下文 | 不可遗漏、零模型往返、毫秒级、不增加调用 |
| Agent Pull | 长尾资料全文、历史发言检索 | 低频、需要任务理解，预判成本高于按需读取 |

### 5.3 渐进式披露：卡片只给标题 + 摘要 + 链接

这正是初版 UI 第一条的要求：**先搜索关键词，只返回简短命中目录；Agent 选中后再读取完整正文**（渐进式披露，§5.3）。

落地：

- 程序维护**资料索引**（`id / title / summary / path / status / author`），注入上下文时**只给索引**；
- Agent 需要正文时调用 `read_material(id)`，只把该片段拉进当轮上下文；
- 前端点击卡片链接同样走 `GET /api/materials/{id}/content`，正文只在侧栏/弹窗展示，不进入任何 Agent 的上下文。

`summary` 的来源与长度（已确认）：

| 情形 | 摘要来源 | 长度约束 |
| --- | --- | --- |
| 正常提交 | 与会者在 `submit_material(title, summary, ...)` 时给出（结构化提交，§5.1） | 建议不超过 300 汉字，超出由程序截断 |
| 未给摘要 | 程序截取正文**前 300 汉字**兜底 | 标记 `summarySource=fellback`，便于事后发现偷懒的摘要 |

- 300 汉字为**默认值**，可配（`summaries.materialSummaryFallbackChars`，§10.5）；
- **UI 不必显示完整摘要**：卡片按容器宽度截断（CSS `-webkit-line-clamp` 或 `text-overflow: ellipsis`），完整摘要放在 `title` 属性或悬浮/展开查看，避免列表被长文本撑开。

### 5.4 角色卡格式

存放 `<exe目录>/data/roles/<id>.md`（**跨会议复用**，与会议资料物理分离）：

```markdown
---
id: product-manager
name: 产品经理
title: 需求与优先级
summary: 10 年 ToB SaaS 产品经验，关注用户价值与优先级判断
role: member                      # member | host
model: deepseek-chat
effort: high
allowedTools: [read_material, list_materials, recall_speech, submit_material]
readOnly: true
---

（正文 = 背景知识 / 职责 / 行为准则 / 发言风格，跨会议长期有效）
```

- `name` / `title` / `summary` 直接支撑与会者卡片的三个展示字段；
- `role: host` 标记主持人 Agent（同一张卡格式，仅权限不同）；
- `readOnly` 决定是否授予 `submit_material` 之外的写能力；
- 角色差异**只进私有节**（见 §7.1），绝不写进共享 system prompt。

### 5.5 资料卡格式

```
data/meetings/<meetingId>/
├── manifest.json          # 会议状态 + 资料登记 + materialVersion
├── materials/<slug>-<seq>.md
└── transcript.jsonl       # 讨论区消息 + 思考记录（append-only）
```

`manifest.json` 中一条资料记录：

```json
{
  "id": "mat-003",
  "title": "定价建议草案",
  "summary": "建议按席位分层定价，先做企业版试用转化",
  "summarySource": "tool",
  "path": "materials/sales-定价建议-003.md",
  "author": "sales-manager",
  "status": "user_review",
  "basedOnVersion": 2,
  "submittedAt": "2026-09-22T10:30:00Z"
}
```

---

## 六、可中断的 Agent 运行时（本方案核心）

### 6.1 step 循环与三个取消点

```
runTurn():
  basedOnVersion = meeting.materialVersion      // ① 记录基准版本
  for (step = 0; step < maxSteps; step++) {
    drainSteerQueue()                           // ② step 边界消费注入
    if (meeting.materialVersion > basedOnVersion) markStale()
    compose(prefix + privateHistory)            // ③ 装配请求
    stream = chatClient.StreamAsync(..., ct)    // 取消点 A：流式读取中
    if (hasToolCalls) executeTools()            // 取消点 B：每个工具调用前
    else return produceSpeech()                 // ④ 产出正文
  }
```

| 取消点 | 位置 | 为什么必须在这里 |
| --- | --- | --- |
| A | 流式响应读取循环（每个 chunk 检查 `ct`） | 用户点"暂停"或资料变更时，能立刻停止钱在燃烧的请求 |
| B | 每次工具调用之前 | 避免"已决定写文件但资料已过期"的无效写入 |
| C | 等待连接 / 首字节期间 | 慢 provider 下最长的等待段，最容易漏 |

`maxSteps` 与"连续无进展"检测走配置（`limits.maxStepsPerTurn`，§10.5）。

### 6.2 两级打断（照搬 Reasonix 已验证的语义）

Reasonix 的实现可对照（`internal/control/controller.go:2547/2558`、`internal/agent/run_loop.go:192`、`internal/agent/agent.go:782`）：

| 机制 | Reasonix 对应 | 触发 | 生效时机 | token 代价 | 本方案用途 |
| --- | --- | --- | --- | --- | --- |
| **软注入 Steer** | `TrySteer`（返回 bool 做准入）/ `Steer`（不接受则降级为普通回合） | 资料被采纳、用户补充说明 | 下一个 step 边界 | 每次注入**必然造成一次前缀缓存 miss**（Reasonix 原注释即如此写明） | 资料更新通报、"请注意新增资料 X" |
| **硬打断 Cancel** | `Controller.Cancel()` | 用户纠偏、议题变更、暂停 | 立即 | 当前 step 的部分进度作废，上下文需重放 | 立刻停止并按新基准重开 |

选型规则（对应原则 3 的"应该被直接打断，立刻读取最新资料"）：

> **能软注入就软注入；软注入不可用（会话不在运行中、或已到 step 末尾）才硬打断**。硬打断是例外路径，不是默认路径——否则每次资料更新都会让所有与会者白烧一遍当前 step。

连续多次注入会被**合并窗口**（默认 2000ms，`limits.steerMergeWindowMs`）合并为一次，见 §13.1-4。

### 6.3 版本兜底（迟到产出）

```
产出时判定：
  if (basedOnVersion < meeting.materialVersion)
      → 不丢弃产出，先登记为 draft
      → 追加一轮："你思考期间 mat-00X/mat-00Y 已变更为…，请基于最新资料修订"
  else
      → 产出即最新，无需补充
```

Reasonix 有等价机制：`Agent.HasUnappliedSteer()` 报告"turn 结束后仍有未被消费的 steer"，宿主据此**让位**再发起合成续轮（`internal/agent/agent.go` 注释明示该用途）。本方案把它用于"迟到产出"的自动续轮。

三层合起来构成原则 3 的完整闭环：**软注入（实时）+ 硬打断（兜底）+ 版本校验（最终正确性保证）**。

### 6.4 暂停与纠偏（原则 4）

| 操作 | 语义 | 实现 |
| --- | --- | --- |
| 暂停 Pause | 停止发起新 turn；运行中的会话可选"跑完当前 step"（默认）或"立即取消" | 会议状态机 → `Paused`；对每个会话按选项处理 |
| 继续 Resume | 从暂停点恢复调度 | 不重放已完成的 turn |
| 补料 Broadcast | 用户输入内容 → 写入共享资料节（常驻，见 §7.2）→ 对运行中会话执行 §6.2 | `POST /api/meeting/broadcast` |
| 纠偏 Direct | 用户对某个与会者说（含 `@`） | 只对目标会话 `Submit` |
| 自主推进 | 用户不输入时，由**主持人 Agent** 决定下一个动作：点名发言、宣告收敛、推进下一轮、判定目标达成 | 主持人 Agent 自身是一个与会者会话，其产出是"调度决策"事件，而非普通发言（§6.5） |

### 6.5 主持人 Agent 的职责边界

主持人 Agent 与其他与会者**共用同一个 `AgentSession` 实现**，差别只在权限与产出类型：

| 能力 | 说明 |
| --- | --- |
| 审核资料 | 对 `draft` 做"驳回/通过"，通过后进入 `user_review`（§4.2） |
| 点名发言 | 决定下一轮谁发言、是否只让部分人发言 |
| 推进轮次 | 判定"本轮讨论已收敛"，开启下一轮 |
| 宣告达成 | 判定目标达成 → 建议结束会议（`Ended` 需用户确认，或用户预先授权自动结束） |
| **不能**做 | 代替用户采纳资料；改写他人产出；未经授权越过用户结束会议 |

它的思考与决策走**独立显示通道**（第三列下半，见 §9.1），不与其他与会者的后台记录混排。

### 6.6 明确不要做的事

不要用会自动跑完工具循环的封装（如 `FunctionInvokingChatClient` 之类）。它会替你把"模型请求 → 工具执行 → 再请求"整轮跑完，**step 边界被藏起来**，导致 §6.1 的取消点 A/B 与 §6.2 的注入点全部失效。自己写 step 循环，工具执行自己调度。

---

## 七、上下文装配与 Token 优化（原则 2）

### 7.1 前缀结构

```
[system prompt]        共享，字节稳定 ─┐
[tools schema]         共享，字节稳定 ─┴─ 同构 provider 下所有与会者完全一致
[会议公共节]            共享：议题 + 用户纠偏 + 常驻资料索引   ← 唯一的变化点
────────────── 以下为各会话私有 ──────────────
[角色卡节]             各与会者不同（身份/背景/行为准则）
[私有对话历史]          各自不同（含 §4.4 的思考总结，不含思考全文）
[本轮任务指令]          各自不同（始终放末尾）
```

硬约束：

- **角色差异绝不写进 system prompt**，否则每个会话的 system hash 都不同，前缀缓存全 miss；
- **工具集不按角色裁剪**（如需限制能力，用工具内权限校验，而不是换 tools 列表）；
- 资料更新只动"会议公共节"这一处 → 该节之前的缓存仍命中，只有其后失效。

### 7.2 常驻与触发式资料（世界书条目模型参考 dsh-tavern）

dsh-tavern 的世界书实现（`docs/design/worldbook-recall.md`）把条目分成两类：常驻条目"启用后每次请求都提供"，非常驻条目按关键词匹配后注入，并受 **token 预算**与**冷却**约束（候选池超过阈值时先由后台 Agent 筛选）。会议场景的映射：

| 类别 | 内容 | 注入方式 | 可配项（§10.5） |
| --- | --- | --- | --- |
| **常驻（always）** | 议题、用户纠偏、标记为核心的 adopted 资料 | 每次请求都带标题 + 摘要 | `context.alwaysMaterialMaxCount`（默认 12） |
| **触发式（triggered）** | 其余 adopted 资料 | 按当前发言任务/用户输入做关键词或标签匹配，命中才带标题 + 摘要 | — |
| 预算 | 触发式注入总量上限 | 超出时按相关性截断，或先做一次轻量筛选 | `context.triggeredTokenBudget`（默认 2000） |
| 冷却 | 同一资料 N 轮内不重复注入 | 避免同一索引反复占位 | `context.triggeredCooldownRounds`（默认 10） |

### 7.3 工具面（最小集）

| 工具 | 作用 |
| --- | --- |
| `list_materials(filter)` | 列资料索引（可按状态/作者/关键词） |
| `read_material(id)` | 读某份资料正文（渐进式披露的第二步） |
| `recall_speech(query)` | 检索历史发言（长程讨论靠它，而不是靠不断堆历史） |
| `submit_material(title, summary, body-or-path)` | 提交待审批资料（结构化提交，§5.1；`summary` 必填，§5.3） |

### 7.4 缓存命中诊断（让原则 2 可观测）

前端展示每个与会者：当前前缀哈希、命中/未命中 token 数、上次 miss 的原因（`steer` / `资料版本更新` / `角色节变化`）。没有这个面板，"省 token"只是愿望而不是指标。

---

## 八、HTTP + SSE 契约

### 8.1 单连接扇出（硬约束）

浏览器对同源 HTTP/1.1 连接有约 6 条的上限，SSE 是长连接。**因此整个前端只开一条 SSE**（`GET /api/events`），所有与会者的事件都在这一条上按 `type` 区分，绝不"每个与会者一条连接"。Reasonix 的 `broadcaster.go` 同样是集中扇出。

### 8.2 端点

> **2026-09 新增**：工作空间与会话的端点（列表 / 新建 / 打开 / 目录浏览 / 导入）见 **§15.7**。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/events` | **唯一的 SSE 流**（全量事件） |
| GET | `/api/meeting/state` | 全量快照（重连校准用） |
| GET | `/api/config` | 当前生效的可调参数（只读，供诊断面板显示；§10.5） |
| POST | `/api/meeting/start` | 开始会议 `{topic, participantIds[]}`（含主持人） |
| POST | `/api/meeting/pause` \| `/resume` \| `/end` | 会议控制 |
| POST | `/api/meeting/say` | 用户发言 `{text, mentions[]}` |
| POST | `/api/meeting/broadcast` | 补料/纠偏（触发 §6.2） |
| POST | `/api/participants/{id}/nudge` | 点名发言（用户可直接点名，绕过主持人） |
| POST | `/api/materials/{id}/review` | `{decision: approve\|reject\|adopt\|park, note}`（主持人用 approve/reject，用户用 adopt/reject/park） |
| GET | `/api/materials/{id}/content` | 读资料正文（前端点击链接，不进 Agent 上下文） |

### 8.3 事件

| `type` | 载荷要点 | 前端落点 |
| --- | --- | --- |
| `meeting.state` | `status, materialVersion, participants[], materials[]` | 全局（含重连后的全量重建） |
| `participant.status` | `id, status` | 与会者卡片状态灯 |
| `participant.delta` | `id, turnId, text` | 讨论区（流式追加） |
| `participant.thought` | `id, turnId, text` | 第三列（主持人）/ 第五列（各与会者） |
| `tool.call` / `tool.result` | `id, name, args/result` | 第五列 |
| `message.committed` | `id, author, kind, text, mentions` | 讨论区 |
| `host.decision` | `action, target, reason` | 第三列下半（主持人记录） |
| `material.submitted` / `material.reviewed` | 资料卡片完整字段 | 第一列 + 第三列上半（待审批） |
| `interrupt.applied` | `participantId, kind(steer\|cancel), reason` | 第五列 + 状态灯（让用户**看见**打断发生了） |
| `interaction.request` | `id, turnId, questions[], defaultSelection, timeoutSeconds` | 指令区上方卡片式浮层（不遮挡五列） |
| `interaction.resolved` | `id, answers[{questionId, selected[]}], timedOut, skipped, skipReason` | 收起浮层；问答同时进第三列（§14.6） |
| `interaction.skipped` | `round, reason`（主持人 / 导演自主跳过本轮提问，§14.5.1） | 第三列（让"连着跳了三轮没问我"看得见） |
| `meeting.error` | `code, message` | 顶部提示条 |
| `roleplay.state` | 叙事全量快照（设定 / 在场角色 / 世界状态 / 最近 20 幕 / 待决事项） | 全局（重连后重建；与 `meeting.state` 并列首推） |
| `roleplay.world` | 世界状态变更：`version, time, place, present[], applied, confirmed, timedOut, reason`（`applied = false` 表示这次提议被驳回 / 未生效） | 第 1 列（随后的 `roleplay.state` 快照重建现场）；第 3 列留痕 |
| `roleplay.started` | `storyId, worldview, situation, cast[]` | 第 1 列设定卡 + 第 4 列在场角色（切叙事语义） |
| `roleplay.turn` | `storyId, revision, directorInput, lines[]` | 第 2 列剧情流（导演旁白 + 角色正文，按幕追加） |
| `roleplay.ooc` | `storyId, directives[], text` | 第 3 列下半（导演记录）：元指令留痕，不进剧情 |
| `roleplay.line` | `storyId, author, name, text` | 第 5 列（收掉该角色的流式草稿） |
| `roleplay.ended` | `storyId, revision` | 顶栏状态 |
| `roleplay.error` | `code, message` | 顶部提示条 |

### 8.4 断线重连

- 每条事件写 `id: <seq>`（单调递增），浏览器断线后自动带 `Last-Event-ID` 请求头；
- 服务端策略：**不做精确补发**，任何一次重连都先推一个 `meeting.state` 全量快照，再继续增量。因为前端 store 是纯事件归约（§9.3），全量快照即可无歧义重建；
- 心跳：默认每 15 秒发一行注释 `: ping`（`server.heartbeatSeconds`），防止中间层或系统休眠策略掐断空闲连接。

---

## 九、前端（原生 HTML/CSS/JS，无构建）

### 9.1 布局（五列，已确认）

> **2026-09 改版**：最左新增一列「工作空间 / 会话」侧栏，第 5 列收窄（侧栏 + 五列）。
> 完整形态见 **§十五**。

| 列 | 内容 |
| --- | --- |
| 1 | 会议桌：资料卡片列表（标题、摘要、链接、状态） |
| 2 | 讨论区（上 4/5，群聊式，含 `@`）+ 指令区（下 1/5，输入框 + 暂停/继续） |
| 3 | 待审批列表（上，含主持人 Agent 的驳回意见）+ 主持人记录区（下，**主持人 Agent** 的思考与调度决策，仅显示、Agent 不可读） |
| 4 | 与会者列表（名字、简述、当前状态） |
| 5 | 后台记录区（仅显示、Agent 不可读；与第 4 列**逐行对齐**） |

主持人 Agent 与用户在两处分开显示：主持人的思考/决策在第 3 列下半，用户自己的输入在第 2 列下半的指令区。

卡片显示规则：摘要按容器宽度截断（不必显示全文），完整摘要放 `title` 属性或悬浮展开（§5.3）。

```css
.workspace { display: grid; grid-template-columns: 260px minmax(0,1fr) 300px 220px minmax(0,1fr); }
```

### 9.2 模块划分（ES modules，浏览器直接加载）

```
wwwroot/
├── index.html                    # 五列骨架 + 各面板的 <template>
├── app.css                       # 布局 + 卡片 + 状态灯
└── js/
    ├── main.js                   # 入口：建 SSE、装配面板、绑定命令
    ├── stream.js                 # SSE 连接 + 断线重连 + 心跳处理
    ├── api.js                    # POST 命令封装（含启动时的一次性 token）
    ├── store.js                  # 事件归约（apply(state, event)）
    ├── render.js                 # <template> 克隆 + 批量渲染调度
    ├── panel-materials.js        # 第 1 列
    ├── panel-discussion.js       # 第 2 列上（含 @ 提及）
    ├── panel-composer.js         # 第 2 列下（输入、暂停、继续）
    ├── panel-review.js           # 第 3 列上
    ├── panel-host-log.js         # 第 3 列下
    ├── panel-participants.js     # 第 4 列
    └── panel-backstage.js        # 第 5 列（与第 4 列行对齐）
```

### 9.3 无框架的最小 MVU

```js
// store.js —— 纯函数归约，无副作用，可单步重放
export function apply(state, ev) { /* switch (ev.type) { ... } */ }
```

- 面板只订阅自己关心的切片；
- **渲染必须按帧批量**：`requestAnimationFrame` 合并同一帧内的多次 `participant.delta`。逐 token 直接改 DOM 一定卡（Reasonix 前端同样专门做了 `raf-batch` / `stream-delta-batch` 处理，见其 `desktop/frontend/package.json` 的 `test:stream`）；
- 卡片一律用 `<template>` + `cloneNode`，不做字符串拼接整块重绘。

### 9.4 Markdown 与安全

- **后端渲染**：Agent 输出是不可信内容，Markdown → HTML 在后端做（Markdig），并做标签白名单过滤后再下发；
- 前端不解析 Markdown、不引第三方库，只用 `innerHTML` 接收已净化片段。

### 9.5 资源交付

`wwwroot/` 与 exe 同级，**开发期与发布期保持一致**（配合 §10.3 的两阶段发布）：

| 阶段 | wwwroot 位置 | 用户可否改样式 |
| --- | --- | --- |
| 调试期 | 项目/输出目录（`dotnet run` 直接读源目录） | 是 |
| 发布期 | zip 解压出的安装目录，与 exe 同级 | 是 |

不做"把前端资源内嵌进程序集"的模式——便于用户自行调整样式与排查问题。代价是发布物是一整个目录（用 zip 分发，见 §10.3）。

---

## 十、便携式部署（原则 1）

### 10.1 目录布局

> **2026-09 改版**：用户数据不再放安装目录的 `data/`，而是放**工作空间**（用户在 §15.4 选的路径）；
> 安装目录只留程序与 `appdata/`（工作空间清单等程序自身状态）。完整布局见 **§15.8**。

```
<安装目录>/
├── MeetingRoom.exe
├── meetingroom.json          # 全部可调参数（§10.5）
├── data/
│   ├── roles/*.md            # 角色卡（跨会议，含主持人）
│   └── meetings/<id>/{manifest.json, materials/, transcript.jsonl}
├── logs/
└── wwwroot/                  # 前端资源（与 exe 同级）
```

规则：**任何路径都从 exe 所在目录派生**，不使用 `%APPDATA%`、用户主目录、系统临时目录、注册表。

**日志已实现（2026-09，待办 A6）**：`logs/` 就在 **exe 同级**，按天一个文件
（`meetingroom-<yyyy-MM-dd>.log`）、保留最近 14 个、**不提供配置覆盖项**——与原则 1 一致，"整目录拷走就能用"也包含日志。
内容是启动诊断 + 框架的请求行与状态码（含 404 / 500）+ 领域动作（开会 / 开叙事 / 切工作空间 / 导入 / 删除会话），
与写控制台并行；**控制台只给"看着那个窗口的人"看，排错看文件**。

### 10.2 定位"程序自身安装路径"（已实测）

**实测条件**：`dotnet publish -c Release -r win-x64 --self-contained true`（**非单文件**），在产物目录旁另放 `wwwroot/marker.txt` 与 `data/marker.txt`，然后运行 exe。环境：win-x64 / .NET 10.0.12 / SDK 10.0.401。

| API | 实测返回值 | 定位 exe 同级 `wwwroot/`、`data/` |
| --- | --- | --- |
| `Environment.ProcessPath` | `…\publish\MeetingRoom.exe` | ✅ OK |
| `AppContext.BaseDirectory` | `…\publish\` | ✅ OK |
| `AppDomain.CurrentDomain.BaseDirectory` | `…\publish\` | ✅ OK |
| `Assembly.GetEntryAssembly().Location` | `…\publish\MeetingRoom.dll` | ✅ OK（取目录后） |
| `Assembly.GetExecutingAssembly().Location` | `…\publish\MeetingRoom.dll` | ✅ OK（取目录后） |
| `Environment.CurrentDirectory` | `…\publish` | ⚠️ 本次 OK，但**不可采信**（见下） |

结论与选型：

1. **在本方案的发布形态（SelfContained 目录 + zip 分发，§10.3）下，`AppContext.BaseDirectory` 就是 exe 所在目录，直接可用**。此前"必须用 `Environment.ProcessPath`，因为 `AppContext.BaseDirectory` 会指向临时解压目录"的说法，**对非单文件形态并不成立**（那是单文件形态的行为）。
2. **仍然统一推荐** `Path.GetDirectoryName(Environment.ProcessPath)`：它在非单文件与单文件两种形态下**都**指向真实 exe 位置；而 `AppContext.BaseDirectory` 在单文件形态下会指向运行时解压的临时目录（该行为据 .NET 官方文档，**尚未实测**，见 §13.3）。
3. **`Environment.CurrentDirectory` 不可用**：它取决于进程的启动工作目录（从别处启动、或快捷方式的工作目录不同即失效）。本次恰好等于 exe 目录属于巧合，不能作为定位手段。
4. `Assembly.Location` 在非单文件下返回 dll 路径（取目录后正确），但在单文件下返回空字符串（据官方文档），因此同样不作为首选。

```csharp
// 两种发布形态下都安全，作为唯一的路径基准
var installDir = Path.GetDirectoryName(Environment.ProcessPath)!;
```

原则 1（"所有运行依赖文件、目录都尽量使用程序自身安装路径"）就落在这一行上：`wwwroot/`、`data/`、`meetingroom.json`、`logs/` 全部由它派生。

**复现方式**（任何装了 .NET SDK 的机器，约 1 分钟 + 一次自包含发布）：

```bash
dotnet new console -o pathprobe
# 在 Program.cs 里打印上表 6 个 API；对每个 API 取目录后探测同级 wwwroot/marker.txt 与 data/marker.txt
dotnet publish pathprobe -c Release -r win-x64 --self-contained true -o publish
mkdir -p publish/wwwroot publish/data && touch publish/wwwroot/marker.txt publish/data/marker.txt
./publish/MeetingRoom.exe     # 期望：除 CurrentDirectory 外全部 OK
```

### 10.3 发布形态（已确认）

| 阶段 | 形态 | 要点 | 目的 |
| --- | --- | --- | --- |
| **调试/日常开发** | `FrameworkDependent`（依赖目标机已安装 .NET 10 runtime） | `dotnet run`，或 `dotnet build` 后直接跑产物；不打包 | 迭代快、产物小，不用每次等自包含发布 |
| **正式发布** | `SelfContained`（内含运行时）+ **zip 压缩包** | `dotnet publish -c Release -r win-x64 --self-contained true -o publish`，再把 `publish/` 整个压成 zip | 目标机无需安装 .NET；单一分发包；解压后 `exe` 与 `wwwroot/`、`data/` 同级 |

打包命令：

```bat
dotnet publish src\MeetingRoom.Host -c Release -r win-x64 --self-contained true -o publish

:: 二选一
powershell -NoProfile -Command "Compress-Archive -Path .\publish\* -DestinationPath .\MeetingRoom-win-x64.zip -Force"
"%ProgramFiles%\7-Zip\7z.exe" a -tzip .\MeetingRoom-win-x64.zip .\publish\*
```

**为什么 zip 是最合适的当前选择**

| 维度 | zip 解压 | 自解压 SFX |
| --- | --- | --- |
| 解压后目录结构 | 普通目录，`exe` 与 `wwwroot/`/`data/`/配置同级 | 取决于配置块；默认会解到临时目录 |
| 数据持久性 | ✅ 落在用户选定目录 | ⚠️ 默认落临时目录，`data/` 会丢（违背原则 1） |
| 改样式 / 排错 | ✅ 直接改 `wwwroot/` | 取决于是否内嵌 |
| 分发体验 | 需用户右键解压 | 双击即装 |
| 实现成本 | 一条命令 | 需验证配置块语法与行为 |

**暂缓项（不是否决，是留到以后）**

- **7z SFX 自解压**：需要 7-Zip 的 `7z.sfx` / `7zCon.sfx`。暂缓的原因只有一个但很硬：它默认"解压到临时目录后运行配置的程序"，会让 `data/`、`meetingroom.json`、`logs/` 落在临时位置，**直接违背原则 1**。将来若要用，必须先做一件事——生成一次真实包，验证 `;!@Install@!` 配置块能否把内容落到用户选定的安装目录（或干脆不配置自动运行，只做解压）。
- **NativeAOT**：反射受限，JSON 需改用 `System.Text.Json` source generator，且部分库不兼容；当前 zip + SelfContained 已满足"目标机零依赖"，收益不值得现在付这个代价。

### 10.4 本地服务的基线防护

- 只绑 `127.0.0.1`，绝不绑 `0.0.0.0`；
- 启动时生成一次性 token，前端从 URL 取出后放进请求头，防同机其他程序跨源调用；
- 校验 `Origin` / `Host`（Reasonix 的 `internal/serve/hostguard.go`、`csrf_test.go` 说明本地服务必须有这一层）。

### 10.5 配置文件与可调参数

**已确认：摘要长度等可调参数一律走配置文件，不在代码里写死；本期不做设置 UI。**

`meetingroom.json`（与 exe 同级，随发布物一起交付）：

```json
{
  "server": { "port": 8787, "bind": "127.0.0.1", "heartbeatSeconds": 15 },
  "provider": {
    "name": "deepseek",
    "baseUrl": "https://api.deepseek.com",
    "model": "deepseek-chat",
    "apiKeyEnv": "DEEPSEEK_API_KEY"
  },
  "limits": {
    "maxStepsPerTurn": 12,
    "maxConcurrentTurns": 4,
    "steerMergeWindowMs": 2000
  },
  "summaries": {
    "materialSummaryFallbackChars": 300,
    "thoughtSummaryMaxChars": 500,
    "thoughtSummaryStyle": "paper-abstract"
  },
  "context": {
    "alwaysMaterialMaxCount": 12,
    "triggeredTokenBudget": 2000,
    "triggeredCooldownRounds": 10
  }
}
```

| 参数 | 默认 | 作用 | 关联 |
| --- | --- | --- | --- |
| `summaries.materialSummaryFallbackChars` | 300 汉字 | 资料卡缺省摘要的程序截取上限 | §5.3 |
| `summaries.thoughtSummaryMaxChars` | 500 汉字 | 思考总结上限（进私有历史） | §4.4 |
| `summaries.thoughtSummaryStyle` | `paper-abstract` | 总结结构模板（目的/做法/结论） | §4.4 |
| `limits.maxStepsPerTurn` | 12 | 单轮最大 step 数，防弱模型空转 | §6.1、§13.1-6 |
| `limits.maxConcurrentTurns` | 4 | 同时运行的会话上限（成本与限流） | §13.1-3 |
| `limits.steerMergeWindowMs` | 2000 | 打断风暴合并窗口 | §13.1-4 |
| `context.alwaysMaterialMaxCount` | 12 | 常驻资料条数上限 | §7.2 |
| `context.triggeredTokenBudget` | 2000 | 触发式资料注入预算 | §7.2 |
| `context.triggeredCooldownRounds` | 10 | 同一资料冷却轮数 | §7.2 |
| `server.heartbeatSeconds` | 15 | SSE 心跳间隔 | §8.4 |

配置规则：

1. **改后重启生效**，不做热重载——运行中改配置会让同一会议的上下文语义漂移，破坏前缀缓存与版本判定的可解释性；
2. 缺项用内置默认值补齐，**未知键忽略并记一条日志**（便于旧配置在升级后仍可用）；
3. **非法值直接拒绝启动并说明原因**，不静默回退到默认——否则用户以为改动生效了；
4. 密钥不写进配置文件，只写环境变量名（`apiKeyEnv`）；
5. 前端可通过 `GET /api/config` 读取生效值用于诊断展示（只读，不提供修改入口）。

---

## 十一、编排策略：会议与角色扮演双模式

> 本节回答"开发完成后要让本工具也能用于 tavern 式的多角色扮演（群聊），还需补充哪些设计点"。核心结论：**内核可以共用，但现在必须把五处抽象预留出来，否则后期要重构内核。**

### 11.1 为什么现在就要抽象

目标形态是同一个工具既能"开会"（与会者产出可审批的结论），也能"跑叙事群聊"（多个 NPC 与导演对话）。这两者的**内核需求一致**：

| 共用内核 | 说明 |
| --- | --- |
| Agent 运行时 | 可中断 step 循环、steer 注入、取消、工具调度、思考落盘（§4.4、§6） |
| 会话装配 | 共享稳定前缀 + 私有节（§7.1） |
| 传输与前端 | 单条 SSE + 五列布局 + 最小 MVU（§8、§9） |
| 资源与卡片 | 角色卡（§5.4）、资料库与渐进式披露（§5.3）、可见性判定 |
| 编排外壳 | 会议状态机、暂停/纠偏、事件总线 |

**不同的只是"编排策略"**：本方案把"发言调度 / 可见性过滤 / 提交边界"抽成一层可替换的策略，两种模式共用同一个内核，而不是做成两个独立运行时：

```
同一内核（AgentSession / SessionCore 编排机制 / SSE / 五列 UI / 角色卡 / 可中断运行时）
        ├── MeetingSession + MeetingStrategy（会议）
        └── RoleplaySession + RoleplayStrategy（角色扮演）

> 落地情况：编排**机制**（状态机、后台任务生命周期、暂停门、状态事件与快照骨架）抽在
> `Core/Orchestration/SessionCore.cs`；两个外壳只保留各自的领域语义（权威状态、控制面工具、
> 上下文装配、快照载荷与事件前缀）。会议外壳用 `MeetingSession` 也持有会议特有的打断泵与轮次控制。
```

#### 11.1.1 三个主体的术语（叙事模式，别混）

| 主体 | 叫法 | 是什么 | 在哪露出 |
| --- | --- | --- | --- |
| **人** | **导演 / DM** | 你。给设定、下指令、拍板一切剧情决策 | 第 1 列「我的身份」、第 2 列「导演指令」、第 3 列「导演记录」、剧情流里署名「导演」的旁白、事件与快照字段 `directorInput` |
| Agent | **说书人**（`Narrator`） | 那张 `role: host` 的角色卡。**只做**：把你的话转写落盘、理解你的意图、控场发言顺序、描写环境；**不做**：剧情决策 | 第 4 列标签「说书人」；导演记录里它的动作（开场 / 点名 / 提议）；字段 `NarratorId` |
| Agent | **角色** | 各 NPC。**剧情由它们推进** | 第 4 列、第 2 列正文 |

> **剧情决策归你（导演 / DM）**：世界状态、时空关系、收束这类变更，说书人**不得自己落盘**——
> 它只能（a）执行你在指令里表达的状态意图，或（b）**提议**并等你确认（见 §11.4 第 6 项）。
> 「导演」这个词**只指你**；那个 Agent 一律叫说书人。

### 11.2 两种策略的差异

| 可变点 | 会议模式 | 角色扮演模式 |
| --- | --- | --- |
| 发言调度 | 全员并行，各自独立 turn | **时序**：判定下一个发言者，后者能看到前者的发言（§11.4-1） |
| 权威状态 | 资料状态机 + `materialVersion` | 剧情时间线：revision（只增不减）+ JSONL 落盘审计 |
| 提交边界 | 资料被采纳 | 一轮导演指令 + 正文（前台回合） |
| 可见性 | adopted 资料全员公开 | **按角色过滤**：谁知道什么，互相保密 |
| 输出形态 | 允许结构化提交（工具） | 纯叙事正文，无元话语、无工具 |
| 打断默认 | 资料更新即打断（原则 3 是特性） | 不因状态更新打断；只在用户显式操作时硬打断 |
| 用户角色 | 主持人（甲方） | **导演 / DM**（剧情由角色 Agent 推进，用户负责控场与纠偏） |
| 回退 | 不需要 | 不做：用户是导演，剧情跑偏由下一条导演指令纠正，不回滚正文版本 |
| 推进力 | 主持人 Agent 自主推进 | **说书人** Agent 控场（发言顺序、转写你的意图、描写环境）；剧情决策（世界状态 / 时空 / 收束）由**你**拍板 |
| 长程 | 单次会议，短时 | 几百轮，需要分层摘要与检索 |

### 11.3 现在必须预留的抽象（最小集，落在阶段 2–4）

这五项**现在做成本极低，后期做要重写内核**：

1. **策略接口** `IOrchestrationStrategy`：把"发言调度 / 可见性过滤 / 提交边界"三件事抽成接口。初期只写接口 + `MeetingStrategy` 一个实现，**不做多余抽象**。
2. **可见性过滤点**：所有资料注入统一走 `visibleTo(participant, material)` 判定。会议模式恒返回 true（零成本），角色扮演模式按标签过滤。**绝不要把"全员可见"写死在装配逻辑里**——这是最难事后改的一处。
3. **领域语言隔离（terminology pack）**：共享前缀里的节标题与措辞**不能写死"会议/议题/资料/审批"**。用一份词表注入：会议模式用会议词，叙事模式用叙事词。否则模型会把元概念带进叙事（NPC 说出"根据会议资料，我认为…"这类出戏台词）。这条同时影响 §5.4 角色卡与 §7.1 前缀装配。
4. **提交的原子性与失败隔离**：状态结算失败**不得撤销正文**——正文一旦产生就成立，后续结算失败既不能撤销它、也不能阻止下一轮。会议里"提交资料失败"只影响资料；角色扮演里"状态结算失败"只影响状态。这条语义现在就要写进 `MaterialStore` 的提交路径。
5. **事件按域分区**：事件 `type` 从一开始就带命名空间（`meeting.*` / `host.*` / `roleplay.*`），前端只订阅自己认识的类型。否则后期加模式要动事件总线与全部面板。

**角色增减的时机（已确认）**：会议进行中不允许加入/移除角色。只有在**所有会话运行结束**——主持人 Agent 判定目标达成，或用户手动停止——之后才允许增减与会者。这条同样适用于角色扮演模式（在场角色集合在一场戏内固定）。

### 11.4 角色扮演模式需要新增的组件（开发完成后做）

按优先级排列。前 3 项是"不做就不成立"的，其余是体验必需：

| # | 组件 | 为什么必须 | 可参考 |
| --- | --- | --- | --- |
| 1 | **发言调度器 TurnScheduler** | 会议是"全员并行"，叙事是"时序对话"。若所有 NPC 并行生成，它们互相看不到对方刚说的话，会出现各说各的、重复回答同一句。需要：下一个发言者判定（用户 `@` / 导演指定 / 角色自荐"我要插话"）、顺序提交、必要时允许同时发言（嘈杂场景）。一轮里**默认让所有在场角色都表态**（上限 = 在场角色数，导演不计入），只有确实要压场面时才收紧 | — |
| 2 | **剧情时间线 StoryTimeline** | 叙事的权威状态：revision **只增不减** + JSONL 落盘、可重建（审计与追溯）。**不做**回退 / 分支 / 存档点（理由见第 4 项） | — |
| 3 | **可见性与知识披露** | 一个角色的秘密不能让其他角色看到；不同角色在场/不在场时知道的信息不同。这是会议模式**完全缺失**的一块（会议只有"公开/未采纳"） | — |
| 4 | ~~回退 / 重生成 / 多候选~~ **不做** | 原来的写法（"保留多个候选版本供切换"）来自"用户=玩家"的模型；本项目的角色扮演里用户是**导演（DM）**、剧情由角色 Agent 推进，跑偏时用下一条导演指令纠正，不需要回滚正文版本。dsh-tavern 的 `docs/design/last-round-replacement.md` 本身也明确"不保存多个正文版本、不提供左右切换" | — |
| 5 | **说书人 Agent 的叙事化** | 会议里主持人推进议程；叙事里**说书人**负责：转写你的指令与旁白、理解你的意图、控场发言顺序、描写环境，你没给指令时按已定设定推进一拍。**剧情决策不由它做** | §6.5 的权限模型 |
| 5b | **说书人的取法** | 按 id 认：`NarratorId`（默认 `narrator`）→ 否则 `role: host` 的卡 → 否则注册顺序第一个在场角色。端点可传 `narratorId`；测试数据里附了说书人卡（`TestData/Roleplay/roles/narrator.md`）。工具集按 id 判定谁带（`ParticipantRegistryOptions.CarriesHostTools`） | — |
| 6 | **世界状态结算** | 时间、地点、在场人物、人物状态（好感/伤病/持有物/已知信息）。由导演或记录角色用结构化工具提交，程序校验 | §5.1 控制面分流 |
| 6b | **谁有权改世界状态（两条路径）** | **用户拍板**：① 你在指令里表达了状态意图（"（天亮了，我们到码头）"）→ 说书人**解析你的意图**并落盘；② 说书人自己觉得该变 → 只**提议**，进第 3 列「待确认事件」等你确认，确认才 `Apply` 并落盘。`WorldStateStore` 已落地并有单测（初始登记 + 只读快照 + 校验失败不撤销正文） | — |
| 7 | **长程记忆与压缩** | 会议是短时的一次性讨论；叙事是几百轮。需要分层摘要 + **按角色视角**的检索（NPC 只能回忆自己经历过或被告知的事） | §5.3 渐进式披露 |
| 7b | **（现状）** | `StoryMemory` 已落地并有单测：当前只按幕 `Record`，`RecallFor` 的按角色视角注入**尚未接** | — |
| 8 | **开场与开局** | 设定由用户给（与会议里"用户给议题"对称），**开场由导演直接写**；不做开局预览与重掷 | — |
| 9 | **元指令（OOC）通道** | 导演要能用括号说"（让 A 出场）""（跳过这段）"。这类指令是元指令，**不进剧情时间线**，也不被角色当成台词 | — |
| 10 | **叙事输出约束与净化** | 无元话语、无格式标记、不重复他人台词、避免所有角色同一种语气（"AI 味"）。可能需要一层轻量输出净化（规则层面剥离元话语与残留标记） | — |

### 11.5 与会议模式相反的两条默认（重要）

1. **打断策略反转**：会议里"资料更新 → 打断思考中的与会者"（原则 3）是**特性**；叙事里这是**破坏性的**——不能因为世界状态变了就把 NPC 说到一半的话掐掉。角色扮演模式下默认只做软注入，硬打断仅由用户显式操作（停止 / 结束本场）触发。
2. **工具集按模式统一，而不是按角色裁剪**：会议模式全员带资料工具；角色扮演模式 NPC **不带工具**（工具调用会产生元话语、破坏沉浸）。这不违反 §7.1 的缓存约束——同模式下所有会话的工具集仍然一致，只是两种模式的 tools schema 不同（各自内部字节稳定）。叙事模式需要的场景资料改为"常驻节 Push"注入（§7.2）。

### 11.6 双模式对 UI 的影响

五列骨架不变，列内语义按模式切换（同一套面板，数据源不同）：

| 列 | 会议模式 | 角色扮演模式 |
| --- | --- | --- |
| 1 | 会议桌（资料卡片） | 世界观 / 场景资料卡（常驻 + 触发式） |
| 2 | 讨论区 + 指令区 | 剧情对话流 + 导演指令（含不进剧情的元指令）；导演旁白以样式与角色正文区分（署名「导演」+ 淡背景斜体） |
| 3 | 待审批 + 主持人记录 | 待确认事件（说书人的**提议**、或 §十四 的提问）+ 导演记录（**你的指令与说书人的动作，两类留痕**） |
| 4 | 与会者列表 | 在场角色列表（含状态） |
| 5 | 后台记录 | 后台记录（各角色生成过程，逐行对齐） |

### 11.7 建议的接入顺序

不要等会议模式全部完成再考虑叙事模式：

1. 阶段 2–4 落地时，顺手实现 §11.3 的五项抽象（成本极低）；
2. 会议模式跑通后，先做 §11.4 的第 1–3 项（调度器 / 剧情时间线 / 可见性）——它们决定叙事模式是否成立；
3. 第 4–10 项按需推进，其中"长程记忆"与"输出净化"最容易在真实长局中暴露问题。

---

## 十二、实现阶段

> 本表只给范围与验收要点。

| 阶段 | 内容 | 交付物 / 验收 |
| --- | --- | --- |
| 0 | 项目骨架（调试期 `FrameworkDependent`，`dotnet run` 一键起）+ `meetingroom.json` 加载与校验（§10.5）+ `wwwroot`/`data` 与 exe 同行定位 + 自动打开默认浏览器 | 调试期一键启动；路径定位已实测（§10.2），阶段 0 只做回归；**SelfContained + zip 打包脚本跑通，并在干净目录解压验证**（§10.3） |
| 1 | EventBus + 单条 SSE + 五列前端骨架 + 假事件流 | 五列界面能按假事件实时更新（验证前端形态） |
| 2 | `AgentSession` step 循环 + provider 接入 + 三个取消点 + **§11.3 的策略接口与可见性过滤点** | 单个与会者能被取消并重开（先用 CLI 验证） |
| 3 | 多与会者并行 + 状态/思考/工具事件 + **思考落盘与 500 汉字有界总结（§4.4）** | 第四、五列能实时反映各自状态且行对齐；思考不进上下文可在诊断中看到 |
| 4 | `MaterialStore` + 工具面 + 六态两级状态机 + **提交失败隔离（§11.3-4）** + 摘要兜底截取（§5.3） | 会议桌与待审批列完整可用 |
| 5 | 主持人 Agent（审核 + 点名 + 推进 + 宣告达成）+ 软注入 + 硬打断 + 版本兜底 + 暂停/纠偏 + 合并窗口 | 跑通"快产出 → 采纳 → 慢者感知 → 最终产出基于最新版"（验证原则 3、4） |
| 6 | 共享前缀 + 常驻/触发式资料 + 缓存诊断面板 | 缓存命中率可观测（验证原则 2） |
| 7（后续） | 编排策略抽象收尾 + 角色扮演模式（§11.4 第 1–3 项先行） | 同一内核承载叙事群聊 |
| 8（后续） | §11.4 第 5–10 项（导演叙事化、世界状态、长程记忆、开场、OOC、输出净化） | 叙事模式可用于真实长局 |

---

## 十三、风险与已确认的决策

### 13.1 风险

1. **发布形态的路径陷阱（已降级）**：§10.2 已实测——当前 zip + SelfContained 形态下五个 API 都可用，无实际风险。需要留意的是两处**将来**才会触发的情况：① 若改用**单文件**（`PublishSingleFile`），`AppContext.BaseDirectory` 会指向解压临时目录、`Assembly.Location` 返回空串，必须切到 `Environment.ProcessPath`；② 若启用 **7z SFX**，其默认"解压到临时目录后运行"会让 `data/` 失去持久性（§10.3 已记为暂缓项）。
2. **SSE 连接与心跳**：必须单连接；代理/休眠会掐断空闲连接，心跳不可省。
3. **并发成本**：每个与会者是独立会话，同时请求意味着多份 token 与限流风险，必须有并发上限与排队（`limits.maxConcurrentTurns`）。
4. **打断风暴**：资料连续多次更新会对同一会话反复注入/取消。需要**合并窗口**（`limits.steerMergeWindowMs`，默认 2 秒），否则 token 全烧在重开上。
5. **前端无类型检查**：靠后端 DTO 单一真源 + 事件表（§8.3）作为契约，契约变更时同步改前端。
6. **工具循环终止**：必须有 `maxSteps` 与"连续无进展"检测，否则弱模型可能空转。
7. **配置错误静默生效**：§10.5 要求非法值拒绝启动。若实现成静默回退默认，用户会误判"改了没用"或"改了生效了"。
8. **双模式抽象不预留就要重构**：§11.3 的五项（尤其"可见性过滤点"与"领域语言隔离"）若被硬编码进装配逻辑，后期接入叙事模式时改动面会扩散到 `ContextComposer`、事件总线与全部面板。
9. **领域语言泄漏**：若共享前缀写死"会议/资料/审批"等元概念词，叙事模式下角色会把它们说出口，直接破坏沉浸。
10. **长程一致性**（叙事模式）：多角色长时间对话最容易出现人设漂移、忘记前文、角色间信息串味（B 知道了只有 A 该知道的事）。

### 13.2 已确认的决策

| # | 议题 | 结论 |
| --- | --- | --- |
| 1 | 列数 | **五列**确认（原稿"第三列"重复为笔误） |
| 2 | "仅显示，不允许读取" | 模型直接输出的思考过程**落盘**，默认**不进上下文**；需要时由程序做简化总结后进私有历史（§4.4） |
| 3 | 主持人 | 是**主持人 Agent**，与用户在界面上分开显示；用户不输入命令时，由主持人自主推进会议（§6.5、§9.1） |
| 4 | 中途增减与会者 | **不支持**。只有在所有会话运行结束（主持人 Agent 判定目标达成，或用户手动停止）之后才允许增减（§11.3） |
| 5 | 异构 provider | 本期**不支持**，按同构做前缀缓存优化 |
| 6 | 资料摘要来源 | 由**工具给出**（提交时一并提交，§5.1、§5.3） |
| 7 | 发布形态 | 调试期 `FrameworkDependent`；发布期 `SelfContained` + **zip 压缩包**；7z SFX 与 NativeAOT 暂缓（§10.3） |
| 8 | 资料摘要长度 | 程序截取兜底**300 汉字**以内；UI 可按容器截断，不必显示全文（§5.3） |
| 9 | 思考总结长度 | **500 汉字**以内，参考论文摘要结构（目的/做法/结论）（§4.4） |
| 10 | 可调参数 | 摘要长度等一律走 `meetingroom.json`，改后重启生效；本期不做设置 UI（§10.5） |
| 11 | 路径基准 | 统一用 `Path.GetDirectoryName(Environment.ProcessPath)`；已实测（§10.2） |
| 12 | 分发形式 | **zip 压缩包**（解压即用，`exe` / `wwwroot` / `data` / 配置同级） |

### 13.3 仍需决定 / 验证

1. **思考总结模板的字段划分**（"目的/做法/结论"三段各自的取词与截断顺序）——阶段 3 用真实数据调参（§4.4）。
2. **单文件形态未实测**——只测了 SelfContained 非单文件。`AppContext.BaseDirectory` 在 `PublishSingleFile` 下指向临时解压目录、`Assembly.Location` 返回空字符串，两点均为 .NET 官方文档行为，尚未验证；若将来改用单文件交付需补测（§10.2）。
3. **7z SFX 解压语义未验证**（仅当将来真要改用自解压分发时才需要，见 §10.3）。

---

## 十四、主持人 / 导演的交互模式（用户决策回路）

> 本节回答"每轮讨论之后，怎样把'接下来做什么'交回用户，再让主持人 / 导演继续控场"。
> 机制参考 Reasonix 的 `ask` 工具与其弹窗（`internal/event` 的 `AskQuestion` / `AskAnswer`、
> controller 的 `AnswerQuestion(id, answers)`）：**Agent 提问 → 前端弹出选项 → 用户选择或自由输入 → 答案回到 Agent 的同一个 turn**。

### 14.1 为什么需要它

现在主持人是"自主推进"的：用户不输入时它按默认推进力走下去（§6.4）。这在"全自动跑一场"时是对的，
但在真正需要用户拍板的地方（要不要收敛、采纳哪份资料、剧情往哪走），用户只能事后补料，
而不是**在决策点上被问到**。交互模式补上这条回路：主持人 / 导演在关键点主动提问，
用户选一个选项或直接打字，主持人据此继续控场。

### 14.2 契约（与 Reasonix 对齐）

提问走**控制面**（§5.1）：主持人 / 导演用带 Schema 的工具提交问题，而不是在正文里写选项。

| 概念 | 形态 |
| --- | --- |
| 工具 | `ask_user(header, prompt, options[{label, description}], multi)`（可一次给多个问题） |
| 事件（服务端 → 前端） | `interaction.request`：`{id, turnId, questions[], defaultSelection, timeoutSeconds}` |
| 回答（前端 → 服务端） | `POST /api/interactions/{id}`：`{answers: [{questionId, selected[]}]}` |
| 跳过 / 超时 | `POST /api/interactions/{id}/skip?reason=…`；或超时后按 `defaultSelection` 自动继续 |
| 投递目标 | 会议与叙事各有一场，端点按事项 id 找"谁在等"；找不到（重复答复 / 已超时 / id 写错）一律 404 |
| 结果回灌 | 工具返回"用户选了 X（或自由输入 Y）"，同时发 `interaction.resolved` 让前端收起浮层 |
| 落盘 | 问答写进 `transcript.jsonl`（`kind=interaction`），可审计"当时用户选了什么" |

> **实现状态（2026-09）**：待决条目机制（`Core/Interaction/`：`InteractionOptions` / `PendingDecision` /
> `PendingDecisionRegistry`）、`MeetingStatus.AwaitingInput` 与等待门（`SessionCore.WaitWhileBlockedAsync`）、
> 这两个端点、以及 `interaction.*` 六项配置与校验已落地，会议与叙事两种模式共用同一套；
> `ask_user` 工具、每轮兜底提问（`everyRound` / `maxConsecutiveSkips`）、浮层与问答落盘属后续批次。
> 说书人的世界状态提议会复用同一条流与同一组端点（`kind = world_state`）。

这三种事件（`interaction.request` / `interaction.resolved` / `interaction.skipped`）的载荷与前端落点已登记进
§8.3 事件表。事件表是前后端唯一契约，实现时**必须**同时把它们加进 `wwwroot/js/stream.js` 的 `EVENT_TYPES`；
前端没有类型检查，漏掉不会被构建发现，只会表现为浮层永远不出现。

三条硬约束：

1. **选项的 `description` 必填**——选了会发生什么必须写清楚。注意这是**本项目比上游更严的一处**：
   Reasonix 的 `ask` 工具 schema 只把 `label` 列为必填（`internal/agent/ask.go` 里的 `"required":["label"]`），
   `description` 可空。本项目里"采纳资料""结束会议""结束本场"都有副作用，不允许只给一个光秃秃的标签，
   由工具参数校验拒绝并说明原因；同层还可一并借鉴上游的两条校验——每个问题至少 2 个选项、
   同一问题内 `label` 不得重复（同样在 `internal/agent/ask.go`）。
2. **自由输入与选项并列**：用户直接打字时，答案作为**单条** `selected` 项回传（与 Reasonix 的 `AskAnswer` 一致），
   主持人 / 导演必须能处理"用户没选任何选项、而是说了别的"。
3. **超时必须能收口**：用户不在时不能把会议永久挂住；`timeoutSeconds` 到期后按默认选项继续，
   并发出带 `timedOut: true` 的 `interaction.resolved`（§8.3），事件里也标注"超时自动选择"。

### 14.3 与状态机的关系

- 提问期间会议进入新的 **`AwaitingInput`** 状态：不再发起新 turn，但**不打断**正在跑的 turn（让它写完当前 step）。
- 用户回答或超时后回到 `Running`，主持人 / 导演在**同一个 turn 内**拿到答案继续决策。
- 与 `Paused` 的区别：`Paused` 是用户主动停的（可以停很久）；`AwaitingInput` 是"会议在等这一刻的答复"，有超时。

### 14.4 两种模式的选项内容

| 模式 | 提问者 | 典型选项 |
| --- | --- | --- |
| 会议 | 主持人 Agent | 继续深入某个分歧 / 点名某人补充 / 采纳 mat-00X / 进入下一轮 / 结束并出结论 |
| 角色扮演 | 导演 Agent | 推进当前场景 / 切换到某地 / 让某角色出场 / 插入一段导演旁白（不进剧情） |

### 14.5 配置（进 `meetingroom.json`）

```json
"interaction": {
  "enabled": true,
  "everyRound": true,
  "allowSelfSkip": true,
  "maxConsecutiveSkips": 3,
  "timeoutSeconds": 120,
  "maxOptions": 4
}
```

| 参数 | 默认 | 作用 |
| --- | --- | --- |
| `interaction.enabled` | true | 关掉即退回现在的"全自动自主推进" |
| `interaction.everyRound` | true | 每轮结束都问；false 时只在主持人 / 导演判断的关键点问 |
| `interaction.allowSelfSkip` | true | **是否允许主持人 / 导演自己判断"这轮没什么可问的"从而跳过提问**（见 14.5.1） |
| `interaction.maxConsecutiveSkips` | 3 | `allowSelfSkip = true` 时的防呆：连续跳到这个次数就强制问一次；0 = 不限制 |
| `interaction.timeoutSeconds` | 120 | 等待答复的上限（秒）；**0 = 永久等待**（用户明确要求"每次都要我拍板"时用，但会议会一直挂到有人来）；超时按默认选项继续 |
| `interaction.maxOptions` | 4 | 单个问题的选项上限（选项太多反而选不动） |

配置校验沿用 §10.5 的原则：**负数一律拒绝**并在启动时报出字段名；`maxConsecutiveSkips = 0` 是合法值（表示不限制）。

#### 14.5.1 `allowSelfSkip` 与 `everyRound` 的组合语义

这两个开关决定"谁掌握提问的节奏"，四种组合各有明确含义：

| `everyRound` | `allowSelfSkip` | 语义 |
| --- | --- | --- |
| true | true | **默认**：每轮都问；但主持人 / 导演认为"这轮没什么值得打断你的"时可以跳过 |
| true | **false** | **每轮必须问**，它不得跳过。模型这一轮没主动提问时，由**编排层用默认选项集兜底发问**（"继续深入 / 进入下一轮 / 结束并出结论"），保证"必有交互"是程序保证的，而不是提示词保证的 |
| false | true | 完全交给主持人 / 导演判断何时提问（`allowSelfSkip` 在此组合下无实际影响） |
| false | false | 语义自相矛盾（"只在关键点问"又"不许跳过"）→ **配置校验直接拒绝**，启动时报错 |

防滥用的两条：

1. **每次跳过都要留痕**：发 `interaction.skipped` 事件（带跳过原因与轮次）并写进第 3 列，
   让用户能看出"它连着跳了三轮都没问我"——而不是无声地把决策权收回去。
2. **`maxConsecutiveSkips` 兜底**：连续跳过达到上限后强制提问一次；用户答复（或超时）后计数清零。
   这样即使 `allowSelfSkip = true`，用户也保证每隔有限轮次能被问一次。

### 14.6 前端

- 弹出**卡片式浮层**（不是整屏 modal）：贴在指令区上方，不遮挡五列；含问题文本、选项按钮
  （`label` + 一行 `description`）、"直接输入"输入框与"跳过"按钮。
- 多问题时分步展示（一问一答），避免一次糊一大片。
- 提问与回答同时写进第 3 列（主持人记录 / 导演记录），让"当时为什么这么走"可追溯。

---

## 十五、工作空间与 UI 改版（2026-09，待实现）

### 15.1 为什么要改

现在的形态是"单场 + 全局 `data/`"：整个进程只有一场会议（`data/meetings/local`）或一场叙事
（`data/stories/local`），角色卡、资料、世界书都堆在安装目录的 `data/` 下，换一场只能删文件。
要同时开几场、要让每场有自己的资料与世界观，就得引入**工作空间**。

### 15.2 三种主体的层次（本次改版的核心）

```
工作空间（一个磁盘目录，创建时定类型：会议型 / 叙事型）
├── 公共资料（工作空间根，跨本工作空间的所有会话共享）
│   ├── roles/         角色卡
│   ├── materials/     已归档资料
│   ├── worldbook/     世界书条目（叙事型）
│   └── drafts/        待整理区（§15.6）
└── 会话（同类型，可多场）
    ├── meetings/<id>/     会议：transcript.jsonl、资料库
    └── stories/<id>/      叙事：story.jsonl、memory.jsonl
```

两条硬规则：

1. **工作空间有类型，创建时就定**：会议型只开会、叙事型只开叙事。侧栏用**两个添加按钮**
   （「新建会议」/「新建叙事」）表达这一点，不做"建完再选类型"。
2. **角色卡、资料、世界书在工作空间根上**，跨该工作空间的**所有会话**共享；
   会话目录里只放这场自己的东西（对话 / 剧情 / 世界状态 / 长程记忆）。

### 15.3 侧栏（最左新增一列）

仿 Reasonix 的抽屉：

- 顶部：两个添加按钮（**新建会议** / **新建叙事**）+ **「打开…」**——打开磁盘上已有的工作空间，它已经有会话就直接切到最近一场（这是"打开一场已有会话"的正门，不必先"新建"再去找）；
- 中段：工作空间列表（名称、类型标识、路径、会话数）；
- 展开一个工作空间 → 它下面的会话列表（会议显示议题与状态；叙事显示世界观一句话与进度）；
- 每项尾部：重命名 / 删除（删除二次确认）；
- 选中会话即切到它；只选了工作空间没选会话 → 空态（"这个工作空间还没有会话，新建一场"）。

宽度与列宽调整：

```css
/* 侧栏 + 五列；第 5 列从 minmax(0,1fr) 收成固定窄列，腾出的横向空间给第 2 列 */
.workspace { display: grid; grid-template-columns: 240px 240px minmax(0,1fr) 260px 200px 200px; }
```

**列宽可拖动、每列可折叠**（2026-09 补）：列与列之间有拖动分隔条（改这一列的宽度；主列留 220px 下限，拖到边界就停），顶栏一排列开关控制显示 / 折叠；宽度与折叠状态都存 `localStorage`。**没动过时把排版交回 CSS**——窄窗口仍按上面的响应式档位（≤1560 / ≤1240）收紧。

> 为什么收第 5 列：它是"只显示、Agent 不可读"的后台记录，本来就不需要宽；
> 而会议模式的讨论区、叙事模式的剧情（第 2 列）最需要宽度。

### 15.4 新建工作空间：选路径

两种方式**都给**：

1. **本机目录浏览**（主路径）：后端提供只读的目录列举接口，前端做一个"选目录"对话框
   （进入子目录 / 上一级 / 新建目录 / 选中）。服务**只绑回环 + 一次性 token**（§10.4），
   所以这个能力等价于"同一台机器上的文件系统浏览"，不额外扩大暴露面。
2. **手输路径**（逃生口）：网络盘、超长路径、从资源管理器复制粘贴。
3. **系统原生对话框**（2026-09 补）：网页里的 `<input type="file">` 拿不到绝对路径（浏览器安全限制），所以由**服务进程**弹一次 Windows 目录选择窗（`Shell.Application.BrowseForFolder`）——服务是用户自己起的、与他在同一个桌面会话里。选中即用它，不必再点一次确认。

校验：路径必须可写；**已存在的工作空间直接打开**（不覆盖、不重写 `workspace.json` 的既有内容）；
同一路径不允许注册两次。

### 15.5 引导页 = 待整理资料的编辑页（同一个页面）

这一页**同时**是两件事：

1. **首次进入工作空间时的引导**（自动打开）：把该有的东西放对位置；
2. **下半区待整理资料的通用编辑页**（随时打开）：增 / 改 / 删 / 归档（§15.6）。

它不是"用完即弃的向导"——**引导只是它的第一次打开**。

| 打开方式 | 形态 |
| --- | --- |
| 新建 / 打开工作空间之后 | 自动打开，**引导态**：文案更详细（说明每个区该放什么），带「跳过」 |
| 第 1 列下半区的「编辑」按钮 | 打开，**编辑态**：直接列出待整理的条目，没有引导文案 |
| 第 1 列下半区的「+ 添加」 | 打开并聚焦新建，可预选类型（角色卡 / 资料 / 世界书条目） |

两种形态**共用同一套控件**，差别只在默认文案与是否显示「跳过」。它能做的事（两种形态一样）：

| 能力 | 行为 |
| --- | --- |
| **导入** | 选磁盘上**工作空间以外**的文件（或目录）→ **复制进工作空间**（原文件不动）。导入的东西按"**外部已经编辑好的成品**"处理：**直接归档、界面不提供编辑**（§15.6） |
| **直接输入** | 会议议题 / 叙事世界观 / 开场情境 / 角色卡 / 世界书条目，直接写进去 → 落盘到**待整理区（可改）** |
| **已有内容自动带入** | 工作空间里已有 `roles/` `materials/` `worldbook/` → 直接列出来（可增删改），不要求重填 |
| **查看已归档** | 上半区（已归档）在这里**只读列出**，作为"该写什么"的参照；要改先撤销归档（§15.6） |
| **归档** | 把整理好的条目推到上半区（§15.6） |
| **跳过** | 什么都不做也能进主界面——下半还是空的，随时点「编辑」回来补 |

**它编辑的范围只有待整理（下半区）**：已归档的不可改是硬规则（§15.6）。

> 为什么合成一页：引导与"以后回来补资料"要的是同一件事——**在同一个地方把资料放对位置**。
> 做成两个界面只会让用户学两遍。

**角色卡是这个页面里的一种类型**（与"资料""世界书条目"并列）：选它时，录入用**表单 + 正文**，不让用户手写
frontmatter——§5.4 的格式有 9 个字段，手写时漏一个 `role:` 或缩进错位，那张卡会**静默失效**（不报错，只是不生效）。
它的字段、校验与两种录入方式见 **§15.11**。

### 15.6 第 1 列资料面板：归档 / 待整理两段

引导页导入的、以及之后随手输入的，都落在**下半区（待整理）**，**允许随时改**；
整理好之后**归档**到**上半区**，归档后**不可更改**（要改就撤销归档，或另存一份再归档）。

| 区 | 内容 | 可改 | 是否进上下文 |
| --- | --- | --- | --- |
| 上：已归档 | 会议资料 / 世界观 / 角色卡 / 世界书条目 | ✗ | 是（按 §7.2 的常驻 / 触发式规则） |
| 下：待整理 | 引导页导入的、用户随手输入的 | ✓ | 只有归档之后才进 |

下半区头部两个按钮：**「编辑」**（打开 §15.5 的编辑态）、**「+ 添加」**（打开并聚焦新建）；
上半区头部一个只读开关：**「查看已归档」**（在下半区的编辑页里只读列出，便于对照着写）。

> 这与会议模式的"待审批 → 采纳"是同一条思路（§4.2）：**用户是唯一的归档者**，
> Agent 只能提议。区别只是归档的对象从"资料"扩到"资料 + 角色卡 + 世界书条目"。

**待整理条目在磁盘上的形状**：`drafts/<slug>/` 一个目录一条，里面一份 `entry.json`
（`{kind: role / material / worldbook, title, createdAt}`）加内容文件（角色卡与资料都是 `<slug>.md`）。
**归档 = 把内容文件移进对应的归档区**（`roles/` / `materials/` / `worldbook/`）并删掉那个 `drafts/<slug>/`；
**撤销归档 = 反过来移回 `drafts/`**。这样"待整理"与"已归档"是同一份数据的两个位置，不会出现
"两处各一份、改了一处另一处还是旧的"。

> **两条录入路径的落点不同**（2026-09 用户拍板）：
>
> - **直接输入**（界面上手写）→ 落 `drafts/`（待整理，**可改**），整理好再归档；
> - **导入**（磁盘上工作空间以外的文件）→ **直接进归档区**（`roles/` / `materials/` / `worldbook/`）：
>   它被当作**外部已经编辑好的成品**，界面**不提供编辑**（要改就撤销归档，或重新导入一份）。
>
> 代码层面两条都支持：`POST /api/drafts`（写待整理）与 `POST /api/workspaces/import` 的 `target`
> 四个值（`drafts` / `roles` / `materials` / `worldbook`）都在——`drafts` 那个留给"导入后还想改"的后续路径，
> 界面暂时不用它（§15.7）。

### 15.7 端点与事件（新增）

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/workspaces` | 列出已注册的工作空间（类型、路径、会话数） |
| POST | `/api/workspaces` | 新建 `{path, kind: meeting / roleplay, name?}` |
| POST | `/api/workspaces/open` | 打开已存在的 `{path}`（已注册直接切；未注册则注册后打开） |
| GET | `/api/workspaces/browse?path=…` | 列举本机目录（只给目录与文件摘要，供 §15.4 的对话框） |
| POST | `/api/workspaces/import` | 从工作空间以外复制进来 `{workspacePath, sources[], target: roles / materials / worldbook / drafts}`；**界面导入默认直投归档区**（当成外部已编辑好的成品，不提供编辑），`drafts` 留给"导入后还想改"的后续路径（§15.6） |
| POST | `/api/sessions` | 在某工作空间下新建会话 `{workspacePath, kind, title}` |
| GET | `/api/drafts` | 列出待整理条目 `[{id, kind, title, createdAt}]`（§15.6） |
| POST | `/api/drafts` | 直接输入一条待整理条目 `{kind, title, …字段, body}`；角色卡的字段见 §15.11 |
| POST | `/api/drafts/{id}/archive` \| `/unarchive` | 待整理 ↔ 已归档（§15.6）；角色卡归档后才有 `roles/<id>.md`、才进上下文 |
| GET | `/api/roles` | 列出工作空间里**已归档**的角色卡（`roles/*.md` 解析出来的字段 + 正文） |

事件（走同一条 SSE，§8.1）：

| `type` | 载荷要点 | 前端落点 |
| --- | --- | --- |
| `workspace.list` | `workspaces[{path, kind, name, sessionCount}]` | 侧栏（重连后全量重建） |
| `workspace.opened` | `path, kind, name, sessions[]` | 侧栏展开态 + 顶栏当前工作空间 |
| `session.list` | `workspacePath, sessions[]` | 侧栏的会话列表 |
| `material.archived` | `id, archived` | 第 1 列两段之间移动 |

### 15.8 磁盘布局（替换 §10.1 里的 `data/` 形态）

安装目录只留程序与它自己的状态；**用户的数据全在工作空间里**：

```
<安装目录>/                     # 程序本体
├── MeetingRoom.Host.exe
├── wwwroot/  meetingroom.json  logs/
└── appdata/                    # 程序自己的状态（不是用户的资料）
    ├── workspaces.json         # 已注册的工作空间清单
    └── settings.json           # 上次打开的工作空间与会话

<工作空间根>/                   # 用户在 §15.4 选的路径，可以是任何地方
├── workspace.json              # 名字 + 类型（meeting / roleplay）+ 创建时间
├── roles/                      # 角色卡（跨会话共享）
├── materials/                  # 已归档资料
├── worldbook/                  # 世界书条目（叙事型）
├── drafts/                     # 待整理区（§15.6）
├── meetings/<id>/              # transcript.jsonl、资料库
└── stories/<id>/               # story.jsonl、memory.jsonl
```

> **迁移**：旧形态的 `data/meetings/local`、`data/stories/local` 在首次启动时**提示**可迁移到某个工作空间，
> **不自动搬**（动用户数据要他自己点头）。

> **W1 落地时改了个做法**（比原计划更小）：**没有动 `PathLayout`**——它继续管安装级（程序本体、配置、日志，外加新增的 `appdata/`），
> 用户数据交给新增的**实例类** `WorkspaceLayout`（`roles/` `materials/` `worldbook/` `drafts/` `meetings/` `stories/`）。
> 两者分工清楚，也就不必把那个静态类拆开。W2 再把会话与角色卡的取路径从 `PathLayout.*` 切到 `WorkspaceLayout.*`。
>
> **W2 落地（2026-09）**：取路径已切到 `WorkspaceLayout.*`。会话元信息落 `<会话目录>/session.json`——
> 侧栏要显示议题 / 世界观，重新打开一场也要靠它复原设定（会议议题此前只活在内存里）；
> 装配收在 `WorkspaceSessionFactory`、生命周期收在 `Host/Sessions/SessionHub.cs`（**一次只活跃一场**，
> 切工作空间 / 切会话 = 旧会话退场 + 装配新会话）。
>
> **升级路径**：程序启动时还没有"当前工作空间"的话，会把安装目录下的旧 `data/` 作为 **Legacy 工作空间**打开——
> `WorkspaceLayout(data)` 的 `roles/` 与 `meetings/` 恰好等于旧的 `PathLayout.*`，所以现形态天然等价、行为连续。
> 旧数据仍是**提示可迁、不自动搬**（动用户数据要他自己点头）。

### 15.9 与既有设计的关系（哪些没变）

- **五列语义不变**（§9.1）：只是最左多一列侧栏、最右收窄；
- **双模式不变**（§11.1–§11.6）：会议型工作空间只跑会议、叙事型只跑叙事，第 3 / 第 4 列的语义切换照旧；
- **单条 SSE 不变**（§8.1）：工作空间与会话的事件走同一条流；
- **"用户是唯一的归档者"不变**（§4.2）：归档对象从资料扩到资料 + 角色卡 + 世界书条目；
- **交互模式不变**（§十四）：待决事项的浮层与端点照旧。

### 15.10 实现顺序（建议四批）

| 批 | 内容 | 验收 |
| --- | --- | --- |
| ✅ W1 | 后端：工作空间模型（`workspace.json`、`appdata/workspaces.json` 注册表）、列表 / 新建 / 打开 / 目录浏览端点 | **已完成**：建两个工作空间（一会议一叙事）；重启后清单与"当前"都还在 |
| ✅ W2 | 后端：会话挂到工作空间下（`meetings/<id>`、`stories/<id>`）、导入端点（工作空间以外 → 复制进来） | **已完成**（单测）：一个工作空间下开两场会议，第一场的资料在第二场里不存在，两场会话共用工作空间根的角色卡；HTTP 层与界面待真机 |
| ✅ W3 | 前端：侧栏（列表 + 两个添加按钮 + 会话列表）、选目录对话框、新建流程、最右列收窄 | **已完成**（HTTP / 真机待验）：侧栏能建 / 切 / 删（删除二次确认）；选目录支持进子目录 / 上一级 / 新建子目录 / 手输；六列 = `240px 240px minmax(0,1fr) 260px 200px 200px` |
| ✅ W4 | 前端：引导页（= 下半区的通用编辑页，§15.5）+ 第 1 列两段式（待整理 → 归档 → 撤销归档） | **已完成**（MCP 真机）：手写一张卡 → 出现在下半 → 归档 → 进上半；关掉页面再点「编辑」能回来接着改；撤销归档把它退回下半 |
| ✅ W5 | 前端：**创建角色卡页**（§15.11）+ 待整理 / 归档端点 | **已完成**（MCP 真机）：表单填字段 → 后端渲染 frontmatter 落待整理 → 归档进 `roles/`；**导入一份 `.md` → 直接进 `roles/`、界面不提供编辑**（只留「正文」只读查看与「撤销归档」） |

五批都做完再跑一次 `dotnet test` + MCP 真机（真机流程：新建叙事工作空间 → 选目录 → 引导页导入角色卡 →
归档 → 开一场叙事 → 侧栏切到第二场）。


### 15.11 创建角色卡页（角色列表的新建入口）

「角色列表」要有自己的新建入口：现在 `roles/*.md` 只能靠用户手写，或等"角色卡为空时写入内置默认卡"（§5.4）——
可叙事模式真正决定成败的恰恰是这几张卡（谁是**说书人**、每个 NPC 的人设）。

**它就是 §15.5 那个页面的"角色卡"类型**（同一套控件、同一份"待整理 → 归档"语义）：

| 打开方式 | 形态 |
| --- | --- |
| 角色列表头部的「+ 新建角色卡」 | 打开并聚焦新建（空表单） |
| 待整理列表里某张卡的「编辑」 | 打开并回填**待整理**那张卡的字段与正文；**已归档的卡只读**，界面上没有编辑入口（要改先撤销归档，§15.6） |
| 导入 | 选磁盘上**工作空间以外**的 `.md`（或一整个角色卡目录）→ **直接归档进 `roles/`**：它被当作外部已经编辑好的卡，**不回填、不提供编辑**（要改就撤销归档或重新导入一份；想看一眼内容用「查看已归档」只读列出） |

字段与校验：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是（默认从 `name` 生成） | 同时是归档后的文件名 `roles/<id>.md`；与已有卡重名时**顺延**（`-2`、`-3`），不覆盖 |
| `name` / `title` / `summary` | `name` 必填 | 与会者卡片上的三个展示字段（§5.4） |
| `role` | 是（默认 `member`） | `host` 表示主持人；叙事里"谁说书人"按 `NarratorId` 认、**不看** `role`（§11.4 第 5b 项） |
| `model` / `effort` | 否 | 留空 = 用全局配置（同构 provider 下前缀才稳定，§13.2-5） |
| `allowedTools` / `readOnly` | 否 | 硬约束：工具集**不按角色裁剪**（§7.1），这两个字段只供工具内权限判断 |
| 正文 | 否 | 背景知识 / 职责 / 行为准则 / 发言风格——卡的主体 |

**录入的落点**：表单 + 正文 → 程序渲染成 §5.4 的 `---` frontmatter + 正文 → 写 `drafts/<slug>/`（**待整理**、可改）；
**归档之后**才成为 `roles/<id>.md`、才进上下文（§15.6）。**导入不走这条路**——它直接归档（见上表）。
归档之后的卡是**只读**的：角色列表与「查看已归档」都只读它（`GET /api/roles` 只用来渲染展示）。

**为什么不让用户手写 frontmatter**：§5.4 的格式由 `RoleCardLoader` 解析，只支持 `key: value` 与 `key: [a, b]`，
不支持多行值 / 嵌套 / YAML 锚点（风险 10）。少写一行 `role: host` 不会报错，只会让那张卡静默失效——
表单把这件事变成"填错了当场告诉你"。

> 与 dsh-tavern 的关系只到**数据对接**这一层：人物卡的字段与导入形式可以参考；
> 它的模块组织与交互流程不构成设计依据。
