> **已由详细计划替代（2026-09-27）**：此文件仅作研究与审核历史；正式候选方案以 docs/QQ微信平台内置双向通信实施计划.md 为准。

# QQ／微信原生 C# 双向通信计划（不使用 OpenClaw）

日期：2026-09-27。状态：**方案草案，只批准调研门禁，不批准功能开发或上线**。本方案覆盖 QQ 官方机器人与普通微信官方“龙虾”账户的私聊双向通信；**不运行 OpenClaw Gateway、Node 侧车或腾讯的 OpenClaw 插件**。此前 `docs/QQ微信官方龙虾通道接入计划.md` 的 OpenClaw 侧车架构已经用户否决；`docs/QQ微信接入可行性验证记录.md` 里的离线探针也不能用作本方案的可行性通过证据。

## 1. 官方依据和可行性边界

| 渠道 | 可核对的官方信息 | 独立 C# 接入当前结论 |
|---|---|---|
| QQ | [QQ 机器人官方 API 文档](https://bot.q.qq.com/wiki/)、[腾讯官方 qqbot-nodejs SDK](https://github.com/tencent-connect/qqbot-nodejs)展示 HTTP REST、WebSocket Gateway、Webhook 两种接收形式；[官方 QQ OpenClaw 插件](https://github.com/tencent-connect/openclaw-qqbot/blob/main/README.zh.md)可参考平台特性，但本方案不装/运行插件 | **技术上有条件可行**：通过官方 QQ 机器人 AppID/Secret，ASP.NET 托管服务实现平台规定的认证、事件接收和应答；SDK 是 JS 参考而非 C# 官方 SDK。主动发消息、图片、额度等按账户实测；不推断普通个人 QQ 可被直接登录。 |
| 普通微信 | [腾讯官方 Weixin 插件](https://github.com/Tencent/openclaw-weixin)仅说明在 OpenClaw Gateway 中接入；[该仓库官方后端协议文档](https://github.com/Tencent/openclaw-weixin/blob/main/docs/protocol.md)公开当前客户端的 QR 登录、`getUpdates` 长轮询、`sendMessage`、`context_token` 等报文；协议文档明确“客户端字段和行为不构成完整服务端契约” | **技术路径已有非 OpenClaw 的 Rust 参考实现**（TerminalBuddy，详见 §1.1），因此可按它的协议流程设计 C# 版本；但当前仅核对了源码，没有验证用户实际运行账号/长期稳定性。公开协议和本地实现不等于腾讯对独立客户端的长期兼容承诺；先核对适用条款，再以 C# 最小程序扫码、真实收发、重连验证。验证失败即停止微信实施，不以非官方 PC/网页协议绕行。 |

这里的“官方”意味着只使用腾讯公布的机器人 API/协议，不借用旧 QQ/个人微信逆向登录。**不承诺微信最终一定可实现独立正式接入。** 若用户改为接受公众号/企业微信，需重新定义产品身份和会话范围，不在本次默认范围内。

## 1.1 TerminalBuddy 源码核对（本机仓库，2026-09-27）

参考项目：`D:\gitee\TerminalBuddy`；本次仅阅读源码与仓库文档，不读取或复用私密配置、不操作登录态。源码参考提交 `900ce90`（工作区状态在审阅时为干净）；它是 **Rust/Tauri 桌面程序**，不能把桌面侧的 Windows 凭据存储、进程常驻和会话身份直接复制到 Quantum 的 Linux/Docker ASP.NET 服务。

| 事实 | 证据（参考项目相对路径） | Quantum 借鉴及边界 |
|---|---|---|
| 微信**无需 OpenClaw**，原生接入 | `terminal-buddy/src-tauri/src/services/bot_scan.rs` 的 `weixin_begin/weixin_poll` 扫码拿 Bot ID、Bot Token、目标微信用户 ID；`bot_weixin_service.rs` 的 `get_weixin_updates`、`send_weixin_text_with_id` 实现 HTTPS 长轮询/出站；`bot_long_connection_service.rs` 的 `run_weixin_connection/handle_weixin_message` 维持连接、解析入站。 | 可参考 API 流程、错误码/超时、账号级隔离并以 .NET `HttpClient` + `BackgroundService` 独立实现；**只证明源码存在，用户称线上可收发，本轮未接触账号与实际运行日志，不能冒称真实复测通过**。 |
| QQ 在这个仓库里**不是原生平台通道** | `bot_channel.rs:509-561` 将 `"qq" | "bridge"` 均发 HTTP 到配置的桥接发送 URL；`web/handlers/bot_handler.rs:34-63` 接收桥接端带 Bearer Token 的回调；仓库 `docs/机器人通知接入指南.md` 明确 QQ Gateway 由外部服务承接。`bot_long_connection_service.rs:469-499` 只启动飞书/微信连接，无 QQ 长连接。 | 只能借鉴桥接 DTO/鉴权、相关消息 ID 关联思路；**不能从该仓库推导出 QQ 官方 API 的完整实现**。仅借鉴渠道抽象与鉴权思路；QQ 仍按 §3 基于官方 API 独立验证和设计，不以获取该桥接器为前置条件。 |
| 双向语义是**通知后的引用决策回复**，不是聊天机器人 | `bot_weixin_service.rs` 出站正文附 `TerminalBuddy-ID` 引用标记；`commands/bot.rs:652+` 的 `process_bot_reply` 校验白名单并消费已存在的 DecisionBinding。 | Quantum 要的是“任意允许的私聊消息→白名单指令→回复”，必须另建入站指令处理和持久幂等，不可误用其引用标记为所有消息唯一身份。 |
| 存储与可靠性**不可照搬** | `bot_settings_service.rs` 对通道 Secret 做桌面平台加密；`bot_weixin_service.rs:25-85` 的游标与 `context_token` 经 `database_service.rs` 存储；`bot_long_connection_service.rs:408-435` **先保存下一游标再逐条处理消息**，仅以内存 1,024 个 messageId 去重；`bot_weixin_service.rs:200-246` 遇部分错误尝试不带 `context_token` 重发。 | Quantum 要避免游标提前推进导致崩溃丢消息；先持久化 Inbox+游标，事务提交后派发、失败可恢复；敏感 token 按服务端加密/访问控制；无 token 重发是否可用单独验证，不要把“请求成功”误当接收方已收到。 |

因此对用户“TerminalBuddy 已正常接入 QQ、微信”的解释是：**微信确实有仓库内独立实现；QQ 双向能力取决于其仓库外桥接服务**。两条都可作为 Quantum 产品目标，但不能用该仓库单独证明两条平台直连的可行性与合规性。
## 2. 能力矩阵与边界

- 首期必须的双向通信：已绑定管理员 QQ 私聊文本入站→Quantum 会话留痕→白名单指令/脚本执行→**同账户同私聊**文本回复；微信同样流程，唯独全部以 §3 的准入门禁为前提。App/Web 历史仍是权威记录；群聊、媒体和多人访问不进入首期。
- QQ/微信主动任务通知为**单独验收的可选能力**，须验证平台窗口/限频和实际回执；未通过则仍保留已经验证的入站+原路回复，不用“主动通知失败”伪造已送达，也不将普通 App 通知默认群发。
- `.cs` 任务脚本 (`IQuantumTask`) **只负责编排和业务逻辑**，通过任务运行上下文拿到经服务端验证后的有限指令参数；不能直接持有平台管理员令牌/服务端长连接，不能启动进程、创建裸 Socket 或充当 HTTP 回调服务。QQ WebSocket/微信长轮询属于 ASP.NET 常驻托管服务；不放宽 `ScriptSecurityGate` 现有门禁。脚本凭据如确需访问外部服务，遵循现有环境变量强制配置规则。
- 默认为私聊单管理员白名单，所有入站绝不等同于 Manager JWT。后台绑定通过 `[ManagerOnly]` 发起；来源 `(channel, platformAccountId, chatType, peerId)` 精确匹配且确有账号持有人确认；未绑定/群聊/超长/高频消息在进入 `MessageProcess` 之前拒绝。AI 提案写入、执行/删除管理任务、系统指令等高危操作默认禁止，日后单独设计确认流程。

## 3. Phase 0：**先证明能不依赖 OpenClaw，才能做实施**

1. **资格与合规**：QQ 在开放平台创建测试机器人、核对允许的接收形式与开发权限。微信从腾讯官方页面、仓库说明、书面反馈或相关条款确认非 OpenClaw 的独立客户端是否允许、二维码登录态是否可由自有服务维护、对“微信龙虾”消息的出站限制；仅有开源仓库、MIT 许可或报文字段**不足以证明平台开放接入资格**。无法取得可核验依据→微信状态“不可实施”，如需改渠道需用户另行确认。
2. **QQ 原生 C# Spike**：隔离临时工程，不改 Quantum；用 .NET BCL `HttpClient`/`ClientWebSocket`（或 SDK 协议公开约定）完成 token 获取/刷新、WS 连接/心跳/Resume（先单实例）；真实私聊入站、按 inbound msgId 原路回复；测试失联重连、重复事件、错误账户发信、平台限流/主动发送。若选 Webhook，需在单独入口按官方验签、挑战流程验证，不能因调试方便跳过；默认 WS 出站连接减少公网入口。
3. **微信原生 C# Spike**：仅在第 1 项通过后，按官方已公开的当前协议写临时最小客户端：获取二维码、扫码授权与失效、令牌安全保存、`getUpdates` 游标/长轮询、`sendMessage` 携带入站 `context_token`，扫码后隔离环境私聊来回各一条；再测重启续接、失效重登、重复/乱序、短时和隔夜主动发送。复核 HTTP 与报文 `ret/errcode`、暂停/退避，不从 TypeScript 的可选字段推断后端必接受。严禁使用真实私密账号进行未经确认的非授权协议试探。
4. **判定记录**：在 `docs/QQ微信接入可行性验证记录.md` 更新两张独立结果表：接入资格依据、测试用途、包/协议源码固定 commit、日期、测试账号类型（脱敏）、登录/入站/出站/重连/主动推送结果和错误码；不复制 token、cookie、微信 `context_token`、AppSecret。没有测试 QQ 机器人和微信账号持有人扫码，不可将“纯 C# 技术可写”记录为“真实双向已通过”。QQ 与微信分别过门禁；**若双渠道是整体硬要求，微信不通过则整个正式上线范围暂不成立**。

## 4. 通过各自门禁之后的目标设计

```text
QQ 官方机器人 API ─ .NET QQ HostedService(WS/REST) ─┐
                                                    ├─ ChannelInboundService ─ 授权/幂等/限额 ─ Quantum 入站持久队列 ─ 指令/脚本
微信官方后端接口 ─ .NET Weixin HostedService(HTTP/QR) ─┘                                       │
                                                     ChannelDeliveryService ← 原路回复/选定通知 ← App/Web 会话与事务 Outbox
```

1. **服务位置**：`Quantum.API/Quantum.Application/` 增 `QqChannelHostedService`、`WeixinChannelHostedService`、入站编排服务与可靠投递器；`Quantum.API/Quantum.Web/Startup.cs` 注册受配置开关门控的 hosted services 和管理服务；DTO/实体在 `Quantum.API/Quantum.Entities/`，双数据库上下文在 `Quantum.API/Quantum.Data/`。不增加通道 Node/OpenClaw 依赖，`Dockerfile`/离线交付只检查 .NET 网络、凭据与持久卷。不在 `Quantum.Web/scripts/` 放需要编入主程序集的 .cs。
2. **身份/凭据**：QQ AppID/Secret、微信扫码令牌及 `context_token` 从有权限控制的 secret/加密存储读取；日志脱敏、不写到任务脚本/仓库/镜像。QQ 平台 token 单实例原子刷新；微信单实例拉取并持久化 `get_updates_buf` 与账户绑定；并行副本先分布式租约/单活再开放，不复制扫码登录态多实例争抢。管理员后台仅能看到脱敏状态和重绑操作，禁止将机器人凭据返给 App/浏览器。
3. **存储/事务**：增加绑定、入站 Inbox、来源回复关联与出站 Outbox/状态；入站幂等键包含 `(channel, platformAccountId, chatType, peerId, platformMessageId)`（唯一域需按 §3 实测）；在同一数据库事务提交去重键、会话消息、待处理指令，再 ACK/推进官方游标；平台事件去重只允许执行一次命令。出站以**同一业务事务**持久化 App 会话气泡和渠道投递意图，后台有限次重试/过期/死信；不承诺对方已读，不支持平台幂等时明示网络超时后的重复投递风险和处置规则。SQLite/MySQL 两侧各加迁移，核对 Up/Down、唯一索引与存量兼容；上线先备份。
4. **与现有消息路径集成**：`AppPushService.SubmitCommandAsync` 当前硬编码 App+管理员用户名，不可把通道事件伪装成 App 直接调该方法；抽取独立授权后的入站服务，继续 App/Web 留痕但带真实来源；`MessageProcess.MessageAsync` 共享 `adminKey` 和多步骤状态，必须按渠道/账户/peer 分区并在系统指令分支前拦截非白名单。`CommunicationType.QQ=1/微信=4` 是旧渠道历史值，不复用，新增专用枚举值并处理任务 `CommunicationTypes` 的历史过滤语义；`MessageProccessDTO.Clone()` 为手工字段复制，新增关联键需显式复制，`TargetTaskId` 仍按旧规则置空防回路。
5. **原路回复定义（解决旧方案审核 R-01）**：A. 快捷回复、任务起止语走入站 DTO 的持久 `ReplyRouteId`；B. 指令触发的脚本 `ctx.Notify` 如属于该次执行，显式传 `RunId→ReplyRouteId` 到 `TaskRunRecorder`/`TaskExcuteService`/`QuantumNotifyFacade`，由 `NotifyService` 查归属后投递，不依赖 `AsyncLocal` 或会话标题猜来源；C. Quartz/手动/App 触发的脚本通知**无入站来源**，保持仅 App，需额外渠道推送则管理员显式订阅并指定收件绑定。回复路由过期/平台拒绝时显示失败并保留 App 消息；不可错发给另一账户、群聊或前一个指令。媒体/选项按首期文本降级边界明确告知，不发送内部鉴权 URL。
6. **对外接口及 UI**：通道按 WS/轮询主动连平台，不额外给 QQ/微信开放可伪造的匿名入站 API。管理状态、绑定、收件人确认、投递状态、关闭开关在 `Quantum.API/Quantum.Web/Controllers/` 新增 `[ManagerOnly]` 端点，保持现有 HTTP 200 `ResultModel`；`quantum-web/` 增系统管理页面，`quantum-app/` 首期仅复用消息同步，若展示来源状态需显式 `@SerializedName` 并核对 App API 契约。对所有敏感日志/重绑接口做服务端正向 Manager claim 判断。

## 5. 安全、验收与发布

- 在改认证/消息链路前先读 `docs/security/公网部署安全审计-2026-09-14.md` 与 `docs/App端API契约.md`；**当前检出的 docs/ 未见这两份**，需取得可核验版本，否则不能批准敏感区域实现。阶段 0 也须先确认供应商规则与数据隐私条款；不通过不得把微信协议工程化上线。
- 服务器限流、消息大小上限、拒绝群聊、平台消息 HTML/URL 转义、QQ WS token 的安全存放、微信登录态过期/撤销、私聊白名单绑定的持有人证明、任务执行权限隔离、重放和跨账号错投都要有单测和端到端负测。不能拿脚本门禁充当第三方入站沙箱（脚本门禁不是对抗性隔离）。
- `Quantum.API.Tests/` 补渠道编排/认证/去重/出站/迁移/脚本路由测试；修改后 `dotnet build` 和完整 `dotnet test` 必须全绿。`quantum-web` 跑 `npm run test`、`npm run build`（构建前后检查 `quantum-release/wwwroot`）；App 如有代码变化跑 Gradle 单测和真机消息同步验证。真实平台验收需各有测试账号：来一条→App/Web 气泡→快捷答复→脚本回复→断线恢复→重复回调不二次执行→平台拒绝可见；QQ/微信分开出报告，停一侧另一侧及 App 可用。
- 发布顺序：备份 DB/密钥 → 双库迁移 → 后端默认关闭上线 → Web 开关与绑定 → QQ 真实小流量 → 微信仅在独立接入门禁通过后小流量。紧急回滚先关闭渠道/撤销绑定和密钥，再回退程序，新增消息/幂等数据不主动删除；升级后重复拉取不重新执行指令。管理员明确批准 Phase 1/2 才能实施。

## 6. 需要用户确认的决策

1. **已确认**：QQ 与微信的双向收发均为最终硬要求，且不得依赖 OpenClaw；任一未通过，不宣称全部交付。TerminalBuddy 只作为架构与风险参考，不复制其实现、不依赖其 QQ 外部桥接服务，也无需提供桥接器代码。
2. 是否同意第一期只做**绑定管理员私聊的文字双向通信**；群聊、媒体及主动推送后续单独实测？
3. 是否有测试用途的 QQ 机器人和微信扫码配合；密钥只通过私密配置管理，不发送在聊天或计划文档。

## 审核记录

### 2026-09-27 首轮审核（七维度全量检查）

**结论：有条件通过，可确认 Phase 0；Phase 1/2 尚有 2 项架构阻塞。** 完整性：渠道、端点、数据、Web/App 影响已列；约束：不恢复旧通道、双库迁移、HTTP 200 信封、权限分层已有约束；安全：微信合规门禁与绑号拒绝策略明确；变更风险/测试/部署均有验证与回退，但持久化执行和出站事务需要实施前细化。此附录仅记录审核，不修改正文或业务代码。

| 编号 | 严重级 | 位置 | 问题与建议 | 状态 |
|---|---|---|---|---|
| R-01 | 🔴 阻塞 | §4 第 3、5 项 | 计划要求入站消息至指令“只执行一次”，但既有 `MessageQueue.ExecTask` 出队后 `Task.Run` 并行、异常仅记录且没有持久 ACK；`TaskRunRecorder.RunStepAsync` 在 Claim 失败分支仍执行脚本。仅添加 Inbox 唯一索引不能保证重复投递不二次执行或重启后可恢复。实施前明确持久消费者的原子 claim/完成/未知终态与消息排序；**通道入站不能绕回既有内存队列实现可靠消费**，Claim 失败不得执行副作用，并补双实例/断电/重投验收。 | 待修 |
| R-02 | 🔴 阻塞 | §4 第 3、5 项 | 文档要求 App 气泡与出站 Outbox 在同一事务提交，然而 `AppMessageService.AppendAsync` 自己开事务并立刻提交，`SendMessageHelper.Send` 再通过静态 `AppPushDispatcher` 单独落库；`NotifyService.SendMessage` 又只入内存出站泵。现有路径不具备原子性。实施前确定新事务入口用 `AppendNoSaveAsync` 与渠道 Outbox 共用 `IQuantumDbContext`/同一 transaction，并把需要原路回发的 `ctx.Notify` 链路改为持久事件后提交广播，保留 App-only 现有路径语义。 | 待修 |
| R-03 | 🟡 建议 | §2 安全、§3 准入 | QQ/微信“绑定由持有人确认”尚未指定可验证的挑战流程。Phase 0 应实测可用于验证 `(accountId,peerId)` 控制权的渠道事件与步骤；不能仅在 Web 填入一个可猜 ID 即绑定。 | 待修 |
| R-04 | 🟡 建议 | §3 微信客户端 | 微信官方协议的 `bot_agent` 是观测字段、不参与认证；若申请得到独立实现资格，应与官方确认标识声明，不应简单复制官方插件的 `OpenClaw` 默认自报身份。 | 待修 |

**本轮核实台账**：
- 已核实到实现体：`Startup.ConfigureServices`（已有 HostedService 与依赖注入）、`MessageQueue.InitMessageQueue/ExecTask`（内存队列 + 并行无持久 ACK）、`AppPushService.SubmitCommandAsync`（App 身份与用户气泡）、`AppMessageService.AppendAsync/AppendNoSaveAsync`（独立事务/共事务接缝）、`MessageProcess.MessageAsync/SelectTaskCandidates`（adminKey 与通道过滤）、`TaskRunRecorder.RunStepAsync`（Claim 失败仍运行）、`TaskExcuteService` 创建 `QuantumNotifyFacade`（只有 taskId、sessionKey）、`NotifyService.SendMessage` 与 `SendMessageHelper.Send`（App-only）、`MessageProccessDTO.Clone`（手动克隆）、`IQuantumTask.RunAsync`/`QuantumTaskContext.Http` 与 `ScriptSecurityGate`（脚本运行时边界）、`CommunicationType` 历史枚举，以及 SQLite/MySQL 的 DbContext 注册。Web/App 具体 UI 新文件未命名，属于实施设计而非现状断言。
- 已核实官方公开资料但**不能推出真实服务端契约/独立客户端许可**：QQ API 文档入口与腾讯 Node SDK README、微信插件仓库 README 和 `docs/protocol.md`。QQ 账户接口权限/限流、微信非 OpenClaw 独立实现授权及扫码真实可用性均**未核实**；保留为 Phase 0 硬门禁。
- 未覆盖：AGENTS.md 所引安全审计和 App API 契约目前未见于当前检出 `docs/`；真实 QQ/微信测试账号及持有人扫码不可得；本轮无业务代码改动、未进行任何构建/测试。


### 2026-09-27 第二轮审核（引入 TerminalBuddy 参考后）

**结论：有条件通过，仍仅建议批准 Phase 0。** 首轮 R-01/R-02 的落地阻塞尚未通过代码设计消除；不能因有其他项目的连接实现而跳过 Quantum 的幂等/事务改造。首轮 R-03/R-04 维持待修。本轮补充以下对照发现（R-05 随后已按用户澄清关闭，见第三轮）：

| 编号 | 严重级 | 位置 | 问题与建议 | 状态 |
|---|---|---|---|---|
| R-05 | 🟢 提示（已降级） | §1.1 QQ 事实、§3 QQ spike | TerminalBuddy 只有 `"qq"→send_bridge` 的 HTTP 适配，未包含 QQ 官方机器人 Gateway 客户端或其外部桥接程序；“QQ 已正常连接”是用户现场说明，无法从此仓库验证其协议、接入方式、是否使用 OpenClaw。原先假设要复制 QQ 桥接器；用户现明确只作参考，不依赖桥接器。QQ 仍须独立进行官方 API Spike，不能把桥接 URL 当作 QQ 官方 API。 | 范围外（无需外部桥接器） |
| R-06 | 🟡 建议 | §1.1、§3 微信 spike | 参考项目的 `get_updates_buf` 在逐条处理消息前即保存，`message_id` 只在本次进程内记忆；Quantum 必须用持久 Inbox+游标的同事务提交并安排掉电/重复回调测试。正文已有原则，实施详细设计仍应画出数据库事务与游标推进顺序。 | 待修 |

**本轮核实台账**：
- 已核实到参考项目方法体：`bot_scan.weixin_begin/weixin_poll`、`bot_weixin_service.weixin_post/get_weixin_updates/send_weixin_chunk/send_weixin_text_with_id/load_weixin_state`、`bot_long_connection_service.run_weixin_connection/handle_weixin_message/sync_bot_long_connections`、`bot_channel.send_bridge/dispatch_channel`、`bot_handler.bridge_inbound`、`bot_settings_service.get_runtime`、`commands/bot.process_bot_reply/validate_bot_sender`、`database_service.read_weixin_state/save_weixin_state`；仓库 `docs/机器人通知接入指南.md` 的 QQ 外桥说明已核对。Quantum 侧首轮核实的方法体未变，R-01/R-02 仍成立。
- 未核实：TerminalBuddy 用户机器的 QQ 外桥服务及运行配置、微信扫码实时成功/长期收发、腾讯平台对独立客户端的后续稳定兼容与使用条件。未覆盖：缺失的 Quantum 安全审计/App API 契约，真实 QQ/微信账号验收。仅编辑计划文档，未修改业务代码或执行构建/测试。

### 2026-09-27 第三轮范围澄清

用户明确 TerminalBuddy **只作参考，不要求照抄其实现**。已移除“需要提供 QQ 桥接器源码/协议”的错误前置条件；第二轮 R-05 关闭为“范围外”，不再作为阻塞。QQ 官方机器人通道由 Quantum 依官方资料独立设计与验证；微信参考其非 OpenClaw 的协议流程，但同样独立实现。R-01/R-02（Quantum 自身持久指令消费、同事务 Outbox）仍待实施前细化；R-03/R-04/R-06 维持建议。未修改业务代码，未执行构建测试。

