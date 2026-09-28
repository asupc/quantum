# 单用户消息通道：接入即用

> 2026-09-28 · 所有者已明确：Quantum 是单用户模式，没有按 QQ/微信/飞书私聊者区分管理员账号的要求。本计划取代“唯一账号绑定 + 挑战 + 二次确认 + 手动启用 + 安全快捷回复白名单”设计。

> 实施状态：通道自动启用、私聊指令与原路回复、管理页简化已落代码；离线回归以 `dotnet test` / `npm run test` 实测为准。微信 `HTTP_412` **根因已定位并修复**（见下表 R3）：分块传输被网关拒绝，与登录态和真机无关；真机送达仍未验收；旧安全审计/App 契约文件仍缺失。

## 要达到的体验

- QQ：填入机器人 AppID/AppSecret 保存后立即连接并接收所有发给该机器人的私聊；微信/飞书：扫码授权完成后立即连接。无需额外挑战、确认、启用或快捷回复授权。
- 来自已接入平台机器人的每位私聊者均可用现有 App/Web 聊天的文字快捷回复、系统命令和脚本指令；结果在 App/Web 会话里显示，并发回**原发件人**。这里的“管理员”只表示聊天指令权限，不签发 Manager JWT，也不开放后台 HTTP 管理接口；群聊和无效事件仍拒绝。
- 管理页只保留机器人配置/扫码、断开接入/重新接入、状态与发送记录；不出现“唯一绑定用户”“候选”“绑定挑战”“确认绑定”“启用通道”“安全快捷回复配置”。账号扫码时未返回单个用户 ID 不影响机器人接入；取决于协议返回的 bot token / bot id 是否有效。

## 最小改动路径

1. QQ 凭据保存、微信与飞书扫码完成时设置通道为运行态；旧账号重配先撤销旧会话/回复路由并启动新连接。更新三个 Worker 的启动条件，直接消费已配置且运行的账号。旧挑战/唯一绑定逻辑停止作为入站门槛，旧行只做兼容清理，不在本次冒险重写数据库迁移。
2. `ChannelInboxService` 仅校验账户运行、机器人私聊事件、合法 peer/message ID、微信 context_token 与消息大小，去重后保存入站消息；把**对应的 peer ID 和本条 reply route**作为内部来源信息，交给现有指令链。避免再发固定“已收到”，快捷回复直接走现有 `MessageProcess`，无需单独勾选名单。
3. 出站只给这条指令的来源 peer 回复，发前再次校验 account ID / 版本 / route 有效期，绝不把 A 的回复发给 B；App/Web 看到聊天消息。过期/不确定回执给出可查的失败状态，不按全局会话名猜接收人；平台不支持的媒体与主动通知不冒充“已送达”。App/Web 独立 AI 助手会话、媒体上传不在本次文字指令范围。
4. 修复 QQ token `expires_in` 字符串解析（本轮已完成并通过全量测试）；微信 `HTTP_412` 需区分收/发接口，脱敏收集平台错误字段后判断根因，不盲猜。
5. 移除 Web 管理页多余按钮和操作文案，保持平台断开/重配的操作可用；更新管理页测试、协议/去重/跨用户隔离/脚本指令测试，`dotnet test` 全绿、`npm run test`、`npm run build`。微信/QQ/飞书需要各自测试号真机确认实际送达。

## 安全与上线边界

- **“人人管理员”仅限能够给已接入的机器人发私聊的人**，等同于向这些人开放 Quantum 聊天系统命令及脚本能力；机器人一旦公开，任何陌生私聊者也在此范围内，这是所有者的明确单用户产品决策。无需额外独立用户权限体系，但必须保留原有上传/脚本门禁和正向 JWT Manager 校验。
- 当前工作区缺少既定必读的 `docs/security/公网部署安全审计-2026-09-14.md` 和 `docs/App端API契约.md`；扩大高权限脚本消息入口前须恢复并审阅，不能将缺失文档视为已读。
- 入站去重、出站收件人隔离和旧回复路由换绑后失效是正确性底线。平台回复时间窗/第三方限制按真实回执处理，不假定永远可以主动发消息。

## 审核记录

| 编号 | 严重级 | 位置 | 问题与建议 | 状态 |
| --- | --- | --- | --- | --- |
| R1 | 阻塞 | 安全与上线边界 | 安全审计与 App 契约缺失；改脚本链前恢复并审阅。 | 待修 |
| R2 | 建议 | 出站 | 群聊拒绝与每条回复 peer/route 绑定必须有跨用户回归测试。 | 待验证 |
| R3 | 建议 | 微信诊断 | `HTTP_412` 来自 `ilink/bot/getupdates`：**`JsonContent` 不预计算长度 → 请求以分块（无 Content-Length）发出，微信网关直接回 412 空响应体**。2026-09-28 用库里真实凭据复现：同一请求带 Content-Length 恒 200（HTTP/1.1 与 h2、代理与直连、四个解析出的 IP 全部一致），改走 `ChannelNetwork.BuildJsonContent` 预缓冲后应用侧长轮询恢复 200。**紧随其后的第二个误判**：`getupdates`/`sendmessage` 成功响应不带 `ret`，旧判定把缺失当失败（管理页显示 `WEIXIN_RET_INVALID`），已收敛为 `WeixinChannelWorker.EnsurePollAccepted`。 | 已修 |

核实到方法体：`QqChannelClient.TokenAsync`、`ChannelInboxService.AcceptBatchAsync`、`ChannelManagementService.ConfigureQqAsync/RegisterWeixinScanAsync`、`MessageProcess.MessageAsync`、`AppPushService.SubmitCommandAsync`、`SendMessageHelper.Send`、`ChannelDeliveryWorker`；外部平台行为与缺失的安全契约未核实。
