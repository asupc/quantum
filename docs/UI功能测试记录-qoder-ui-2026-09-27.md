# Web UI 级别功能测试记录（2026-09-27）

> 本文档由本次「启动服务后完成 UI 级别功能测试」的执行链独立产出，文件名带 `-qoder-ui` 专名，
> 不与并行的其它执行链文档（`docs/QQ微信内置通道实施状态.md`、`docs/QQ微信P0实施验证记录.md` 等）共用写入。
> 文中用例编号统一加前缀 `Q-`，与任何其它台账编号无关。

## 1. 测试环境（实况，非推断）

| 项 | 实况 |
| --- | --- |
| 后端 | `dotnet run --project Quantum.API/Quantum.Web`，监听 `:5088`，版本 V5.09.17 Beta 1；首轮 17:44:03 启动，本链为修迁移/补密钥先后重启 3 次，最终回到**默认配置**（不带本链临时密钥）运行。当前 PID 以 `netstat -ano \| grep :5088` 实测为准 |
| 前端 | `vite` dev server 监听 `127.0.0.1:8080`（PID 56308，启动时间 17:34:02，**非本链启动**，本链直接复用），proxy → 127.0.0.1:5088 |
| 数据库 | `appsettings.json` 的 `Quantum.DBType=MySql` 指向的远端 NAS 实例 `quantum` 库（42 张表；连接串含口令，本文不复述） |
| 通道主密钥 | 首轮**未配置**（故写操作按设计被拒，见 Q-M7/Q-M10）；补测轮由用户放行后在仓库外临时目录生成一次性密钥并以 `QUANTUM_CHANNEL_KEY_FILE` 指向，测完已撤除 |
| 驱动方式 | Chrome（Qoder Browser Connector）+ 可访问性快照 + DOM 断言（`evaluate_script`），断言口径为渲染结果与 `ResultModel` 信封，不只看接口 200 |
| 启动日志 | 仓库外临时目录，避免污染工作树 |

登录凭据使用 `appsettings.json` 中 `Quantum` 节的管理员账号口令（本文不复述其值）。

## 2. 用例结果

### Q-L 登录

| 编号 | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| Q-L1 | 错误口令登录 | 通过 | 停留在 `#/login`，toast「登录失败，用户名密码错误！」；HTTP 仍 200（信封 `Code=401`），符合恒 200 约定 |
| Q-L2 | 正确口令登录 | 通过 | `localStorage.accessToken` 写入，落地 `#/chat/index`，侧栏菜单渲染完整 |
| Q-L3 | 口令找回提示 | 通过 | 登录页展示「如忘记密码，请修改部署目录下 appsettings.json 中 Quantum 节的 PassWord 字段」 |

### Q-M 系统管理 → 消息通道（本次新增页面）

| 编号 | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| Q-M1 | 存量库菜单自动补入口 | 通过 | `GET /api/Menu` 返回 `/settings/channel`（component=`channel/index`，标题「消息通道」）；`t_menu` 为存量数据、`menu.json` 不会重播，说明 `MenuService` 补入分支在真实 MySQL 上生效 |
| Q-M2 | 菜单可见性 | 通过 | 面包屑「系统管理 / 消息通道」，顶部页签出现「消息通道」 |
| Q-M3 | 状态接口与双卡片渲染 | 通过 | `GET /api/Channel/status` 返回 QQBot/WeixinBot 两条；两张卡片分别显示「已关闭 / 机器人 未配置 / 唯一绑定 未绑定 / 待确认候选 无 / 待发条数 0」 |
| Q-M4 | 未配置态按钮门禁 | 通过 | 仅「刷新状态」「配置机器人」「扫码绑定」「投递记录」可点；「发起绑定挑战」「确认绑定」「启用通道」「解绑」「安全快捷回复」全部 `disabled=true`（两卡片一致） |
| Q-M5 | 配置 QQ 机器人弹窗 | 通过 | 弹窗标题/警示语（「AppSecret 只会发往服务端加密保存，本页不缓存」）与两个输入项正常渲染 |
| Q-M6 | 空凭据前端拦截 | 通过 | 提交后 toast「请填写完整的测试机器人凭据」，**零网络请求**（`performance` 资源计数验证），弹窗保持打开 |
| Q-M7 | 无主密钥时保存 QQ 凭据 | 通过 | toast「先配置仓库外的通道主密钥 QUANTUM_CHANNEL_KEY_FILE」；随后 `status` 复核 `Configured=false`，**未落脏数据** |
| Q-M8 | 微信协议版本弹窗 | 通过 | 「微信协议版本核对」渲染，含「不可冒称 OpenClaw」提示；两个版本字段 |
| Q-M9 | 非法版本号前端拦截 | 通过 | `iLink-App-ClientVersion` 填非十进制值 → toast「请填写已核对的协议版本」，**零网络请求** |
| Q-M10 | 合法版本号但无主密钥 | 通过 | toast「先配置仓库外的通道主密钥再扫码」，未误开二维码弹窗 |
| Q-M11 | 投递记录弹窗 | 通过 | 标题「QQBot · 投递记录（最多 50 条）」，6 列表头齐全，空态显示「无数据」，警示语「Accepted 只表示平台接口受理」 |
| Q-M12 | 模态关闭与页面残留 | 通过 | 关闭后 `.n-modal` 归零，页面可继续操作 |

### Q-M2 补测：配置主密钥后的成功路径（Q-M13～Q-M19）

> 经用户放行后，在仓库外临时目录生成一次性 32 字节 Base64 主密钥文件（`%TEMP%\quantum-ui-qoder\quantum-channel.key`），
> 以进程环境变量 `QUANTUM_CHANNEL_KEY_FILE` 指向它并重启后端，随后在**同一 MySQL 开发库**上补测。
> 全程未点「发起绑定挑战」，避免用假凭据向 QQ 官方端点发真实请求。

| 编号 | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| Q-M13 | 保存 QQ 机器人（成功路径） | 通过 | toast「机器人已保存，默认关闭」；`status` 转 `Configured=true`、`Enabled=false`、`BotIdMasked="1234****7890"`（掩码，明文不出接口），卡片同值 |
| Q-M14 | 配置后按钮门禁重算 | 通过 | 「发起绑定挑战」「解绑」「安全快捷回复」「投递记录」转可点；「确认绑定」「启用通道」**仍禁用**（无绑定不得启用） |
| Q-M15 | 安全快捷回复白名单选择与保存 | 通过 | 候选下拉返回 1 项「ws测试」（服务端口径 `Enable && !EnableRegex && MessageType==文本`，11 条指令中仅此 1 条合格，符合最小权限设计）；勾选后 toast「安全快捷回复白名单已保存」，重开接口回读 `Selected=true` |
| Q-M16 | 白名单持久化 | 通过 | `GET /api/Channel/QQBot/quick-replies` → `ws测试=true` |
| Q-M17 | 更换机器人二次确认后取消 | 通过 | 弹「确认更换 QQ 机器人？/ 旧绑定和待发回复会作废。」；点「取消」后 `BotIdMasked` 仍为 `1234****7890`，未发生改写 |
| Q-M18 | 解绑 | 通过（行为与设计一致，但见 QT-5） | 弹「解绑并撤销旧路由？」；确认后无报错，`t_channel_binding`/`t_channel_allowed_command` 均 0 行；**但 `Configured` 仍为 `true`、掩码仍在** |
| Q-M19 | 微信侧在有密钥但未核对协议时 | 未覆盖 | 只验到前端版本字段校验与（无密钥时的）拒绝文案；本轮未在有密钥状态下再走扫码分支 |
| Q-M20 | 数据清空后「刷新状态」与页签缓存滞留 | 通过（附 QT-6） | 删除库中探测记录后切回该页签，卡片仍显示旧掩码 `1234****7890`（页签被 keep-alive，组件只在挂载时拉一次）；点「刷新状态」后立即回落到「未配置」，与 `status` 接口一致 |

### Q-T 执行记录抽屉（本次改动 `runHistory.vue`）

| 编号 | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| Q-T1 | 任务列表打开执行记录 | 通过 | 行内图标 `i.op-icon[title="执行记录"]` 可点，抽屉标题「执行记录 · 音乐搜索」 |
| Q-T2 | 固定列宽与摘要列 | 通过 | 实测列宽 67/58/86/198/125/104/71px（与新增 `table-layout="fixed"` 及各列 width 一致），无单元格裁切（`scrollWidth<=clientWidth`），横向无溢出 |
| Q-T3 | 执行详情区块 | 通过 | RunId、状态、触发源、耗时、开始/结束、安全摘要、「查看实时日志」「重新执行」与失败策略面板（重试次数/退避基数/告警阈值/告警冷却/恢复通知/保存策略）完整渲染 |
| Q-T4 | 分页翻页 | **无法在本库覆盖** | 该库 `t_task_run` 全库仅 1 条记录（`SELECT COUNT(*)` 实测=1），不足一页。要造数据只能真跑任务，而现有 9 个任务都会打外部平台（B站/SMZDM/音乐/推送），本链不擅自触发 |
| Q-T5 | 详情日志正文 | 部分覆盖 | 唯一那条记录（17:48 的「音乐搜索」）`LogFileName` 有值，但抽屉「日志」区显示为空；未能验到日志正文渲染 |

### Q-R 核心页回归（同环境、同登录态）

| 编号 | 页面 | 结果 | 证据 |
| --- | --- | --- | --- |
| Q-R1 | 会话 `/chat/index` | 通过 | 10 个会话分组渲染，消息列表 50 条载入 |
| Q-R2 | AI 助手 `/chat/ai` | 通过 | 页面内容非空（innerText 5980 字符） |
| Q-R3 | 任务列表 `/task/index` | 通过 | 9 行任务 + 分页/导入导出/操作列正常 |
| Q-R4 | 脚本版本 `/task/script-versions` | 通过 | 3 行 |
| Q-R5 | 外触内执 `/task/open-trigger-task` | 通过 | 表格渲染，无错误 toast |
| Q-R6 | 数据管理 `/custom-data/quantum_health` | 通过 | 15 行 |
| Q-R7 | 环境变量 `/env/index` | 通过 | 16 行 |
| Q-R8 | 快捷回复 `/replay/index` | 通过 | 11 行（即通道「安全快捷回复」白名单的候选数据源，页面本身正常） |
| Q-R9 | Docker `/docker/containers` | 通过 | 2 行 |
| Q-R10 | 系统设置 / 菜单管理 / AI供应商 / AI设置 | 通过 | 四页均渲染，无错误 toast |
| Q-R11 | 日志中心 `/logs/index` | 通过 | 15 行日志，无错误 toast |

## 3. 缺陷清单

> 本节各条的**处置结果统一见第 7 节「修复记录」**（同日第二轮，用户要求全部修复）；此处保留原始发现与证据。

### 阻塞（本轮曾阻断，现已修复并复验）

**QD-1（阻塞·数据库）MySQL 挂起迁移 `ChannelRequiredKeys` 应用失败，且已部分执行**

- 现象：后端启动即记录
  `[ERROR] 挂起迁移应用失败，库结构未更新：BLOB, TEXT, GEOMETRY or JSON column 'Content' can't have a default value`。
- 根因：`Quantum.Data/Migrations/MySqlMigrations/20260927085211_ChannelRequiredKeys.cs` 对
  `t_channel_outbox.Content`（`longtext`）做 `AlterColumn(nullable: false, defaultValue: "")`。
  MySQL 不允许 BLOB/TEXT/JSON 列带 DEFAULT（error 1101）。SQLite 侧无此限制，故 680 个离线用例全绿也照不出来——**属双库差异盲区**。
- **已实际发生的副作用（须知）**：MySQL 的 DDL 非事务，迁移在第 4 个列上中断，前面的列**已经改成 NOT NULL DEFAULT ''** 并留在库里：
  - `t_channel_reply_route`：`AccountId` / `PeerId` / `ReplyMessageId` → `NO null, default ''`
  - `t_channel_outbox`：`Status` / `Purpose` / `PeerId` → `NO null, default ''`
  - 未触及（仍 nullable）：`t_channel_outbox.Content/ClientId/AccountId`、`t_channel_inbox.*`、`t_channel_cursor.AccountId`、`t_channel_binding.*`、`t_channel_allowed_command.*`、`t_channel_account.Platform/BotId`
  - `__EFMigrationsHistory` 仍停在 `20260927084622_ChannelSafeCommands`，即**该迁移未登记为已应用**，每次启动都会从头重放同一段 DDL 并在同一列再次失败。
- 影响面：本次 UI 走查未受影响（通道表 0 行，读写路径都还没接真数据）。但 MySQL 部署的库结构与此库模型不一致会一直存在，后续任何依赖 `t_channel_inbox.Status` 等 NOT NULL 语义的写入都可能在此库上以「 nullable 列 + 迁移永远挂起」的形态漂移。
- 建议修法（任选其一，改完 SQLite/MySQL 两侧各生成一次并核对 Up/Down）：
  1. 该 `longtext` 列去掉 `defaultValue: ""`，改成先 `UPDATE ... SET Content=COALESCE(Content,'')` 回填、再 `AlterColumn(nullable:false)`（不带默认值）；
  2. 或把 `Content` 声明为 `nvarchar(max)`/`varchar(N)` 形态（若业务上限可控）；
  3. 或在实体上给该列 `HasDefaultValueSql("")` 之外的等价处理。
  修完需人工核对 `__EFMigrationsHistory` 与已改列（本次被改过的 6 个列已在目标形态，重放应幂等）。
- 说明：`docs/QQ微信内置通道实施状态.md` 已自陈「MySQL 迁移尚未连真实实例演练」，本次即首次真实演练，结论是**不通过**。

#### QD-1 修复与复验（经用户放行后由本链实施）

- 改法：只动出问题的那一个 LOB 列 —— `t_channel_outbox.Content` 的 `AlterColumn` **去掉 `defaultValue: ""`**，仅保留 `nullable: false`。
  MySQL 与 SQLite 两侧同步改（保持两侧语义一致，LOB 列两侧都不给默认值）；其余 varchar 列的 `defaultValue: ""` 保留（MySQL 允许 VARCHAR 带默认值）。
  - `Quantum.API/Quantum.Data/Migrations/MySqlMigrations/20260927085211_ChannelRequiredKeys.cs`
  - `Quantum.API/Quantum.Data/Migrations/SqliteMigrations/20260927085208_ChannelRequiredKeys.cs`
  - 就地改而未重新生成：该迁移**从未被任何实例登记为已应用**（`__EFMigrationsHistory` 当时停在 `ChannelSafeCommands`），属修改挂起迁移，不是改写已落库历史。
- 复验（真实 MySQL 实例）：重启后启动日志**不再出现** `[ERROR] 挂起迁移应用失败`；
  `__EFMigrationsHistory` 头部出现 `20260927085211_ChannelRequiredKeys`；
  `information_schema` 复查通道表：本轮迁移要求收紧的列已全部 `NO null`，仅剩 `t_channel_inbox.Content` / `t_channel_inbox.PeerId` 为 nullable（模型本就允许为空，非脚本漏洞）。
- 模型一致性：`dotnet ef migrations has-pending-model-changes --context QuantumMySqlDbContext` → 「No changes have been made to the model since the last migration」。
- 离线门禁：`dotnet test Quantum.API/Quantum.API.sln` 本轮实测全绿（失败 0；通过数以命令输出为准，本轮为 681）。
- 前次部分执行遗留的 6 个列（`t_channel_reply_route` 3 列 + `t_channel_outbox` 3 列）已在目标形态，重放幂等，无需人工回滚。
- 遗留风险（未解除）：本次只在**这一台** MySQL 实例上验证过升级路径。按 `AGENTS.md` 双库纪律，其它环境（含 SQLite 存量库）升级前仍须各自备份并演练 Up/Down。

### 建议

**QD-2（建议·既有缺陷，非本次改动引入）会话页通知气泡样式类丢失 + 每条消息刷一条 Vue 警告**

- 现象：`src/view/chat/components/NotifyCard.vue:9` 写 `<rich-text class="notify-body" .../>`，
  而 `RichText.vue` 渲染多根节点，Vue 报
  `Extraneous non-props attributes (class) were passed to component but could not be automatically inherited`。
- 实测：同一次会话页载入产生 **约 30 条** 该警告；DOM 中 `document.querySelectorAll('.notify-body').length === 0`，
  但 `<style>` 里 `.notify-body` 规则存在（`NotifyCard.vue:124`）→ **样式规则永不命中，通知正文样式实际失效**。
- 归因：该文件未在本次工作树改动清单里（属 HEAD 既有问题），是回归走查顺带暴露。
- 修法：给 `RichText.vue` 根节点接 `class`（单根或显式 `v-bind="$attrs"`），或在 `NotifyCard.vue` 外层套 `<div class="notify-body">`。

**QD-3（建议·文案一致性）投递记录弹窗标题用原始枚举名**

- 现象：卡片用友好名「QQ 官方机器人 · 私聊」，弹窗标题却是「QQBot · 投递记录（最多 50 条）」，微信侧同理（`WeixinBot`）。
- 位置：`quantum-web/src/view/channel/index.vue:96` 直接用 `deliveryPlatform` 拼接。

**QD-4（建议·功能缺口，用户口述要求）QQ 侧没有扫码入口，与「两端都用扫码完成对接」的要求不符**

- 用户在本轮走查中途明确要求：**QQ 与微信机器人都应使用扫码完成对接**。
- 现状：微信侧有「扫码绑定 → 二维码 → 轮询 → 确认」完整入口；**QQ 侧只有「配置机器人（AppID/AppSecret）→ 发起绑定挑战（要测试账号先给机器人发挑战码）」**，页面上不存在任何扫码元素（`quantum-web/src/view/channel/index.vue` 的 `v-if="row.Platform === 'QQBot'"` 分支）。
- 影响：两平台绑定动线不一致，运营要记两套流程；按用户口径这属未完成项，不是本次走查能判通过的项。
- 需先澄清的技术前提：QQ 官方机器人（OpenAPI/C2C）是否有可授权的扫码登录/扫码绑定能力；若官方无此能力，需把「QQ 只能 AppID+AppSecret+挑战码」写进文档并对齐预期，而不是在前端造一个假扫码壳。

### 提示

- **QT-1** 通道页保存失败后弹窗保持打开，表单里已输入的 AppID/AppSecret 仍在（`qqModal` 的 `after-leave` 才清 Secret）。安全上可接受（本页不回显、不缓存到 storage），但若期望失败即清空需显式处理。
- **QT-2** 关闭模态后浏览器控制台出现一条 `Blocked aria-hidden on an element because its descendant retained focus`（Naive UI 模态通用行为，非本页面独有）。
- ~~**QT-3** 路由无 catch-all~~ —— **本条撤销，属本人误报**。复查发现 `src/router/routers.js:189` 已注册 `/:pathMatch(.*)*` → `view/error-page/404.vue`；复测未登记路径 `#/env/env` 时页面确实渲染「404 Oh~~您的页面好像飞走了~ / 返回首页 / 返回上一页(4s)」。先前判「空白壳」是因为只截取了正文前 33 个字符（那部分是壳文案），把 404 内容截掉了。
- **QT-4** 侧栏「日志中心」分组标题与其唯一子项标题完全同名（`/logs` 与 `/logs/index` 的 title 都是「日志中心」），两项在侧栏同屏出现，点选时无法从文案区分分组与页面。属信息架构小瑕疵。
- **QT-5** 「解绑」文案说「将关闭通道、作废旧待发消息与回复上下文」，实测行为与之相符，但解绑后 `Configured` 仍为 `true`、机器人掩码仍显示（QQ 侧保留凭据，仅微信侧清 `CredentialCiphertext`，见 `ChannelManagementService.UnbindAsync`）。页面上**没有彻底删除机器人配置的入口**，换号只能走「配置机器人 → 更换」二次确认。逻辑自洽，但对运营而言容易误以为解绑即清空；建议在卡片上补一句「已配置未绑定」态说明或提供删除入口。
- **QT-6** 通道页只在挂载时拉一次状态（`onMounted(load)`），页签被 keep-alive 后**切回不会自动刷新**，管理端可能长时间盯着旧的机器人掩码/绑定态。建议在 `onActivated` 里补一次 `load()`，或加个轻量轮询。

## 4. 未覆盖项与解除条件

| 项 | 为什么没做 | 需要什么 |
| --- | --- | --- |
| 发起 QQ 绑定挑战 / 微信扫码二维码 / 验证码链路 / 确认绑定 / 启用通道 / 入站消息气泡 / Outbox 重试 | 会向 QQ/微信官方端点发起真实请求并需要真机配合，用假凭据只会制造外部失败噪声与平台侧风控痕迹，本链不擅自做 | 官方测试机器人 AppID/AppSecret、已验证私聊测试账号、可扫真机的微信测试号；以及正式主密钥（**须与本轮临时密钥不同**，本轮配置数据已全部删除） |
| 入站消息 → App/Web `channel:qqbot`、`channel:weixinbot` 会话气泡；Outbox 人工重试 | 依赖上一行 | 同上 |
| Q-T4 执行记录翻页、Q-T5 日志正文 | 该 MySQL 库 `t_task_run` 总共只有 1 条记录（实测 `COUNT(*)=1`），造不出第二页；唯一那条的日志正文区为空 | 需要连跑多次某个任务：现有任务都会打外部平台，属外部副作用，需用户放行后才做 |
| App（安卓）端 UI | 本目标口径为「启动服务后」的 Web 走查，安卓需模拟器/真机另开一轮 | 确认后本链可续跑 `gradlew assembleDebug` + 模拟器走查 |
| `npm run test` 前端单测 | 属离线门禁，另一执行链已记录通过（计数以其命令输出为准）；本链改的是后端迁移文件，未动前端 | — |

## 5. 其它需知悉的环境实况（归因留档）

- 走查窗口内（本地 17:48:10–17:48:20）`t_task_run` 新增 1 条记录：任务「音乐搜索」，`TriggerSource=2`（UI 显示「指令」），`Status=2`（成功），Attempt=1，耗时 9.75 秒。**本链未点过任何「执行」或发送任何会话指令**，该记录由启动后的消息泵/指令通道消费，或由同一 `:8080` 上的另一操作方触发（vite 由非本链进程于 17:34 启动）。如需精确归因，请核对当时是否有人在该浏览器操作。
- 后端进程与 `:5088` 端口现**仍在运行**（本链未停），如需收口：结束当时监听 5088 的 dotnet 进程即可；`quantum-release/`、`git` 提交（add/commit/push）本链均未触碰。
- **本链写过的东西，逐条列清**：
  - 工作树代码：仅 `MySqlMigrations/20260927085211_ChannelRequiredKeys.cs` 与 `SqliteMigrations/20260927085208_ChannelRequiredKeys.cs` 两个文件的 `Content` 列定义（QD-1 修复），加本文档一个新文件。未改 `appsettings.json`、未改前端代码、未改其它执行链的文档。
    **（第二轮追加）** 用户要求全部修复后，本链另改了 5 个前端文件 + 1 个后端种子文件：`chat/components/RichText.vue`、`chat/components/NotifyCard.vue`、`view/channel/index.vue`、`view/components/setting.vue`、`Quantum.Web/jsons/menu.json`；对共享 MySQL 库追加 1 行 `t_menu.Title` 更新（QT-4）。门禁：`npm run test` 88/88、`npm run build` 成功（产物写到既有输出目录 `quantum-release/wwwroot`）。全程仍未 `git add`/commit/push。
    **（第三轮追加）** 菜单重排又改了：`Quantum.Web/jsons/menu.json`（结构）、`Quantum.Application/MenuService.cs`（重置保留隐藏 + 通道入口归属）、`view/setting/menu.vue`（重置文案），新增 `Quantum.API.Tests/MenuStructureTests.cs`；并在真实实例上点了一次「重置菜单」（`t_menu` 全表按新结构重建，7 个自定义数据子页由 `CustomDataTitle` 原样再生成，收尾无任何项被隐藏）。详见第 8 节。
  - 远端 MySQL 库结构：应用了 `ChannelRequiredKeys` 迁移（本轮修复后成功落库，`__EFMigrationsHistory` 多 1 条）。
  - 远端 MySQL 数据：补测期间写入 1 条 QQ 机器人探测记录（假 AppID）+ 1 条白名单选择；**已按主键精确删除**，收尾实测 `t_channel_account / _allowed_command / _binding / _inbox / _outbox / _reply_route / _cursor` 全部 0 行，与开工前一致。
  - 仓库外：`%TEMP%\quantum-ui-qoder\` 下的一次性密钥文件与运行日志（密钥文件已不再被任何进程引用，可直接删除；**不要**把它当作正式主密钥复用）。
  - 服务重启：首轮以默认配置启动（无密钥）→ 补测轮带临时密钥重启并验证 QD-1 修复 → 收尾再按**默认配置（不带本链临时密钥）**重启一次，实测启动日志 0 条 ERROR、`:5088` 正常监听、通道页回落「未配置」。当前后端仍在运行（监听 5088 的 dotnet 进程），临时密钥文件已不再被任何进程引用。

## 6. 结论

- 消息通道页：无主密钥的「拒绝态」12 条用例全通过（门禁、前端校验零请求、后端拒绝文案、脏数据兜底）；换临时主密钥后的「成功态」再补 6 条通过（保存→掩码→门禁重算→白名单选择与持久化→二次确认后取消→解绑），逐条见 Q-M 与 Q-M2 两张表。
- 执行记录抽屉的列宽/固定布局改动达到预期，无裁切无溢出。
- Q-R 表逐条列出的既有页面（会话/AI 助手/任务/脚本版本/外触内执/数据管理/环境变量/快捷回复/Docker/系统设置/菜单管理/AI供应商/AI设置/日志中心）回归未见白屏、未见错误 toast。
- **QD-1 曾为阻塞项**（MySQL 迁移在 LOB 列上带默认值 → 永远应用失败并已部分改列），本轮已修复并在真实 MySQL 实例上复验：迁移登记成功、启动无 ERROR、双库文件同步改动、离线测试全绿。
- 仍待收口：QQ 挑战 / 微信扫码 / 入站气泡 / Outbox 重试需真机与正式密钥（第 4 节）；QD-2、QD-3、QD-5、QT-1、QT-2、QT-4、QT-5、QT-6 已在第 7 节修完并复验；QT-3 经复查为本人误报已撤销；**QD-4（QQ 也用扫码）不是本链可自决的实现，见第 7.3 节**。

## 7. 修复记录（同日第二轮，用户要求「全部都要修复」）

### 7.1 已修并逐条复验

| 编号 | 改了什么 | 文件 | 复验证据 |
| --- | --- | --- | --- |
| QD-2 | `RichText` 根节点是 fragment，父级传入的 `class` 无法自动继承 → 加 `inheritAttrs: false` 并把 `$attrs` 显式绑到正文根节点；又因多根子组件不带父作用域 id，`NotifyCard` 的 `.notify-body` 规则改用 `:deep()` | `chat/components/RichText.vue`、`chat/components/NotifyCard.vue` | 整页刷新后 `.notify-body` 命中 29 个元素（修复前 DOM 里 0 个），computed 样式 `font-size:13.5px / -webkit-line-clamp:3 / overflow:hidden / margin-top:4px` 全部生效；控制台 `Extraneous non-props attributes` 不再新增（修复前后两次抓取缓冲区计数同为 81，无新增条目） |
| QD-3 | 抽 `platformName()`，卡片标题与投递记录弹窗标题统一用友好名 | `view/channel/index.vue` | 弹窗标题实测为「QQ 官方机器人 · 投递记录（最多 50 条）」（原为 `QQBot · …`） |
| QD-5（新发现） | 系统设置页把后端的**数值**字段直接 `v-model` 到 `n-input`，触发 `Invalid prop: type check failed for prop "value". Expected String \| Array, got Number` → 改为 `:value="String(...)"` + `@update:value="Number(v) \|\| 0"`，模型侧仍是数字 | `view/components/setting.vue`（消息发送间隔 / 队列处理间隔） | 页面两个输入框实测显示 `"0"` 与 `"100"`；**未点保存**（保存会写服务端 `appsettings.json`，不在本链授权范围） |
| QT-1 | `saveQq` 的 AppSecret 清理移到 `finally`：保存失败也不把凭据留在输入框与组件里 | `view/channel/index.vue` | 用假凭据触发失败（无主密钥）后实测：AppID 仍保留 `1234567890`（便于改对再试），AppSecret 输入框为空 |
| QT-2 | 所有模态都关闭时主动 `document.activeElement.blur()`，消除「焦点留在被 aria-hidden 容器内」的浏览器告警 | `view/channel/index.vue`（`anyModalOpen` + `watch`） | 关窗后实测 `document.activeElement` 由 `INPUT` 变为 `BODY` |
| QT-4 | 「日志中心」分组与其唯一子项同名 → 子项改名「系统日志」：`menu.json`（新装实例）+ 存量库单行 `UPDATE t_menu SET Title='系统日志' WHERE Name='logs-index' AND ParentName='logs'`（affected 1） | `Quantum.Web/jsons/menu.json`、存量 `t_menu` 一行 | `GET /api/Menu` 回读标题为 `["日志中心"(分组), "系统日志"]`；未使用会全表删除重建的 `PUT /api/Menu`，避免动到其它自定义菜单 |
| QT-5 | 卡片标签由「已启用/已关闭」二态改为四态：`已启用 / 已绑定未启用 / 已配置未绑定 / 未配置`；解绑确认文案补一句「机器人配置本身不会被删除，换号请在「配置机器人」里覆盖」 | `view/channel/index.vue` | 未配置态实测标签为「未配置」（原显示「已关闭」，与「机器人 未配置」自相矛盾） |
| QT-6 | 页签被 keep-alive 缓存后切回不刷新 → 增 `onActivated(load)`（用一次性标志与 `onMounted` 去重，首挂载不重复请求） | `view/channel/index.vue` | 以 XHR 探针计数：点「刷新状态」=1 次；切到 `/env/index` 再切回 `/settings/channel` = 第 2 次（修复前不增长） |

未改后端代码；上一轮的两个迁移文件改动保持不变。

### 7.2 撤销的误报

- **QT-3**：`routers.js:189` 早就注册了 `/:pathMatch(.*)*` → 404 页；本人先前只截取正文前 33 字符（全是布局壳文案）就判了「空白壳」，复查已确认 404 页正常渲染并会自动返回。教训：**截断的取证口径会造出假缺陷**。

### 7.3 QD-4（QQ 也用扫码）：不是能自决的实现项，需用户选路

调研结论（官方文档，非推测）：

1. QQ 侧确实存在「扫码接入」，但它发生在 **OpenClaw / 云厂商控制台**：新版（OpenClaw 2026.5.19+）在控制台「通道 Channels → 添加通道 → QQ → 扫码接入 IM → 扫码配置」用手机 QQ 扫码授权后机器人自动生效，**免 AppID/AppSecret**；旧版才需要去 `q.qq.com` 开放平台创建机器人并回填 AppID/AppSecret。
2. 本仓库 `docs/QQ微信官方龙虾通道接入计划.md:1` 记录：**用户已于 2026-09-27 明确否决 OpenClaw 路线**（「不使用 OpenClaw」），现行代码是原生 C# 通道（`Quantum.Application/Channels/`）。
3. 也就是说，「QQ 也用扫码」在官方能力下依赖的正是被否决的那条路线；QQ 开放平台自身的扫码只是**开发者登录**，没有对外的「第三方扫一下码就把机器人绑到此私聊」的授权 API。微信侧能扫码，是因为走的是官方插件 CLI 的扫码登录态。
4. 因此三条路只能由用户挑，本链不擅自实现：
   - **A 恢复 OpenClaw/控制台扫码路线**（与既有否决冲突，需明确改口）；
   - **B 接受 QQ 走「凭据 + 私聊挑战码」**，把「两端都扫码」的要求收敛为「仅微信扫码」；
   - **C 折中：引导式二维码**——页面生成指向 `q.qq.com` 的二维码，用户手机扫码在官方页面创建/查看机器人后再回填凭据。移动端省事，但**凭据模型不变**，不能宣称是扫码授权对接。

> 我明确排除了「在前端做一个看起来像扫码的壳」——那会把未经验证的外部流程伪装成已实现，属于产品性造假。

## 8. 菜单结构按功能重排 + 重置保留隐藏状态（同日第三轮）

### 8.1 重排后的结构（顶级 8 个 → 5 个功能组）

| 分组 | 子页 |
| --- | --- |
| 消息中心 `/chat` | 会话、AI助手、消息通道、外部推送 |
| 数据管理 `/custom-data` | 标题管理 + 各自定义数据页（由 `CustomDataTitle` 联动生成） |
| 任务与脚本 `/task` | 任务管理、脚本编辑、脚本版本、外触内执、快捷回复、环境变量 |
| Docker管理 `/docker` | 概览、容器管理、镜像管理、网络管理、卷管理 |
| 系统管理 `/settings` | 系统设置、菜单管理、AI供应商、AI设置、系统日志 |

消灭的三个单页顶级组：环境变量、快捷回复、日志中心（此前各自成组，与 8 个顶级项里的其它分组不对等）。

### 8.2 约束：叶子完整路径一律不变

换组会把路由前缀换掉，从而打断深链。因此**移动到新组的叶子改用绝对路径**（Vue Router 中子路径以 `/` 开头即绝对，本仓库 `docker/*` 与 `custom-data/*` 早就这么用）：`/env/index`、`/replay/index`、`/logs/index`、`/settings/channel` 全部保持原地址。
实测受益方：`ai/index.vue` 里硬编码跳转 `/logs/index`、Docker 概览跳 `/docker/containers` 等均未受影响；面包屑则按新分组显示（如「系统管理 / 系统日志」）。

### 8.3 改了哪些代码

- `Quantum.Web/jsons/menu.json`：按 8.1 重排；顺带把 `docker-images` 的图标从占位的 `fa-house` 改为 `fa-box-archive`，`env-Index` 名称大小写规范为 `env-index`，`脚本指令` 标题改为 `任务管理`。
- `MenuService.ReseedAsync`：重置前先按 `Name` 记下当前 `HideInMenu` 的项，重建后回填 → **重置只重建结构，不回收运营态的隐藏标记**（用户需求「重置菜单 不恢复隐藏显示状态」）。已不在新结构里的项自然丢弃标记。
- `MenuService.GetAsync` 的消息通道补入入口：改为优先挂到「消息中心」组（实例无该组时退回「系统管理」，不产出游离顶级项），路径同步为绝对 `/settings/channel`。
- `setting/menu.vue`：重置确认文案由「这将恢复所有菜单项」（与事实不符）改为「菜单层级会回到 menu.json 的定义，但你标记为隐藏的菜单项会保持隐藏」。
- 新增 `Quantum.API.Tests/MenuStructureTests.cs` 三条用例（入口归属优先消息中心 / 重置保留隐藏且不误隐藏未标记项 / 分组结构与叶子完整路径不重复）。**未改动并行链的 `ChannelMenuTests.cs`**——它构造的库只有 `settings` 组，走的是我保留的退回分支，仍绿。

### 8.4 验证

- 后端：`dotnet test` 684/684 全绿（681+3）。前端：`npm run test` 88/88、`npm run build` 成功。
- 真实实例点「重置菜单」→ `GET /api/Menu` 回读为 5 个根分组，自定义数据 7 个子页完整保留。
- 端到端实测隐藏语义：`Docker管理` 点隐藏 → 状态「已隐藏」→ 点重置菜单 → **状态仍为「已隐藏」** → 点显示 → 回到「正常」（收尾时无任何项处于隐藏，与开工前一致）。
- 逐个访问换组后的地址：`/env/index`、`/replay/index`、`/logs/index`、`/settings/channel`、`/external-push/index`、`/chat/ai`、`/task/index` 均正常渲染（注：判 404 只能看 404 视图特征串，`/logs/index` 页面上有日志行含「404」文本，用 `has404=/404/` 会误判——本轮就差点再次踩到同一类假缺陷）。

### 8.5 两处需要你过目的取舍
1. **外部推送**（`external-push/index`）此前只存在于路由表、没有任何菜单入口（按其代码注释是「由菜单管理页手工登记」）。按功能归类后我把它补进了「消息中心」。若不想看到，在菜单管理里点隐藏即可——现在重置不会再把它放出来。
2. **数据库迁移页**（`database-migration/index`，页面自身连喊三遍「请不要轻易尝试使用该功能」）我**没有**给它加入菜单：它不属于日常功能动线，且暴露入口的风险大于收益。

## 9. 用户反馈三条（同日第四轮）

### 9.1 QD-6（阻塞级视觉缺陷，已修）通道页弹窗被撑满视口

- 现象：配置 QQ 机器人、微信协议版本核对等弹窗实测宽度 **1042px = 整个视口**，设计值本应是 560px。
- 根因：宽度写在 `<style scoped>` 里，而 `n-modal` 内容被 **teleport 到 body**，节点上没有本组件的 scope 属性，编译后的 `.modal[data-v-…]` 永不命中 → 样式整体失效，Naive 的卡片默认占满容器。与 QD-2 是同一类「scoped × 传送/多根」盲区。
- 修法：把弹窗宽度规则挪到同文件的**非 scoped** 块，并用 `channel-` 前缀避免影响其它页面；同时按用户意见把普通弹窗从 560px 收窄到 400px。
  - `.channel-modal` 400px（QQ 配置 / 微信版本核对 / 挑战码 / 扫码）
  - `.channel-modal--form` 520px（安全快捷回复，多选要留宽度）
  - `.channel-modal--wide` 850px（投递记录表格）
  - 三者都带 `max-width: calc(100vw - 32px)`，窄屏仍安全。
- 复验（真实页面量测）：配置机器人 400px、微信版本核对 400px、投递记录 850px；`npm run test` 88/88、`npm run build` 通过。
- 未覆盖：安全快捷回复弹窗的 520px 无法在无主密钥状态下打开（按钮按门禁禁用），需下轮带密钥复量。

### 9.2 「配置 QQ 机器人不能扫码？」——见 QD-4，结论是不能，且不是实现偷懒

QQ 侧唯一的官方「扫码接入」发生在 **OpenClaw / 云厂商控制台**（新版免凭据、扫码授权后机器人自动生效）；而本仓库 `docs/QQ微信官方龙虾通道接入计划.md:1` 记着你已否决 OpenClaw 路线。QQ 开放平台的扫码只是**开发者登录**，没有对外「第三方扫码即把机器人绑到此私聊」的授权 API。因此现行代码走 AppID/AppSecret + 私聊挑战码是这条约束下的唯一自洽实现。三选一：A 恢复 OpenClaw/控制台路线；B 收敛为「仅微信扫码」；C 折中做引导式二维码（扫码跳到官方页面创建/查看机器人，再回填凭据，凭据模型不变）。

### 9.3 「微信扫码为什么还要手填协议版本？不能自动？」——一半是设计约束，一半是实现不一致（待你放行才改）

- 不能自动查的部份：`iLink-App-ClientVersion` 是**客户端自报的兼容性标识**，只随请求头发出去（`ChannelNetwork.cs:51`），微信后端不提供「返回你该用哪个版本」的发现接口。要"自动"只能在代码里硬编一个猜测常数，而计划文档明确禁止把没写明的经验值当常数、也禁止冒称官方身份，所以实现留给了管理端核对。
- 确实不一致的部份（我建议修）：运行时路径已支持环境变量兜底——`WeixinChannelWorker.cs:44`、`ChannelDeliveryWorker.cs:122` 在凭据里没有版本时读 `QUANTUM_CHANNEL_WEIXIN_CLIENT_VERSION` / `QUANTUM_CHANNEL_WEIXIN_CHANNEL_VERSION`；但**扫码起点** `WeixinQrLoginService.StartAsync` 先调 `ValidateVersions` 强校验，留空直接抛错，等于绕过了已有的兜底，每次都要手打。
- 建议改法（约 5 行，属协议敏感链路，等你点头再动）：`StartAsync` 收到空值时先取上述两个环境变量，取到就用、取不到再抛错且错误文案点名变量名；前端把两个输入改为可留空、占位文案写明「留空则用服务端环境变量默认值」。这样配好一次 env 之后，扫码就不必再填，同时不往代码里塞猜测常数。
