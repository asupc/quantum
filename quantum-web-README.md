# Quantum-Web 项目概览

## 项目简介

**Quantum-Web** 是量子助手系统的 Vue 管理后台前端，提供任务调度与脚本管理、Docker 容器管理、会话（Web 端 IM，与 App 共用契约）、内置 AI 助手、环境变量、系统设置、自定义数据等功能。

---

## 技术栈（2026-09 风格 B「暗夜量子」改版后）

| 类别 | 技术 |
|------|------|
| 框架 | Vue.js 3.5（存量页 Options API，新组件可用 `<script setup>`） |
| 路由 | Vue Router 4.6 (Hash 模式，动态菜单路由由 beforeEach 单点加载) |
| 状态管理 | Pinia 3（stores/app、user、chat） |
| UI 组件库 | Naive UI 2.45（darkTheme + themeOverrides，主色 `#22D3EE`） |
| HTTP 客户端 | Axios 1.x（拦截器统一鉴权/错误提示/loadingBar） |
| 单元测试 | Vitest 5（`tests/*.spec.js`，`npm run test`） |
| 构建工具 | Vite 7（产物输出 `../quantum-release/wwwroot`） |
| 图表 | ECharts 5.5.1（dark 主题） |
| 代码编辑器 | 等宽 textarea 封装（monaco 因生产构建随机主线程自旋移除，见 git 历史） |
| 图标 | Font Awesome 7.1（本地资产，后端菜单 icon 即 FA class） |
| 设计 Token | `src/styles/tokens.css`（权威来源 `docs/ui-redesign/preview-b-dark-tech.html`） |

---

## 目录结构

```
quantum-web/
├── index.html               # Vite 入口
├── vite.config.js           # alias @/_c、8080 代理 /api→127.0.0.1:5088、outDir
├── public/
│   └── favicon.ico
├── src/
│   ├── api/                 # API 请求模块
│   │   ├── ai.js/aiAgent.js # AI 供应商/模型、AI 助手会话与提案
│   │   ├── app.js           # App 会话/消息/通知（会话页复用 App 契约）
│   │   ├── bookmark.js      # 书签 CRUD
│   │   ├── command.js       # 快捷回复
│   │   ├── customerData.js  # 自定义数据
│   │   ├── docker.js        # Docker 管理
│   │   ├── menu.js          # 菜单管理
│   │   ├── task.js          # 任务/脚本操作
│   │   ├── user.js          # 登录认证
│   │   └── ...              # envs/logs/setting/scriptVersion 等其余业务 API
│   ├── assets/              # 静态资源（fontawesome-7.1 本地资产、logo、错误页图）
│   ├── components/
│   │   ├── FaIcon.vue       # FA 图标封装（后端菜单 icon 即 FA class）
│   │   ├── login-form/      # 登录表单（n-form 校验）
│   │   └── main/            # 主框架（侧栏 n-menu / header-bar / tags-nav / user / fullscreen）
│   ├── config/index.js      # 应用配置（homeName、baseUrl）
│   ├── libs/
│   │   ├── axios.js         # HttpRequest 封装（401 跳登录/错误 toast/loadingBar 队列）
│   │   ├── api.request.js   # Axios 实例工厂
│   │   ├── naive-discrete.js# 非组件上下文 discrete API（window.$message 等）
│   │   ├── icons.js         # 历史库图标 → FA class 对照表
│   │   ├── ws.js            # 会话 WebSocket 客户端
│   │   ├── util.js / tools.js # 路由/标签/面包屑工具
│   ├── plugin/mixin/        # 全局 tdTip mixin（表格溢出复制/弹详情）
│   ├── router/
│   │   ├── routers.js       # 静态路由 + 后端菜单→路由映射（VIEW_COMPONENTS 注册表 + 青龙过滤）
│   │   ├── index.js         # 动态路由单点加载（Promise 去重 + 404 重导航）
│   │   └── before-close.js  # 页签关闭确认（discrete dialog）
│   ├── stores/              # Pinia（app：菜单/页签/面包屑；user：登录态；chat：会话/未读角标）
│   ├── styles/tokens.css    # 风格 B 设计 Token + 玻璃卡片/网格底纹/状态胶囊/深色滚动条
│   ├── theme/naive.js       # Naive UI darkTheme + themeOverrides
│   ├── utils/               # richText/chatFormat/messagePayload/chatMedia/lineDiff/op-actions
│   └── view/                # 业务页面（全部 Naive UI）
├── tests/                   # vitest 单测（chatFormat/chatStore/lineDiff/messagePayload/richText）
└── publish.bat              # 发布脚本（dotnet publish + 两个前端构建）
```

---

## 关键文件说明

### 入口文件
| 文件 | 用途 |
|------|------|
| `src/main.js` | createApp 启动，装配 Pinia/router/plugin + 全局 $message/$dialog/$notification |
| `src/App.vue` | n-config-provider（darkTheme+zhCN）+ 四大 provider + 网格底纹根层 |
| `index.html` | Vite HTML 入口 |

### 配置文件
| 文件 | 用途 |
|------|------|
| `vite.config.js` | 端口 8080、`/api` 代理、outDir `../quantum-release/wwwroot`、NaiveUiResolver |
| `src/config/index.js` | homeName、baseUrl（dev 直连 5088，prod 走同源托管） |

### 路由系统
- **Hash 模式**（createWebHashHistory）
- **动态路由**：后端 `GET /api/Menu` → `VIEW_COMPONENTS` 注册表懒加载；未登记组件渲染占位页
- 守卫：未登录跳登录；已登录访问未知路径时单点加载动态路由后原路径重导航
- **青龙管理模块已移除**：映射处过滤 `qinglong/` 前缀（后端数据未动）

### API 请求模式
```javascript
import axios from '@/libs/api.request';

export const SomeAction = (params) => {
    return axios.request({
        url: '/api/Endpoint',
        method: 'get|post|put|delete',
        data: params,        // POST/PUT
        params: params,       // GET (query string)
        noLoading: true       // 可选：跳过 loadingBar
    });
};
```

### 响应处理 (axios.js)
- Code 200: 返回 `response.data.Data`
- Code 401: 跳转登录页，清除 Token
- 其他: discrete message 显示错误

---

## 页面视图 (view/)

| 目录 | 页面 | 描述 |
|------|------|------|
| chat | index.vue (+components) | 会话（双栏列表+详情、全类型富文本气泡、输入条附件/快捷指令、全屏媒体、上翻分页） |
| ai | index, providers, setting | AI 助手（会话列表+对话）、AI供应商与模型、AI设置（全局+AI 写权限） |
| task | index.vue | 任务中心（主/子任务、导入导出、执行） |
| taskScripts | index.vue, versions.vue | 脚本编辑器（整页，textarea 方案）/脚本版本 |
| open-trigger-task | index.vue | 外触内执 |
| docker | index, containers, images, networks, volumes | Docker 管理（资源轮询+echarts 实时图） |
| custom-data | index.vue, title.vue | 自定义数据/标题管理（CSV 导出、echarts 可视化） |
| setting | index.vue, menu.vue | 系统设置/菜单管理 |
| env | index.vue | 环境变量管理 |
| logs | index.vue | 日志查看 |
| replay | index.vue | 快捷回复 |
| navigation | index.vue | 书签导航（首页看板） |
| database-migration | index.vue | 数据迁移 |
| login | login.vue | 登录页（玻璃卡片+网格底纹） |
| error-page | 401.vue, 404.vue, 500.vue | 错误页面 |

---

## 认证流程

1. 用户访问 `/login` 提交凭证
2. 调用 `POST /api/Login`
3. 成功后将 Token 存储为 `Bearer {token}` 到 `localStorage.accessToken`
4. Axios 拦截器在请求头中注入 `Authorization: {token}`
5. 路由导航守卫验证 Token
6. 动态加载后端菜单 `GET /api/Menu`
7. 401 响应时跳转登录页

---

## npm 脚本

```bash
npm run dev      # Vite dev server（127.0.0.1:8080）
npm run build    # 生产构建 → ../quantum-release/wwwroot
```

---

## 组件架构

```
App.vue（n-config-provider + message/dialog/notification/loading-bar provider + grid-bg）
└── router-view
    ├── Main（主框架）
    │   ├── Sider（渐变侧栏：logo + n-menu(FA renderIcon) + 版本号）
    │   ├── HeaderBar（折叠按钮 + n-breadcrumb + User 下拉 + Fullscreen）
    │   ├── TagsNav（胶囊页签：滚动/右键菜单/关闭策略）
    │   └── Content（keep-alive include 页签缓存 + router-view，支持 full-bleed 内容区与 ?session= 原位切换不新增页签）
    └── Login（玻璃卡片登录页）
```

## 历史版本

Vue 2.5 + iView 4.7 + vue-cli 3 实现保留在 git 历史（`upgrade/net10` 及更早分支）。
