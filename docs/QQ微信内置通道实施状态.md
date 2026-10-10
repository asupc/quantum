# QQ／微信／飞书内置通道代码实施与联调状态

## 2026-10-09：App 与机器人任务消息来源隔离（本地完成，未发布／未做真实账号验收）

### 根因与修复范围

- **多步骤等待状态串来源**：QQ／微信／飞书入站与 App／Web 均沿用唯一登录账号，旧 MessageProcess 只按该账号查找或退出等待步骤。机器人进入多步骤任务后，App 输入可能继续该任务，也可能被它挡住其他指令；不同平台／私聊发件人之间同样会互相影响。
- **消息处理入口缺少明确路由边界**：原路回复原先仅由 ChannelCommandWorker 的外层异步上下文提供，MessageProcess 自身不重设上下文。现在每条消息按受信 ChannelReplyRouteId 设置回复路由，App／Web 的空路由显式清空，结束后恢复调用方上下文；子步骤采用当前入站消息的回复路由，不复用起始消息的旧窗口。
- **等待步骤按来源隔离**：工作器生成稳定的平台／账户 ID／账户版本／私聊发件人归属键，存于进程内 TaskCommandStep.ActorKey。App／Web 保留独立来源域及原有跨任务会话续接；切换平台、发件人或账户版本不得接管旧等待步骤。脚本 user_id 仍为原登录账号，没有引入角色、聊天管理员或新的授权入口。
- **Q 只退出当前来源**：App／Web 与各平台私聊互不清除等待步骤。原路回复 ID 与来源键都不能经 Newtonsoft／System.Text.Json 客户端 JSON 注入；手写出站克隆仍保留受信元数据。使用 BCL 标记，无新增包依赖。
- **保留原任务会话归组规则**：ctx.Notify 和任务开始／结束消息仍按现有任务会话名／任务 ID 归组；不会因机器人曾触发任务而把其他来源的任务改挂机器人窗口。没有修改数据库模型／迁移、任务配置、平台凭据、通道主密钥或平台入站权限，也不自动重写历史聊天记录。

### 本地验证

- 修改前后端全量基线：858／858 通过（包含工作区已有的断线恢复改动）。
- 原五项最小回归在修复前全部失败；新增 MessageSourceRoutingTests 共 28 例，覆盖 App 与三平台互相隔离、同私聊新消息续接、不同发件人／账户版本隔离、Q 作用域、并行上下文、双 JSON 元数据边界，以及 Notify 门面到内存 SQLite 会话落库／机器人 Outbox 的离线链路。
- 定向回归（新来源测试／ChannelProtocolTests／SessionGroupingTests／TaskCommandStepAuthTests）：85／85 通过。
- 后端全量：dotnet test Quantum.API/Quantum.API.sln --no-restore --nologo，886／886 通过。
- 仅改后端来源路由及其测试／本文档，未改 Web／App 客户端，也未执行发布／提交／推送／部署。

### 待真实账号验收

1. 更新后的后端启动后，分别从 QQ／微信／飞书发起多步骤任务，再从 App 发起其他指令；App 不得继续或被机器人等待步骤挡住，其回复不得进入机器人出站队列。
2. 在同一平台的原私聊继续下一步，机器人应使用新入站消息的回复路由正常回复；其他平台／其他发件人不得续接该等待步骤。
3. 分别在 App 和机器人私聊发送 Q，确认只清理当前来源；定时／手动通知仍按原配置归组。历史错归消息不自动搬迁，平台终端实际送达须手机侧确认。

## 2026-10-09：断线自动恢复修复（本地完成，未发布／未做真实账号稳定性验收）

本轮依据现行代码修复连接恢复缺陷，未修改数据库模型、平台凭据、通道主密钥或私聊权限；不清空凭据、不自动扫码、不改变主动断开接入的语义。

### 已修复的问题

- **飞书心跳原来没有实际发包**：旧 KeepAlive 只定时检查 WebSocket.State，半开／黑洞连接仍会显示 Open；改为按官方 SDK 帧协议主动发送带 endpoint service_id 的控制 Ping 并等待 Pong，超时或发送失败主动断开。并发发送加串行锁和发送截止时间。
- **飞书任务异常被遗失**：旧代码只等待 Task.WhenAny，不观察先结束任务的异常，也不等待其余收发任务结束；改为传播故障、取消并等待接收／心跳／账户监视任务，然后复用已保存 AppID／AppSecret 取新地址重连。已建连后的重试采用服务端 ReconnectInterval（本地上限 300 秒），连续失败指数退避；连接仍经官方域名／公网 DNS 校验且不走代理。
- **QQ 握手／心跳故障可能卡住接收循环**：增加建连 20 秒、HELLO／READY／RESUMED 握手 30 秒、发送 15 秒的截止时间；心跳发送故障和缺 ACK 必须中止旧 socket，唤醒 Receive 并进入自动重试。4006／4007／4009 及 4900–4913 等失效会话清除旧 session／sequence 后重新 Identify，刷新 AccessToken；4004 也使缓存 token 失效，再用原 AppID／AppSecret 获取新 token；4008 限流至少等待 60 秒。不删除账户凭据，仍有效的会话继续 Resume。
- **微信完整长轮询缺少截止时间**：原 HttpClient.Timeout 在 ResponseHeadersRead 模式下不覆盖响应体读取；现在 65 秒期限覆盖请求头和响应体，超时转为 NETWORK_TIMEOUT 并退避重试。临时网络故障保留原 bot_token 和持久游标，成功后清错并重置退避；平台明确返回 -14 时仍按 WEIXIN_SESSION_EXPIRED 停用，要求重新扫码，不做绕过。
- **旧会话不得误伤新扫码结果**：QQ／微信／飞书状态更新按账户 ID、凭据密文和 BindingVersion 匹配；微信失效停用与写错原子完成，旧轮询的 -14 不能关闭刚扫码的新会话。QQ 旧会话失效也不得清掉新配置的检查点。
- **主密钥问题单独报错**：CHANNEL_KEY_UNAVAILABLE 表示主密钥不可用，CHANNEL_CREDENTIAL_UNREADABLE 表示现有密文无法读取。应核对启动方式的密钥来源并恢复原密钥，不应把它当平台退出或反复扫码掩盖；本轮未读取或轮换实际密钥。
- **管理页不再一律显示“连接异常”**：临时故障显示“重连中／无需再次扫码”，明确失效显示“登录失效”，密钥问题显示“凭据不可用”。在活动页签每 10 秒刷新状态，离开缓存页签停止刷新。

### 本地验证

- 修改前后端基线：823／823 通过。
- 新增 35 例 ChannelReconnectTests，包含三个真实后台 Worker 的离线断线重连、微信停止后重启复用 token／游标、失效 session／token、半开连接、发送故障、完整 HTTP 超时、取消与新旧凭据隔离。使用伪造传输和测试库，不连接真实平台账号。
- 修改后后端全量：dotnet test Quantum.API/Quantum.API.sln --no-restore --nologo，858／858 通过。
- Web：npm run test，98／98 通过；新增 9 例状态展示测试。
- Web：npm run build -- --outDir <本任务临时目录>/web-dist 通过，未覆盖 quantum-release，也未执行发布／提交／推送／部署。

### 待真实账号验收

1. 保持原数据库、原主密钥与原平台凭据启动更新版本；已启用且登录态有效的账户应自动连接，不重新扫码。
2. 三个平台分别断开网络再恢复，管理页应经历重连中到已接入；QQ 保留可恢复 session，失效时自动重新 Identify；微信继续使用持久游标；飞书重新取得地址并收发 Ping／Pong。
3. 在手机分别发送私聊文字并确认机器人回复实际送达；同时检查 Web／App 留痕与 Inbox 去重。仅界面显示已接入不代表消息送达。
4. 测试服务重启与持续断线后恢复。微信真实 -14 应显示登录失效；主密钥不一致应显示凭据不可用，恢复正确密钥后再观察，不将这些情况当普通断网。

下文为历史实施记录；如与上述行为冲突，以本节及现行代码／测试为准。

> **2026-09-28 更新（覆盖下文旧版状态）：**单用户消息通道现改为 QQ 保存凭据/微信与飞书扫码后直接接入，全部有效私聊共享现有 App 文字指令能力；不再需要唯一私聊绑定、挑战、确认、手动启用或安全快捷回复白名单。下文涉及旧流程和“脚本未接入”的叙述均为历史记录。QQ token 有效期字符串已兼容，微信 412 仅已区分收/发阶段，三个平台手机侧实际送达仍待真机验收。

> **2026-09-28 上午：飞书「扫码后永远连接异常」已定位并修复（真机送达仍待验收）。**
> 用库里真实凭据离线复现得到两条根因，均在 `FeishuChannelClient.WssEndpointAsync`：
> ① 取长连接地址的请求体字段名，飞书只认 `AppID`（大写 D），原实现发 `AppId` → 恒 HTTP 400 + `code=9499 Bad Request`；
> ② 成功响应把地址嵌在 `data` 里（`{"code":0,"data":{"URL":"wss://msg-frontier.feishu.cn/…","ClientConfig":{…}}}`），原实现在顶层读 `URL`/`ClientConfig` → 即使①修好也会判成 `FEISHU_WS_ENDPOINT_MISSING`。
> 修复后实测：bootstrap 返回 HTTP 200，`ClientConfig={ReconnectCount:-1,ReconnectInterval:90,ReconnectNonce:25,PingInterval:90}`，`ClientWebSocket` 握手 `State=Open`。
> 同时把 `FeishuWssWorker` 的异常收敛改为透传协议错误码（此前一律压成 `FEISHU_WSS_ERROR`，管理页无法定位环节），并在建连成功时清零 `LastErrorCode`（此前只会置错不会清除，恢复后仍显示「连接异常」）。
> 新增 5 例 `ChannelEndpointTests` 钉死字段名与响应层级；`dotnet test` 776/776 通过。
> **顺带推翻 `docs/飞书消息通道接入计划.md` 的一条前提**：长连接并非「仅企业自建应用」——扫码注册出的 PersonalAgent 个人应用同样能取到 wss 地址（已实测）。
> **另记一颗地雷**：`appsettings.json` 的 `ChannelMasterKey` 与 `private/quantum-channel.key` 内容不一致，库中凭据是用**密钥文件**那把加密的（`dotnet run` 经 launchSettings 注入 `QUANTUM_CHANNEL_KEY_FILE`）；若改用发布包/容器等不注入该变量的方式启动，三平台凭据会全部解密失败并集体报「连接异常」。

> **2026-09-28 上午（续）：微信 `WEIXIN_POLL_HTTP_412` 根因也已定位并修复。**
> 与登录态、机器人资格、请求字段都无关：`ChannelNetwork.PostAsync` 用 `JsonContent` 装请求体，而 **`JsonContent` 不预计算长度 → 请求以分块传输（无 Content-Length）发出，微信 iLink 网关对此直接回 `HTTP 412` 且响应体为空**，长轮询因此永远拿不到消息。
> 判据（全部用库里真实凭据离线复现，凭据不出本机）：同一请求换成自带 Content-Length 的预缓冲内容后恒 `HTTP 200`，且在「走系统代理 / 禁用代理直连 / 按 IP 直连 / 无 charset」「HTTP/1.1 与 h2」「DNS 四个 IP 逐个钉」以及「连续四次长轮询复用连接池」下全部一致通过；而调用应用自己的 `ChannelNetwork` 则稳定复现 412。
> 修法：`ChannelNetwork.BuildJsonContent` 统一把请求体预缓冲成带长度的字节内容（QQ/飞书 POST 一并受益），并加 `PostJson_RequestBody_CarriesContentLength_NotChunked` 钉住该不变量。
> 分块修好后立刻暴露出**同一条链路上的第二个误判**：`getupdates` 成功响应根本不带 `ret` 字段（只有扫码/二维码接口带 `ret:0`），旧判定把「字段缺失」当失败 → 管理页改显示 `WEIXIN_RET_INVALID`，HTTP 200 也永远转圈。现收敛为 `WeixinChannelWorker.EnsurePollAccepted`（缺失视为成功，形状由 `msgs` 校验兜底；`ret=0` 且 `errcode≠0` 时报出 errcode 而不是 ret），出站 `sendmessage` 同口径修正。
> 实测：重启后飞书与微信账户的 `LastErrorCode` 均已清空（两平台建连/长轮询成功）；`t_channel_inbox`/`t_channel_outbox` 仍为空，**手机侧实际送达仍未验收**。

> **2026-09-28 下午：入站通了但「机器人不回」，又查出三处协议缺陷（两处飞书、一处微信）。**
> 飞书①鉴权 scheme 串台：`ChannelNetwork.AddHeaders` 在 `version == null` 时一律写 `Authorization: QQBot <token>`（那是 QQ 开放平台的方案），飞书复用同一方法却没传 version，于是**每条发送恒 `HTTP 400`**；而取 token、长连接 bootstrap 都不带 Authorization，所以入站一直正常——表象正是「连接正常、消息收到、就是不回」。现 `authScheme` 参数化，飞书传 `Bearer`，QQ/微信路径不变。
> 飞书②`message_id` 也嵌在 `data` 里：顶层读不到 → `SendCardAsync` 抛 `FEISHU_SEND_NO_MESSAGE_ID` → 投递 Worker 按设计降级补发文本，**卡片其实已投递成功，用户收到两条重复消息**。现由 `ExtractMessageId`（先顶层再 `data`）解决。
> 微信：`ParseIncoming` 要求 `message_type==1` **且** `item_list` 恰好 1 项；而本仓库出站 1:1 文本用的就是 `message_type=2`，真实私聊也会带表情/引用等兄弟项——所以每条真实消息都被判 `UnsupportedContent`，长轮询正常却永不出站。现改为「取第一个有内容的 `type=1` 文本项、`message_type` 接受 1/2」，并让拒收原因携带**只含结构、不含正文**的指纹 `NotText:mtype=…,items=…,types=[…]`（`ChannelIncoming.RejectHint` → `t_channel_inbox.RejectReason`），后续再被拒可直接定位。
> 本轮另删除了两个只会伤害单管理员模式的开关：`Setting.BlackQQ`（配成 `admin` 时把唯一操作者的每条消息在 `MessageProcess` 入口静默丢弃，只打 Console 不落日志）与 `CommandModel.CommunicationType`（含双侧迁移 `DropCommandCommunicationType`，该列此前 12 行全为 NULL）。
> 回归：`ChannelProtocolTests` +4 例（scheme 选择、`data.message_id`、message_type 兼容、指纹不含正文），`ChannelEndpointTests` +4 例；`dotnet test`、`npm run test`、App 两个模块 `compileDebugKotlin`（须用 JDK 17）均通过。

> **2026-09-28 下午（续）：确立出站原则「从哪儿触发就回哪儿」，并补齐两处。**
> 原则落到代码上原本只差一步：`ctx.Notify` 已把触发路由带进 DTO（`NotifyService` 取 `ChannelReplyContext.Current`），但 `SendMessageHelper` 的原路投递条件是 `appContentType == "text"` —— 音乐搜索那 8 条 `MessageType=音频` 因此整批被跳过（App 会话里看得到、平台无回音）。现改为**非文本也回，降级为文本替身**：`[音频] <公网链接>` 逐条发送（所有者口径：不合并）；同时守住脱敏红线——本地路径、内网/回环地址、非 http(s) 方案一律不发往平台，只回「[类型] 结果已在 App 生成」（判据 `SendMessageHelper.IsPublicHttpUrl`，复用 `ChannelNetwork.IsPublicIp`）。
> 原路回复窗口由「三平台统一 5 分钟」改为**按平台分档**（`ChannelManagementService.ReplyWindow`，创建路由与投递校验共用一处）：飞书 24 小时（按 open_id 主动推送，本无被动窗口；旧值会让超过 5 分钟的长任务结果被静默丢弃），微信/QQ 保持 5 分钟（`context_token` 与 QQ 被动窗口的真实时长仍未实测，不确定就保守，QQ 另有单消息四条上限）。
> 待实测：飞书发一条 `音乐搜索 …` 应收到说明 + 8 条 `[音频] 链接`；微信需再发一条纯文本，验证宽松解析后是否出 `Accepted`（若仍拒，`RejectReason` 的结构指纹会直接告出真实 `message_type` 与 item 类型）。

> **2026-09-28 傍晚：微信拒收指纹定案，并新增「发送测试消息」。**
> 上一轮补全的指纹给出 `NotText:group` —— **微信 1:1 私聊报文也带 `group_id`**，它不是群聊标识。旧判据用它拒收，所以长轮询、鉴权、解析全对，机器人却永不回复。私聊判据现改为「`to_user_id` 是本机器人 + `message_type` 为 1/2 + 有文本项」，`group_id` 只以关系形式（`group==to`/`group==from`/`group`）留在拒收指纹里，等平台真下发群事件时再补精确判据；新增 `WeixinPrivateText_CarryingGroupId_IsStillAccepted` 钉住这个真机形态。
> 新增管理端点 `POST /api/Channel/{platform}/test-send`（`ManagerOnly`，未认证实测返回 401）：Web 通道页每张卡片多一个「发送测试消息」。安全口径是**目标只能取该平台最近一条仍在有效期内的原路回复路由**，接口不接收任何收件人参数——Manager 接口不能变成任意收件人投递器；无路由时报「请先用手机给机器人发一条消息」，因为三家都只能按入站消息原路回复（QQ 被动回复、微信 `context_token`、飞书按 open_id），凭据本身不支持主动外发。自检复用 `ChannelReplyService.QueueAsync` 同一条投递链路与判据，避免「自检过了、真实回复仍失败」。
> `ChannelReplyService.QueueAsync` 改为返回出站记录 Id（被窗口/上限拒时返回 null），`TestSendAsync` 据此给出可读原因；回归 `dotnet test` 803/803、`npm run test` 88/88。


日期：2026-09-27。仓库所有者已明确调整顺序：先落正式代码，后续在 Web「系统管理→消息通道」使用测试账号绑定与真机验证。**本文件仅记录实现与缺口，不表示平台实测通过或允许生产启用。**

> ⚠️ **2026-09-27 补充：扫码对接失败已定位并修复，见 `QQ微信通道接入方式核实与偏差清单.md`。**
> 核实发现**微信与 QQ 各有 2~4 处实现偏差**（微信：`channel_version` 被误当机密导致 `ValidateVersions` 直接拦路、`get_bot_qrcode` 误用 POST、多余的 `iLink-App-Id` 头；QQ：取 token 域名应为 `bots.qq.com` 而非 `api.bot.qq.com`、Gateway 路径应为 `/gateway`）。
> **这些已全部修复**（`ValidateVersions` 改为「入参→环境变量→内置默认」不再拦路、扫码改 GET、请求头按官方口径精简、QQ 拆出 `QqTokenUri`、Gateway 改 `/gateway`、并新增沙箱/正式环境开关绕开 IP 白名单），
> 新增 `ChannelEndpointTests` 作为端点协议回归护栏；`dotnet test` 707/707、`npm run test` 88/88 通过。
> **但真机扫码与收发仍未实测**——「不再必然失败」不等于「一定成功」。
> 另需注意：本文件 §「尚未完成」中「隔夜主动投递需实测」的表述不准确——iLink 协议受 `context_token` 机制约束，**本就不支持主动推送**。
> 详见该核实文档，**以它为准**。

## 已实现的代码（默认关闭）

- `Quantum.Entities/Model/ChannelModels.cs` + `Quantum.Data`：账户/唯一绑定、Inbox、Cursor、ReplyRoute、Outbox、显式允许的纯文本快捷回复；SQLite/MySQL 各三份增量迁移（核心表、安全快捷回复、关键字段非空约束），账户/绑定的平台键及 Inbox 去重键由数据库唯一索引兜底。账号、绑定版本和目标 peer 均在发送前再次比较；换绑/解绑废止旧待发及旧回复路由，历史 App 消息保留。
- `Quantum.Application/Channels/`：QQ AccessToken/Gateway/心跳/Resume 与 C2C 文本，微信扫码（含验证码、可信域名跳转、扫码无 `ilink_user_id` 时的一次性挑战）/游标长轮询/`context_token` 被动回复；`message_id` 同时兼容数字和字符串报文。用户只可进入当前唯一已验证私聊绑定；未绑定和非文本事件不执行任务、不记录正文。
- 入站 Inbox、游标、App 会话气泡与原路回复 Outbox 同数据库事务；Outbox 接口受理不代表终端已读，未知回执不自动重发，QQ 被动窗口暂按 5 分钟保守处理，微信 context_token 缺失不作 tokenless 回退。Web Manager 可查看脱敏状态、最近 50 条投递状态、未知回执人工确认重试及单次扫码/挑战；QQ/微信都需要**再次确认绑定并单独启用**。
- 只支持管理员显式选择的「启用中/非正则/纯文本」快捷回复精确匹配；旧枚举值 QQ=1、微信=4 不复用（新 QQBot=7、WeixinBot=8），不能靠 CommandModel.CommunicationType 为空自动取得权限。系统命令、任务脚本、AI 写入目前不会由聊天消息触发。
- 第三方 bot_token、QQ AppSecret、微信游标和回复 context_token 由独立 AES-GCM 主密钥加密入库，Web 响应/操作日志不回明文；HTTP/WSS 强制官方域名 HTTPS/WSS、公网 DNS 验证+IP 直连、禁止代理和 HTTP 跳转。首期部署**只能一个有通道连接的后端副本**，单进程锁不替代多副本 fencing。
- Web 新增管理页及菜单，已有实例的系统管理菜单在读取时补入入口；`docker-compose.channels.yml` 为**可选**只读密钥卷覆盖，不影响旧实例默认启动。

## 本地测试账号配置与试运行（请勿把密钥发到聊天）

1. **通道主密钥（2026-09-28 起默认自动生成，经仓库所有者确认改变旧政策）**：appsettings.json 的 `Quantum.ChannelMasterKey`（随机 32 字节的 Base64，44 字符）——首次启动为空时自动生成并写回，一次生成终身使用；丢失后已保存平台登录态无法解密、只能重新扫码，**备份 appsettings.json 即备份此密钥**（该文件本就含数据库口令与 JWT 密钥，勿提交 Git、勿打进镜像）。需要密钥与配置分离的部署仍可用环境变量，优先级更高：`QUANTUM_CHANNEL_KEY_FILE`（绝对路径密钥文件，Base64 内容）> `QUANTUM_CHANNEL_MASTER_KEY`（Base64 文本）> appsettings.json；单容器可用 `docker-compose.channels.yml` 挂载 `./private/quantum-channel.key` 为容器 `/run/secrets/quantum_channel`。密文与主密钥分库分文件存放：仅泄露 appsettings.json 或仅泄露数据库均不足以解密通道凭据。主密钥不可用时旧 App 功能保持可用，管理页保存/扫码通道会提示具体原因。
2. QQ：在官方平台准备已获 C2C 事件权限的**测试机器人**；Web 管理页输入 AppID/AppSecret 保存（默认禁用），发起一次性挑战；让测试私聊用户给机器人发送挑战码；刷新页面，核对候选账号掩码及指纹；Manager 确认后再单独点击启用。更换机器人或私聊用户时必须二次确认，旧待发消息会作废。
3. 微信：先核对独立 C# 客户端接入范围和版本，在管理页填写已核对的 `iLink-App-ClientVersion` 十进制值与 `channel_version`，扫码、必要时输入验证码；若扫码结果未带用户 ID，由扫码测试账号给机器人私聊发送一次性挑战码；核对候选指纹、Manager 确认并启用。扫码令牌不会传到浏览器。若平台返回登录失效（`-14`），该通道停用并显示错误码，需重新扫码。
4. 两端各发送一条纯文本：App/Web 的 `channel:qqbot`、`channel:weixinbot` 会话应出现入站气泡；平台收到回执或已显式允许的安全快捷回复；管理页 Outbox 状态 **Accepted=接口受理，不等于用户看到**。请在手机侧确认实际送达并记录脱敏错误码，不用正式账号首测。

## 尚未完成／上线门禁

- **脚本任务指令、多步骤 ActorKey 隔离、`ctx.Notify` 原路路由、任务执行 Claim 的不重复副作用、主动通知订阅/授权**尚未接入。这些都属于原计划范围；不能将当前安全快捷回复等同于完整任务指令功能。改脚本/认证敏感链路前 AGENTS.md 要求的 `docs/security/公网部署安全审计-2026-09-14.md` 与 `docs/App端API契约.md` 在当前仓库仍缺，须先补齐并审阅，不能凭猜测改写安全基线。
- QQ 被动回复 5/60 分钟冲突、微信独立客户端资格/扫码/隔夜主动投递均需使用测试账号实测；任一接口 HTTP 200/`ret=0` 不足以证明送达。微信无 `ilink_user_id` 的挑战备用路径为代码准备，未实测。不开群聊/媒体/个人 QQ 逆向/侧车。
- SQLite 迁移已在内存库验证 Up/Down；MySQL 迁移的操作集合与 SQLite 对比通过，**尚未连接真实 MySQL 实例做演练**；生产升级前备份数据库与独立主密钥，演练升级/回滚并验证单副本配置。

## 本轮离线结果

- `dotnet test Quantum.API/Quantum.API.sln --no-restore --nologo`：680/680 通过（2026-09-27 本轮最终全量回归）。
- `quantum-web/ npm run test`：88/88 通过；`npm run build` 成功，构建目标仍为忽略跟踪的 `quantum-release/wwwroot`。
- `docker compose -f docker-compose.yml -f docker-compose.channels.yml config --quiet`：通过。以上均不代替 QQ/微信真机收发或安全基线审查。
