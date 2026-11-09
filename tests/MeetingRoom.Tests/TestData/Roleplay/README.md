# 叙事模式测试数据：侦探事务所（一桩夜雨里的案子）

角色扮演（叙事）模式用的一组**固定夹具**：4 张角色卡 + 1 份场景设定。
用它做手工验收，或将来写自动化测试时复用同一套设定。

## 内容

| 文件 | 说明 |
| --- | --- |
| `roles/detective.md` | 侦探 —— 案子的主导者，先反推"谁最不希望真相被说出来" |
| `roles/detective-assistant.md` | 侦探助手 —— 记录与查证，指出前后矛盾 |
| `roles/client.md` | 委托人 —— 出钱的人，急切且有隐瞒 |
| `roles/coroner.md` | 法医 —— 只讲证据能支撑的判断 |
| `roles/director.md` | 说书人 —— 本场的导演（`role: host`）：只写环境与调度、不出角色台词 |
| `scene.json` | 本幕设定：世界观 / 情境 / 用户身份 / 卡司 |

四张卡都是 `role: member`。本项目里**用户自己就是导演（DM）**，叙事模式不设"玩家"；
`role: host` 的卡在叙事里被当作"导演"（见下「已知边界」）。

## 怎么用

1. 把角色卡放进运行期目录（服务启动时按文件名顺序加载，见 `PathLayout.RolesDir`）：

   ```bash
   cp tests/MeetingRoom.Tests/TestData/Roleplay/roles/*.md \
      src/MeetingRoom.Host/bin/Debug/net10.0/data/roles/
   ```

2. 起服务，确认启动日志的"已注册与会者"里出现这四个名字：

   ```bash
   dotnet run --project src/MeetingRoom.Host -- --no-browser
   ```

3. 界面上点顶栏「新建叙事」，按 `scene.json` 填「世界观 / 开场情境 / 你的身份 / 在场角色 id」并提交
   （提交即开写，没有"候选开局"这一步）；也可以直接用同一份 JSON 打端点——
   `scene.json` 的字段名与 `POST /api/roleplay/start` 的请求体一致：

   ```bash
   curl -s -X POST http://127.0.0.1:8787/api/roleplay/start \
     -H "Content-Type: application/json" \
     --data-binary @tests/MeetingRoom.Tests/TestData/Roleplay/scene.json
   ```

> 注意：界面浮层没有 `storyId` 字段，从界面创建时它落成默认的 `local`；
> 只有直接打端点（上面那条 curl）才会用 `scene.json` 里的 `storyId`。

## 预期现象

- 第 1 列四张设定卡：世界观 / 开场情境 / 我的身份 / 当前世界状态；
- 第 4 列「在场角色」正好是这四人（`cast` 之外的卡不会列出来）；
- 第 3 列下「导演记录」出现一条「开场 · 在场：…」；
- 配了 `DEEPSEEK_API_KEY` 时导演侧写出开场正文（第 2 列出现一幕）；没配 key 时这一步会失败、
  顶栏提示条报 `DEEPSEEK_API_KEY 未设置`——界面结构仍然正确，只是没有正文。

## 已知边界

1. **开场由谁写**：引擎按"显式 `directorId`（默认 `director`）→ 否则 `role: host` 的卡 → 否则注册顺序第一个在场角色"取导演。
   把本目录的 `director.md`（说书人）复制到 `data/roles/` 即可让叙事用说书人当导演（它的 id 就是默认的 `director`，
   优先于运行期那张**会议主持人**卡 `host.md`——后者的内容是议程与资料审批，被当叙事导演用会语义错位）。
   也可以用 `POST /api/roleplay/start` 的 `directorId` 显式指定。（界面浮层暂未暴露该字段。）
2. **卡司顺序不等于发言顺序**：在场判定按 `cast` 走，但开场与接话的角色顺序按**角色卡文件名**排序。
3. 角色卡只支持 `key: value` 与 `key: [a, b]` 两种 frontmatter 形态（`RoleCardLoader`），
   不支持多行值、嵌套结构或 YAML 锚点。
