# Quantum（量子助手）工作区指引

个人自动化任务管理平台，三个交付物共用一个仓库：
- `Quantum.API/` — ASP.NET Core 后端（**net10.0**，六项目分层：Quantum.Web 组装接入[Controllers/Filters/Middleware，AssemblyName=Quantum] / Quantum.Application 业务服务+任务执行引擎+消息通道（QQ/微信/飞书） / Quantum.Data[DB+Migrations] / Quantum.Utils / Quantum.Entities / Quantum.Plugin.Abstractions[任务契约，零包依赖纯 BCL]，统一属性在 Directory.Build.props），DI 用 Autofac，日志 log4net，JSON 用 Newtonsoft，调度 Quartz 4，EF Core 10，任务源码编译 Roslyn（Microsoft.CodeAnalysis.CSharp 5.0.0，与 EFCore.Design 传递依赖对齐）。
- `quantum-web/` — Vue 3 + Vite 7 + Naive UI 管理端（暗色科技风，"风格B"）。
- `quantum-app/` — Kotlin 2.1 + Jetpack Compose (M3) 安卓客户端，Gradle 多模块（app + core×5 + feature×7，清单见 `settings.gradle.kts`），版本统一锁 `gradle/libs.versions.toml`。
- `Quantum.API.Tests/` — xUnit 测试（项数以 `dotnet test` 实测为准，2026-10-11 实测为 1201；含 SecurityHardeningTests / PermissionLayeringTests / ScriptEngineTests[门禁/编译/执行器/门面] / ScriptGateEscapeTests[脚本沙箱逃逸] / TaskHttpSsrfTests[外部请求边界] 等安全基线，**改动后必须全绿**）。
- `docs/` — 设计文档与契约（改敏感区域前先读，见下）。
- `quantum-release/` — 发布产物（publish.bat 会整目录删除重建，勿手改）；`Quantum-Docker/`、`quantum-test/` 为部署配套；`quantum-image/` 存 build-service.bat 导出的离线镜像 tar。

## 构建与测试命令

```bash
# 后端（解决方案 Quantum.API/Quantum.API.sln）
dotnet build                      # 编译
dotnet test                       # 全部测试（改后端代码后必跑）
dotnet run --project Quantum.API/Quantum.Web  # 本地运行（端口 appsettings.json 的 Port，默认 5088）

# 数据库迁移：双 DbContext（Sqlite/MySql）须各生成一次，用脚本一次搞定
CreateMigration.bat <迁移名>       # 产物在 Quantum.API/Quantum.Data/Migrations/{SqliteMigrations,MySqlMigrations}/

# Web
cd quantum-web && npm run dev     # 开发（vite proxy 到后端）
npm run build                     # 产物直接输出到 ../quantum-release/wwwroot
npm run test                      # vitest 单测（tests/*.spec.js）

# 安卓（JDK 17+；本机可用 D:\DataCenter\jdk\jdk-17.0.20.1+1；local.properties 写 sdk.dir）
cd quantum-app && ./gradlew assembleDebug
./gradlew testDebugUnitTest

# 整体发布 / 镜像 / APK
publish.bat                       # dotnet publish 后端 + npm build 前端，产出 quantum-release/
build.bat                         # docker build + push asupc/quantum
build-service.bat                 # 离线交付：publish + docker save 到 quantum-image\（版本号自动 +1，不推 Hub）
build-apk.bat                     # App release APK（keystore 注入）
```

## 架构边界与硬规则

- **HTTP 恒 200 信封**：所有接口经 ResultFilter/ExceptionFilter 包装为 `ResultModel`，`Code=200` 成功 / `401` 认证或权限 / `500` 业务失败（抛 `BusinessException`）。新增端点不要直接返回裸对象或非 200 状态码表达业务错误。
- **单用户登录**：Web/App 共用唯一账号；新令牌须有 `TokenPurpose=User`、当前配置账号名与有效 `LoginTime`。旧令牌只在原有效期内按主体兼容，不依赖角色 claim。受保护端点走登录用途校验，Open AppKey、PushKey、外触 Secret 不得获取登录能力；详见 `docs/单用户登录与外部凭据契约.md`。
- **首次部署初始化向导（2026-10-10）**：无配置首启或账号密码留空 = 未初始化（`Setting.SetupCompleted=false`），Web 端自动进入 `/setup` 向导：创建登录账号 → 选择数据库（SQLite/MySQL，可试连）→ 勾选自带脚本建任务；`/api/Setup/*` 匿名仅未初始化期有效，完成即关闭（防未授权接管账号）。换库需重启，勾选清单经 `SetupPendingScripts` 跨重启补种（`ScriptTaskSeeder.RunAfterInit` 按「向导未完成不播 / 勾选补种 / 存量新库全量」执行）。首次启动不再自动生成随机账号密码（详见 `docs/首次部署初始化向导实施计划.md`）。
- **双数据库**：`DBType`（appsettings.json，MySql/Sqlite）决定 `QuantumSqliteDbContext` / `QuantumMySqlDbContext` 哪个生效。实体或模型改动必须两侧各加迁移并核对 Up/Down 一致。
- **任务执行引擎（2026-09-16 改造，.cs 源码任务进程内执行）**：脚本唯一形态 `.cs`（实现 `Quantum.Plugins.IQuantumTask`，示例见 `Quantum.Web/scripts/quantum/demo/`）。保存/上传走「门禁三级扫描（语法粗筛+语义黑名单+启发式警告）→ Roslyn 编译 → SHA256 哈希缓存」，任一不过拒绝落盘（PUT /task/scripts 返回 blocked/warnings/errors 三类诊断）；执行经 collectible ALC 加载产物、独立 DI scope 组装 ctx（`ctx.Env`/`ctx.Notify`/`ctx.CustomData` 门面进程内直调 EnvService/NotifyService/CustomDataService，免环回 HTTP/免令牌；`ctx.Http` 仅外部请求）。**已移除**：node/python 执行、`Extends.TemporaryToken`、`Dockerfile-no-python`/`build-no-python.bat`；根 Dockerfile 已删 node/python/build-essential 安装段。
- **指令触发路由（2026-10-10 增会话内省略指令词）**：`MessageProcess.SelectTaskCandidates` 五级路由——①点选精确（TargetTaskId）/ ②全局唯一命中 / ③会话任务集（P4：组内多命中不外溢全局）/ ④全局 / ⑤会话内省略指令词。⑤：App/Web 在任务会话里手打裸消息（如会话「电影港」发「西游记」）且未命中任何触发指令时，若会话组内**恰一个**带触发指令的任务，把消息拼回「指令词 消息」按手打全指令等价触发（指令环境变量收到拼回文本，脚本「剥离指令词取关键字」口径不变）；显式命中恒优先，点选代发 / 机器人来源 / 默认会话 / 组内多个带指令任务均不走⑤。
- **任务脚本数据脱敏（2026-09-19 定，存量已全量改造）**：脚本内不允许保留私有服务地址、账号、密码、Cookie/令牌等敏感数据——一律改为**环境变量必填**读取，缺失即抛「缺少环境变量 X，请先在环境变量页配置」，不写内置缺省值；公网第三方 API 端点（bilibili、SMZDM、财经/晨报数据源等任务固有公开地址）可保留为常量。变量名须匹配 `^[a-zA-Z][a-zA-Z0-9_]{1,64}$`（字母开头、可含下划线、不能带连字符）；多账号配同名多条变量，执行时平台按 `&` 合并投递、脚本自行拆分；注释与帮助文案中的真实内网 IP 一律写 `192.168.x.x` 占位。同步脚本到新实例前，先在目标环境变量页补齐脚本头部声明的全部变量（缺失首跑即报错）。
- **任务脚本外部请求的网络边界（2026-10-11 对抗性测试后加固）**：`ctx.Http` / `ctx.File.DownloadAsync` 默认**只允许公网目标**——本机回环、RFC1918 私网、链路本地（含云元数据 169.254.169.254）、CGNAT、组播/保留段、IPv6 ULA/link-local/Teredo 一律拒绝，且**关闭自动重定向**（防「公网地址 302 引向内网」）；判定见 `Quantum.Utils/NetworkGuard`，连接级与逐请求级双校验。**内网自建服务（qBittorrent、NAS、局域网 API）必须先在「系统设置 → 任务内网白名单」`Setting.TaskNetworkAllowlist` 显式登记**（IP/CIDR/主机名，精确匹配不含子域），否则脚本报「外部请求被拒绝」。这一条会打断未登记的内网脚本（自带 `qBittorrentAdd.cs`/`dygangs.cs`/`music_search.cs` 即属此列），迁移脚本到新实例时记得同步登记。
- **内置 AI 助手**：`AgentService`/`LlmClient`/`AgentToolbox` + `AiProviderService`（Quantum.Application），端点 `AiAgentController`/`AiProviderController` 要求登录身份；Web 侧「AI供应商」「AI设置」（全局设置+AI 写权限高危项）挂系统管理、会话分组有「AI助手」页，App 侧 `feature:ai` 为底部 tab；限值上限 最大轮数 100 / 单次时限 3000，保存与运行时双重 clamp；「失败自动分析」带防重（2026-10-08）：同一脚本有待确认提案（Pending）或默认会话有运行中 run 时不重复发起，提案应用/忽略/过期后自动恢复。
- **App 通道**：WebSocket 走 `Middleware/AppWebSocketManager`，应用层 25s 心跳；消息同步 REST 分页 + msgId 幂等（原契约文档已清理，以后端代码与测试为准）。
- **单用户消息通道（QQ/微信/飞书，2026-09 重构回归）**：`Quantum.Application/Channels/*` + `ChannelController`——QQ 保存凭据、微信/飞书扫码直连，全部有效私聊共享 App 文字指令能力；收发走 `ChannelInboxService`/`ChannelDeliveryWorker`，凭据用密钥文件加密（启动须注入 `QUANTUM_CHANNEL_KEY_FILE`，否则三平台凭据集体解密失败报「连接异常」）。实施状态、已修复根因（微信 412 分块传输、飞书 wss 字段名等）与地雷记录见 `docs/QQ微信内置通道实施状态.md`，动通道代码前必读。
- **已移除的旧通道**：公众号/WxPusher/Web-Chat/青龙（QingLong）及旧版 QQ/微信实现均已整体删除，勿恢复、勿引用其表或接口；现行通道以上一条新架构为准。

## 安全红线（现行凭据边界见 docs/单用户登录与外部凭据契约.md）

- 上传/任务脚本链路要求登录身份，并有扩展名白名单、三级门禁与 SafeFile 路径兜底；改动时不得放宽。
- 登录口令按当前配置校验并限流；改密作废全部登录 access token 与 App refresh token。`UserTokenNotBefore` 与旧配置键 `ManagerTokenNotBefore` 在过渡期同步。
- 生产 Swagger/CORS 由 `EnableSwagger` / `AllowedOrigins` 门控；`KnownProxies` 配合 ForwardedHeaders 防伪造 XFF。
- `appsettings.json` 内含真实连接串与密钥，改动时不要泄露到日志/文档/新文件中。

## 约定

- C# 命名按 `.editorconfig`（Pascal case、接口 I 前缀）；CS1591（XML 注释缺失）已关闭。注释与文档用中文。
- 前端组件自动引入 `unplugin-vue-components`（NaiveUiResolver）已注册生效——Naive UI 组件无需手动 import；但 **API 级 `unplugin-auto-import` 未注册**，`message`/`h`/`computed` 等仍需显式 import。monaco 已降级为 textarea，勿重新引入重型编辑器。
- 安卓 DTO 对后端 PascalCase 靠显式 @SerializedName 标注；厂商离线推送（JPush）已整体移除，消息实时性由常驻 WS + `core:push` 本地通知承担（引导用户开锁定后台/自启动）。
- release keystore 在仓库外，经 `quantum-app/keystore.properties`（已 gitignore）注入；**签名丢失即无法端内自更新**。
- 提交信息中文、`[模块] 变更描述` 格式（如 `[后端安全]`、`[App客户端]`）；主分支 main。

## 已知坑

- NuGet 本机缓存在 `D:\DataCenter\Nuget`，私有镜像源偶发拉取失败——还原失败先重试或确认缓存。
- `Quantum.Web/scripts/`（任务脚本目录）、`scripts-bak/`、`logs/` 等目录已从编译排除：scripts 下的 `.cs` 是任务源码（运行时经门禁+Roslyn 编译执行），**不能**被编进主程序集；勿放入需要编译的代码。

## 改动前的文档索引（docs/）

> 2026-10-08 核对重建：早期 App/AI/脚本引擎/后端规范化等计划与契约文档已清理；文档里引用到不存在文档的旧说法，一律以代码与测试为准。

- `QQ微信内置通道实施状态.md` — 现行单用户消息通道（QQ/微信/飞书）的实施与联调状态、已修复根因与待验收项（最新更新在文件顶部）；动 Channels 相关代码前必读。
- `飞书消息通道接入计划.md` — 飞书通道设计前提（部分已被实施状态文档推翻，注意甄别）。
- `微信机器人管理员会话与HTTP412修复计划.md`、`QQ微信通道接入方式核实与偏差清单.md`、`QQ微信P0实施验证记录.md`、`QQ微信接入可行性验证记录.md`、`QQ微信官方龙虾通道接入计划.md`、`QQ微信原生CSharp双向通信计划.md` — 通道方案演进的历史记录，勿当现行依据。
- `执行记录与外部推送API契约.md` — 执行记录查询与外部推送（PushKey）接口契约。
- `单用户登录与外部凭据契约.md` — 现行凭据边界：单用户登录、TokenPurpose、AppKey/PushKey/外触 Secret 的能力与约束。
- `单用户模型与角色机制移除计划.md` — 角色机制移除的决策记录（现行单用户设计的来由）。
- `功能增强分期实施计划.md`、`一期功能增强运维手册.md` — 功能增强的分期计划与一期运维手册。
- `UI功能测试记录-qoder-ui-2026-09-27.md` — Web 端 UI 功能测试记录。
- `首次部署初始化向导实施计划.md` — 首次部署初始化向导（建账号/选库/勾选自带脚本建任务）的设计取舍与实施记录。
- `全功能对抗性测试记录-2026-10-11.md` — 三端全功能对抗性测试的真实缺陷、**被击穿的修复**、误报判定与遗留建议。改安全边界（脚本门禁/SSRF/鉴权/协议解析/凭据）前必读；其中「验证为安全并已记录防线」一节可避免重复怀疑同一批面。

> 工作方式：大型改动（升级、跨模块重构）先出详细计划文档（放 docs/）经确认后再动手，是本仓库所有者的既定要求。
