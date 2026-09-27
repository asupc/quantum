# QQ／微信接入可行性验证记录（不是实施计划）

日期：2026-09-26。结论：**尚未完成真实可行性验证，不允许据此启动实施或重新提交实施计划。** 前一份未经验证的计划已从工作区撤回，本记录仅记可复核的探针事实与缺口。

## 已完成：无账号离线探针

- Windows 测试机 Node v26.3.1、npm v11.16.0；显式从公共 npm Registry 下载 `@tencent-connect/qqbot-nodejs@1.0.4`、`@tencent-weixin/openclaw-weixin@2.4.9`、`openclaw@2026.9.6` 到独立临时目录，`npm install --ignore-scripts --no-audit --no-fund --registry=https://registry.npmjs.org` 成功（330 个包；没有向项目写入依赖或执行 npm 安装脚本）。当前系统默认 npm Registry 是另一个 HTTP 镜像，本次探针没有使用它。
- 用假占位符只做**无网络构造**：`new QQBot({appId,appSecret})` 成功，存在 `start` 与 `sendText`；Weixin 2.4.9 插件在 mock host `runtime.version=2026.9.6` 上 `register` 成功，注册 `openclaw-weixin`，可见 `gateway.startAccount` 和 `outbound.sendText`。这只能说明**包可安装、JS API 形状和基础版本兼容**，不能证明 QQ／微信网络可登录、正常收发或外发权限。
- 本地检视 OpenClaw 2026.9.6 的类型定义可见 `before_dispatch` 钩子输入和 `{handled,text?}` 返回类型；腾讯 Weixin 插件源码通过 `dispatchReplyFromConfig` 分发入站、通过 `contextToken` 和 `outbound.sendText` 发送出站。**没有启动宿主和真实插件消息链，钩子实际拦截效果未验证。**

临时探针与原草案的本地备份位于系统 `%TEMP%/quantum-channel-spike-3f33d952cfd9478fb7db309736787471/`；临时目录不纳入交付。原草案仅保留供后续重新编写计划参考，不构成已审核实施文件。

## 尚未完成：不可冒称通过的真实验收

1. **QQ**：未提供测试机器人 AppId/Secret，未与 QQ 开放平台鉴权或收取私聊/群事件；未实测文本、图片、按钮、回复限流、长时间主动推送。
2. **普通微信**：未扫码登录 Weixin 插件；未验证自制桥接钩子在真实入站事件中确实截获全部需要的消息、能阻断默认 Agent/内置命令、能经插件出站回复；未测长时间静默后的主动发送（平台拒绝后必须视为不可用，而非忽略错误）。
3. **Quantum 多端**：尚未改动现有后端，因此不存在 App／QQ／微信同条消息分发、原路回复、媒体降级、去重、权限隔离的集成测试或平台实测；仅有现有代码的架构分析。

## 真实可行性通过判据（仅定义验证结果，不是实施计划）

使用**测试用途**的 QQ 官方机器人、测试微信账号，在隔离环境跑最小侧车：QQ 与微信分别完成一次扫码/凭据登录、用户私聊入站、服务端自动回发；QQ 主动推送文本与选项/图片能力逐项打点，微信静默后主动推送按短时和隔夜各实测一次；微信桥接钩子必须确认事件只被桥接一次，绝不触发默认 Agent。再接最小 Quantum 沙箱实现同一事件 App 留痕 + QQ/微信双端投递，对重复回调、断连、未授权私聊、拒绝消息执行负测。**任何一项不通过均标明不成立的范围并停止相应渠道方案，不从文档推断为通过。**

所需前置：QQ 机器人测试 AppId/Secret 以本机私密环境变量／secret 文件配置（不要粘贴到聊天）；微信侧需要账号持有人配合交互式扫码并向机器人发测试消息。未取得这两项前，不能完成真实可行性验证。
