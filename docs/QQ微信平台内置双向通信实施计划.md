# QQ／微信平台内置双向通信实施详细计划

> 2026-09-27 · 供仓库所有者确认 · **所有者已要求先实施代码、后经 Web 管理页绑定测试；正式代码正在实施，真实平台验收尚未通过**（进度与未完项见 `QQ微信内置通道实施状态.md`、P0 探针见 `QQ微信P0实施验证记录.md`）。本文件取代 `QQ微信原生CSharp双向通信计划.md`；此前 OpenClaw 侧车方案亦已否决。最终目标是**QQ 官方机器人 + 普通微信机器人都可与 Quantum 双向私聊**，通道是 Quantum 后端内置 .NET 服务，**不运行 OpenClaw、Node/Rust 侧车，也不用 `.cs` 任务脚本充当通信服务**。脚本如被用户指令触发，仍仅执行已有业务任务。

> 执行顺序变更（2026-09-27）：仓库所有者明确要求先实现代码，随后在管理页绑定 QQ/微信测试账号验证。此变更仅调整开发与真机验收顺序，**不**自动批准微信独立客户端资格、主动通知或生产上线；原 P0 的真实收发与 P1 安全文档仍是验收/敏感链路门禁。尚未拿到安全审计与 App API 契约时不改认证/脚本执行链路。

## 0. 结论、边界与证据

**QQ**：官方开放平台提供 AccessToken、WebSocket Gateway 和单聊收发 API，可以用 ASP.NET Core 原生 `HttpClient`/`ClientWebSocket` 接入。前提是测试机器人具备 `GROUP_AND_C2C_EVENT` 等对应权限、账户和平台额度允许。仅指 QQ *机器人*，不等于登录个人 QQ。

**普通微信**：腾讯 `openclaw-weixin` 仓库的官方协议文档列明微信机器人扫码登录、`getUpdates`、`sendMessage` 的现行报文；本机 `D:\gitee\TerminalBuddy` 的 Rust 代码已实现无 OpenClaw 的客户端路径，用户报告其账号可正常使用，**本轮没有读取凭据或复测收发**。腾讯的协议文档特别说明其类型/客户端行为不构成完整服务端契约，且主要描述该插件；自有 C# 客户端的适用条件、功能稳定性及后续兼容仍须核对官方规则并用 Quantum 测试账号完成真实验证。**若任一通道 Phase 0 门禁不通过，不宣称双渠道需求已完成**。

| 来源 | 本计划采用的依据及优先级 |
|---|---|
| [QQ 当前官方鉴权](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/api-use.html) / [WebSocket](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/event-emit/websocket.html) / [单聊事件结构](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/c2c_message_create.html) | 2026-07 更新：`POST https://api.bot.qq.com/app/getAppAccessToken`，`Authorization: QQBot {AccessToken}`；`GET /gateway/bot`，Identify/心跳/Resume；单聊 `GROUP_AND_C2C_EVENT`（`1<<25`）。不硬编码示例 Gateway 地址/心跳周期，按返回值。 |
| [QQ 当前消息概述](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/overview.html) / [单聊事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/c2c_message_create.html) / [发送单聊](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages.post.html) | 单聊 `C2C_MESSAGE_CREATE`，`d.author.user_openid`，`d.id`，扩展 `msg_idx`；`POST /v2/users/{user_openid}/messages` 用 `msg_type=0`、`content`、`msg_id`、`msg_seq`。概述与发送接口对回复时效的文案**存在冲突**：概述/接口顶部说单聊被动 60 分钟，发送字段又写 `msg_id` 5 分钟；实现先按 5 分钟保守截止，必须以测试机器人和实际错误码核对，不把 60 分钟写成保证。旧版 GitHub bot-docs 一度宣告主动消息停用，与当前网页（2026-07/09 更新）不同；以**当前官网和实际账号回执**为准，主动通知另列能力门禁。 |
| [微信官方插件仓库](https://github.com/Tencent/openclaw-weixin) / [官方协议](https://github.com/Tencent/openclaw-weixin/blob/main/docs/protocol.md) | 官方 HTTP JSON/HTTPS 报文参考；QR `get_bot_qrcode`/`get_qrcode_status`，`getupdates` 游标，`sendmessage` 带 `context_token`；`message_id` 在协议类型中为数字，不能把它当成永远是字符串。版本字段/`bot_agent` 是来源元数据而非鉴权凭证；**不得谎称运行 OpenClaw**，独立实现的声明取值在 Phase 0 验证。 |
| `D:\gitee\TerminalBuddy`，参考提交 `900ce90` | 微信扫码→长轮询→发送形成代码级例证；QQ 实际是其**外部 HTTP 桥接**，该库未实现 QQ Gateway。只参考边界与缺陷，不复制或依赖它的代码、部署、账号与桥接器。其微信游标先保存后处理消息、内存去重及 tokenless 重发都不是 Quantum 的可靠性模板。 |

**“不设置管理员”的含义**：QQ/微信聊天通道不再有“管理员用户”配置或多用户白名单，仅有唯一绑定的私聊账号。Web 配置入口仍按仓库安全基线使用现有 `[ManagerOnly]`（JWT 正向 Manager claim）；这仅保护密钥、扫码和换绑操作，**不会给 QQ/微信消息附加 Manager 令牌**。解绑/换绑必须由 Web Manager 显式确认：原子废止旧绑定、待发 Outbox 与旧回复路由，清除旧微信 context_token，保留历史 App 消息；新绑定验证通过前拒绝所有业务消息，且不得因为机器人刚启动便“首条消息抢占”。

### 功能范围

首期每种平台**各仅一个机器人账户、各仅绑定一个私聊用户账号**，不设置 QQ/微信聊天“管理员”角色，也不配置多用户白名单；仅精确匹配唯一绑定用户的文本才进入命令引擎；回 App/Web 会话留痕；回复发送状态可查看；账号重连/扫码重绑；QQ/微信相互隔离。主动推送仅在平台权限和时效实测通过后按目标订阅开放，不默认复制所有 App 消息。不做群聊、公众号/企微、富媒体、语音、所有人任意指令、文件上传或跨平台转发。保持 Web/App 原通信和任务行为。

## 1. 平台内置架构（无脚本通道）

```text
官方 QQ Bot Gateway/REST  ─→ QqChannelWorker ─┐
                                              ├→ ChannelInbox(单活持久入站) → 授权 → CommandService/MessageProcess → TaskRun
微信机器人 HTTPS(扫码/长轮询/发送) ─→ WeixinChannelWorker ─┘              │
                                          ChannelOutbox ← App 会话气泡 + 原路回复/已订阅主动通知
                                                    └→ QQ/Weixin 专用 Sender → 状态/审计
```

- **`Quantum.Application/Channels/`（新）**：`QqChannelWorker : BackgroundService`、`WeixinChannelWorker : BackgroundService`（网络协议）；`ChannelInboxService`、`ChannelCommandConsumer`、`ChannelOutboxService`、`ChannelDeliveryWorker`、`ChannelBindingService`、`ChannelHealthService`（共用业务）。传输接口 `IChannelTransport` 不让业务层碰平台凭证。取消由 `CancellationToken` 驱动，单账户单活，限并发、重试抖动、熔断；禁用任一通道不影响另一个及 App。Web 项目 `Startup.cs` 注册 hosted services + scoped 业务，依现有 Autofac/Quartz 生命周期管理；网关运行于既有 .NET 主进程，不新增 Node、OpenClaw 及脚本引用/执行能力。
- **`Quantum.Entities/`（新模型/DTO）**：`ChannelAccount`（每平台最多一条，状态、凭据引用）、`ChannelBinding`（每平台恰好 0 或 1 个已验证私聊 peer，关联机器人账号和换绑版本）、`ChannelInbox`（原始事件指纹、接收序号、授权与执行状态）、`ChannelCursor`（QQ session_id/s 与微信 `get_updates_buf`，加密敏感字段）、`ChannelReplyRoute`（入站来源、过期、QQ `msg_id`/`msg_seq` 或微信 `context_token` 的安全引用）、`ChannelOutbox`（消息、目标、意图、可发送时间、平台回执、重试/失败、过期）。账户/peer 从平台**服务端接收的事件**得出，严格匹配当前唯一绑定记录；聊天侧不存在管理员身份/白名单列表。增量消息来源字段只新增可空字段（不更改旧客户端既有字段）。
- **数据**：`IQuantumDbContext`、`QuantumSqliteDbContext`、`QuantumMySqlDbContext` 两侧增相同模型/唯一索引及迁移；数据库约束 `ChannelAccount.Platform` 唯一、`ChannelBinding.Platform` 唯一，保证 QQ/微信各最多一个机器人和一个绑定 peer；应用层不能绕开数据库限制添加第二个。`CommunicationType` 的历史 QQ=1/微信=4 **不恢复使用**；新增 `QQBot` / `WeixinBot` 不复用旧枚举值，排查任务 `CommunicationTypes` 存量筛选，避免老任务被无意触发。配置模型仅存脱敏显示/账号标识/启停，不用明文列或环境变量页向 Web 显示凭据；正式服务端凭据用独立 secret/key-ring（容器卷持久化、权限限制、密钥轮换/恢复流程）。
- **API/UI**：`Quantum.Web/Controllers/ChannelController` `[ManagerOnly]`：查询脱敏状态、启用一个机器人与一个账号绑定/换绑、发送策略、QQ 挑战码校验、微信发起扫码/轮询/换绑、查看投递错误、禁用/撤销；所有端点沿用 HTTP 恒 200 `ResultModel`，业务失败 `BusinessException`。`quantum-web/` 系统管理新增“消息通道”页；App 首期只通过原有会话流读新气泡，确需展示来源时再补可空字段、显式 `@SerializedName`、WS/REST 向后兼容与真机验证；**不会把 bot_token 送到浏览器**。

## 2. 官方 QQ 适配详细步骤

1. 管理员在线创建 QQ 官方机器人，凭 AppID/AppSecret 配平台权限，**私聊事件订阅必须有** `GROUP_AND_C2C_EVENT (1<<25)`；先在测试机器人/白名单下做，不能混同 QQ 个人号。QQ 凭证通过 secret 注入，`HttpClient` 用 `POST https://api.bot.qq.com/app/getAppAccessToken`；按 `expires_in`（目前至多 7200 秒）提前刷新，单账户刷新锁和失败退避；REST 请求 `Authorization: QQBot {AccessToken}`，不使用已废弃的旧 Token 签名机制。
2. 获取 `GET /gateway/bot` 返回的 `url`、推荐 shards、`session_start_limit`；首期 `[0,1]` 单连接；`ClientWebSocket` 收 Op10 Hello，Op2 Identify 提交 `QQBot {AccessToken}` + `1<<25`；Op0 READY 保存 session_id。按服务器 `heartbeat_interval` 发 Op1 + 已安全持久化的 s，要求 Op11 ACK；Op7、Op9、无 ACK、socket 异常触发断连；Op6 Resume 用 session_id 与持久化 s，Op9 Invalid Session/特定关闭码才回退 Identify，并遵守 `remaining/max_concurrency/reset_after`、指数退避，不无限快速重连。
3. 只接受 `C2C_MESSAGE_CREATE` 的文本；记录 `op/s/t/id` 及 `d.id`、`d.author.user_openid`、`message_scene.ext` 数组中唯一的 `msg_idx=...` 字符串（不能当作 `ext.msg_idx` JSON 字段；同数组内 `auth_token=...` 必须忽略且不得落日志）；**event id、消息 id、消息索引不是同一个字段**。鉴权依据唯一 `(QQBot, BotId, C2C, user_openid)` 绑定；QQ 绑定流程为：Web 管理端发起**一次性短时挑战码**（只存散列、5 分钟、最多 5 次尝试、只在待绑定状态有效，候选 OpenID 与确认进度持久化）→待绑定用户通过平台 C2C 给机器人发送挑战码→服务端从 QQ 官方事件提取 OpenID、原子消耗挑战码并由 Web 管理端确认后写入唯一绑定；绑定前只解析挑战码、任何其他文本不得进任务/日志正文；同一机器人不同场景 id 隔离，其他平台事件只推进可安全提交的游标，不触发指令。去重键定为 `(accountId, eventType, d.id, msg_idx)`，出现字段缺失必须拒绝执行并报警（实测若消息索引缺失则收紧规则/升级契约）；源报文字段另保留受限短期摘要以供排障，不能存 `auth_token` 明文。
4. 回发 `POST /v2/users/{user_openid}/messages`，`msg_type=0` + `content` + `msg_id=d.id` + `msg_seq`（同入站的多条回复独立分配并持久化序号（单聊每条入站最多 4 次，超出须合并或明确拒绝））；`msg_id` 限时、重复和权限失败按**平台错误码**停重试，过期不自动改用主动消息以规避授权。限频/主动额度从当前官方文档确认后以配置保守 clamp，窗口冲突按更短时限；短时超时重试沿用同一个 `msg_seq` 防二次显示（需 Phase 0 实测错误/回执）。Web Manager 选择的主动通知（如可用）无 `msg_id` 且走**独立权限/频控/失败状态**，拒收则关闭该绑定主动发送。
5. Webhook 是备选而非首期并存：若服务器网络无法稳定出站 WS，另出变更评审，独立公网路由按官方 Op13/Ed25519 等官方验签与限流，不把未经验证的回调直接送 Quantum 任务入口。

## 3. 微信机器人适配详细步骤（条件实施）

1. **先验资格与产品条款**：核实腾讯当前允许的扫码机器人用途、非 OpenClaw 独立 C# 客户端可用范围，以及宿主为 Linux/Docker 时登录态持久化/迁移政策；本地 TerminalBuddy 的 Rust 运行报告不代替平台接入许可或 Quantum 的真实账号验证。官方协议说明只是“当前客户端行为”，每个未在服务端正式承诺的字段都要作为 Phase 0 实测项。若无法合规确认或真实测试不通过则停此阶段，不能退回个人号逆向协议/第三方挂机服务。
2. `HttpClient` 从官方文档默认 HTTPS API 基址启动，`POST /ilink/bot/get_bot_qrcode?bot_type=3`、`GET /ilink/bot/get_qrcode_status?qrcode=...`；按 `wait/scaned/confirmed/expired/need_verifycode/scaned_but_redirect/verify_code_blocked/binded_redirect` 状态机处理。**只有 `[ManagerOnly]` 管理员可发起/查看二维码与提交验证码**，扫码确认的 `ilink_bot_id` / `bot_token` 在服务端保存、不回明文 token；`ilink_user_id` 若可靠返回即作为**唯一绑定微信用户**并做确认展示，不返回则进入待绑定状态，由该微信账号发短时一次性挑战码完成精确绑定；任何情况下不采取“首条消息自动绑定”；`baseurl/redirect_host` 仅允许 HTTPS 且可信 `weixin.qq.com` 子域，禁止 private IP/重定向到第三方（SSRF）；重绑须管理员再次确认并注销旧会话。
3. 使用官方协议要求的 `AuthorizationType: ilink_bot_token`、`Authorization: Bearer <bot_token>` 等适用 header 和 `base_info`；`channel_version`/`iLink-App-ClientVersion` 取已确认的兼容版本配置，不硬编码 TerminalBuddy 的旧版本或冒用 OpenClaw 产品身份；`bot_agent` 声明真实客户端并核实后端接受情况。`POST /ilink/bot/getupdates` 长轮询，使用 `get_updates_buf`（不要回退到已弃用的 `sync_buf`）；只接**唯一已绑定微信用户**的私聊文本，保留 `message_id` 的**数字表示**、账户/peer、`context_token`，严格判断 `ret/errcode/HTTP`，`-14` 暂停并要求扫码或按官方行为退避；不把超时等价成一次空消息成功。
4. `POST /ilink/bot/sendmessage` 的 `msg` 包含 `to_user_id`、稳定的 `client_id`、消息类型/完成状态、文本项和**该入站会话最新可用**的 `context_token`；`client_id` 在超时重试中保持不变。默认仅有可用 token 才发送被动回复；token 缺失/失效时标记失败供管理员处理，**不仿照 TerminalBuddy tokenless 重发并冒称后端必收**。主动推送（无新入站 token/隔夜）是独立实验，必须真实投递验证；平台拒绝即不开启微信主动通知。非文本媒体需要下载/加解密与路径白名单，首期不做。
5. 同一微信账户只允许一个后台轮询者；`ChannelCursor` 保存已提交 `get_updates_buf`，每次响应中的所有合法消息与“拒绝原因+游标”**在同一 DB 事务落库**后才能推进游标并发下一轮轮询；如果事务失败，继续用原游标重取。账号禁用/修改/停机能取消长轮询并保存最后游标。敏感 `context_token` 加密、短时保存/过期清理，不进日志/App DTO/脚本变量。

## 4. 共用消息事务、权限与回复路由（关键改造）

**入站持久化顺序**（对 QQ `s` 与微信 `get_updates_buf` 一致）：`平台事件 → 校验类型/账号绑定/白名单/大小/速率 → EF 事务内唯一 Inbox + 安全元数据 + 游标/QQ Session 状态 → Commit → 回 WS 心跳已提交序号或发下一次长轮询 → 后台消费者竞争认领 Inbox → 下游气泡/任务 → 完成/失败`。受拒事件也留下最小脱敏拒绝记录并推进游标；数据写失败绝不推进游标。跨重启按 Inbox 继续处理，平台补发时命中唯一索引只返回既有状态。

**副作用语义**：分账号/peer 单键串行；Inbox `Received → Claimed → Executing → Completed/Failed/Unknown` 原子状态机，先登记执行尝试再调用命令；重复事件不重新运行。崩溃在脚本副作用发生后而完成状态未提交时标 `Unknown`，**禁止自动重新执行非幂等任务**，由管理员核对后人工处理。原有 `MessageQueue.ExecTask` 是内存队列且 Task.Run 并行、`TaskRunRecorder.RunStepAsync` 领跑失败仍执行；渠道 Inbox 消费**不通过这条内存队列**，要在受控调用层修正/绕开“Claim 失败照跑”的路径，确保通道指令每个 claim 最多执行一次；App/Web 既有调用语义要保留并测试。

**授权隔离**：`AppPushService.SubmitCommandAsync` 现把用户硬编码为 App 管理员，不能给外部事件直调。新增已验证 `ChannelCommandContext(accountId, channel, peerId, InboxId, ReplyRouteId, ActorKey)`，授权先于 `MessageProcess.SystemCommand`；只允许在 Web 中明确启用的**具体安全指令**，这是功能权限而非聊天用户管理员/多用户白名单；高危系统命令及脚本/AI 写入默认不可由消息触发，不能仅靠 task CommunicationTypes 空值自动授权。`MessageProcess` 当前 `adminKey` 共用多步骤状态，应将步骤 ActorKey 分区为 App 管理员/QQ账户+peer/微信账户+peer，确保某一端的 `q` 不退出另一端正在执行的任务；脚本所需 `user_id` 与实际渠道身份字段分别定义，不将微信/QQ ID 伪装成 Manager。AI 高危提案与脚本上传仍需原 Manager-only 审批路径，不能由 IM 文本直接授权。

**原路回复矩阵**：A 快捷回复、任务开始/结束/失败摘要：`InboxId→ReplyRouteId→Outbox`，只送原账户/peer，同时为 App/Web 留气泡；B 指令触发脚本 `ctx.Notify`：经 `TaskCommandStep→TaskRunRecorder→TaskExcuteService→QuantumNotifyFacade→NotifyService` 显式传只读 `ReplyRouteId`（仅内部服务可访问），若来自 QQ/微信则登记同一原路 outbox；C 手动/App/Quartz 脚本和普通系统通知：仍仅 App，只有 Web Manager 对“通知类别+该平台唯一绑定账号”显式订阅且平台主动发送通过才可跨平台推送。**不能根据最新会话/标题猜回复目标，也不能把 App 消息默认广播。** `MessageProccessDTO.Clone()` 手工复制新增关联元数据且清除 `TargetTaskId` 防自触发；任务跨会话 `MessageMover` 保持 App WS/REST 归属，但不改变平台回复路由。

**出站原子性**：现有 `AppMessageService.AppendAsync` 自开/提交事务，`SendMessageHelper.Send` 通过静态 `AppPushDispatcher` 落 App，`NotifyService.SendMessage` 入内存队列；三者不保证渠道投递与 App 气泡原子。为**需双通道投递**的新路径增加 scoped `ChannelMessageCommitService`，同一 `IQuantumDbContext` 事务中调用 `AppMessageService.AppendNoSaveAsync` + 插 Outbox、提交后才发 WS；Inbox 的最终 Completed/Failed 状态仅在**整次指令及脚本执行结束**后单独提交，不能把某条通知已受理误记为指令完成；原 App-only 路径不无关重构。任务 `ctx.Notify` 的异步门面先 await 持久受理，不在静态泵“已入内存”时标远端成功。Outbox Worker 原子 Claim，成功记录平台消息 ID/接受状态（**不等于用户已读**）；可重试错误按退避/速率/过期处理、4xx/权限拒绝停止、超时保持同一幂等引用并考虑未知回执重复风险；提供死信/人工重试且敏感日志脱敏。

## 5. 管理端、迁移与部署步骤

| 阶段/交付 | 具体内容 | 验收门禁 |
|---|---|---|
| P0 独立可行性/账号 | 固定官方 QQ 文档版本和机器人权限、排查 QQ 5/60min 与主动消息文档冲突；微信确认许可/范围，用隔离 .NET 项目完成二维码→账号→轮询→回发→重启后收发；短时/隔夜主动通知分别记录。全程测试账号/脱敏记录，不写生产密钥。 | QQ/微信两边**各**有真实文本双向、断线重连；微信独立接入资格/行为可接受；失败即暂停相应功能。缺账号不视为通过。 |
| P1 核心事务/迁移 | SQLite/MySQL 双 migration，实体、索引、Inbox/Outbox 与消费者、身份绑定/会话隔离、原路路由接缝；对原 App/Web 行为做前后回归。 | 重复消息/崩溃/多实例下非幂等指令不自动重放；两套 Up/Down 一致，迁移可回滚；后端全测绿。 |
| P2 QQ 内置 | token、WS Gateway 状态机/Resume、C2C 事件/REST、受控主动通知、配置和健康/错误状态。 | 私聊真实来回、5/60min 实测、限额/拒收/expired 负测、重启补发不重复执行。 |
| P3 微信内置 | 扫码页/验证码、账号状态、HTTPS 轮询/游标、`context_token` 原路回复、过期/重绑、主动通知有条件启用。 | 真实私聊与断线/过期/掉电恢复、账号单活、敏感 token 不外泄、无 token/主动拒绝可见。 |
| P4 管理体验/上线 | Web 管理页/绑定流程/通道日志、运维手册、默认关闭灰度。App 如改 DTO 需单独发布同步；`publish.bat`、`build-service.bat`/Docker 产物只需验证新的配置/证书/卷，**不引入侧车镜像**。 | 按 QQ→微信分别灰度，任一关闭不影响 App/另一通道，双渠道均过方可称“完成”。 |

**迁移与兼容**：新增非空字段要有默认值或为 nullable；MySql/Sqlite 双库对唯一键长度、大小写排序、时间 UTC、事务语义逐项测。存量 `CommunicationTypes`=1/4 只能历史展示，不自动启用；新增枚举值不能误命中旧任务。`ChatMessageModel` 来源字段可空且旧端未知字段可忽略；如要在 App 实际展示，先更新 `docs/App端API契约.md` 后提供 App DTO `@SerializedName` 与版本兼容。配置示例只列键名不附真实 Secret；数据库/登录态备份加密与恢复需演练。

**部署**：备份 DB/配置/加密 key-ring → 两库 migration 演练 → 后端默认两通道关闭上线 → Web 管理页启动 QQ 挑战码绑定、微信扫码绑定（需平台 Manager 登录，仅控制配置）→ 分渠道小流量打开 → 平台错误/重连监控 → 双端签收。回滚先 kill switch/撤销平台 token/停止后台轮询，保留 Inbox/Outbox/游标，不直接删新表使事件重放；程序回退与 DB 兼容策略要以发布版本演练。单活首期由 deployment 限制后端仅一个有通道连接的副本；未来多副本要 DB 租约和 fencing token，不能以进程内锁替代分布式锁。

**安全基线**：`[ManagerOnly]` 用 JWT 正向 `Manager="true"`，不反向推断；QR/口令/第三方 token/微信 `context_token` 不进日志或客户端结果；DNS 回绑、重定向、SSRF、长轮询超时、请求体/附件大小、重连风暴、主动推送限额均需测试。动认证/脚本链路前须先拿到本次检出仓库缺失的 `docs/security/公网部署安全审计-2026-09-14.md` 和 `docs/App端API契约.md`；拿不到不能批准敏感实现。`appsettings.json` 现含真实信息，不读取输出、不复制到文档。

## 6. 验证清单与确认点

- 后端单测（`Quantum.API.Tests`）：QQ WebSocket Op10/2/0/1/11/6/9/7 的状态机/断线、Token 刷新与额度、C2C event/id/msg_idx/message_type、过期/重复 `msg_seq`；微信 QR 状态全分支、版本/headers、`message_id` number、重复/乱序、`get_updates_buf` 崩溃原子、HTTP/业务 `ret/errcode`、`-14`；未绑定拒绝、挑战码抢占/重放、单账号约束、QQ/微信相互隔离/任务副作用 Unknown、Outbox 多次重试/阻断跨账号错发、双库迁移 Up/Down。已有 SecurityHardeningTests/PermissionLayeringTests 必须全绿；`dotnet build` + 完整 `dotnet test` 数量以实测为准。
- Web 管理 UI：`npm run test` / `npm run build`（build 写入 `quantum-release/wwwroot`，执行前后核对 Git）；浏览器实测二维码仅 Manager 可见、重绑需确认、管理员关闭开关立刻停轮询、非 Manager API 受服务端拦截。App 仅在改 DTO/画面后跑 `assembleDebug`、`testDebugUnitTest`、真机/已有 WS REST 同步回归。
- 平台真测（**不能由 mock 替代**）：QQ 私聊消息→App/Web 留痕→快捷回复→指令脚本通知原路回复；微信扫码/重登后同样流程；掉线、重复推送、静默隔夜主动通知、账号注销、权限拒绝、限额拒绝均有脱敏请求 ID/错误码/实际截图。平台可能拒绝但本地“HTTP 成功”不等于用户收到；分别记录 QQ 与微信能力矩阵。全面通过且所有者确认后再实施/开放群聊或媒体。

**本轮必须确认**：是否同意先以“私聊文字双向 + App/Web 留痕”为首期，群聊/媒体独立后续规划；QQ、微信**均需**最终达成，各通道恰好一个机器人账号和一个私聊用户绑定；不设置聊天管理员/白名单，无 OpenClaw、无任务脚本通信服务。测试前需要由账号持有人提供*本机私密配置*方式的 QQ 测试机器人和微信扫码配合，**不要在聊天发送密钥/二维码登录令牌**。无法确认微信的独立客户端政策或真实扫码前，P3 是条件阶段而不是已获准实现。


## 审核记录

### 2026-09-27 首轮审核（七维度全量）

**结论：有条件通过，可确认 P0 调研与最小可行性验证；P1—P4 尚不能直接开工。** 目标、内置架构、QQ/微信官方协议步骤、双库/安全、验收及回退已经覆盖；保留以下需要确认前收敛的实施阻塞。与旧草案相比，R-01/R-02（内存队列与气泡-出站原子）已在本计划 §4 转化成具体的新 Inbox/Outbox 与受控执行设计，旧 QQ 外桥依赖已取消。

| 编号 | 严重级 | 位置 | 问题及建议 | 状态 |
|---|---|---|---|---|
| D-01 | 🔴 阻塞 | §1 绑定、§2 第 3 项、§5 P0 | QQ `accountId+user_openid` 虽来自平台事件，但“管理员已验证绑定”的**持有人证明流程**仍未定：如果只由管理页填 openid，误绑定/冒名风险无法排除。应定义一次性挑战码（管理页生成、TTL、仅指定 Bot C2C 事件由对方实际发送验证，服务端原子消耗后绑定，操作日志脱敏；管理员也可经 QQ 持有人实测验证）；微信扫码的 `ilink_user_id` 如何作为唯一允许 peer、重新扫码后如何撤销旧绑定也须明确。P0 验证真实事件字段后固化接口/状态机。 | 已修复待复核（方案已定义 QQ 短时挑战码+Web 确认、微信扫码绑定/挑战备选；须 P0 真机复核） |
| D-02 | 🔴 阻塞 | §5 安全基线 | 本次检出仓库仍缺 AGENTS.md 要求的安全审计文档和 App API 契约。P0 可开展；P1 涉及认证/消息契约之前必须获得并核对文件或由所有者明确补齐，不能将未读材料默认为已审阅。 | 待修 |
| D-03 | 🟡 建议 | §1 数据、§4 入站/出站 | 计划指明两库唯一索引但没有字段级索引清单/长度及 MySQL 索引上限对策；P1 迁移前应落表：账户（平台+平台 Bot ID）、绑定（账户+场景+peer）、Inbox（账户+事件类型+消息 ID+msg_idx）、Outbox（回复路由+分片/序号/用途）、Cursor（账户），对缺 ID 消息拒绝或安全回退，测试 SQLite/MySQL 同一事务与顺序。 | 待修 |
| D-04 | 🟡 建议 | §2 第 4 项、§4 出站 | QQ `msg_id` 5 分钟与页面顶部“被动回复 60 分钟”冲突已识别并采用短窗口；实际发送多段回复若超过短窗口，App/Web 中应显示“平台回复过期”且不把通道状态标为已送达；Web 文案、日志和主动通知权限单独测试，P0 记录当前账户实际错误码。 | 待修 |

**本轮核实台账**：
- 已核实到方法体（本仓库）：`Startup.ConfigureServices` 注册托管服务及 scoped 应用服务；`AppPushService.SendChatMessageAsync/SubmitCommandAsync`；`AppMessageService.AppendAsync/AppendNoSaveAsync`；`MessageQueue.ExecTask`；`MessageProcess.MessageAsync/SelectTaskCandidates`；`TaskRunRecorder.RunStepAsync`（Claim 失败仍执行）；`TaskExcuteService` 组装 `QuantumNotifyFacade`；`QuantumNotifyFacade.SendAsync`、`NotifyService.SendMessage`、`SendMessageHelper.Send/SendMessage`、`MessageProccessDTO.Clone`；`CommunicationType` 历史枚举、`TaskCommandStep`；`IQuantumDbContext`/Sqlite/MySql 的 DBSet。已核对当前 `docs/` 列表及仓库安全/HTTP 约束。
- 已核实为**官方文档事实，未实测平台行为**：QQ 当前官网鉴权、WebSocket 事件/协议、单聊事件/发送接口及文案差异；微信官方仓库 README/后端协议 QR/getUpdates/sendMessage/context_token 与“不是完整服务端契约”的限制。已核实 `D:\gitee\TerminalBuddy` 参考代码微信独立实现与 QQ 外桥的差异，但没有借用或执行该项目。
- 未核实：测试 QQ 机器人权限/主动额度/5 或 60 分钟真实窗口、微信不依赖 OpenClaw 的账号政策及扫码/重连/隔夜主动发信、平台应答的最终到达语义。未覆盖：当前缺失的安全审计与 App API 契约全文，真实账号与生产配置；本轮仅文档改动，未运行 dotnet/npm/Gradle 测试。




### 2026-09-27 第二轮审核（单账号约束澄清）

**结论：有条件通过；P0 可按计划开展，P1—P4 仍以 D-02 安全文档补齐及平台真实验证为门禁。** 功能范围已改为每个平台最多一条机器人账号及一条唯一绑定私聊账号；聊天侧没有管理员角色，也不设置用户白名单。Web 配置入口的 `[ManagerOnly]` 是仓库既有的系统级安全要求，不能误删。

| 编号 | 严重级 | 位置 | 问题与建议 | 状态 |
|---|---|---|---|---|
| D-01 | 🔴→关闭待平台实测 | §0“唯一绑定”、§2.3、§3.2、§5 P0 | QQ 挑战码经官方事件持久候选 OpenID + Web 管理确认，微信优先扫二维码返回的 `ilink_user_id`，未返回时也走一次性挑战；不再依赖“填 ID 即绑定”或“首条消息抢占”。Phase 0 仍须核验 QQ C2C OpenID 与微信扫码返回目标字段的真实性。 | 已修复待复核 |
| D-02 | 🔴 阻塞 | §5 安全基线 | 当前检出仓库缺安全审计与 App API 契约；P1 前需获得并核对。 | 待修 |
| D-03 | 🟡 建议 | §1 数据 | 已加账户、绑定表各以平台为唯一键；Inbox/Outbox 复合索引及 MySQL 索引长度仍需在 P1 迁移设计时具体化。 | 部分修复 |
| D-04 | 🟡 建议 | §2 第 4 项 | QQ 回复 5/60 分钟冲突仍待平台真实验证；该限制与是否单账号无关。 | 待修 |

**本轮核实台账**：已复核 `ChannelAccount/ChannelBinding` 仍为拟新增、目前无现成单账号约束；`AppMessageController`/既有管理控制器类级 `[ManagerOnly]`、`MessageProcess.MessageAsync` 中 `adminKey` 仅是旧 App 单管理员处理语义，不能未经重构让 QQ/微信消息获得 Manager 身份。官方 QQ C2C/微信二维码事件的账号字段真实性属于外部平台行为，未实测；安全审计/App 契约仍缺；未覆盖新的运行时代码（本轮未实现），未运行构建测试。
