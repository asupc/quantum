# Quantum（量子助手）

个人自动化任务管理平台：**任务调度 + C# 脚本引擎 + 会话式指令交互 + 内置 AI 助手**，单仓库交付三个端——ASP.NET Core 后端、Vue 3 Web 管理端、安卓客户端。


## 功能清单

### Web 管理端（quantum-web）

后端菜单驱动的动态暗色管理台（Vue 3 + Naive UI），默认菜单分组：

| 分组 | 功能 |
|---|---|
| 会话 | Web 端 IM（与 App 共用 `/ws/app` 通道与消息契约）、AI 助手（多轮会话、脚本修复提案 diff、影子试运行） |
| 数据管理 | 自定义数据标题管理（Data1–15 任意建模，供脚本读写） |
| 指令中心 | 脚本指令（任务 CRUD / 定时调度 / 手动执行）、外触内执（Open API + OpenTriggerTask HTTP 触发）、脚本版本（留痕与回滚）、脚本编辑（在线编辑 + 三级门禁） |
| Docker 管理 | 容器 / 镜像 / 网络 / 卷 的查看与启停 |
| 系统管理 | 系统设置、菜单管理（含父级调整）、AI 供应商（OpenAI 兼容多供应商与模型）、AI 设置（全局参数 + AI 写权限高危开关） |
| 环境变量 | 脚本运行时变量；同名多条按 `&` 合并投递，支持多账号 |
| 快捷回复 | 常用指令一键发送 |
| 日志中心 | 运行/系统/AI 等多类日志检索与统计 |

### 安卓客户端（quantum-app）

Kotlin 2.1 + Jetpack Compose (M3) 多模块工程：

- **会话**：与机器人的 IM 对话——指令收发、图片/文件、快捷指令、历史漫游、富文本气泡；
- **AI 助手**：底部 tab 直达，会话/对话/脚本提案 diff 三级页面，支持应用/忽略闭环；
- **任务**：任务列表、执行与日志中心；
- **管理**：环境变量、用户、系统设置、Docker 面板等管理宫格；
- **我的**：设备管理（远程解绑）、通知偏好与免打扰、生物识别门禁；
- **消息实时性**：常驻 WebSocket（25s 应用层心跳）+ REST 分页补拉 + msgId 幂等，后台弹本地通知；
- **深链**：`quantum://` 协议直达会话/AI 等页面。

### 平台核心能力（Quantum.API）

- **任务执行引擎**：任务脚本唯一形态为实现 `IQuantumTask` 的 `.cs` 源码；保存/上传走「三级门禁（语法粗筛 + 语义黑名单 + 启发式警告）→ Roslyn 编译 → SHA256 哈希缓存」，进程内隔离执行，Quartz 4 定时调度；
- **脚本门面 API**：`ctx.Env`（环境变量）、`ctx.Notify`（文本/图片/音视频/可点选项）、`ctx.CustomData`（自定义数据）、`ctx.Http`、`ctx.File`，免环回 HTTP、免令牌；
- **双数据库**：`DBType` 切换 SQLite / MySQL（双 DbContext，实体改动双库迁移）；
- **HTTP 恒 200 信封**：接口统一 `ResultModel`（Code 200/401/500），业务失败抛 `BusinessException`；
- **权限分层**：JWT `Manager="true"` claim + `[ManagerOnly]`，口令 PBKDF2，改密作废全部管理令牌；
- **内置 AI 助手**：多供应商管理、流式对话、工具调用、脚本修复提案（人工应用前影子试运行）、上下文超长压缩兜底；
- **App 通信层**：`/ws/app` 长连接（握手鉴权、心跳、ACK、增量同步）+ REST 分页补拉；
- **公网安全基线**：上传/脚本链路 ManagerOnly + 白名单 + SafeFile、ForwardedHeaders/KnownProxies、Swagger/CORS 生产门控等，详见 `docs/security/`。

## 仓库结构

| 目录 | 内容 |
|---|---|
| `Quantum.API/` | 后端（.NET 10）：Quantum.Web 组装层 / Quantum.Application 业务与引擎 / Quantum.Data 双 DbContext + 迁移 / Utils / Entities / Plugin.Abstractions（任务契约，纯 BCL） |
| `quantum-web/` | Vue 3 + Vite 7 + Naive UI 管理端 |
| `quantum-app/` | Kotlin 2.1 + Compose 安卓客户端（`app` + `core:*` + `feature:*` 多模块） |
| `Quantum.API.Tests/` | xUnit 测试（含安全基线，改后端必须全绿） |
| `docs/` | 计划与契约文档（动手前先读对应文档） |
| `Quantum-Docker/`、`docker-compose.yml` | Docker 部署配套与服务编排 |
| `AGENTS.md` | **权威工作区指引**：架构硬规则、安全红线、提交约定、文档索引 |

## 安装与使用

### 环境要求

- 后端：.NET 10 SDK
- Web 管理端：Node.js 20+（npm）
- 安卓客户端：JDK 17+、Android SDK（`compileSdk 36`）
- 可选：MySQL 8.x（不配则用 SQLite 零依赖起步）、Docker

### 方式一：Docker 部署（推荐生产 / 快速体验）

镜像可直接拉取，也可本地构建（见下文「构建与发布」）：

```bash
docker pull asupc/quantum:latest
```

**快速试用（自动生成随机凭据）**：编辑 `docker-compose.yml`，注释掉

```yaml
- ./app/appsettings.json:/app/appsettings.json
```

这一行挂载，然后：

```bash
docker compose up -d
docker logs quantum   # 首启自动初始化配置，控制台输出随机管理员账号/密码，请立即保存
```

**正式部署（自带配置）**：

```bash
mkdir -p app
cp Quantum.API/Quantum.Web/appsettings.example.json app/appsettings.json
# 编辑 app/appsettings.json（必改项见下节）
docker compose up -d
```

> ⚠️ 先在宿主机放好 `app/appsettings.json` 再 `up`，否则 Docker 会把挂载点建成同名**目录**导致启动失败。

访问 `http://<服务器IP>:5088`。App 与 WebSocket 同端口；公网部署请前置反向代理并启用 HTTPS，要点见 `docs/App部署说明.md`。

### 方式二：本地源码运行（开发）

```bash
# 1. 后端（仓库根目录）
dotnet run --project Quantum.API/Quantum.Web
# 首次启动若无 appsettings.json，会自动生成随机管理员账号/密码与 SQLite 库，
# 账号密码打印在控制台，请立即保存。

# 2. Web 管理端（另开一个终端）
cd quantum-web
npm install
npm run dev        # http://localhost:8080，/api 与 /ws 代理到 127.0.0.1:5088
```

浏览器打开 `http://localhost:8080`，用控制台输出的账号（或你配置的账号）登录。

### 配置说明（appsettings.json）

配置模板：`Quantum.API/Quantum.Web/appsettings.example.json`（`Quantum-Docker/` 下另有一份）。真实 `appsettings.json` 已被 `.gitignore` 排除，不会随仓库分发。关键项：

| 键 | 说明 |
|---|---|
| `UserName` / `PassWord` | 管理员账号口令。**不带配置首启时随机生成并打印到控制台**；用模板则必须改掉占位值 |
| `DBType` | `SQLite`（零依赖，默认）或 `MySql` |
| `DBAddress` | SQLite 填库文件名（如 `quantum.db`）；MySQL 填连接串 `server=...;port=...;database=...;Uid=...;Pwd=...;` |
| `Port` | 监听端口，默认 `5088` |
| `SymmetricSecurityKey` / `SecurityIssuer` / `SecurityAudience` | JWT 签名密钥与签发/受众，**必须换成随机长字符串**（泄露即可伪造管理员令牌） |
| `EnableSwagger` | 生产环境保持 `false` |
| `AllowedOrigins` / `KnownProxies` | 公网部署时收敛 CORS、配置可信反向代理 |

改完配置重启生效。首次登录后建议立即在 Web 管理端修改口令，并在「系统管理 → AI供应商」配置模型后使用 AI 助手。

### 安卓客户端构建

```bash
cd quantum-app
# 在 quantum-app/local.properties 写入（不入库）：
#   sdk.dir=<你的 Android SDK 路径>
./gradlew assembleDebug     # 调试包：app/build/outputs/apk/debug/
```

安装后首启填写**服务器地址**（你的 Quantum 服务地址）与管理员账号登录。Release 签名、版本发布与保活引导见 `quantum-app/README.md` 与 `docs/App部署说明.md`；终端用户日常使用见 `docs/App用户使用说明.md`。

### 测试

```bash
dotnet test Quantum.API/Quantum.API.sln        # 后端（改后端后必跑、须全绿）
cd quantum-web && npm test                      # Web 端 vitest
cd quantum-app && ./gradlew testDebugUnitTest   # 安卓端单测
```

### 构建与发布

```bash
publish.bat        # dotnet publish 后端 + npm build 前端 → quantum-release/
build.bat          # docker build + push asupc/quantum
build-service.bat  # 离线交付：发布产物 + docker save 到 quantum-image\（不推 Hub）
build-apk.bat      # 安卓 release APK（keystore.properties 注入签名）
CreateMigration.bat <迁移名>   # 实体改动后双库（Sqlite/MySql）各生成一次迁移
```

> `appsettings.json` 不会打进编译产物与镜像（csproj 已排除），部署时以挂载/外置配置提供；`publish.bat` 会在产物缺配置时失败并提示。

## 开发指引

- 权威工作区指引见 **[AGENTS.md](AGENTS.md)**：架构硬规则、安全红线、代码与提交约定、已知坑、docs 文档索引——动手前先读。
- 提交信息用中文、`[模块] 变更描述` 格式；主分支 `master`。
- 大型改动（升级、跨模块重构）先出计划文档放 `docs/`，经确认后再动手。
