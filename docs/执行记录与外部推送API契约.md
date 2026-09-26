# 执行记录与外部推送 API 契约

> 一期功能增强（G1~G4）与独立交付包 G-Push 的接口契约与运维口径。
> 编制日期：2026-09-26。计划来源：`docs/功能增强分期实施计划.md`。
> 说明：仓库原有的 `docs/security/公网部署安全审计-2026-09-14.md`、`docs/App端API契约.md`、AI 计划文档在当前检出中不存在，
> 且 `docs/` 从未被 git 跟踪（`git log --all -- docs/**` 为空），已确认**不可找回**。
> 经仓库所有者确认：以分期实施计划文档为契约基准推进 G1~G4 与 G-Push（本文件即其落地结果）。

## 0. 全局约定

- HTTP 恒 200 信封：`ResultModel{Code,Message,Data}`。`Code=200` 成功 / `401` 认证或权限 / `500` 业务失败（`BusinessException`）。
  业务错误**不用**非 200 状态码表达；边缘层（Kestrel/反代）在进入 MVC 前先行拒绝的场景（如请求体过大 413）不在该保证范围内。
- 时间：后端持久化一律 UTC；界面按客户端时区显示。字段名以 `Utc` 结尾者即 UTC。
- 权限：管理员判定一律看 JWT 正向 `Manager="true"` claim。
- 「受理成功」不等于「脚本执行成功」：`exec-task` / `execute-runs` 返回 true 或 RunId，只表示运行记录已落库。

## 1. 执行记录（G2）

### 1.1 `GET /api/TaskRun`

查询参数：`taskId`（可空）、`status`（可空，枚举名）、`page`（≥1）、`pageSize`（1~100，超出服务端收敛）、`days`（默认 90）。

返回 `PageResult<TaskRunRow>`：`Data` / `TotalCount` / `Page` / `PageSize`。

`TaskRunRow` 字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | string | RunId |
| RootRunId | string | 根执行 Id（首次尝试等于自身，重试链共享） |
| Attempt | int | 第几次尝试，1 起 |
| TaskId | string? | 任务 Id（影子试运行/无持久任务为 null） |
| TaskName / ScriptFile | string | 任务名与脚本路径**快照**（任务改名或删除后仍可正确显示） |
| TriggerSource | string | `Manual/Cron/Command/OpenTrigger/Shadow/Retry` |
| Status | string | `Pending/Running/Succeeded/Failed/Rejected/Canceled/Interrupted` |
| FailureCode | string | 受控失败码（`InvalidScriptPath/ScriptMissing/UnsupportedScriptExtension/ScriptReadFailed/GateBlocked/CompileFailed/AssemblyLoadFailed/ScriptException/CanceledByShutdown/CanceledByForceEndTime/EngineFault/None`） |
| SafeSummary | string? | **已脱敏、≤512 字符**的安全摘要；不含堆栈、环境变量明文、令牌 |
| StartedAtUtc / FinishedAtUtc / NextAttemptAtUtc | DateTime? | 起止与下次重试时刻 |
| CreatedAtUtc | DateTime | 受理时间（列表排序键，UTC + Id 倒序） |
| IsRetry | bool | 是否重试尝试 |
| CancelReason | string? | 中止原因（如「脚本已变更，取消自动重试」） |
| ElapsedMs | long? | 起止齐全才有值，否则 null（不把未知画成 0） |

权限：非 Manager 令牌不返回 Manager 任务的执行记录（按 `ManagerSnapshot` 服务端过滤，列表与总数同步收敛）。

### 1.2 `GET /api/TaskRun/{runId}`

返回 `TaskRunDetail{ Run, Attempts[], LogAvailable, LogId }`。`Attempts` 为同一根执行的全部尝试（按 Attempt 升序），用于时间轴。

- 越权与「不存在」返回同一份失败文案，不区分（避免用该接口探测 RunId 是否存在）。
- 只回受限 `LogId`，**不回任何文件路径**。`LogAvailable=false` 表示日志已清理或尚未落库，前端显示占位。
- 日志详情仍走 `GET /api/Logs/details/{id}`，该端点原有的受限类型校验保留，并新增 Manager 任务归属校验（详见 §5）。

### 1.3 `POST /api/Task/execute-runs`

请求体：任务 Id 数组。返回 `[{ TaskId, RunId }]`。

旧端点 `POST /api/Task/exec-task` 行为不变（仍返回 `bool`），内部复用同一执行服务；待客户端全部迁移后再讨论移除。**既有成功信封语义未改。**

### 1.4 `POST /api/TaskRun/{runId}/retry`（`[ManagerOnly]`）

手动重新执行：产生**新的**根执行 Id（不复用旧执行链、不计入旧链重试次数），并留操作日志。任务已删除或该执行无持久任务时返回业务失败。

入口：Web 任务页 →「执行记录」抽屉 → 详情内「按当前脚本与环境变量再跑一次」（带确认弹窗，受理成功后详情直接跳到新 RunId）。App 首期不提供（写操作，见 `TaskRunsScreen` 注释）。

## 2. 失败策略（G3）

### 2.1 `GET /api/TaskRun/policy/{taskId}`

无配置行时返回全默认值（**不写库**）：`RetryCount=0`、`BackoffSeconds=60`、`AlertAfterConsecutiveFailures=1`、`SendRecovery=false`、`CooldownMinutes=60`、`Enabled=false`。
即存量未配置任务**只跑一次、不重试**。

### 2.2 `PUT /api/TaskRun/policy/{taskId}`（`[ManagerOnly]`）

服务端逐项 clamp，UI 只辅助：

| 字段 | 范围 | 默认 |
|---|---|---|
| RetryCount | 0~3 | 0（= 关闭自动重试） |
| BackoffSeconds | 30~3600 | 60 |
| AlertAfterConsecutiveFailures | 1~10 | 1 |
| SendRecovery | bool | false |
| CooldownMinutes | 0~1440 | 60 |
| Enabled | bool | false |

重试语义：

- 仅 `Failed`（脚本抛异常）且 `Enabled=true`、`RetryCount>0`、任务仍启用、脚本未变更时排重试；
  `Rejected/Canceled/Interrupted` 一律不重试；指令触发/外触/影子试运行不参与自动重试（只记录结果）。
- 第 n 次退避 = `min(BackoffSeconds × 2^(n-1), 3600)` 秒，`NextAttemptAtUtc` 持久化，由托管后台轮询（10s）领取。
- **重试会重放脚本的外部副作用，仅适合幂等脚本**；首次配置默认不自动重试。
- 同任务已有非终态记录时，到期重试顺延 30 秒，累计 20 次后放弃并记 `CancelReason`（不排队重入，不与手动/Cron 互踩）。
- 任务禁用/删除、脚本变更、策略关闭 → 待重试排程立即作废。
- 服务重启：超过 2 分钟宽限仍挂 `Running` 的记录判 `Interrupted`（真实结果不可知，既不重放也不判成功）。

## 3. 规则告警（G4）

- 计数单位是**根执行**：同一 `RootRunId` 的多次尝试只算一次；只在链条走到终局（最后一次尝试确定失败）时评估。
- 连续最终失败达 `AlertAfterConsecutiveFailures` → 生成 `FailureOpened` 事件；此前处于打开状态且本次成功 → 生成 `Recovered` 事件。
- 去重键 `(TaskId, AlertType, RootRunId)` 唯一；冷却期内不重复投递，冷却后仍失败才可再次摘要提醒。
- 事件与状态同事务写入；投递由后台按事件幂等执行并记录 `DeliveryStatus/DeliveryAttempts`（上限 5 次）。
  **不承诺 WS 网络传输 exactly-once**，客户端按 `MsgId` 去重。
- 通知走 `AppPushService`（含会话镜像），`category=task`；深链 `quantum://task/{taskId}/runs/{runId}`。
  App 侧全局免打扰/分类偏好只影响本地弹窗，与服务端「规则是否生成消息」是两件事。
- 与存量口径的兼容（审核项 R-03）：**未配置或 `Enabled=false` 的任务保留旧的「一次失败一条通知 + 一次 AI 分析」**；
  策略一旦启用，旧路径让位，失败通知只由最终失败事件投递（`TaskRunCompletion.PolicyOwned` 即该判定）。
- `AgentAutoAnalyze` 只在最后一次确定失败后触发，不因每次重试重复触发。

## 4. 保留与容量

- 执行记录默认保留 90 天（`QuantumRuntimeOptions.RunRetentionDays`，进程内可调，暂不入库配置）；
  告警事件保留不少于执行记录。清理批量 500 条/轮、限速 6 小时一次，**不删运行中/待重试数据**，不删脚本版本，不改变现有日志清理行为。
- 列表分页上限 100 与 `(TaskId,CreatedAtUtc)`、`(RootRunId,Attempt)` 唯一、`(Status,NextAttemptAtUtc)` 索引为必须项。

## 5. 任务日志的 Manager 归属过滤（R-02）

`GET /api/Logs` 与 `GET /api/Logs/details/{id}` 的「任务日志/指令触发」类型虽对全员开放，但内容可能属 Manager 任务：

- 列表：非 Manager 令牌额外按「Manager 任务脚本名 → 同款净化目录名」过滤，并用运行记录 `ManagerSnapshot` 的
  `LogId` 子查询兜住**任务已删除**后的历史日志；列表与总数同步收敛。
- 详情：受限类型判定之外再走 `IsManagerTaskLogAsync`，越权返回信封 `Code=401`（不允许仅凭 LogId 直读）。

## 6. G-Push：第三方受限富文本推送

### 6.1 鉴权

`Authorization: PushKey {credentialId}.{plainSecret}` — 只此一种，凭据由管理端创建（**默认禁用**，需显式启用）。

- 该凭据**不是** JWT：不产出身份/角色声明，无法访问任务/日志/管理等任何旧端点。
- 旧 Open AppKey、10 分钟 JWT、Manager JWT、OpenTrigger secret **均无推送权限**。
- 无凭据/格式错/Id 不存在/已禁用/已过期/密钥不符 → 一律 HTTP 200 + `Code=401`，文案相同（不区分原因，防凭据探测）。
- 无效尝试按来源 IP 限流（5 分钟窗口内 20 次）；未核对 `KnownProxies`/ForwardedHeaders 前不轻信 XFF。

### 6.2 `POST /api/ExternalPush/messages`

请求头：上述 `Authorization` + `Idempotency-Key: <8~128 个 ASCII 可打印字符且无空白>`。

```json
{
  "Title": "设备告警",
  "SessionTitle": "机房监控",
  "Content": "{{tag:red|紧急}} 温度过高\n{{link:查看详情|https://example.com/status}}"
}
```

限制：`Title` 必填、trim 后 ≤100 且不含换行/控制字符；`Content` 必填、≤4096 字符；`SessionTitle` 可选、非空时 1~80；
请求体 ≤8 KiB（UTF-8 字节计）。服务端**不接受**客户端指定来源、会话键、分类、跳转地址、接收用户。

成功 `Data`：

```json
{ "NotificationId": "...", "MsgId": "...", "SessionKey": "external:<凭据Id>:<sha>", "Duplicate": false }
```

- `200` 只代表**通知与会话消息已持久化**，不代表设备已收到/已展示；第三方必须判断信封 `Code`。
- 提交后的 WS 广播与送达标记都是 best-effort，失败只留痕，不回改已提交的成功结论。
- 同「凭据 + 幂等键 + 归一化 Title/SessionTitle/Content」重放 → 返回原 Id 与 SessionKey，`Duplicate=true`，不重复广播；
  同键不同内容 → `Code=500`（安全文案）。**幂等记录被物理清理之前，同键恒按原请求去重；清理之后同键才算新请求**（保留窗口 72 小时）。
- 标题/正文/会话标题均不记录原文到请求日志或错误日志。

脱敏示例（密钥为占位符）：

```bash
curl -X POST https://<host>/api/ExternalPush/messages \
  -H 'Authorization: PushKey <CREDENTIAL_ID>.<PLAIN_SECRET>' \
  -H 'Idempotency-Key: 2026-09-26-room-a-0001' \
  -H 'Content-Type: application/json' \
  -d '{"Title":"设备告警","SessionTitle":"机房监控","Content":"{{tag:red|紧急}} 温度过高"}'
```

### 6.3 受限富文本契约（不是 HTML / Markdown）

语法沿用 `quantum-web/src/utils/richText.js`、`quantum-app/core/common/RichText.kt`、`Quantum.Plugins.QuantumText`：

- 六色强调：`{{red|文字}}`、`{{green}}`、`{{orange}}`、`{{blue}}`、`{{purple}}`、`{{gray}}`
- 胶囊标签：`{{tag:颜色|文字}}`
- 命名链接：`{{link:文字|https://绝对地址}}`
- 允许换行与普通文字；单条最多 **50 个**有效标记。

服务端拒绝（选定「拒绝」而非「按字面显示」，两端一致）：未知/残缺标记、大括号不成对、
普通文本里的明文 URL（含 `www.` 与任何 `scheme://`）与危险协议、命名链接非绝对 https 或带 userinfo、
含尖括号 `< >` 的内容、超长与标记超限、裸 JWT/凭据形态不在校验范围内（服务端不落原文日志）。

> 尖括号按「拒绝」处理是对计划的一条**收紧偏差**：契约原意允许 `<script>` 作为字面文字显示，
> 但那要求 Web `RichText.vue`（`v-html` 拼接）与 App 富文本解析器先完成转义/链接规则的双端测试。
> 在该测试落地前不向第三方输入开放这条 HTML 可达面。

不支持图片/附件、富交互 `Payload`、自定义 `jump`、指定接收用户、访问 AI 工具或触发任务。

### 6.4 会话标题与归属

- 会话键 = `external:{凭据Id}:{SHA256(归一化标题)}`（小写十六进制），**不含明文标题**；不同接入方同名标题绝不混会话。
- 归一化：trim + Unicode NFKC，大小写敏感；`Notification.Title`（单条通知标题）与 `SessionTitle`（会话标题）独立，不得互换。
- 同接入方改标题 = 新会话，**不隐式改名、不搬迁旧消息**。
- `ChatSessionModel.DisplayTitle` 与 `ChatMessageModel.SessionTitle`（消息标题快照）在同一事务写入，
  使「仅做消息增量补拉」的离线端也能还原标题；`ChatSessionOverview.SessionTitle` 同名字段随会话列表返回。
- **两套命名风格各自内部一致**（审核项 R-11 的落地口径）：
  REST 一律 PascalCase（`SessionTitle`，服务端实体/DTO 原名直出）；WS 帧沿用本仓库既有的手写小写驼峰
  （`msgId/seq/session/contentType/...`，故 notify 帧里的键是 `sessionTitle`）。
  App 侧两套各有独立 DTO（`ChatMessageDto` 用 `@SerialName("SessionTitle")`、`WsFrame` 用 `sessionTitle`），
  映射到同一个本地列 `chat_message.sessionTitle`。
  旧客户端忽略未知字段仍能收消息（可能短暂显示 opaque 会话键）。
- `external:` 前缀固定留给外部会话：任务新增/修改/导入的会话名入口已禁止该前缀；启动时扫描存量冲突并以 ERROR 留痕，
  有冲突须先安全迁移再启用凭据。

### 6.5 滥用防护与运维

- 每凭据默认 60 次/分钟、1000 次/日，超限业务失败；计数源为幂等表本身（跨重启有效），计数异常不放行。
- 强制 HTTPS 或可信代理 TLS；代理与网关日志需屏蔽 `Authorization`。凭据丢失立即禁用/轮换。
- 生产 CORS 策略不为第三方放宽。
- 发布顺序：双库迁移与后端（**默认关闭**）→ 支持 `SessionTitle` 的 Web/App → 只给测试接入方启用凭据。
- 回退：先禁用凭据与停请求端点，保留新表/列与消息历史；**不在事故回退时执行 Down 删数据**。

## 7. 部署与验证状态

- 单实例调度约束：Quartz 由托管服务提供、调度状态内存重建，本期无跨节点选主/分布式锁。
  **不得横向扩容调度节点**；多副本需先补独立选主与持久调度设计，再启用自动重试。
- 新功能默认关闭：`RetryCount=0`、策略 `Enabled=false`、外部推送凭据 `Enabled=false`。运行记录采集默认开启（只增表不改行为）。
  说明：计划 §5 提的「运行记录采集可独立打开」未做成开关——带外部风险的能力（重试、告警、对外凭据）都已各自默认关闭、
  可独立启用，而记录本身只新增表且可按保留期清理；为它再开一条「记录/不记录」双写路径会让日志落库口径分叉，风险大于收益。
- 灰度观察项：卡住的 `Running`、待重试堆积、告警投递失败数、日志与 `t_task_run` 磁盘增长。
- 回退：先关策略与凭据（停新投递）并停后台领取，再退旧 Web/App；**事故回退时不执行 Down 删新表/历史数据**，
  回退顺序与 §2.2/§6.5 一致。迁移失败按维护窗口恢复备份，不带病启动。

### 7.1 已完成的验证（证据）

| 项 | 结果 |
|---|---|
| `dotnet test` | 659 全绿（SQLite 内存库；含 5 个新测试类 + 迁移链 + 鉴权隔离 + 中断恢复双口径。项数以命令实测为准） |
| SQLite 迁移链 | 从空库真跑 `Migrate()`：Init→TaskRunBaseline→ExternalPushBaseline，6 张新表、`DisplayTitle`/`SessionTitle` 两列、4 个 `CREATE UNIQUE INDEX` 均在，`GetPendingMigrations()` 为空，重复 `(RootRunId,Attempt)` 被唯一索引拒 |
| **MySQL 迁移链** | 本地一次性 `mysql:8.4` 容器（localhost:33069，用后即删）实跑：**新库全链 Up** → 校验 6 表 + 5 唯一索引 + 2 标题列齐备；**Down 回 Init** → 新表与新列全部消失、业务表数回到 27；**旧库增量升级**（停在 Init 再 Up）→ 只应用新两条迁移。**未触碰生产 NAS MySQL** |
| 大数据量压测 §3.6 | 各 50 000 行 `t_task_run`：SQLite 文件库 插入 0.16s／列表计数 0.26ms／分页 20 条 0.21ms／到期重试扫描 0.20ms／深分页 offset 20000 为 21.1ms；MySQL 8.4 插入 2.15s／计数 0.35ms／分页 3.4ms／到期扫描 3.2ms／深分页 offset 20000 为 32.2ms。`EXPLAIN` 实证命中 `IX_t_task_run_TaskId_CreatedAtUtc`（range + Backward index scan + Using index）与 `IX_t_task_run_Status_NextAttemptAtUtc`（range + Using index） |
| `quantum-web` | `npm run test` 88/88 全绿、`npm run build` 通过 |
| **浏览器点击冒烟** | 隔离 SQLite 实例 + `npm run dev`，真浏览器点完 G2/G3/G-Push 新 UI，**四个只在真点时暴露的缺陷已修**（抽屉刷新按钮落在不存在的 `#header-extra` 插槽、会话标题三处未传 `SessionTitle`、通知标题被双重 `【】` 包裹、自动刷新撞上「判失败/排定重试」两次写入的间隙而永久停机）。全过程与留证见 §9.2 |
| `quantum-app` | 全模块 `testDebugUnitTest` 通过；Room **v7→v8 非破坏迁移**双重验证——JVM 侧用真 SQLite 造 v7 库跑迁移 SQL，**Android 15（API 35）模拟器真机升级**：先装旧版建出 v7 库并播种 `outbox` 待发正文与带 `pickedKeys` 的消息，覆盖安装新版后实测 `user_version` 7→8、`chat_message.sessionTitle` 与 `chat_session.displayTitle` 均建出、两条前置数据**逐字存活**、启动无崩溃。注：`assembleDebug` 首次构建因本检出残留了另一份检出（`D:\gitee\quantum`）的 Gradle 缓存/中间产物而失败（dexing 报"文件位于根目录之外"），`clean` + 删 `.gradle`/`build` 后重建成功 |
| **发布产物形态复跑** | `dotnet publish -c Release` + `quantum-release/wwwroot`（正式前端分包）同目录、后端自托管静态站点、独立 SQLite，浏览器直连复跑全链路；**暴露两个发布形态专属缺陷**（新装无 `scripts/quantum` 致存脚本 500；启动恢复一次性 + 2 分钟宽限期致 Running 永久卡死），均已修并实测收敛。详见 §9.3 |
| **App 模拟器走查** | android-35 镜像新建 AVD 无头启动，装 debug APK 登录隔离实例：会话列表/会话页（外部推送标题与富文本退化）、任务页「历史」列表与详情（只读口径、时间轴、日志缺失提示）逐屏核对；修掉顶栏不显示推送标题、探活成功文案被渲染成红色登录错误两处。详见 §9.4 |
| **生产 MySQL 升级** | 已执行（备份 → 恢复演练 → 沙箱预演 → 等价性证明 → 生产应用）。6 张新表 + 2 列 + 5 个新唯一索引落地；表数 29→35；业务行数与升级前逐项一致（`t_task=9`、`t_log=1109`、`t_chat_message=121`、`t_app_notification=60`、`t_env=28`）。详见 §10 |

### 7.2 仍未验证（不得当作已验证）

- 生产 MySQL 的**应用侧验证**：库结构已升级，但线上镜像仍是升级前代码，新界面/新端点在正式镜像下的运行需随下次发布一起做。
- 浏览器点击（§9.2 开发形态、§9.3 发布产物形态）与安卓模拟器走查（§9.4）**均已做**；
  仍未验证的只剩三件：**Docker 交付镜像（`asupc/quantum`）内**的运行（含容器里的目录与挂载布局，
  §9.3 是 Windows 本机 publish 目录）、**第三方接入方 SDK 与真实反代/TLS/限流**下的现场联调、
  **物理手机 + 厂商 ROM** 上的系统通知展示（模拟器只验证了端内会话与页面，未验证厂商推送通道）。
- 第三方 SDK 实调与真实反向代理/TLS/限流配置下的行为（含 §6.5 与 R-08 的边缘层 413 边界）。
- 真实生产数据量下的容量与增长评估（压测用的是合成数据）。
- 时区口径的现场确认：后端 `DateTimeZoneHandling.Local` + `DateFormatString="yyyy-MM-dd HH:mm:ss"` 输出**不带时区标记**，
  本功能所有 `*Utc` 字段按 UTC 墙钟持久化，Web/App 侧均已按「补 Z 当 UTC 解读再转本地时区」处理；
  存量非 UTC 字段（如 `t_log.CreateTime`）仍按本地墙钟解读，两套并存，改动前须逐字段确认。

## 8. 计划审查台账闭环（R-01~R-12）

对应 `docs/功能增强分期实施计划.md` 的三轮审核记录。**计划文档本身不由本仓库实施方改写**（它可能是他人维护的评审件），
闭环状态记录在此处，供所有者核对后自行回写。

| 编号 | 原级别 | 闭环状态 | 代码/测试证据 |
|---|---|---|---|
| R-01 | 🔴 阻塞 | 已闭 | 终态与 `t_log` 行同一事务同步落库、`LogId` 预分配（`TaskRunService.CompleteAsync`）；日志行不在库时 `LogAvailable=false`，详情只显示占位。测试：`TaskRunPersistenceTests.Complete_WritesRunAndLogInOneCommit_*`、`UniqueRootRunAttempt_*` |
| R-02 | 🔴 阻塞 | 已闭 | 列表按「Manager 任务脚本名→同款净化目录名」过滤 + 运行记录 `ManagerSnapshot` 子查询兜任务删除后的历史；详情走 `IsManagerTaskLogAsync` 二次判定。测试：`TaskRunAuthorizationTests` 6 例（含子目录脚本口径、任务删除后快照） |
| R-03 | 🔴 阻塞 | 已闭 | 无策略/未启用 → 保留旧式一次失败通知 + 一次 AI 分析；策略启用 → `TaskRunCompletion.PolicyOwned=true`，旧路径让位、只由最终失败事件投递。测试：`TaskAlertDedupTests.Alerts_WithoutEnabledPolicy_AreNotManaged`、`TaskRetryPolicyTests.FailedExecution_WithEnabledPolicy_*` |
| R-04 | 🟡 建议 | 已闭（选定「顺延后放弃」） | 重试领取前查同任务非终态行 → 顺延 30s，累计 20 次后作废并记 `CancelReason`；不排队、不与手动/Cron 重入。测试：`TaskRetryPolicyTests.DueRetry_*` 三例 |
| R-05 | 🟡 建议 | 范围外 | 二期完整备份/恢复单独设计，本期未动（本手册 §1 只给操作要求，不代表二期能力） |
| R-06 | 🔴 阻塞 | 已闭 | 提交后的 `MarkDeliveredAsync` 改 try/catch 留痕，事务提交即成功事实源。测试：`ExternalPushTests` 提交后语义 + `IdempotencyRow_SurvivesUntilPruned_ThenReusable` |
| R-07 | 🔴 阻塞 | 已闭 | 幂等行物理清理前同键恒按原请求去重；后台按 72 小时窗口批量清理。测试：同上（清理前 `PruneIdempotencyRecordsAsync` 返回 0、清理后同键才判新请求） |
| R-08 | 🟡 建议 | 已闭（写入契约） | §6.2 与 §0 明确：应用层业务错误走 HTTP 200 信封，边缘层（Kestrel/反代）先行拒绝（如 413）不在该保证范围内 |
| R-09 | 🟡 建议 | 已闭 | 反向隔离测试落地：`ExternalPushIsolationTests` 6 例（PushKey 打旧端点被拒、Bearer 打通用品证端点被拒、合法凭据不产出 ClaimsPrincipal 且 `IsManager` 恒 false、密钥错与 Id 不存在响应不可区分） |
| R-10 | 🔴 阻塞 | 已闭（选定「一律拒绝」） | 摘掉合法标记后再查明文 URL 与危险协议，普通文本里出现即拒（避免 `{{tag:orange}}` 冒号误判）；命名链接限绝对 https 且无 userinfo。测试：`ExternalPushTests.Content_RejectsBypassAndUnknownForms` 11 类绕过 |
| R-11 | 🔴 阻塞 | 已闭（选定「实体同名 + 两套风格各自一致」） | 实体属性直接命名 `SessionTitle`，REST 直出 PascalCase；WS 帧沿用本仓库手写小写驼峰 `sessionTitle`；App 两套 DTO 分别显式标注并落到同一本地列。见 §6.4 与 App `WsFrame` 注释 |
| R-12 | 🟡 建议 | 已闭（统一为「归一化后忽略前后空白」） | 标题按 trim + NFKC 归一后分组与计摘要，带空白视为合法并按归一值处理（不再同时写「trim」与「禁止前后空白」两条冲突口径）。测试：`SessionKey_IsStableNormalized_AndIsolatedAcrossCredentials` |

G0 门禁的「找回缺失文档」一项已确认为**不可完成**：`git log --all -- docs/**` 为空，`docs/` 从未被版本控制跟踪。
经仓库所有者裁决改为「以本计划与本文档为契约基准推进」，本文档即该裁决的落地记录。

## 9. 隔离实例端到端冒烟结论（2026-09-26）

### 9.1 接口级冒烟

在仓库外的临时目录用**独立 SQLite 库**起了一个后端实例（scratch `appsettings.json` 指向
`DBType=Sqlite` + 自定口令，绝不触碰生产库），用真实 HTTP 走了一遍新链路。

**发现并修复一个只在真跑时暴露的缺陷**（单测共用同一个 DbContext，照不出来）：
`TaskService.AcceptAndRunAsync` 派后台执行时捕获了**请求作用域**的 scoped `TaskRunService`，
请求一结束其 DbContext 被 Dispose，终态落库抛 `ObjectDisposedException` →
运行记录**永久停在 Running**、`t_log` 行也永远不写。修复：后台执行统一落到
`TaskRunRecorder.LaunchAcceptedRun` 自建的作用域里（与既有 `TaskExecutionRetryRunner` 同一套路），
未注入容器时（单测）退回同步执行避免静默不跑。

修复后的实测结果：

- `POST /api/Task/execute-runs` → `{TaskId, RunId}`；约 1 秒后该运行记录终态为 `Failed` + `ScriptException`，`ElapsedMs=846`。
- 安全摘要为「冒烟失败任务 执行异常：InvalidOperationException: deliberate failure password=\*\*\* …」——
  脚本异常里明文写的口令**没进摘要**，也没出现在列表响应里。
- `LogAvailable=true`；`GET /api/Logs/details/{LogId}` 返回的日志正文含完整堆栈与
  `执行结果：执行异常（Failed/ScriptException）` footer；`t_log` 行与 `t_task_run` 按预分配 `LogId` 一一对应，
  且该行 `Success=0`——**失败不再被记成成功**这条一期核心口径在真实运行态成立。
- 策略写入 `99/999999/0/99999` 被服务端 clamp 为 `3/3600/1/1440`；`RetryCount=0` 时不排重试。
- 手动 `POST /api/TaskRun/{runId}/retry` 返回 200 并新增一条历史记录；不存在的 RunId 返回「执行记录不存在」。
- G-Push：凭据创建默认 `Enabled=false`、密钥 43 字符且列表响应里搜不到它；启用后
  `PushKey` 推送成功、`SessionKey` 以 `external:` 开头；同键同内容重放 `Duplicate=true` 且 `MsgId` 不变；
  正文含裸 `http://` 被拒；`PushKey` 打 `GET /api/Task` 得 401；坏 JWT 打 `GET /api/TaskRun` 得 401；
  `POST /api/App/sessions/overview` 响应含 `SessionTitle` 字段。
- 启动恢复：把上一轮因该缺陷卡在 Running 的两条记录判为 `Interrupted`（真实结果不可知，未重放未判成功）。

**本次冒烟顺带发现的既有缺陷（不属本期范围，未深修）**：`POST /api/Task` 的请求体形状不规范时
`TaskService.AddAsync` 会抛 `NullReferenceException`，堆栈原文经 ExceptionFilter 回进响应 `Message`。
已补 `saveModel == null` 的干净业务文案；非空但字段缺失的形态未逐一定位，留作单独修复项
（同类信息泄露面：未处理异常的 `Message` 含堆栈，建议后续统一在 ExceptionFilter 侧收口）。

### 9.2 浏览器点击冒烟（2026-09-27，同一隔离实例）

同一隔离实例（SQLite + 独立口令，绝不触碰生产库）配 `npm run dev`（8080），用真浏览器把新 UI 点了一遍：
登录 → 任务页 →「执行」确认弹窗 → 抽屉自动打开并聚焦本次 RunId → 失败策略表单读写 → 详情尝试时间轴 →
「查看实时日志」→ 登记菜单 `external-push/index` → 外部推送凭据创建/启用/密钥一次性展示 →
会话列表与会话页渲染受限富文本。

**四个只在真点时才暴露的缺陷（本轮全部修掉）**：

1. `NDrawerContent` 只有 `header`/`default`/`footer` 三个插槽，**没有 `header-extra`**：「刷新」按钮写在
   `#header-extra` 里被 Vue 静默丢弃，DOM 里根本没有这个节点（`npm run build` 与单测都照不出来）。改放正文首行。
2. Web 会话列表 / 会话页顶栏 / 搜索结果三处调用 `sessionTitle(key, taskMap)` 漏传第三个参数 `SessionTitle`，
   外部推送会话只显示 `external:<凭据Id>:<sha>` 原始会话键。三处补传——原单测只覆盖 util 自身，
   **测不到组件装配**，这正是漏网原因。
3. `ExternalPushService` 把会话标题折进通知标题（`【会话】标题`），而 `AppPushService` 又按通知约定再包一层
   `【】`，页面显示成 `【【机房监控】机房温度过高】`。改为通知标题**只用请求 `Title`**，会话身份由
   `SessionTitle`/`DisplayTitle` 单独承载（契约 §6.4「不得互换」），并加后端断言 `notification.Title == 请求 Title`。
4. 抽屉自动刷新的写入间隙竞态：终态落库与「排定下一次重试」是先后两次写入，刚判失败的一瞬列表里
   既无活动行也无 `NextAttemptAtUtc`，轮询就此永久停机、重试链在页面上再也刷不出来。改为**连续 4 轮判空才停**，
   并把判据抽成纯函数 `hasOpenChain(runs)` 补 3 例单测（浏览器环境无法稳定复现节流后的时序，逻辑改由单测锁）。

顺带补上 Web 缺失的「重新执行」入口：§1.4 端点早已实现/已测/已写文档，但 Web 只有 `api/taskRun.js` 的导出、
没有任何按钮（App 侧按设计不提供），等于只能 curl。现于详情面板加确认弹窗后 `POST /api/TaskRun/{runId}/retry`，
受理成功即把详情跳到新的 RunId。

实测留证（本地时间，界面与库两侧对齐）：

- 完整重试链 `00:56:58 手动 → 00:57:35 自动重试 → 00:58:45 自动重试`（退避 30s/60s + 10s 领取轮询粒度），
  三条尝试在同一抽屉里**无需手点**逐条出现，时间轴同步补齐。
- 「重新执行」→ 新行 `Attempt=1`、`RootRunId=自身`、`TriggerSource=Manual`、`IsRetry=0`（确认未并入旧链、
  不占旧链重试次数）；旧链第三条的 `TriggerRef=retry-of:<旧 RunId>` 语义保持不变。
  注：`TriggerRef` 超 24 字符按既有规则哈希成 `ref:<16 hex>`，故手动重跑的 `manual-retry:<RunId>` 落库为
  `ref:...`——完整动作仍在操作日志里，运行记录只留不可逆摘要。
- `SafeSummary` 全程 `password=*** Bearer ***`；而「查看实时日志」正文里脚本自己打的原文照旧完整保留
  （脱敏只作用于摘要面：列表、App、AI 提示、推送），日志仍仅 Manager 可读。
- 告警台账随链推进：`ConsecutiveFailures` 1→2→3，每条 `DeliveryStatus=1`、创建到送达约 10s（维护轮询粒度）。
- 鉴权与内容契约同批复测：禁用凭据推送 401「推送凭据无效」；Manager JWT 打推送端点是**同一句**401 文案
  （不可区分）；PushKey 打 `GET /api/TaskRun` 得 401「Token验证失败」；裸链/尖括号/未知标记/缺幂等键各返回
  对应 500 文案；同键同内容重放 `Duplicate=true` 且 `MsgId`/`NotificationId` 不变；
  `{{tag:red|紧急}}` 与 `{{link:详情|https://...}}` 在 Web 会话页渲染成样式文本与命名链接，无 `{{}}` 泄漏。

**本环境的观测口径（不影响结论，但记录以免被误读）**：内置浏览器无可见视口（指针点击报
`NATIVE_BROWSER_VIEWPORT_UNAVAILABLE`），交互一律经页面内 JS 派发真实 DOM 事件，驱动的是真 SPA、真 XHR、真渲染；
页面处于 `hidden` 状态时 Chrome 会把 2s 的 `setInterval` 节流到约每分钟一次，因此抽屉自动刷新在本环境表现为
「分钟级」——真实可见标签页不受此限，且该判据已由单测锁定。

### 9.3 发布产物形态复跑（2026-09-27）

§9.2 跑的是 `npm run dev` + 本地构建后端，**不是交付形态**。这一轮把 `dotnet publish -c Release`
的产物与 `quantum-release/wwwroot`（`npm run build` 的哈希分包）放进同一个仓库外目录，
由后端自身托管静态站点（无 vite、无代理），仍用独立 SQLite 库，浏览器直连 `http://127.0.0.1:5088/`。

**两个只在发布形态下暴露的缺陷（均已修）**：

1. **新装实例存不了脚本**：csproj 有意只随产物分发 `scripts/demo`（`scripts` 其余内容由运行期投放），
   所以发布目录里没有 `scripts/quantum`。`PUT /api/Task/scripts` 三级门禁与 Roslyn 编译**全部通过**，
   却在最后落盘那步抛 `DirectoryNotFoundException`，响应 500 且堆栈原文回进 `Message`。
   并入 `Program.cs` 既有的首建目录清单（`logs/config/db`）。
2. **重启后 Running 永久卡死**：启动恢复只在启动后 20 秒扫一次，且只判「超过 2 分钟宽限期」的行——
   崩溃前 2 分钟内启动的执行在重启时仍落在宽限期内，这一轮漏掉后再无人管，记录永远停在 Running
   （计划把「卡住的 Running」列为灰度观察项，这里成了确定会发生的漏网）。改为维护轮询每轮按
   **本进程启动时刻**补扫：早于本次启动的 Running 行不可能还在本进程里跑，无需等宽限期；
   本进程自己的行绝不触碰（长任务跑超 2 分钟是正常态）。实测：`17:26:36` 启动的执行被强杀，
   新实例 `17:27:09` 就判它 `Interrupted`（行龄 33 秒，旧口径要等满 2 分钟且当轮已错过）。

发布产物下复验通过的项：登录/菜单/任务页/执行记录抽屉（刷新按钮在位、`执行中 → 执行异常 1.18 秒`
自动刷新、重新执行入口、详情尝试时间轴、查看实时日志）；`SafeSummary` 全程 `password=*** Bearer ***`；
缺脚本的执行显示「已拒绝执行 · 任务脚本文件不存在」而**不是**成功；未配置策略的任务只跑一次
（列表共 2 条、无第三条）；外部推送凭据默认禁用 → 401「推送凭据无效」，UI 启用后 200、
同键重放 `Duplicate=true`；会话列表与聊天室顶栏显示「机房监控」而非 `external:` 原始键；
`{{tag:red|紧急}}` 渲染为样式文本，无 `{{}}` 泄漏、无双重 `【】` 前缀。

### 9.4 App 模拟器走查（2026-09-27，Android 15 / API 35）

用本机已有的 android-35 镜像新建 AVD（`smoke35`）无头启动，装 `app-debug.apk`，
服务器地址填 `http://10.0.2.2:5088`（模拟器访问宿主）→「连接成功 / 在线」→ 账号登录 → 通知授权。

- 会话列表三条：「机房监控」（外部推送，取 `displayTitle`）、「冒烟失败任务」（G4 告警）、「默认会话」；
  会话页消息显示「机柜温度越限」+「[紧急] 3 号机柜 85 度」——`tag` 标记按契约退化为方括号纯文本。
- 任务页 →「历史」：`共 3 条 · 只读`、状态筛选（全部/待执行/执行中/成功/执行异常/已拒绝执行）、
  行内「中断/未知」+「进程重启后残留的运行记录，真实结果不可知」。
- 详情页明确标注「只读视图 · 状态描述脚本执行结果，不代表接口是否成功」，含 RunId、
  执行时间轴（同一根执行含自动重试）、日志缺失时提示「日志已清理或尚未落库，仅保留本条执行记录」。
- 走查中修掉两处 App 缺陷：① 会话页顶栏不显示外部推送标题（只显示原始会话键）；
  ② 「保存并测试连接」成功后，探活文案被当作登录错误渲染成红色一行（`saveBaseUrl` 注释写
  「null=成功」而实现直接 `return probe()`）。两处均已在同一模拟器上复验修正。
- 环境说明：AVD `smoke35` 保留在机器上（模拟器已关机），不需要时用 `avdmanager delete avd -n smoke35` 清除。

## 10. 生产升级实施记录（2026-09-26）
按运维手册 §1-§2 走完全程，全程只对本机一次性容器与生产库操作，未留下任何临时容器/文件：

1. **备份**：`mysqldump --single-transaction --routines --triggers` 导出（1005 行 / 632 KB），存仓库外目录。
2. **恢复演练**：还原到本地实例，逐项比对与生产一致（表数 29；`t_task=9`、`t_log=1109`、
   `t_chat_message=121`、`t_app_notification=60`）。未演练通过的备份不算备份。
3. **沙箱预演发现真实缺陷**：对还原副本直接跑 `database update` 报 `Table 't_ai_conversation' already exists`
   ——生产迁移历史表**缺 `Init` 基线行**，且 `HasFullCurrentSchema` 的"代理表"判定已过期，
   会把三条新迁移一起误标为已应用、新表永不创建。修复判定（逐实体逐列比对模型）后再继续（见提交 382c9dd）。
4. **等价性证明**：用 EF 从零建一份纯 `Init` 库与还原副本做**逐表逐列 + 逐索引**比对，
   差异仅为生产侧两张历史遗留表（`t_user`、一张带日期的 `t_custom_data_title_*`），
   共享表的列级与索引级差异为 0 —— 由此才能断言"补记 Init 历史行"不跳过任何 Init 步骤。
5. **生产执行**：补 `Init` 历史行 → `database update` 只应用两条新迁移 → 复核新表/新列/唯一索引与业务行数（见 §7.1）。

> 部署提示：线上镜像仍是升级前的代码。在其更新到含本期改动的版本之前，新表处于"已建但无人写"状态，
> 不影响旧功能；正式切换时按 §3 的阶段表推进（默认全部关闭，灰度开重试与凭据）。
