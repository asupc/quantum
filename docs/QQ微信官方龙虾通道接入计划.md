> **旧方案，已否决（2026-09-27）**：用户明确要求不使用 OpenClaw。本文件的侧车方案不再作为实施依据；新方案见 docs/QQ微信原生CSharp双向通信计划.md。

# QQ／微信官方 OpenClaw 通道接入计划（待可行性门禁）

日期：2026-09-27；状态：**候选方案，未经真实通道验证；仅供确认验证范围，不授权实施上线**。

## 0. 决策前提与证据分级

- “龙虾机器人通道”在本方案中专指腾讯发布的 **OpenClaw QQBot 渠道插件** `@tencent-connect/openclaw-qqbot` 和 **OpenClaw Weixin 渠道插件** `@tencent-weixin/openclaw-weixin`，不是已移除的 QQ/微信旧渠道、微信个人号网页模拟登录，也不把 QQ 开放平台机器人等同于普通 QQ 个人号。
- 微信指腾讯插件提供的普通微信扫码接入；**不等于微信公众号、企业微信**。QQ 优先 C2C 私聊；群聊另行验收，不因为官方插件宣称支持就默认开启。
- `docs/QQ微信接入可行性验证记录.md` 的 2026-09-26 离线探针只证明 QQ SDK 与微信插件的安装和对象形状；它**未验证** QQ 官方插件的完整 Gateway 链、微信真实扫码和收发、桥接拦截及 Quantum 原路回复。本文件不撤销原记录的“不允许据此启动实施”结论。
- 官方 QQ 插件 README（仓库 README.zh.md 展示 v2.0.3）与 2026-09-27 npm dist-tag（`@tencent-connect/openclaw-qqbot@2.0.4`）不一致；依赖不得照文档标题或 `@latest` 直接生产安装。微信同日 npm dist-tag `@tencent-weixin/openclaw-weixin@2.4.9`；OpenClaw dist-tag `2026.9.6`。这些是调研快照，实施前重新核验并固定精确版本、校验兼容矩阵、审阅许可和供应链。
- 官方依据与复查入口：[QQ 官方渠道插件（安装、多账号、主动消息、WebSocket/Webhook）](https://github.com/tencent-connect/openclaw-qqbot/blob/main/README.zh.md)、[QQ 机器人开放平台](https://bot.q.qq.com/wiki/)、[微信官方渠道插件（版本、扫码、隔离）](https://github.com/Tencent/openclaw-weixin/blob/main/README.md)、[微信官方后端协议（context_token）](https://github.com/Tencent/openclaw-weixin/blob/main/docs/protocol.md)、[OpenClaw 官方插件与通道文档](https://docs.openclaw.ai/channels)。注意“后端协议”描述插件和微信后端的交互，**不是**已提供给 Quantum 的稳定桥接 API；桥接点须另行验证。

## 1. 目标、范围与不做

目标：保留 Quantum App/Web 现有会话为系统记录入口，增加受控的 QQ／微信私聊入站和**明确选择目标**的出站（原路回复、指定通知）；失败可查询、可重试或明确不可重试，禁用渠道不影响 App。默认关闭，以单管理员自用为第一阶段。

首期不做：恢复任何旧 QQ/微信/公众号/WxPusher/Web-Chat 类、旧表、旧登录流程；用非官方个人号协议替代；在 Quantum 内实现/逆向微信 iLink 协议；把 OpenClaw 自带 AI、工具执行或自动升级当作 Quantum 管理员；全量 App 通知跨平台群发；群聊/多租户/所有媒体语义等价承诺；静默丢弃平台拒绝的推送。不把微信扫码成功等同于稳定可主动推送。

## 2. 候选架构（必须经 §3 才可定案）

```text
QQ 官方机器人 ── 官方 QQBot 插件 ─┐
                                 ├─ 隔离 OpenClaw Gateway + 最小 Quantum 桥接插件 ── 私有双向桥接 ── Quantum.API
微信扫码账户 ── 官方 Weixin 插件 ─┘                                    │
                                                                      └─ 现有 App/Web 会话、指令、日志
```

- **推荐路径**：隔离侧车运行固定版本的 OpenClaw Gateway 及两个官方渠道插件。自有桥接插件只负责消息封装/拦截、请求 Quantum、按原平台账户和会话精确回送；**只在官方渠道走通、且已实证可在分发给默认 Agent 前截获并阻断时采用**。OpenClaw 安装为新服务，不塞进 ASP.NET 主镜像，不把脚本执行交给 Gateway；Gateway 的 AI/exec/web/文件权限默认禁用，禁用或限制官方插件的远程升级、管理命令要逐项实测，不可配置时对该风险作阻塞处理。
- **替代决策**：若官方插件无法可靠拦截默认 Agent 或无法把回复绑定到精确账户/会话，停止该侧车实施；QQ 可单独评估官方 QQBot SDK 的进程外适配器（须重新核对用户“官方龙虾通道”要求，不能冒称同一方案），微信不得自行猜测协议或回退旧实现。允许最终只启用通过验收的单一渠道。
- QQ 默认使用插件的 **WebSocket 出站连接**（无需为 QQ 回调开放公网入口）；Webhook 模式仅经单独评审后使用插件内置验签、可信代理和独立路由；绝不将未经验证的第三方回调直连 Quantum。微信按官方 CLI 扫码，经插件维持连接；扫码、续登及账号可用性按真实环境验收。
- 对每条入站构造 `channel / accountId / chatType / peerId / sourceMessageId / occurredAt / contentType / content / contextToken(仅微信必要时)`。桥接只将允许的 C2C 文本发送至私有 Quantum 入口；原始平台凭据和微信 context_token **只在侧车安全存储**，Quantum 如需回发仅保存短生命周期的不透明路由引用。任何媒体按白名单、大小、私有下载处理，首期统一回“暂不支持”，不把私有 URL 直接落日志。
- 侧车→Quantum 使用专用短期身份/HMAC 请求签名（时间戳+nonce+body 摘要）、重放窗口、限额和仅内部网络访问；Quantum→侧车独立签名，凭据分权、轮换，TLS/私网互联。**侧车密钥绝不是 Manager JWT**；服务端先验签，再以管理员在 Web 配置的 `(channel, accountId, chatType, peerId)` 精确绑定验证，默认拒绝，不能使用“不是非管理员就是管理员”的反向逻辑。变更绑定需 `[ManagerOnly]`、审计和扫码/持有权校验；仅填一个可猜 peerId 不视为完成绑定。
- 首期入站只允许用户确认的快捷指令白名单，屏蔽管理员系统指令、任务管理/脚本/AI 写操作；放开后须逐项威胁建模与二次确认。平台原生命令（例如升级命令）和默认 Agent 不得绕开 Quantum 授权。黑名单 `BlackQQ` 和来源自报 `user_id` 不是管理员验证机制。
- **出站规则**：从 QQ/微信来的单条指令，其回复关联同一个 `(channel, accountId, peerId, inboundId)`；App/Web 普通消息和任务通知默认只投 App，管理员为**具体通知类别和目标**启用后才额外推送。写 App 会话和渠道投递各有独立结果，不因渠道失败伪装“已送达”；状态 `Accepted/Delivered/Rejected/Expired/Retrying`（实际状态定义须遵从平台回执能力；无回执不标 Delivered）。QQ 官方插件展示定时主动推送但 README 也提醒平台可能拦截；微信官方协议要求回复携带入站 `context_token`，缺失也尝试发送的插件行为**不证明后台会接收**，因此隔夜主动推送是单独阻塞门禁，无法验证即在 UI 禁用微信主动推送。

## 3. Phase 0：真实可行性验证（先做，不改生产业务代码）

隔离环境、测试 QQ 机器人及测试微信账户，按 `docs/QQ微信接入可行性验证记录.md` 原判据补充**带时间、版本、请求/结果（脱敏）**的记录。测试凭据通过本机私密文件或环境注入，不入 Git/日志/聊天；真实扫码由账户持有人自行完成。

| 门禁 | 最小实测 | 不通过处理 |
|---|---|---|
| QQ 登录 | 在固定 OpenClaw+官方插件版本下鉴权、断线重连；私聊文本入站→桥接→原路回复；多账号发错 accountId 负测 | 停 QQ |
| 微信登录 | CLI 扫码、重启后续登、私聊入站→桥接→回发、登录过期恢复 | 停微信 |
| 单一路由 | 验证桥接 hook/adapter 实际执行顺序、阻止默认 Agent/内置命令、重复事件不二次触发；恶意输入不调用工具 | 未通过禁止任何渠道接入 Quantum |
| 权限 | 未绑定 peer、同 QQ 不同机器人、伪造签名/过期时间/重放、群聊、非法媒体全部拒绝；命令不提升 Manager | 未通过禁止上线 |
| 主动发信 | QQ 主动文本、配额/频控/平台拒绝；微信短时与隔夜各测试发送及错误码，测试重启后的路由上下文 | 不通过该项即关闭对应渠道的主动推送，**不影响原路回复已通过的渠道** |
| 业务最小闭环 | Quantum 沙箱（非生产）记录 App 气泡、按来源原路回复；一次断线+重投仅执行一次指令 | 不进入 Phase 1 |

验收人记录平台可用区域、所用 QQ 机器人类型/权限、微信账户状态、消息类型及限制；官方资料没写明的额度/主动发送窗口不得凭经验当常数。完成记录并经仓库所有者确认后再确定范围和实施，不允许依据离线探针跳过门禁。当前缺少测试 QQ AppID/Secret 与微信扫码配合，故**本轮无法宣布 Phase 0 通过**。

## 4. Phase 1：后端、安全与持久化（仅门禁通过且确认后实施）

1. 在 `Quantum.API/Quantum.Entities` 建桥接 DTO、绑定、幂等/投递模型；在 `Quantum.API/Quantum.Data/IQuantumDbContext.cs`、`QuantumSqliteDbContext.cs`、`QuantumMySqlDbContext.cs` 注册实体和唯一索引：绑定唯一键 `(channel, accountId, chatType, peerId)`；入站唯一键 `(channel, accountId, sourceMessageId)`；出站唯一键 `(channel, accountId, correlationId, deliveryPurpose)`。平台无稳定消息 ID 时 Phase 0 必须给出经过验证的替代键。SQLite/MySQL 各建迁移并逐条比对 Up/Down；旧 `CommunicationType.QQ=1/微信=4` 是历史保留值，**不得直接重启用**，新通道追加新枚举值并核查存量 `CommunicationTypes` 任务过滤，确保默认任务不会误触发。数据迁移只新增，无旧渠道表自动恢复。
2. `Quantum.API/Quantum.Application` 加 `ChannelBridgeService`、`ChannelDeliveryService`/托管重试器；私有入口在 `Quantum.API/Quantum.Web/Controllers` 加专用认证过滤器（不能假冒 `[ManagerOnly]` 或使用管理员 JWT），保持 ResultFilter/ExceptionFilter 的 **HTTP 恒 200 `ResultModel`**；侧车须以信封 `Code` 判断业务结果，而非只看 HTTP 码。管理绑定/投递设置端点必须 `[ManagerOnly]`。桥接密钥从部署 secret 注入，不落系统设置明文字段/日志；请求大小、速率、超时、重放窗口、审计留痕、轮换/撤销与通道 kill switch 配套实现。
3. **不能直接转调用** `AppMessageController.Command` 或 `AppPushService.SubmitCommandAsync` 并假装变更来源：现有实现硬编码 `CommunicationType.App`、管理员用户名，`MessageProcess` 又按单管理员 `adminKey` 共享多步骤状态。抽取已认证的内部“接收→App 落库→入队”服务，对桥接身份/白名单先验证，再安全生成渠道来源并保留 Web/App 既有语义；多步骤状态按通道/账户/peer 分区，防不同 QQ/微信/App 会话互相退出/接管。`MessageProcess.SelectTaskCandidates`、`TaskExcuteService` 的 `CommunicationType` 与会话路由须配合检查，**不让未经授权消息走到系统命令分支**。
4. `SendMessageHelper.Send` 当前把所有出站送 App：保留既有 App 留痕/推送行为，但只对带可信来源路由的回复生成渠道出站记录；`ctx.Notify`/`NotifyService` 的主动通知则走显式管理员订阅表，禁止“所有 App 消息默认多发”。队列状态入库（重启可恢复）、指数退避/上限、平台 4xx/权限拒绝停止重试、幂等发送与过期丢弃；不能让静态发送泵失败吞单或桥接反投形成死循环。SDK 发出成功不等于对端已读。`AppPushService` 落库后的平台失败不回滚 App 记录，记录渠道状态供管理员查看。
5. 配置只含启停/限额/功能开关，密钥在 secret 存储；现有 API 权限分层、脚本门禁、HTTP 信封与 App WS/REST 分页契约均保持原状。改认证和渠道上传前需先读安全审计及 App API 契约：当前检出的 `docs/` 中未见 AGENTS.md 所指 `docs/security/公网部署安全审计-2026-09-14.md`、`docs/App端API契约.md`，实施前要定位可核验版本或由所有者补齐，不可假设已审阅。

## 5. Phase 2：侧车、Web 设置与部署

- 新建独立侧车目录（建议 `channel-gateway/`），固定 Node/OpenClaw/两官方插件+桥接插件版本及 lockfile/镜像 digest；本机隔离构建，许可与 SBOM 审核；桥接端到端测试，禁默认 Agent/命令和非必要权限。`docker-compose.yml` 增加**可选 profile**、私网和持久卷（微信登录态/路由凭据）及健康检查，不公开 Gateway 管理端口；禁止将 Docker socket 挂给侧车。备份登录态须加密及限权；QQ AppSecret/微信 token、context_token 不入镜像。`publish.bat`/`build-service.bat`/离线镜像交付需要补侧车发布路径，单独验证无外网 npm 时的安装过程；不手改 `quantum-release/`。
- `quantum-web/` 管理端新增 `[ManagerOnly]` 对应的“通道设置”页：状态、绑定（持有权确认）、白名单/订阅、实际投递错误和一键禁用；脚本/AI 写指令默认关闭。App (`quantum-app/`) 首期不加渠道设置；继续从现有会话读取镜像气泡，若新增来源/投递状态展示才新增 DTO `@SerializedName` 和版本兼容测试。Web/安卓新增入口在设计和权限策略评审后确定，不能暴露密钥。
- 灰度顺序：DB 双迁移与备份 → 后端（渠道全部关闭）→ 侧车安装并完成扫码/QQ 绑定 → Web 设置开启只读镜像 → QQ 私聊 → 微信私聊 → 可验证的平台定向主动通知。故障先关渠道再停侧车，App/WS/任务照常运行；回滚程序保留新表/幂等数据，勿直接删除迁移导致旧消息重放。凭据疑似泄露时撤销机器人 Secret/微信登录态及桥接密钥。

## 6. 测试与签收清单

- 后端：签名/重放/未授权/群聊拒绝；App+QQ+微信隔离；多步骤会话互不覆盖；相同平台事件重复、顺序乱序、跨重启重试；渠道拒绝不再标成功、不重复执行；媒体拒绝；意外默认 AI/工具执行为 0；HTTP 200 信封；双库迁移 Up/Down。跑 `dotnet build` 与完整 `dotnet test`（数量以实测为准，必须全绿）。
- 侧车：固定版本真正私聊扫码/收发/隔夜推送，模拟网络断连、Secret 轮换、微信重新登录和升级回归；QQ 群与所有媒体在首期都必须拒绝或静默禁用，不能暗中转入 AI。
- 前端：`npm run test`、`npm run build`（注意构建写入 `quantum-release/wwwroot`，测试前后核对 Git 状态）；实际浏览器验证管理员/非管理员显隐与服务端拒绝。App 如改运行代码则 `assembleDebug`/`testDebugUnitTest` 和真机回归。
- 发布验收：Docker 单独开关侧车不影响现有 Quantum，停侧车后 App 可读历史；凭据不出现在镜像/日志/配置模板/备份导出；记录 QQ/微信各项“已通过／不支持／待验证”能力矩阵及版本和具体错误码，用户确认后才放量。

## 7. 待用户确认的产品与环境问题

1. 是否接受“官方 OpenClaw 渠道插件 + 独立隔离 Gateway”，而非要求仅 ASP.NET 进程内直连；如果不接受，则微信方案暂不能据此成立。
2. 首期是否只开放**绑定管理员的私聊+指定白名单指令**（推荐），群聊和全部 App 通知广播不做；微信主动通知若实测不通过，是否接受仅被动回复。
3. 可否提供测试 QQ 机器人权限及微信账号持有人在隔离环境配合扫码（**不要在聊天里发送密钥**）；何处取得当前检出仓库缺失的安全审计/App 契约文档。

> **下一步**：先确认 Phase 0 验证范围与测试账号条件；在验证记录附脱敏证据和失败范围、重新审查本计划后，仓库所有者明确批准才实施 Phase 1/2。

## 审核记录

### 2026-09-27 首轮全量审核

**结论：有条件通过（仅可确认 Phase 0；Phase 1/2 有 1 个阻塞项）。** 七维核对：目标/范围与门禁明确；架构/认证限制明确；安全与双库迁移/测试/部署均有安排。实施前必须解决下列消息归属断点；本审核不修改计划正文或业务代码。

| 编号 | 严重级 | 位置 | 问题与建议 | 状态 |
|---|---|---|---|---|
| R-01 | 🔴 阻塞 | §2 出站规则、§4 第 3—4 项 | “原路回复”未定义脚本执行中 `ctx.Notify` 发出的消息归属，也未规定路由字段怎样穿过 `MessageProccessDTO.Clone()`、任务执行边界和静态发送泵；现有 `NotifyService` 无条件转 App，无法据现有设计保证完整原路回复。实施设计须给出逐类消息的原路/仅 App/显式订阅矩阵；对需要回平台的回复定义持久关联键与过期策略（不可依赖跨队列 AsyncLocal），核查克隆透传；明确异步任务脚本消息是否原路回发，未覆盖者在产品中显式标为 App-only，并补跨会话/跨重启测试。 | 待修 |
| R-02 | 🟡 建议 | §4 第 1、4 项 | 入站去重键若平台 messageId 只在会话内唯一，则现写法会误去重；出站至少一次重试可能令平台重复消息。Phase 0 应验证 ID 唯一域和平台幂等能力，必要时把 chatType/peerId 纳入键；文档约定出站的重复风险与自动重试上限。 | 待修 |
| R-03 | 🟡 建议 | §5 发布 | 微信扫码持久卷的备份/恢复与多副本单活需要操作说明，避免复制登录态使两侧同时拉取；QQ WebSocket 同样先单活，水平扩展另评估。 | 待修 |

**本轮核实台账**：
- 已核实到方法体：`AppPushService.SubmitCommandAsync`（硬编码 App/管理员用户名并将用户气泡入队），`MessageProcess.MessageAsync`/`SelectTaskCandidates`（管理员共享步骤状态、旧枚举参与筛选），`MessageProccessDTO.Clone`（手写字段克隆），`SendMessageHelper.Send`（统一 App 推送）、`NotifyService.SendMessage`/`QuantumNotifyFacade.SendAsync`（脚本通知只到 App）、`ExternalPushService.SendAsync`（为独立外部推送及事务受理，不是渠道入站）、`AppMessageController.Command`（ManagerOnly 入口）、`ChatMessageModel`（没有现成渠道投递状态），并核对两 DbContext/IQuantumDbContext、`docker-compose.yml` 与当前 docs 清单。
- 已核实但**仅为外部文档/离线资料，不等于真实平台实测**：腾讯两个插件仓库 README、微信协议说明、2026-09-26 本地可行性记录、2026-09-27 npm dist-tag 快照。官方插件桥接钩子是否可阻断默认 Agent、微信主动发送窗口与 QQ 额度、真实账号行为均**未核实**，全部保留 Phase 0 门禁。
- 未覆盖：当前检出仓库缺少 AGENTS.md 指向的安全审计和 App API 契约；未取得可核验版本前不能审查它们的具体条文，实施前须补齐。未取得 QQ/微信测试凭据和扫码，未执行任何真实收发；本轮只写计划，无代码改动，未运行构建/测试。

