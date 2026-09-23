# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 权威指引：先读 AGENTS.md

根目录 `AGENTS.md` 是本仓库的权威工作区指引，动手前先读；它覆盖架构硬规则、安全红线、代码与提交约定、已知坑、docs/ 文档索引。本文件只补充常用命令与仓库全景，不重复其内容，规则冲突时以 AGENTS.md 为准。

## 常用命令（均在仓库根目录执行）

```bash
# 后端（.NET 10，解决方案 Quantum.API/Quantum.API.sln）
dotnet build Quantum.API/Quantum.API.sln
dotnet test Quantum.API/Quantum.API.sln          # 改后端后必跑、须全绿（数量以实测为准）
dotnet test --filter "FullyQualifiedName~ScriptEngineTests"   # 单个测试类（后缀 ~方法名 即单个方法）
dotnet run --project Quantum.API/Quantum.Web     # 本地运行，端口见 appsettings.json（默认 5088）

# 数据库迁移：双 DbContext（Sqlite/MySql）必须各生成一次
CreateMigration.bat <迁移名>                      # 产物 Quantum.API/Quantum.Data/Migrations/{SqliteMigrations,MySqlMigrations}/

# Web 管理端（quantum-web/）
cd quantum-web
npm run dev                                       # 开发：8080，vite proxy /api → 127.0.0.1:5088
npm run build                                     # 产物输出 ../quantum-release/wwwroot
npm test                                          # vitest，跑 tests/**/*.spec.js
npx vitest run tests/lineDiff.spec.js             # 单个测试文件

# 安卓（quantum-app/，JDK 17+，本机路径见 AGENTS.md）
cd quantum-app
./gradlew assembleDebug
./gradlew testDebugUnitTest
./gradlew :feature:chat:testDebugUnitTest --tests "*类名*"     # 模块单测

# 整体发布 / 镜像 / APK
publish.bat        # dotnet publish 后端 + npm build 前端 → quantum-release/
build.bat          # docker build + push asupc/quantum
build-apk.bat      # App release APK（keystore 注入）
```

## 仓库全景

三个交付物 + 测试 + 文档共用一个仓库；细节读对应文档，勿凭空推断：

| 目录 | 内容 | 详情 |
|---|---|---|
| `Quantum.API/` | ASP.NET Core 后端，六项目分层：Quantum.Web（Controllers/Filters/Middleware 组装，AssemblyName=Quantum）/ Quantum.Application（业务服务+任务执行引擎）/ Quantum.Data（双 DbContext+Migrations）/ Utils / Entities / Plugin.Abstractions（任务契约，纯 BCL） | AGENTS.md |
| `quantum-web/` | Vue 3 + Vite 7 + Naive UI 暗色管理端（风格 B）；**后端菜单驱动动态路由**：菜单 component 名须在 `src/router/routers.js` 的 `VIEW_COMPONENTS` 注册表登记，未登记渲染占位页 | `quantum-web-README.md` |
| `quantum-app/` | Kotlin 2.1 + Jetpack Compose (M3) 安卓客户端，Gradle 多模块（`app` + `core:*` + `feature:*`，清单见 `settings.gradle.kts`），版本统一锁 `gradle/libs.versions.toml` | `quantum-app/README.md` |
| `Quantum.API.Tests/` | xUnit 测试，含安全基线（SecurityHardeningTests / PermissionLayeringTests / ScriptEngineTests 等），改后端必须全绿 | AGENTS.md |
| `docs/` | 计划与契约文档；大型改动先出计划文档放这里、经确认再动手 | AGENTS.md 文档索引 |

跨端机制速记（详见 AGENTS.md 及对应 docs）：

- **HTTP 恒 200 信封**：接口统一包 `ResultModel`（Code 200/401/500，业务失败抛 `BusinessException`）；管理员判定只看 JWT 正向 `Manager="true"` claim，管理端点加 `[ManagerOnly]`。
- **双数据库**：`DBType` 决定 Sqlite/MySql 哪侧生效；实体改动须 `CreateMigration.bat` 两侧各生成迁移并核对 Up/Down。
- **任务脚本唯一形态 `.cs`**（实现 `IQuantumTask`），保存走「三级门禁 → Roslyn 编译 → 哈希缓存」；`Quantum.Web/scripts/` 是运行时数据、不参与主程序编译。
- **App 实时通道**：WebSocket（`Middleware/AppWebSocketManager`，25s 应用层心跳）+ REST 分页补拉 + msgId 幂等；契约以 `docs/App端API契约.md` 为准。
- **内置 AI 助手**：`Quantum.Application` 的 `AgentService` / `LlmClient` / `AgentToolbox`，端点 `AiAgentController` / `AiProviderController`；计划文档见 `docs/AI*.md`、`docs/App端AI助手功能计划.md`。
- **任务脚本部署同步**（dev → 正式）用 `.agents/skills/deploying-quantum-scripts/`（先 `--dry-run` 看差异）。
