# quantum-app（量子助手安卓客户端）

按《docs/App实施详细计划.md》A5.1-A5.5 交付的安卓客户端：Kotlin 2.1 + Jetpack Compose（M3）+
Retrofit/OkHttp + OkHttp WebSocket + Room + DataStore + Hilt（单仓库 Gradle 多模块，
version catalog 统一锁版本 `gradle/libs.versions.toml`）。

## 1. 模块结构

```
quantum-app/
├─ app                 壳：单 Activity + NavHost + 底部导航（会话/AI助手/任务/管理/我的）、
│                      登录/服务器地址页、WS 生命周期、深链路由、生物识别门禁、消息到达中枢
├─ core:common         envelope 解包异常/时间原串/深链路由表/M3 动态深色主题/通用组件
├─ core:network        Retrofit/OkHttp、EnvelopeInterceptor（401 单飞刷新+重放）、
│                      AppWsClient（25s 应用层心跳/指数退避）、App/Admin API、DTO（PascalCase 显式标注）
├─ core:storage        Room（chat_message/notification/sync_cursor/outbox）+ DataStore（token/偏好）
├─ core:push           通知渠道/系统通知（本地通知，消息实时性由常驻 WS 承担）
├─ core:media          媒体核心（MediaFormat/MediaSeek/MediaSessionService/PlayerManager，播放器与系统媒体会话）
├─ feature:chat        会话 IM（同步引擎/发送链路/三型气泡/上传/快捷指令）
├─ feature:notify      通知中心（三来源 msgId 幂等/分类 tab/已读）
├─ feature:task        任务列表/执行 + 日志中心
├─ feature:docker      Docker 面板（启停二次确认）
├─ feature:admin       环境变量/系统设置（单管理员体系，用户管理已随用户体系移除）
├─ feature:ai          AI 助手（会话/对话/diff 提案三级页，AiMarkdown/AiLineDiff/影子试运行）
└─ feature:mine        设备/通知偏好/生物识别
```

> 推送方案（2026-09-18）：厂商离线推送（极光 JPush）已整体移除。消息实时性由常驻
> WebSocket 承担：App 存活期间 WS 保持连接（退后台不断连），notify 帧到达时后台弹
> 本地系统通知；进程被杀则退化为回前台补拉。请引导用户对 App 开启「锁定后台/自启动」。

## 2. 构建

要求：JDK 17+、Android SDK（compileSdk 36）。`local.properties` 写 `sdk.dir`（不入库）。

```bash
cd quantum-app
# 本机未装 JDK17 时可指定（Windows 示例）
set JAVA_HOME=D:\DataCenter\jdk\jdk-17.0.20.1+1
./gradlew assembleDebug          # 调试包：app/build/outputs/apk/debug/
./gradlew assembleRelease        # 需先配置签名（见下）
```

### 签名（自分发依赖同一签名，丢失即无法覆盖安装——务必异地备份）

在 `quantum-app/keystore.properties`（.gitignore 已排除）：

```
storeFile=../quantum-release.keystore
storePassword=***
keyAlias=quantum
keyPassword=***
```

存在该文件时 release 构建自动签名；否则回退 debug 签名（仅供开发）。

## 3. 与服务端的契约

- 全部接口契约见 `docs/App端API契约.md`（envelope/HTTP 恒 200、分页三风格、WS 帧定义、
  权限分层语义、任务通知受众）。
- 消息同步：登录/重连/切网统一走 REST 500/页翻页（终止条件=本页条数 < limit）；
  WS sync 帧仅在线期轻量补偿；三来源以 msgId 幂等。
- 心跳：每 25s 发应用层 `{"type":"ping"}`（协议层 ping 不计活）；前台与后台均保持连接
  （后台实时通知依赖进程存活）。

## 4. 客户端单测（plan §9.1 本地可完成项已落地）

`./gradlew testDebugUnitTest`：

| 模块 | 用例 |
|---|---|
| core:network | **401 单飞刷新**（并发 10 次刷新仅 1 次 HTTP，MockWebServer 计数）；**EnvelopeInterceptor 重放**（401→刷新→带新令牌重放一次；并发 8×401 仅 1 次刷新全部成功）；RawAuthHttp 成功/业务失败/空地址快速失败 |
| feature:chat | **seq 分页终止**（500+200 两页，条数 < limit 终止）；**sync_cursor 持久化**与空页 MaxSeq 校准；**msgId 三来源去重**（WS 直推/REST 补拉/再次 WS）；发送链路（WS 优先→REST 兜底→限流失败标记 outbox） |
| core:common | 深链路由表（未注册前缀回退会话页）；服务器时间原串解析往返 |
| feature:ai | **AiChatViewModel**、**AiLineDiff**（行级 diff）、**AiMarkdown**（渲染）、**AiPayloadParser**（工具结果解析）四套单测 |
| 同上（帧） | WS 帧解析（小写驼峰、sync 帧 direction、ping 编码） |

仍以服务端测试为全局回归门禁。

## 5. 版本发布（管理员操作）

1. 改 `app/build.gradle.kts` 的 `versionCode`（单调递增）与 `versionName`；
2. 用 `build-apk.bat` 构建 release APK（JDK 自动切换 + keystore 注入）；
3. 把 APK 直接发给使用人安装：**签名与已装版本一致即可覆盖安装**。
   服务端不再托管安装包——版本管理端点与 `t_app_version` 表已于 2026-09-17 整体移除，App 端也不再检查更新。
