# Quantum（量子助手）工作区指引

个人自动化任务管理平台，三个交付物共用一个仓库：
- `Quantum.API/` — ASP.NET Core 后端（**net10.0**，六项目分层：Quantum.Web 组装接入[Controllers/Filters/Middleware，AssemblyName=Quantum] / Quantum.Application 业务服务+任务执行引擎 / Quantum.Data[DB+Migrations] / Quantum.Utils / Quantum.Entities / Quantum.Plugin.Abstractions[任务契约，零包依赖纯 BCL]，统一属性在 Directory.Build.props），DI 用 Autofac，日志 log4net，JSON 用 Newtonsoft，调度 Quartz 4，EF Core 10，任务源码编译 Roslyn（Microsoft.CodeAnalysis.CSharp 5.0.0，与 EFCore.Design 传递依赖对齐）。
- `quantum-web/` — Vue 3 + Vite 7 + Naive UI 管理端（暗色科技风，"风格B"，设计稿见 docs/ui-redesign/）。
- `quantum-app/` — Kotlin 2.1 + Jetpack Compose (M3) 安卓客户端，Gradle 多模块（app + core×5 + feature×7，清单见 `settings.gradle.kts`），版本统一锁 `gradle/libs.versions.toml`。
- `Quantum.API.Tests/` — xUnit 测试（项数以 `dotnet test` 实测为准，2026-09-23 实测为 552；含 SecurityHardeningTests / PermissionLayeringTests / ScriptEngineTests[门禁/编译/执行器/门面] 等安全基线，**改动后必须全绿**）。
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
- **权限分层**：管理员判定一律看 JWT 正向 `Manager="true"` claim（**不要**用"没有某限制就算管理员"的反向推断——Open 匿名令牌/任务临时令牌会穿透）。管理端点加 `[ManagerOnly]` filter；非 Manager 令牌的任务列表/操作由服务端过滤拦截，客户端不做显隐。
- **双数据库**：`DBType`（appsettings.json，MySql/Sqlite）决定 `QuantumSqliteDbContext` / `QuantumMySqlDbContext` 哪个生效。实体或模型改动必须两侧各加迁移并核对 Up/Down 一致。
- **任务执行引擎（2026-09-16 改造，.cs 源码任务进程内执行）**：脚本唯一形态 `.cs`（实现 `Quantum.Plugins.IQuantumTask`，示例见 `Quantum.Web/scripts/quantum/demo/`）。保存/上传走「门禁三级扫描（语法粗筛+语义黑名单+启发式警告）→ Roslyn 编译 → SHA256 哈希缓存」，任一不过拒绝落盘（PUT /task/scripts 返回 blocked/warnings/errors 三类诊断）；执行经 collectible ALC 加载产物、独立 DI scope 组装 ctx（`ctx.Env`/`ctx.Notify`/`ctx.CustomData` 门面进程内直调 EnvService/NotifyService/CustomDataService，免环回 HTTP/免令牌；`ctx.Http` 仅外部请求）。**已移除**：node/python 执行、`Extends.TemporaryToken`、`Dockerfile-no-python`/`build-no-python.bat`；根 Dockerfile 已删 node/python/build-essential 安装段。平台能力清单与旧脚本迁移对照见 `docs/脚本执行引擎改造计划.md` 2.5/2.7。
- **任务脚本数据脱敏（2026-09-19 定，存量已全量改造）**：脚本内不允许保留私有服务地址、账号、密码、Cookie/令牌等敏感数据——一律改为**环境变量必填**读取，缺失即抛「缺少环境变量 X，请先在环境变量页配置」，不写内置缺省值；公网第三方 API 端点（bilibili、SMZDM、财经/晨报数据源等任务固有公开地址）可保留为常量。变量名须匹配 `^[a-zA-Z][a-zA-Z0-9_]{1,64}$`（字母开头、可含下划线、不能带连字符）；多账号配同名多条变量，执行时平台按 `&` 合并投递、脚本自行拆分；注释与帮助文案中的真实内网 IP 一律写 `192.168.x.x` 占位。同步脚本到新实例前，先在目标环境变量页补齐脚本头部声明的全部变量（缺失首跑即报错）。
- **内置 AI 助手**：`AgentService`/`LlmClient`/`AgentToolbox` + `AiProviderService`（Quantum.Application），端点 `AiAgentController`/`AiProviderController` 为 `[ManagerOnly]`；Web 侧「AI供应商」「AI设置」（全局设置+AI 写权限高危项）挂系统管理、会话分组有「AI助手」页，App 侧 `feature:ai` 为底部 tab；限值上限 最大轮数 100 / 单次时限 3000，保存与运行时双重 clamp；计划文档 `docs/AI*.md`、`docs/App端AI助手功能计划.md`。
- **App 通道**：WebSocket 走 `Middleware/AppWebSocketManager`，应用层 25s 心跳；消息同步 REST 分页 + msgId 幂等。App 相关端点契约以 `docs/App端API契约.md` 为准（分页三风格、WS 帧定义、错误文案均已定稿）。
- **已移除的旧通道**：QQ/微信/公众号/WxPusher/Web-Chat/青龙（QingLong）均已整体删除，勿恢复、勿引用其表或接口。

## 安全红线（改认证/上传/脚本链路前必读 docs/security/公网部署安全审计-2026-09-14.md）

- 上传/任务脚本链路有 ManagerOnly + 扩展名白名单 + SafeFile 执行路径兜底；改动时不得放宽。
- 口令 PBKDF2（旧 AES 密文登录自动升级）；改密会作废全部 Manager 令牌（ManagerTokenNotBefore）。
- 生产 Swagger/CORS 由 `EnableSwagger` / `AllowedOrigins` 门控；`KnownProxies` 配合 ForwardedHeaders 防伪造 XFF。
- `appsettings.json` 内含真实连接串与密钥，改动时不要泄露到日志/文档/新文件中。

## 约定

- C# 命名按 `.editorconfig`（Pascal case、接口 I 前缀）；CS1591（XML 注释缺失）已关闭。注释与文档用中文。
- 前端组件自动引入 `unplugin-vue-components`（NaiveUiResolver）已注册生效——Naive UI 组件无需手动 import；但 **API 级 `unplugin-auto-import` 未注册**，`message`/`h`/`computed` 等仍需显式 import。monaco 已降级为 textarea，勿重新引入重型编辑器。
- 安卓 DTO 对后端 PascalCase 靠显式 @SerializedName 标注；厂商离线推送（JPush）已整体移除，消息实时性由常驻 WS + `core:push` 本地通知承担（引导用户开锁定后台/自启动）。
- release keystore 在仓库外，经 `quantum-app/keystore.properties`（已 gitignore）注入；**签名丢失即无法端内自更新**。
- 提交信息中文、`[模块] 变更描述` 格式（如 `[后端安全]`、`[App客户端]`）；主分支 master。

## 已知坑

- NuGet 本机缓存在 `D:\DataCenter\Nuget`，私有镜像源偶发拉取失败——还原失败先重试或确认缓存。
- `Quantum.Web/scripts/`（任务脚本目录）、`scripts-bak/`、`logs/` 等目录已从编译排除：scripts 下的 `.cs` 是任务源码（运行时经门禁+Roslyn 编译执行），**不能**被编进主程序集；勿放入需要编译的代码。

## 改动前的文档索引（docs/）

- `App端API契约.md` — 动任何 App 通道（AppXxx* 控制器/服务/WS）前必读。
- `App端AI助手功能计划.md` / `AI*.md` — 内置 AI 助手（Web/App 会话、提案写权限、运行恢复、脚本修复 Agent）改动前必读。
- `Web端会话交互与UI预览方案.md` — Web 端新增会话页的方案（端点复用清单与后端补强项、token 映射、UI 预览入口）；预览稿 `ui-redesign/preview-chat-web.html`。
- `App实施详细计划.md` / `App功能清单与技术架构.md` — App 端功能与模块结构全景。
- `后端规范化与数据层优化设计.md` / `后端规范化-接口变更映射与匿名白名单.md` — 接口规范化与匿名白名单依据。
- `security/公网部署安全审计-2026-09-14.md` — 安全审计与修复记录。
- `ui-redesign/风格B实施计划.md` — 当前 UI 风格的实现规范。

> 工作方式：大型改动（升级、跨模块重构）先出详细计划文档（放 docs/）经确认后再动手，是本仓库所有者的既定要求。
