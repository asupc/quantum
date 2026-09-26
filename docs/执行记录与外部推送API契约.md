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
| `dotnet test` | 651 全绿（SQLite 内存库；含 5 个新测试类 + 迁移链 + 鉴权隔离） |
| SQLite 迁移链 | 从空库真跑 `Migrate()`：Init→TaskRunBaseline→ExternalPushBaseline，6 张新表、`DisplayTitle`/`SessionTitle` 两列、4 个 `CREATE UNIQUE INDEX` 均在，`GetPendingMigrations()` 为空，重复 `(RootRunId,Attempt)` 被唯一索引拒 |
| **MySQL 迁移链** | 本地一次性 `mysql:8.4` 容器（localhost:33069，用后即删）实跑：**新库全链 Up** → 校验 6 表 + 5 唯一索引 + 2 标题列齐备；**Down 回 Init** → 新表与新列全部消失、业务表数回到 27；**旧库增量升级**（停在 Init 再 Up）→ 只应用新两条迁移。**未触碰生产 NAS MySQL** |
| 大数据量压测 §3.6 | 各 50 000 行 `t_task_run`：SQLite 文件库 插入 0.16s／列表计数 0.26ms／分页 20 条 0.21ms／到期重试扫描 0.20ms／深分页 offset 20000 为 21.1ms；MySQL 8.4 插入 2.15s／计数 0.35ms／分页 3.4ms／到期扫描 3.2ms／深分页 offset 20000 为 32.2ms。`EXPLAIN` 实证命中 `IX_t_task_run_TaskId_CreatedAtUtc`（range + Backward index scan + Using index）与 `IX_t_task_run_Status_NextAttemptAtUtc`（range + Using index） |
| `quantum-web` | `npm run test` 85/85 全绿、`npm run build` 通过 |
| `quantum-app` | 全模块 `testDebugUnitTest` + `assembleDebug` 通过；Room **v7→v8 非破坏迁移**用真 SQLite 验证：新列可空出现、存量行为 NULL（不伪造标题）、`outbox` 待发正文与 `pickedKeys` 已选态活过迁移 |

### 7.2 仍未验证（不得当作已验证）

- 生产 MySQL 实例上的实际升级执行与维护窗口演练（本机验证走的是一次性容器）。
- 浏览器端与安卓真机的端到端点击行为（本轮为编译 + 单元/集成级验证，无真机）。
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
