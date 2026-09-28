# QQ／微信原生通道 P0 实施与验证记录

日期：2026-09-27。依据 `QQ微信平台内置双向通信实施计划.md` 的 P0 门禁。**当前状态：探针可编译、未使用测试账号；两平台真实双向、资格、重启恢复及静默主动通知均未验证，P0 真实验收未通过。所有者已调整为先实施正式代码，当前实现进度及剩余缺口见 `QQ微信内置通道实施状态.md`；本文件只记录隔离探针。**

## 本次交付

- `tools/ChannelPhase0Probe/`：独立 .NET 10 控制台探针，不加入 Quantum 后端解决方案、不注册服务、不引用任务/DB/脚本，绝不使用 OpenClaw 或侧车。只能由操作者在本机私密交互式终端运行；不接受输出重定向。
- QQ：短时随机测试口令；测试机器人 AccessToken（检查 `expires_in`，REST 回发到刷新点前重新取令牌）、Gateway/Op10/Identify/Op1/ACK/Resume、C2C 字段检查、指定 OpenID 被动单聊回发。无指定 OpenID 时只显示测试口令匹配事件的 OpenID，不回发；运行期内存 Session，遇网络断开退避 5 秒。令牌依实际有效期提前刷新，长期监听时回复前再次检查。`qq-capture` + `qq-passive` 可用两次独立测试入站的 ID 手工验证不同回复时长，避免把 5/60 分钟文案冲突当作既定能力；它必须打开专用试验开关并二次确认。另有必须人工双重确认的 `qq-active` 测试模式（不带 `msg_id`），仅供持有人核对主动发送权限/到达。
- 微信：扫码二维码内容仅本地显示，处理扫码等待/确认/验证码/HTTPS 的 `weixin.qq.com` 子域名白名单重定向（HTTP/QQ WS 均直连并校验每次 DNS 解析返回的公网 IP，固定本次实际连接的 IP；禁用代理与 HTTP 跳转）；确认返回的 `ilink_user_id` 是唯一允许的测试 peer；轮询中只识别该 peer 发出的随机测试口令，必须有**数字** `message_id` 与 `context_token` 才回发文本。其余消息不回发；被动回复不做无 token 退路。另有必须人工双重确认的 `weixin-active` 独立实验模式（无入站 `context_token`），只在使用已扫码并加密保存的唯一测试账号时提供，不将接口受理冒称送达。默认登录 token、游标仅在进程内存中；设置 32 字节测试密钥时改为本机 AES-GCM 加密单文件检查点，并对相同消息 ID 先持久登记尝试再回发，以便测试重启时不自动重复回发。回复 `context_token` 不写磁盘。
- 出站回执仅表示平台接口已受理，不能代替手机侧到达确认。本探针的可选加密文件**不是**数据库事务/正式 Inbox、Outbox 或跨机器单活；未知回执保守停止自动重发，并不等于消息一定送达。本探针不提供生产级幂等、正式绑定或安全审计，不可作为生产服务部署。

## 官方依据核对快照（非实测）

- 核对 QQ C2C 官方事件示例时发现 `message_scene.ext` **实际为 `key=value` 字符串数组**（如 `msg_idx=REFIDX_...`，还可能带 `auth_token=...`），不是 `ext.msg_idx` JSON 字段。探针仅提取一个非空 `msg_idx=` 索引；后续 P1 入站模型/解析测试必须以数组为准，不能复制原计划描述的字段访问写法。
- QQ 开放平台官方 [鉴权](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/api-use.html)、[WebSocket](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/event-emit/websocket.html)、[发送单聊](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages.post.html)：2026-09-27 从官方页面取得；WS HTML SHA256 前 16 位 `f163bcaffc64cde7`；发送单聊页面 SHA256 前 16 位 `06afbfebe0383950`。消息时效、额度、机器人权限及主动能力须账号实测。
- 腾讯 [openclaw-weixin 协议](https://github.com/Tencent/openclaw-weixin/blob/main/docs/protocol.md)：2026-09-27 官方仓库 main `24de5c9eb0dd`（截短），示例并非自有独立客户端的完整服务端契约。仓库 README 说明其产品是 OpenClaw 插件，**未找到对 Quantum 独立 C# 客户端接入资格的明确许可**；资格待账号持有人/腾讯确认。探针将来源标为 `QuantumPhase0/1`，不冒称 OpenClaw。
- 接入资格补充核查（2026-09-27）：腾讯仓库 [LICENSE](https://github.com/Tencent/openclaw-weixin/blob/main/LICENSE) 为 MIT，许可针对开源**代码/文档**，不能据此推断腾讯后端允许任意独立客户端使用。仓库内针对[不依赖 OpenClaw 的独立客户端是否获准使用 iLink Bot 的公开咨询 #265](https://github.com/Tencent/openclaw-weixin/issues/265)仍处于提问、未见官方答复状态；因此资格门禁保持**未通过**。该核查不能替代平台正式规则或账号持有人取得的书面确认。
- 腾讯仓库 issue [#263](https://github.com/Tencent/openclaw-weixin/issues/263) / [#264](https://github.com/Tencent/openclaw-weixin/issues/264) 有用户报告 `sendmessage ret=0` 但手机未见消息；这些是**用户报告，不是 Quantum 实测结论**，进一步说明必须由测试账号手机端确认最终送达，不能仅用 HTTP/业务码判成功。

## 本机运行（仅测试账号）

```powershell
# 从 D:\github\quantum 执行；使用本机私密交互式终端，不要记录会话或将二维码/凭证截图、提交到仓库。
dotnet build .\tools\ChannelPhase0Probe\ChannelPhase0Probe.csproj
$env:P0_QQ_APP_ID = Read-Host '测试机器人 AppID'
$env:P0_QQ_APP_SECRET = Read-Host '测试机器人 AppSecret' -AsSecureString | ConvertFrom-SecureString -AsPlainText
dotnet run --project .\tools\ChannelPhase0Probe -- qq  # 首次仅发现挑战码对应 OpenID，不发消息
$env:P0_QQ_PEER_OPENID = Read-Host '测试用户 OpenID（以首次本地探针输出为准）'
dotnet run --project .\tools\ChannelPhase0Probe -- qq  # 使用新生成的口令再发一次，测试回执

# QQ 被动回复窗口人工实验（仅测试号，不能把过期请求自动改成主动消息）：
dotnet run --project .\tools\ChannelPhase0Probe -- qq-capture  # 测试用户再发本轮口令，仅本地打印入站 ID/接收 UTC，不自动回复
$env:P0_QQ_MSG_ID = Read-Host '本次测试的 QQ 入站消息 ID' -AsSecureString | ConvertFrom-SecureString -AsPlainText
# 使用手机消息发送时间核对 4 分钟、6 分钟、接近 60 分钟窗口；每轮先重新捕获新的消息 ID，避免重复 msg_seq 干扰。
$env:P0_ENABLE_WINDOW_EXPERIMENT = 'YES'
dotnet run --project .\tools\ChannelPhase0Probe -- qq-passive  # 再次输入 SEND-TEST 确认；仅表示平台接口受理，仍需手机核验

# 微信客户端版本需先由账号持有人核对当前官方协议/真实后端兼容值；不得任意伪造。
$env:P0_WEIXIN_CLIENT_VERSION = Read-Host '已确认的 iLink-App-ClientVersion 十进制值'
$env:P0_WEIXIN_CHANNEL_VERSION = Read-Host '已确认的 channel_version 字符串'
dotnet run --project .\tools\ChannelPhase0Probe -- weixin

# 若测试重启，先在本机密码管理器保存 32 字节随机密钥的 Base64（不要写入仓库/聊天）。
# 仅在测试账号、已核实独立接入资格后，将同一个密钥不回显地放入两个会话的环境变量：
$env:P0_WEIXIN_STATE_KEY = Read-Host 'P0 私密状态密钥 Base64' -AsSecureString | ConvertFrom-SecureString -AsPlainText
dotnet run --project .\tools\ChannelPhase0Probe -- weixin  # 首次扫码，保存加密登录态/游标/发送尝试
# 结束进程后在新终端重新输入同一密钥及上述版本，再运行 weixin；不会重新扫码，发送新的本轮口令。
# 会话失效需先停止探针，明确删除本机 LocalApplicationData/Quantum/ChannelPhase0Probe/weixin.state 再扫码。

# 主动消息仅在测试账号持有人已核准平台权限/独立客户端资格后人工执行；
# 与被动回复严格独立，若平台拒收，不自动改变回复策略、不对真实用户试发。
$env:P0_ENABLE_ACTIVE_EXPERIMENT = 'YES'
dotnet run --project .\tools\ChannelPhase0Probe -- qq-active      # 必须先设置 QQ 测试 peer；本地再输入 SEND-TEST 确认
dotnet run --project .\tools\ChannelPhase0Probe -- weixin-active  # 必须先有微信加密扫码状态；本地再输入 SEND-TEST 确认
# 在短时与隔夜窗口分别人工运行并用手机核对；结束后移除 P0_ENABLE_ACTIVE_EXPERIMENT。
```

结束时在同一终端清理 `$env:P0_QQ_APP_SECRET`、`$env:P0_QQ_APP_ID`、`$env:P0_QQ_PEER_OPENID`、`$env:P0_QQ_MSG_ID`、`$env:P0_ENABLE_WINDOW_EXPERIMENT`、`$env:P0_WEIXIN_CLIENT_VERSION`、`$env:P0_WEIXIN_CHANNEL_VERSION`、`$env:P0_WEIXIN_STATE_KEY`、`$env:P0_ENABLE_ACTIVE_EXPERIMENT`，并关闭终端。启用持久化时，加密状态保存于当前用户的 LocalApplicationData/Quantum/ChannelPhase0Probe/weixin.state；`.lock` 文件只用于防止同机并行轮询。丢失密钥或 token 失效后**先停进程**、仅删除该状态文件再扫码；不要删除其他 Quantum 数据。不要将真实凭据或二维码数据发到聊天。运行时只能用测试账号；如果无法确认独立客户端的许可，请勿执行微信联网探针。

## 本轮本机验证结果

- `dotnet build tools/ChannelPhase0Probe/ChannelPhase0Probe.csproj`：成功，0 警告、0 错误。
- `dotnet run --project tools/ChannelPhase0Probe -- selftest`：离线 QQ token 刷新时点/字段、消息字段/业务错误/HTTPS 目标白名单、HTTP/QQ WS 的连接回调及私网 IP 拦截、加密状态重启恢复、错误密钥/篡改拒绝、同机排他锁自检通过；未发起平台请求。
- `dotnet test Quantum.API/Quantum.API.sln --nologo`：通过 **659/659**，跳过 0；构建中原仓库存在警告，本轮未改后端代码。
- 在交互式终端试运行未启用开关的 `qq-active`/`qq-passive`：均拒绝执行，进程返回码 2，未发起平台请求。尚未执行 `qq`、`qq-capture`、`weixin` 或启用开关后的被动窗口/主动联网模式；真实权限、后端协议兼容、双向收发、重连及 QQ/微信主动通知均未通过门禁。

## 待持有人完成的 P0 门禁

1. 提供**本机私密配置方式**的 QQ 测试机器人/私聊事件权限；配合手机收发、5/60 分钟被动窗口、限额和主动消息权限实测；记录脱敏错误码与到达情况。
2. 确认微信独立客户端接入资格与客户端版本值，使用测试微信号扫码及手机收发；验证验证码、token 失效、断线/重启后恢复、短时及隔夜主动通知。**探针已有加密恢复及人工主动实验入口，但尚未使用真实账号验证重启/隔夜可达。**
3. 补齐并核对 `docs/security/公网部署安全审计-2026-09-14.md`、`docs/App端API契约.md`（当前仓库及本地 Git 历史均未检出），P1 前不可跳过；不能自行编造为“已审阅”。
4. 经账号持有人核验并记录两边的实际回执/手机收件及独立客户端政策后，才能决定 P1；任一通道没过不得宣称双通道完成。确认后补做真实掉电/断线恢复、旧游标重放、未知回执负测；生产级事务/游标/幂等实现仍属 P1。
