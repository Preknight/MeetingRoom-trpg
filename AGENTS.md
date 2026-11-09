# Meeting Room 项目约定

给在本仓库工作的 AI agent 与贡献者。**先读完这一页再动手。**

## 项目是什么

多角色 TRPG 叙事工具：**用户当导演（DM）**，说书人（narrator）负责控场与转写，若干角色 Agent 多人群聊推进剧情。
战斗、检定、地图与规则查询由程序结算，模型不自己编数值。

产品默认且只保留**叙事模式**。后端的会议端点（`Host/Meeting/`、`Core/Orchestration/MeetingSession.cs`）仍然保留，
用于兼容旧数据与测试，但前端不再提供会议入口，也不按会议语义渲染。

## 技术栈

- 后端：.NET 10 + ASP.NET Core Minimal API，单进程
- 前端：原生 HTML/CSS/JS + ES modules，**无构建步骤（不引入 node/npm）**
- 实时：**单条** SSE（服务端 → 客户端）+ POST（客户端 → 服务端）
- 持久化：exe 同级目录下的 JSON / JSONL，无数据库、无注册表
- 发布：调试期 `FrameworkDependent`；发布期 `SelfContained` + zip

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/MeetingRoom.Agent/` | step 循环、provider 接入、打断与思考摘要；只依赖 `Microsoft.Extensions.AI.*` |
| `src/MeetingRoom.Core/` | 领域模型、编排、叙事、战斗、规则、地图、工作空间 |
| `src/MeetingRoom.Host/` | Minimal API 端点、SSE、启动与探针 |
| `tests/MeetingRoom.Tests/` | xUnit 测试 |
| `wwwroot/` | 前端（无构建，服务读 exe 同级副本） |
| `docs/` | 设计与规范文档 |
| `tools/` | 规则数据导出脚本 |

## 硬约束（违反会返工）

1. `MeetingRoom.Core` 与 `MeetingRoom.Agent` **不得引用任何 ASP.NET Core 包**——否则无法用 `dotnet test` 直接驱动。
2. 前端资源**不内嵌**进程序集，始终是 exe 同级的 `wwwroot/` 物理目录。
3. 所有路径经 `PathLayout.InstallDir` 派生（`Path.GetDirectoryName(Environment.ProcessPath)`）；**不用** `%APPDATA%`、系统临时目录、`Environment.CurrentDirectory`。
4. **只开一条 SSE**。浏览器同源 HTTP/1.1 连接上限约 6 条，多角色时"每人一条"会直接卡死。
5. **不要用** `FunctionInvokingChatClient` 之类的自动工具循环封装——step 边界被藏起来后，三个取消点与 steer 注入点全部失效。必须自己写 step 循环。
6. 系统提示前缀（system + tools + 叙事公共节）必须**字节稳定**；角色差异只进私有节。
7. 依赖方向固定为 Core → Agent（Core 的与会者持有 `AgentSession`）；`IContextComposer` 定义在 Agent，实现 `ContextComposer` 在 Core。

## 常用命令

```bash
dotnet build
dotnet test
dotnet run --project src/MeetingRoom.Host                    # 起服务并自动打开浏览器
dotnet run --project src/MeetingRoom.Host -- --no-browser    # 不自动开浏览器
dotnet publish src/MeetingRoom.Host -c Release -r win-x64 --self-contained true -o publish
```

## 开发约定

- **开发期 `wwwroot/` 是 `bin/<Config>/net10.0/wwwroot` 的副本**（不是链接）：改完仓库根的 JS/CSS 必须重新 `dotnet build` 或重启 `dotnet run`。
  `dotnet run --no-build` **不会**刷新副本——编译成功、进程重启，界面仍是旧的。浏览器缓存会叠加这个假象，调试时先强制刷新。
- 配置集中在 exe 同级的 `meetingroom.json`，**改后重启生效**（不做热重载）；非法值应当**拒绝启动并说明原因**，不要静默回退默认。
- 思考过程一律落盘、默认不进上下文；进上下文的只是程序生成的有界摘要。
- **开发期 `data/roles/` 保持精简**：`RoleCardLoader.LoadDirectory` 只扫顶层 `*.md`，开会话时目录里**每张卡都会成为在场角色**。
  验证期只留当前需要的卡，其余移到 `roles/_unused-<时间戳>/` 归档。
  **`narrator.md` 必须保留**：缺它且没有 `role="host"` 的卡时，说书人解析为 null → 工具面全空、模型一个工具都不调。
- **验收若要改数据，必须用一次性测试数据**：不要动用户正在用的工作空间（地图、迷雾、角色表都可能不可恢复）。
- 文档修订记录的时间精确到分钟（`YYYY-MM-DD HH:mm`）；文档与代码不符时按现状改对并加状态标注。
- 前端没有类型检查：事件类型表（`wwwroot/js/stream.js` 的 `EVENT_TYPES`）是前后端唯一契约，加事件时两边一起改。

## 文档

| 文档 | 用途 |
| --- | --- |
| [`docs/Agent 会议（Meeting Room）设计方案 - .NET 10 版.md`](docs/Agent%20会议（Meeting%20Room）设计方案%20-%20.NET%2010%20版.md) | 架构、领域模型、可中断运行时、SSE 契约、双模式设计 |
| [`docs/叙事模式后台与上下文设计.md`](docs/叙事模式后台与上下文设计.md) | 前台三层上下文、后台观察页、DM 提醒通道 |
| [`docs/战斗托管设计.md`](docs/战斗托管设计.md) | 战斗规则、工具面、胜负守卫 |
| [`docs/地图编辑器设计.md`](docs/地图编辑器设计.md) / [`docs/地图视野与特殊区域设计.md`](docs/地图视野与特殊区域设计.md) | 自带地图、编辑器与战术板、迷雾与特殊区域 |
| [`docs/角色卡与数值表模板.md`](docs/角色卡与数值表模板.md) | 角色卡 `.md` 与 `.sheet.json` 的完整字段与校验规则 |
| [`docs/规则数据导出方法.md`](docs/规则数据导出方法.md) | 规则包字段契约与重新导出步骤 |

前端调试可以用浏览器自动化工具（打开页面、执行 JS、读 console/network、截图）。起服务时带 `--no-browser`，
否则会多开一个系统浏览器窗口；改了 `wwwroot/` 后必须强制重载，避免缓存误判。
