---
name: quantum-sync
description: 把本地 Quantum 任务脚本（Quantum.Web/scripts 下的 .cs）同步/发布到正式环境：安全预检（脱敏规则）→ 生成差异计划（脚本内容/环境变量/任务）→ 用户确认后执行。当用户提到「同步脚本到正式环境」「发布脚本」「脚本上线」「推脚本」「检查脚本能不能同步」「补环境变量」「quantum sync」等涉及本地脚本与正式环境（quantum1.678120.cn）对齐的任何场景时使用本 skill，即使用户没有明说"同步"二字（如"把热点速览更新到服务器上"）也应触发。
---

# Quantum 任务脚本同步（本地 → 正式环境）

把用户点名的本地任务脚本安全地发布到正式环境：**安全预检 → 只读计划 → 用户确认 → 执行 → 汇报**。
同步源是用户点名的具体脚本（不做全量对齐），本地脚本根为 `Quantum.API/Quantum.Web/scripts/`（含根目录、`private/`、`demo/`）。

## 前置

- 配置文件 `config.local.json`（与 SKILL.md 同目录，已 gitignore）：target=正式环境，source=本地 dev 实例（localhost:5088，缺环境变量时的取值来源；本地实例没在跑也能工作，只是缺变量值要问用户）。
- 工具脚本：`scripts/quantum_sync.py`（纯 Python 标准库，Windows 可直接跑）。
- 首次使用或凭据可疑时先跑 `ping` 验证连通。

## 标准流程（严格按此编排）

### 第 1 步：定位与预检（不联网）

```bash
python .claude/skills/quantum-sync/scripts/quantum_sync.py check <脚本名...>
```

- 用户点名可省 `.cs`、可只写文件名（`热点速览`、`private/新悦广场签到` 均可）。
- 输出每脚本的安全发现（`blocked`=明确违规 / `warnings`=疑似）与头部声明（环境变量清单、建议 Cron）。
- **出现 blocked：停下来向用户展示问题，问「修复后重试」还是「跳过该脚本」**。不得带 blocked 上传。warnings 向用户提示一句，由用户决定。

### 第 2 步：生成计划（只读远端）

```bash
python .claude/skills/quantum-sync/scripts/quantum_sync.py plan <脚本名...> -o "$env:TEMP\quantum_sync_plan.json"
```

计划包含（全部来自远端实况，不是猜测）：
- `scripts[].action`：`create`（远端无此文件）/ `update`（内容有差异）/ `skip`（已一致）
- `envOps`：脚本声明的变量在远端缺失的清单；`source=local-instance` 表示已从本地实例取到值（展示用 `maskedPreview`，**不要把明文值贴进对话**），`need-user` 表示本地也没有、需要向用户要值
- `tasks[]`：远端任务匹配结果；`existing`=已有任务（**默认不动它的配置**），`proposed`=建议的新任务骨架（Enable 默认 false）

### 第 3 步：向用户展示计划并确认

用中文摘要展示（值打码）：每个脚本的动作与差异规模、要新建的变量（名+打码值+来源）、要建的任务（名称/指令/Cron/是否启用）。**逐项问清**：
- `proposed.command`（触发指令）脚本注释里没有就必须问用户；同时确认 Cron 与「建好后立即启用还是保持停用」。
- `envOps` 中 `need-user` 的变量：问用户要值（多账号给多个值）；用户不给就跳过该变量并在汇报里注明。
- 用户答复后，把最终决定写回计划文件（`values` 填好、`source` 改 `user-provided`；不做的项把 `action` 改成 `skip` 或删掉该项）。

**确认口径：把写操作清单原文复述给用户，得到明确同意（"可以"/"执行"）才进入第 4 步。**

### 第 4 步：执行

```bash
python .claude/skills/quantum-sync/scripts/quantum_sync.py apply "$env:TEMP\quantum_sync_plan.json"
```

- 执行顺序固定：环境变量 → 脚本 → 任务（先补变量，脚本首跑不缺参）。
- 远端还有一道服务端门禁（语法+语义黑名单+Roslyn 编译）：若 `success:false` 且返回 `gate`（blocked/errors），原样转述诊断，**不要重试**，停下来和用户看原因。
- 计划文件含敏感值，apply 成功后脚本会自动删除它；不要把它写进仓库或贴进对话。

### 第 5 步：汇报与（可选）试跑

汇总每项结果。远端门禁的 `warnings` 原样转达。问用户是否要在正式环境跑一次验证：

```bash
python .claude/skills/quantum-sync/scripts/quantum_sync.py run <任务名>
```

（返回 RunId；是否真跑由用户决定，不要擅自执行。）

## 硬规则（违反任何一条都是事故）

1. **apply 只能在用户看到完整写操作清单并明确同意之后执行**。plan/check 是只读的，可随时跑。
2. **环境变量只做「远端完全缺失的变量名 → 无 Id 新增」**。绝不更新、绝不同名追加——正式环境存在「同名多条 = 多账号，执行时按 `&` 合并」的语义（`AddOrUpdateEnvs`：带 Id 更新、同名同值跳过、同名不同值追加），乱写会覆盖/ duplication 多账号配置。远端已有的变量一律不碰。
3. **已有任务只更新脚本内容，不改任务配置**（指令/Cron/启停/推送配置动一下都可能在正式环境产生真实消息推送），除非用户明确要求改某项。
4. 凭据安全：`config.local.json` 已 gitignore；不得把密码、变量明文值写进任何进 git 的文件或对话日志。
5. 脚本源码本身必须满足脱敏规范：无私有地址/账号/密码/Cookie 硬编码（公网公开 API 端点可保留），敏感数据一律环境变量必填读取，缺失即抛错；注释里的内网 IP 用 `192.168.x.x` 占位。`check` 的静态扫描是第一道闸，服务端门禁是第二道，两道都不能绕。

## 已知坑

- **自签证书**：正式环境 HTTPS 证书不受信，脚本已内置豁免（`ssl unverified`）；不要用会校验证书的工具调这些接口。
- **登录限流**：失败次数越多等待越久（10s×失败数）。token 有效 7 天且已缓存到 `%TEMP%\quantum_sync\`，正常不会频繁登录；密码错误时不要连试，问用户。
- **换行口径**：远端写盘会自动补一个尾换行，对比/提交统一「去尾空行 + `\r\n`→`\n`」规范化（脚本已处理）；本地文件保持原样上传，不做整文件换行转换。
- **路径映射**：PUT 的 `path` 相对远端 `scripts/quantum/`。远端已有同名文件时**跟随远端实际路径**（即使本地在别的子目录）；新文件按本地相对路径映射（`private/x.cs` → `private/x.cs`）。
- **任务 FileName** = 相对远端 `scripts/quantum/` 的路径（根目录脚本就是裸文件名）。
- **信封协议**：HTTP 恒 200，成败看体里 `Code`（200 成功 / 401 认证 / 500 业务失败）；`Authorization` 头放**裸 JWT，无 Bearer 前缀**。
- 本地 `scripts/quantum/` 目录是本地 dev 的运行副本（已 gitignore），**不是同步源**；同步源是 `scripts/` 根 + `private/` + `demo/`。
- `demo/` 下是示例脚本，用户一般不同步它们；点名了就照做。

## 失败速查

| 现象 | 处置 |
| --- | --- |
| `check` 退出码 2 | 有 blocked 安全发现，展示给用户，修复或跳过 |
| `plan` 报 401/登录失败 | 确认 `config.local.json` 凭据；连败勿重试（限流） |
| `apply` 返回 `gate`（blocked/errors） | 服务端门禁拒绝，原样转述诊断，不重试 |
| 本地实例取不到变量值 | source 挂了不影响流程，改为向用户要值 |
| 同名脚本远端多路径命中 | plan 会提示，先和用户核对要更新哪个 |
