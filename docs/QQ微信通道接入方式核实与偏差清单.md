# QQ／微信通道接入方式核实与偏差清单

> 编制日期：2026-09-27。触发原因：仓库所有者反馈「目前 QQ 微信的对接都不能正常完成扫码对接」，要求核实正确接入方式（参照 Hermes / OpenClaw 生态）。
> 本文只做**核实与偏差定位**，不改代码。结论分三档：**已证实**（有官方文档或多个独立来源一致）、**待实测**（来源可靠但本机未验）、**未证实**（仅单一来源）。
> 被核对的实现：`QqChannelClient.cs` / `WeixinQrLoginService.cs` / `WeixinChannelWorker.cs` / `ChannelDeliveryWorker.cs`（微信分支）/ `ChannelNetwork.cs`。

---

## 0. 结论摘要

> ✅ **修复状态（2026-09-27 已实施）**：本文 §1.2 的 W-1~W-4、§2.2 的 Q-1~Q-2 **均已修复**，
> 并新增「沙箱/正式环境切换」以绕开 IP 白名单门槛。新增 `ChannelEndpointTests` 作为端点协议回归护栏。
> `dotnet test` **707/707 通过**、`npm run test` **88/88 通过**。
> **仍未验证**：真机扫码/收发（需真实 QQ 机器人与 iOS 微信 ClawBot 测试号，见 §6）。

| 平台 | 路子对不对 | 能不能修好 | 根因 |
|---|---|---|---|
| **微信** | ✅ **方向完全正确** | ✅ 能，**4 处实现偏差** | `channel_version` 被当成了机密导致校验拦路虎 + 扫码接口用错方法/请求头 |
| **QQ** | ✅ 方向正确 | ⚠️ 修完代码仍有一道**硬门槛** | 2 处域名/路径错误 + **新增机器人强制 IP 白名单，本机无公网 IP 就无法上线**（已加沙箱开关绕过） |

**最重要的一条认知纠正**：iLink 协议是**腾讯官方的标准 HTTP/JSON API，与 OpenClaw 框架无耦合**。仓库代码注释里「不冒用 OpenClaw 身份」是个误解——`channel_version` 是**公开的 npm 包版本号**（如 `1.0.2`），不是身份凭据也不是密钥。把它当机密，才导致 `ValidateVersions` 变成拦路虎。

**「参照 hermes」的正解**：不是去装 Hermes，而是**照着腾讯官方发布的 npm 插件源码逐字对齐协议**。官方包是 TypeScript 源码直接发布、未混淆，是协议的事实标准来源。

---

## 1. 微信：iLink / ClawBot

### 1.1 协议定性（已证实）

- **腾讯官方协议**，2026 年 3 月下旬发布，官方 npm 包 `@tencent-weixin/openclaw-weixin`。
- 接入域名 `https://ilinkai.weixin.qq.com`，纯 HTTP/JSON，**不需要 WebSocket**。
- 有正式法律文件《微信ClawBot功能使用条款》背书，**不是灰色地带**（对比历史：微信 Web 协议 2017 被封、iPad 协议被起诉、PC Hook 随时封号）。
- 首次为个人微信号提供**官方合法**的 Bot API。

> **仓库的选型是对的**：`WeixinChannelWorker` / `WeixinQrLoginService` 用的就是 `ilinkai.weixin.qq.com` + `get_bot_qrcode` + `get_qrcode_status` + `getupdates` + `sendmessage` + `context_token`。协议选型层面没有问题，不需要推倒重来。

### 1.2 四处实现偏差（这是「扫码对不上」的直接原因）

| # | 仓库现状（file:line） | 正确做法 | 后果 |
|---|---|---|---|
| **W-1** | ~~`WeixinQrLoginService.ValidateVersions:29-41` 强制要求 Manager 填「经官方协议核对的」`ClientVersion` + `ChannelVersion`，**填不出就抛 BusinessException 直接拒绝**~~ | `channel_version` = 官方 npm 包版本号（公开，`1.0.2`/`1.0.3`）；`get_qrcode_status` 的 `iLink-App-ClientVersion` 固定为 `1` | 🔴 **扫码第一步就被自己的校验挡住**。不知道填什么 → 扫码流程根本启动不了。这是最可能的根因 |
| **W-2** | ~~`StartAsync:59` 用 **POST** + body `{local_token_list:[]}` 调 `get_bot_qrcode`~~ | **GET，无 body** | 二维码请求不符合协议 |
| **W-3** | ~~`ChannelNetwork.AddHeaders:50-53` 对所有微信请求加 `iLink-App-Id: bot` + `AuthorizationType: ilink_bot_token`~~ | 官方头只有 `Content-Type` / `AuthorizationType` / `Authorization` / `X-WECHAT-UIN` / `Content-Length` | 多余头，可能被服务端拒绝 |
| **W-4** | ~~`PollAsync:84` 传 Manager 填的 ClientVersion 查状态~~ | 固定 `iLink-App-ClientVersion: 1` | 扫码状态轮询失败 |

#### ✅ 已实施的修复（2026-09-27）

| 编号 | 改法 | 位置 |
|---|---|---|
| W-1 | `ValidateVersions` 语义改为 `ResolveVersions`：**入参 → 环境变量 → 内置默认值**（`ClientVersion="1"`、`ChannelVersion="1.0.3"`），**留空不再拒绝**；仍校验显式传入值的格式防脏值。旧名保留为兼容入口 | `WeixinQrLoginService.cs` |
| W-1 连带 | `WeixinChannelWorker` 的 `WEIXIN_VERSION_MISSING` 改为 `WEIXIN_VERSION_INVALID`（只在**显式传了脏值**时才触发），不再因缺版本锁死已配置账户 | `WeixinChannelWorker.cs` |
| W-2 | `get_bot_qrcode` 改 `GetAsync`、去掉请求体 | `WeixinQrLoginService.StartAsync` |
| W-3 | 去掉 `iLink-App-Id: bot`；`AuthorizationType`/`X-WECHAT-UIN`/`Bearer` 改为**仅在拿到 token 后**才发 | `ChannelNetwork.AddHeaders` |
| W-4 | 随 W-1 的默认 `ClientVersion="1"` 生效 | 同上 |
| 运行时统一 | `ChannelDeliveryWorker` 的微信分支改用同一套 `ResolveVersions`，不再要求运行前配好环境变量 | `ChannelDeliveryWorker.cs` |

**W-1 附带问题**：`WeixinChannelWorker.cs:48-49` 在版本缺失时抛 `WEIXIN_VERSION_MISSING`，导致**已配置好的微信账户也会因为版本字段为空而完全无法收消息**。这个「安全兜底」实际上把通道锁死了。

### 1.3 实现正确的部分（不要动）

`ChannelDeliveryWorker.cs:125-142` 的微信发消息分支**已经把关键字段全补齐了**，包括社区踩坑总结的「幽灵字段」：

```csharp
var msg = new {
    from_user_id = "",            // ✅ 官方要求空字符串，不能不传
    to_user_id = binding.PeerId,
    client_id = outbox.ClientId,  // ✅ 每条唯一，服务端用于去重路由
    message_type = 2,             // ✅ 2=BOT（1=用户）
    message_state = 2,            // ✅ 2=完成态
    context_token = context,      // ✅ 必须来自收到的消息
    item_list = new[] { new { type = 1, text_item = new { text = outbox.Content } } }
};
// base_info.channel_version ✅
```

`context_token` 的存取也对：`ChannelInboxService.cs:82` 入站时加密存进 `ReplyRoute`，发送时解密取回（`ChannelDeliveryWorker.cs:121`）。`ret/errcode` 判空逻辑也恰好能正确处理「`sendMessage` 返回 `{}` 即成功」。

### 1.4 必须知道的真实约束（现有状态文档写得不准）

| 约束 | 说明 | 对本项目的影响 |
|---|---|---|
| 🔴 **仅 iOS 微信 8.0.70+** | ClawBot 插件内置在 iOS 微信，安卓未必有 | **如果用安卓微信扫码，可能根本找不到入口**——这是「扫码对不上」的另一个可能原因，与代码无关 |
| **一个 ClawBot 绑一个微信用户、连一个 agent 实例** | 不可多绑 | ✅ 仓库「每平台唯一私聊绑定」模型正好契合，不用改 |
| **不能主动推送** | 必须用户先发消息才有 `context_token` | ⚠️ `QQ微信内置通道实施状态.md` 写「隔夜主动投递需实测」——**实际是不支持的**。但同一 `context_token` 可复用连发（社区验证过连发 10 条），所以定时任务在用户当天发过消息后仍可投递 |
| **逐步放量** | 不是所有用户都能启用 ClawBot | 需确认测试号已开通 |
| **腾讯保留管控权** | 可随时限速、拦截、终止 | 通道随时可能失效，状态页要能显示脱敏错误码 |
| **目前仅文本** | 图片/文件需自行处理 CDN AES-128-ECB 加解密 | 与仓库「不开媒体」的范围声明一致 ✅ |

### 1.5 微信改造建议

1. **删掉 `ValidateVersions` 对 `ClientVersion` 的强制要求**。`channel_version` 改为常量（跟随官方 npm 包版本，随升级更新），或做成普通可选项。**这是解开死结的最小改动。**
2. `get_bot_qrcode` 改 **GET 无 body**；`get_qrcode_status` 的 `iLink-App-ClientVersion` 固定 `1`。
3. 请求头精简到官方那 5 个。
4. 状态文档修正「隔夜主动投递需实测」→「协议不支持主动推送，受 context_token 约束」。

---

## 2. QQ：官方开放平台机器人

### 2.1 资格与场景（已证实）

- **个人用户 2026-03-07 起全量开放**创建 QQ 机器人（每 QQ 号最多 5 个），免费。
- 开发场景矩阵（官方原表）：

| 认证身份 | QQ频道 | QQ群 | 消息列表单聊 |
|---|---|---|---|
| 企业开发者 | ✅ | ✅ | ✅ |
| 个人开发者 | ✅ | ✅ | ✅ |

→ **个人可以做单聊，仓库选的路子可行。**

- 但机器人需**配置 → 开发 → 提交审核 → 手动上线**后，才能在 QQ 客户端添加。
- 沙箱单聊入口：移动端 QQ 扫管理端「QQ群和消息列表机器人二维码」→ 打开机器人资料卡 → 点「发消息」→ 授权确认添加。

### 2.2 两处确定的实现错误

| # | 仓库现状 | 正确做法 | 后果 |
|---|---|---|---|
| **Q-1** | ~~`QqChannelClient.TokenAsync:27` 取 token 用 `QqUri("/app/getAppAccessToken")` → `https://api.bot.qq.com/app/getAppAccessToken`~~ | **`https://bots.qq.com/app/getAppAccessToken`**（官方 curl 示例；token 域名一直是 `bots.qq.com`，**不在** `api.bot.qq.com` 上） | 🔴 **取 token 直接失败**，`TokenAsync` 抛 `QQ_AUTH_INVALID`，后续 Gateway/发消息全部无从谈起 |
| **Q-2** | ~~`GatewayAsync:43` 用 `GET /gateway/bot`~~ | **`GET /gateway`** | 拿不到 Gateway 地址，连不上事件流 |

#### ✅ 已实施的修复（2026-09-27）

| 编号 | 改法 | 位置 |
|---|---|---|
| Q-1 | 新增 `ChannelNetwork.QqTokenUri()` 固定指向 `https://bots.qq.com`；`IsQqHost` 本就覆盖该域，**传输白名单无需改动** | `ChannelNetwork.cs` / `QqChannelClient.cs` |
| Q-2 | Gateway 路径 `/gateway/bot` → `/gateway` | `QqChannelClient.GatewayAsync` |
| 沙箱/正式 | 新增 `QqChannelSaveDto.ApiBase` 与 `ChannelStatusDto.Environment`；`QqUri(path, root)` 对**空值回落正式环境**（存量账户凭据无此字段也不会炸），并校验必须是 https 的 qq.com 官方域 | `ChannelNetwork.cs` / `ChannelManagementService.cs` / 前端配置弹窗 |
| 护栏 | 新增 `ChannelEndpointTests`（11 例）把凭证域名、Gateway 路径、沙箱接入点、URL 校验、微信版本默认值全部钉死 | `Quantum.API.Tests/ChannelEndpointTests.cs` |

**关于域名迁移（避免改错方向）**：

- 官方 wiki 现文仍写：token `bots.qq.com`、OpenAPI `api.sgroup.qq.com`、沙箱 `sandbox.api.sgroup.qq.com`。
- 但第三方 Go 库（`remilia`）注释明确：**「2026-08-10 起所有接口调用域名统一为 `api.bot.qq.com`（旧域名 `api.sgroup.qq.com` 已下线）」**。
- ✅ **仓库用 `api.bot.qq.com` 作为 OpenAPI 域名是对的、且是最新的**——不要往回改成 `api.sgroup.qq.com`。
- ❌ 但 **token 接口要单独指回 `bots.qq.com`**。仓库的 `QqUri` 把两者混在一起，这是 Q-1 的根因。
- ⚠️ 沙箱域名是否同步迁移为 `sandbox.api.bot.qq.com`，**待实测**。

### 2.3 🔴 硬门槛：IP 白名单（这可能才是「根本用不了」的原因）

官方原文（已证实）：

> 「IP 白名单功能启用之后，开放平台将会在**正式环境**对开发者的部署 IP 进行限制：**只有白名单 IP 才能连接 websocket 和调用 OPENAPI 的接口**。」
> 「**对于新增机器人，平台默认启用 IP 白名单功能，并限制只有填写了 IP 白名单才能提审和上线。**」
> 「当前 IP 白名单**只作用于正式环境，不影响机器人在沙箱环境的使用**。」

**含义**：

- Quantum 这台机器**如果没有公网唯一 IP，正式环境一定连不上**（仓库的 ChannelNetwork 还要做公网 DNS 校验 + IP 直连，NAT 后面同样不可用）。
- 唯一出路是**先在沙箱环境联调**（沙箱不受 IP 白名单约束），正式上线则必须给部署机申请公网 IP。
- 仓库目前**没有区分沙箱/正式环境**，应把环境作为账户配置项（`Env=Sandbox|Prod`），联调默认走沙箱。

### 2.4 其他已证实要点

- **单聊 openid 必须来自 Gateway 事件**：腾讯云社区案例证实，用后台拿到的 openid 直接调 `POST /v2/users/{openid}/messages` 会返回 **11255**。仓库的「Gateway 事件 → 挑战码 → 确认绑定」设计**是正确的** ✅，但 Gateway 连不上（Q-2）就永远拿不到 openid。
- **消息 URL 白名单**：卡片/链接域名需提前 ICP 备案并报备，上限 20 条。与飞书计划的链接白名单要求同类。
- 仓库 `ChannelNetwork.IsQqHost` 允许 `*.qq.com`，沙箱域名也能覆盖 ✅ 不用改。

### 2.5 QQ 改造建议

1. `QqUri` 拆成两个：token 走 `bots.qq.com`，OpenAPI/Gateway 走 `api.bot.qq.com`。
2. Gateway 路径改 `/gateway`。
3. 账户配置增加**环境开关**（沙箱/正式），联调走沙箱。
4. 管理页显式提示 **IP 白名单**要求，并在 `LastErrorCode` 里能区分 11255 / 白名单拒绝。

---

## 3. 「参照 Hermes」应该怎么理解

腾讯为这两家都发布了**官方 OpenClaw 插件，TypeScript 源码直接发布在 npm、未混淆**：

| 平台 | 官方包 | 备注 |
|---|---|---|
| 微信 | `@tencent-weixin/openclaw-weixin` | 5 个 HTTP 接口，41 个源文件，含 `api.ts`/`login-qr.ts`/`inbound.ts`/`send.ts`/`session-guard.ts`/`cdn/aes-ecb.ts` |
| QQ | `@tencent-connect/openclaw-qqbot` | 接入 OpenClaw |
| DeepSeek Harness | `@tencent-connect/dsh-qqbot` | 接入 DSH |

**正解**：把这些包的源码当作**协议规范**逐字对齐——字段、请求头、magic 常量。`channel_version` 就是这些包的版本号（`buildBaseInfo()` 里返回 `{channel_version:"1.0.3"}`）。

**误解**（当前代码注释里的说法）：把 `ClientVersion`/`channel_version` 当成需要「冒用身份」的机密。实际它是公开的协议版本标识，不填反而一定失败。

官方插件还提供两个**本仓库尚未实现但很有价值**的能力：

- **Session Guard**：`errcode=-14`（会话过期）时自动暂停该账号 60 分钟，防触发风控。仓库目前是直接 `Enabled=false`（`ChannelDeliveryWorker.cs:162-164`），行为更硬，建议对齐。
- **typing 状态**：`getConfig` 取 `typing_ticket` → `sendtyping` 显示「正在输入」，显著改善交互体感。

---

## 4. 改造优先级建议

### ✅ 已完成（2026-09-27）

| 优先级 | 改动 | 状态 |
|---|---|---|
| **P0** | 微信：解除 `channel_version` 强制校验 + `get_bot_qrcode` 改 GET + 扫码状态接口 `ClientVersion` 默认 1 | ✅ 已实施，有测试护栏 |
| **P0** | QQ：token 域名改 `bots.qq.com` + Gateway 改 `/gateway` | ✅ 已实施，有测试护栏 |
| **P1** | QQ：增加沙箱/正式环境开关（管理端填沙箱接入地址，状态页回显环境） | ✅ 已实施 |

### ✅ 已完成（2026-09-28，零填写扫码收尾）

| 优先级 | 改动 | 状态 |
|---|---|---|
| **P1** | 微信：管理页「扫码绑定」直接出码——前端删除「协议版本」填写弹窗（版本元数据服务端内置默认值，`QUANTUM_CHANNEL_WEIXIN_*` 环境变量仍可覆盖），普通用户扫码不再面对看不懂的版本字段 | ✅ 已实施 |
| **P1** | QQ：沙箱接入地址改为**留空即用官方默认** `https://sandbox.api.sgroup.qq.com/`（`QqChannelSaveDto` 新增 `Environment`，`ChannelNetwork.ResolveQqApiBase` 解析：显式地址 > 内置默认），不再要求用户去开放平台「沙箱配置」复制地址；配置弹窗改为三步引导。QQ 官方没有扫码注册接口，AppID/Secret 粘贴是唯一官方路径（与 Hermes 生态一致） | ✅ 已实施，`ChannelEndpointTests` 补 4 例护栏 |
| **P2** | 微信：补 Session Guard（-14 冷却 60 分钟而非直接停用） | ⏸ 未做 |
| **P2** | 微信：typing 状态；QQ：单聊 11255 专门错误码 | ⏸ 未做 |
| **P2** | 修正本仓库旧状态文档中「隔夜主动投递需实测」等不准确表述 | ✅ 已加指向并更正 |

### 🔲 仍需你做的前置动作（非代码问题）

1. **确认测试微信号是 iOS 微信 8.0.70+ 且已开通 ClawBot** —— 安卓微信可能根本没有该入口，代码再对也扫不出来。
2. **QQ 机器人须先在开放平台提交审核并「手动上线」** —— 否则客户端里搜不到、也加不进单聊。
3. **QQ 沙箱需在开放平台「沙箱配置」处拿到接入地址** —— 填进管理端配置弹窗的「沙箱地址」。

### ⏳ 尚未验证（本轮只做到离线可验证的部分）

- **真机扫码与收发两端均未实测**：本次修掉的是「必然失败」的确定性问题，但「不再失败」不等于「一定成功」。
- QQ 沙箱域名是否随 2026-08-10 迁移到 `sandbox.api.bot.qq.com`（故做成**可配置**而非硬编码，按开放平台实际给出的地址填）。
- Gateway 路径 `/gateway` 的依据来自第三方库注释与官方文档链接，**未在真机拉通验证**。
- iLink 各状态枚举是否完备、二维码在真实微信客户端下的完整流转。

---

## 5. 证据来源与核实状态

| 事实 | 来源 | 状态 |
|---|---|---|
| iLink 为腾讯官方、域名/接口/字段/幽灵字段 | 腾讯新闻逆向分析 + chihao.li 协议解析 + GitHub `x1ah/wechat-ilink-demo` + allclaw.org，**四源一致** | 已证实（均为二手，**建议再对官方 npm 包源码复核一次**） |
| iLink 与 OpenClaw 解耦、`channel_version` 是包版本号 | 同上（官方 `buildBaseInfo()` 返回 `{channel_version:"1.0.3"}`） | 已证实 |
| ClawBot 仅 iOS 8.0.70+、单账号单 agent、不能主动推送 | chihao.li / allclaw.org | 已证实（待本机实测） |
| QQ 单聊个人开发者可用、2026-03-07 全量开放 | 官方 wiki + okyn.com 汇总 | 已证实 |
| QQ token 域名 `bots.qq.com` | 官方 wiki「接口调用与鉴权」curl 示例 | 已证实 |
| QQ OpenAPI 域名 2026-08-10 迁移到 `api.bot.qq.com` | **仅第三方 Go 库注释**（remilia） | ⚠️ **未从官方文档直接证实**——但仓库已在用新域名且这是最可能的选择，改动方向需实测确认 |
| QQ Gateway 路径为 `/gateway` | 同上第三方 Go 库 | ⚠️ 未从官方文档直接证实，需实测 |
| QQ IP 白名单强制 | 官方 wiki 原文 | 已证实 |
| 单聊 openid 必须来自事件（11255） | 腾讯云开发者社区问答 | 已证实（个案，建议自测） |

**未核实项**：沙箱域名是否同步迁移；`get_qrcode_status` 在 `confirmed` 之外的状态枚举是否完备；QQ 机器人「消息列表单聊」场景的 openid 字段名（仓库用 `PeerId` 存）是否与事件字段严格对应。

---

## 6. 与飞书计划的关系

本文结论**不影响** `飞书消息通道接入计划.md` 的选型（长连接 + 私聊 + 决策触发任务），但有两点交叉：

1. 飞书计划 §5 的「凭据面收窄」经验同样适用于微信——**别把公开的协议常量当机密**。
2. 三个通道并行的**运维成本**（各自审核、各自风控、各自失效条件）需要在管理页统一呈现脱敏状态，便于一眼看出是「配置问题」还是「平台侧限制」。
