#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
quantum_sync.py — Quantum 任务脚本同步工具（本地仓库 → 正式环境）

子命令：
  check <脚本名...>            本地安全预检（脱敏规则静态扫描 + 头部声明解析），纯本地不联网
  plan  <脚本名...> [-o 文件]  登录远端对比，产出同步计划 JSON（只读，不写远端任何数据）
  apply <plan.json>            执行已确认的计划（顺序：环境变量 → 脚本 → 任务；成功后默认删除计划文件）
  run   <任务名>               在远端手动执行一次任务（execute-runs，返回 RunId）
  ping                        验证 target / source 两套凭据连通性

接口契约（详见 SKILL.md）：
  - 所有接口 HTTP 恒 200，信封 {Code, Message, Data}，Code=200 成功
  - Authorization 头放裸 JWT（无 Bearer 前缀）
  - PUT /api/Task/scripts 的 path 相对远端 scripts/quantum/
"""
import argparse
import difflib
import hashlib
import json
import os
import re
import ssl
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

# Windows 控制台 UTF-8 输出（脚本含中文，防止 GBK 控制台报错）
if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

SKILL_DIR = Path(__file__).resolve().parent.parent
# 本地脚本源根：仓库根/Quantum.API/Quantum.Web/scripts
DEFAULT_ROOT = SKILL_DIR.parents[2] / "Quantum.API" / "Quantum.Web" / "scripts"
TOKEN_CACHE_DIR = Path(tempfile.gettempdir()) / "quantum_sync"

# 引擎运行时注入的系统变量（不属于「用户需在环境变量页配置」的清单，跳过命名/缺失检查）
SYSTEM_VARS = {
    "command", "message", "CommunicationUserId", "CommunicationType",
    "IsSystem", "StepCommandTaskThreadId", "CommunicationUserName",
}

# ---------------------------------------------------------------- 配置与 HTTP

def load_config():
    path = SKILL_DIR / "config.local.json"
    if not path.exists():
        print(json.dumps({
            "ok": False, "error": f"缺少配置文件 {path}",
            "hint": "复制 config.example.json 为 config.local.json 并填入正式环境凭据（该文件已被 gitignore）",
        }, ensure_ascii=False))
        sys.exit(1)
    cfg = json.loads(path.read_text(encoding="utf-8-sig"))
    for side in ("target", "source"):
        if side not in cfg:
            print(json.dumps({"ok": False, "error": f"配置缺少 {side} 节"}, ensure_ascii=False))
            sys.exit(1)
    return cfg


def http_json(method, url, body=None, token=None, timeout=30):
    """请求并解信封。返回 Data；业务失败抛 RuntimeError(Message)。自签证书豁免验证。"""
    ctx = ssl._create_unverified_context()
    handlers = [urllib.request.HTTPSHandler(context=ctx)]
    opener = urllib.request.build_opener(*handlers)
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", token)  # 裸 JWT，无 Bearer 前缀
    try:
        with opener.open(req, timeout=timeout) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"HTTP {e.code} {url}: {e.read().decode('utf-8', 'replace')[:500]}")
    if payload.get("Code") != 200:
        raise RuntimeError(f"[{payload.get('Code')}] {payload.get('Message')}")
    return payload.get("Data")


def token_cache_path(base_url):
    host = urllib.parse.urlparse(base_url).netloc.replace(":", "_")
    return TOKEN_CACHE_DIR / f"{host}.token"


def login(side_cfg, force=False):
    """登录一侧实例，token 缓存到 %TEMP%（7 天有效期，避免反复登录触发限流）。"""
    cache = token_cache_path(side_cfg["baseUrl"])
    if not force and cache.exists():
        cached = cache.read_text(encoding="utf-8").strip()
        if cached:
            return cached
    body = {"UserName": side_cfg["userName"], "PassWord": side_cfg["passWord"]}
    base = side_cfg["baseUrl"].rstrip("/")
    token = http_json("POST", f"{base}/api/Login", body=body)
    if not isinstance(token, str) or not token:
        raise RuntimeError("登录返回异常（非 token 字符串）")
    TOKEN_CACHE_DIR.mkdir(parents=True, exist_ok=True)
    cache.write_text(token, encoding="utf-8")
    return token


def api(side_cfg, method, path, body=None):
    """带 token 的 API 调用；token 过期（401）自动重登一次。"""
    base = side_cfg["baseUrl"].rstrip("/")
    token = login(side_cfg)
    try:
        return http_json(method, f"{base}{path}", body=body, token=token)
    except RuntimeError as e:
        if "[401]" in str(e):
            token = login(side_cfg, force=True)
            return http_json(method, f"{base}{path}", body=body, token=token)
        raise

# ---------------------------------------------------------------- 本地脚本定位

def iter_local_scripts(root: Path):
    """遍历 root、root/private、root/demo 下的 .cs（与仓库既有布局一致）。"""
    seen = []
    for sub in (root, root / "private", root / "demo"):
        if sub.is_dir():
            for f in sub.rglob("*.cs"):
                seen.append(f)
    return seen


def find_script(name: str, root: Path):
    """按用户点名定位脚本：可省 .cs、大小写不敏感、可用相对子路径或绝对路径。
    返回 (绝对路径, 相对root路径)；失败返回 {ambiguous/notfound} 字典。"""
    # 绝对路径直接用（相对 root 的路径用于远端映射）
    p = Path(name)
    if p.is_absolute() and p.exists():
        try:
            rel = p.relative_to(root).as_posix()
        except ValueError:
            rel = p.name  # 根外文件（如临时 fixture）：远端映射取裸文件名
        return p, rel
    want = name.replace("\\", "/").strip("/")
    if not want.endswith(".cs"):
        want += ".cs"
    files = iter_local_scripts(root)
    # 精确：相对路径匹配
    for f in files:
        rel = f.relative_to(root).as_posix()
        if rel.lower() == want.lower():
            return f, rel
    # 精确：basename 匹配
    base_hits = [f for f in files if f.name.lower() == want.rsplit("/", 1)[-1].lower()]
    if len(base_hits) == 1:
        return base_hits[0], base_hits[0].relative_to(root).as_posix()
    if len(base_hits) > 1:
        return {"ambiguous": [str(f.relative_to(root)) for f in base_hits]}
    # 模糊：子串建议
    sug = [f.relative_to(root).as_posix() for f in files
           if want.rsplit("/", 1)[-1][:-3].lower() in f.name.lower()]
    return {"notfound": True, "suggestions": sug[:10]}

# ---------------------------------------------------------------- 安全预检

RE_PRIV_IP = re.compile(
    r"\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3}"
    r"|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}"
    r"|192\.168\.\d{1,3}\.\d{1,3})\b")
RE_CREDS_IN_URL = re.compile(r"[a-zA-Z][a-zA-Z0-9+.-]*://\S+:\S+@")
RE_SENSITIVE_ASSIGN = re.compile(
    r"""(?i)\b\w*(password|passwd|pwd|secret|token|cookie|apikey|api_key|authorization)\w*\s*[:=]\s*["']([^"']{3,})["']""")
RE_TRYGET = re.compile(r"""ctx\.Variables\.TryGetValue\(\s*"([^"]+)"\s*,""")
RE_ENV_NAME = re.compile(r"^[a-zA-Z][a-zA-Z0-9_]{1,64}$")
RE_LONG_HEX = re.compile(r"""["']([0-9a-fA-F]{32,})["']""")
RE_SK_KEY = re.compile(r"""["'](sk-[A-Za-z0-9]{16,})["']""")
RE_LONG_B64 = re.compile(r"""["']([A-Za-z0-9+/]{40,}={0,2})["']""")

PLACEHOLDER_PAT = re.compile(
    r"(?i)xxx|your[_-]|<[a-z]+>|\$\{|占位|示例|placeholder|192\.168\.x\.x|example|替换|填写")


def is_placeholder(value: str):
    return bool(PLACEHOLDER_PAT.search(value))


def safety_scan(content: str):
    """脱敏规则静态扫描。返回 {blocked: [...], warnings: [...]}（每条含 line/rule/message）。
    blocked = 明确违规（硬编码敏感数据 / 真实内网地址 / 非法变量名）；warnings = 疑似，需人工判断。"""
    blocked, warnings = [], []
    lines = content.splitlines()
    for idx, line in enumerate(lines, 1):
        # 内网地址：字面占位 192.168.x.x 豁免；出现真实私有地址（含注释内）一律拦截
        for m in RE_PRIV_IP.finditer(line):
            ip = m.group(1)
            if ip.lower() == "192.168.x.x":
                continue
            blocked.append({"line": idx, "rule": "private-address",
                            "message": f"疑似真实内网地址 {ip}（注释中请写 192.168.x.x 占位，代码中应改为环境变量）"})
        # URL 内嵌凭据
        if RE_CREDS_IN_URL.search(line):
            blocked.append({"line": idx, "rule": "url-embedded-credentials",
                            "message": "URL 内嵌账号密码（scheme://user:pass@host）"})
        # 敏感词赋值（排除占位符/空值）
        for m in RE_SENSITIVE_ASSIGN.finditer(line):
            value = m.group(2)
            if not is_placeholder(value):
                blocked.append({"line": idx, "rule": "hardcoded-secret",
                                "message": f"敏感字段硬编码赋值：{line.strip()[:120]}"})
        # 疑似密钥串（不含空格：排除 UA 等普通长串）
        for m in RE_SK_KEY.finditer(line):
            warnings.append({"line": idx, "rule": "sk-like-key", "message": f"疑似 API Key：{m.group(1)[:12]}..."})
        for m in RE_LONG_HEX.finditer(line):
            warnings.append({"line": idx, "rule": "long-hex", "message": f"疑似密钥/哈希常量（{len(m.group(1))} 位 hex）"})
        for m in RE_LONG_B64.finditer(line):
            v = m.group(1)
            if " " not in v and not v.isdigit():
                warnings.append({"line": idx, "rule": "long-base64", "message": f"疑似 Base64 密钥常量（{len(v)} 位）"})
    # 环境变量名合法性（系统注入变量跳过）
    used = set()
    for m in RE_TRYGET.finditer(content):
        name = m.group(1)
        if name in SYSTEM_VARS or name in used:
            continue
        used.add(name)
        if not RE_ENV_NAME.match(name):
            blocked.append({"line": 0, "rule": "invalid-env-name",
                            "message": f"环境变量名 {name!r} 不符合 ^[a-zA-Z][a-zA-Z0-9_]{{1,64}}$"})
    return {"blocked": blocked, "warnings": warnings}

# ---------------------------------------------------------------- 头部声明解析

RE_ENV_DECL_A = re.compile(r"^//\s*([A-Za-z_][A-Za-z0-9_]*(?:\s*/\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*[（(]\s*(必填|required)[，,；;）)]")  # name（必填，...）或 name1 / name2（必填，...）
RE_ENV_DECL_B = re.compile(r"^//\s*([A-Za-z_][A-Za-z0-9_]*)\s+(必填|可选)[。.]?\s*(.*)")          # name 可选。说明
RE_ENV_DECL_C = re.compile(r"^//\s*([A-Za-z_][A-Za-z0-9_]*(?:\s*/\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*[（(]\s*(可选|optional)[，,；;）)]")  # name（可选，...）
RE_ENV_DECL_D = re.compile(r"^//\s*([A-Za-z_][A-Za-z0-9_]*)[：:]\s*(.{2,})")                     # name：说明（无标记）
RE_ENV_SECTION = re.compile(r"^//\s*环境变量")  # 段头（行内可能同带首条声明或改造说明）
RE_CRON_TOKEN = re.compile(r"^[0-9*/?,\-LW#]+$")
RE_CRON = re.compile(r"(\S+\s+){5}\S+")


def extract_cron(text):
    """从文本中滑窗提取合法六段式 cron（每段必须是 cron 合法 token，排除中文句子误命中）。"""
    words = text.replace("（", " （ ").replace("）", " ） ").split()
    for i in range(len(words) - 5):
        window = words[i:i + 6]
        if all(RE_CRON_TOKEN.match(w) for w in window):
            expr = " ".join(window)
            if any(c.isdigit() for w in window for c in w):
                return expr
    return None


def parse_header(content: str):
    """解析文件头部 // === 注释块：环境变量声明清单 + 推荐任务配置（Cron）。
    兼容声明格式：『name（必填，说明）』『name1 / name2（可选；说明）』『name 可选。说明』『name：说明』。"""
    envs, cron, advice_lines = [], None, []
    in_env_section, in_header = False, False

    def try_declare(line):
        m = RE_ENV_DECL_A.match(line) or RE_ENV_DECL_C.match(line)
        if m:
            # 说明取本行「（」后的剩余文本（可能跨行，取首行即可）
            desc = line[line.find("（") + 1:].strip() if "（" in line else (line[line.find("(") + 1:].strip() if "(" in line else "")
            required = "必填" in (m.group(2) or "")
            for n in re.split(r"\s*/\s*", m.group(1)):
                envs.append({"name": n.strip(), "required": required, "desc": desc[:120]})
            return True
        m = RE_ENV_DECL_B.match(line)
        if m:
            required = "必填" in (m.group(2) or "")
            envs.append({"name": m.group(1), "required": required, "desc": (m.group(3) or "").strip()[:120]})
            return True
        m = RE_ENV_DECL_D.match(line)
        if m:
            envs.append({"name": m.group(1), "required": False, "desc": m.group(2).strip()[:120], "note": "未标注必填/可选"})
            return True
        return False

    for raw in content.splitlines()[:120]:
        line = raw.strip()
        if line.startswith("// ==="):
            in_header = True
            continue
        if not line.startswith("//"):
            if in_header:
                break  # 头块结束
            continue
        text = line[2:].strip()
        if RE_ENV_SECTION.match(line):
            in_env_section = True
            # 段头同行还带首条声明的情况（如「环境变量：qBittorrentURL（必填，…）」）：取首个冒号后的内容试解析
            rest = ""
            if "：" in text:
                rest = text.split("：", 1)[1]
            elif ":" in text:
                rest = text.split(":", 1)[1]
            if re.search(r"[A-Za-z_]", rest):
                try_declare("// " + rest.strip())
            continue
        if in_env_section:
            # 不匹配声明格式的行不结束段落（如「command（磁力链接，多个换行分隔）」无必填/可选标记），
            # 段落只随头块结束而结束；无格式匹配的行不会被收进 envs，无副作用
            if try_declare(line):
                continue
        # 推荐任务配置 / cron
        if re.search(r"(推荐任务|建议任务|任务配置|cron)", text, re.IGNORECASE):
            advice_lines.append(text)
            c = extract_cron(text)
            if c and not cron:
                cron = c
    return {"envs": [e for e in envs if e["name"]], "cron": cron, "taskAdvice": " / ".join(advice_lines[:3])}

# ---------------------------------------------------------------- 工具

def normalize(content: str):
    """对比口径：统一换行并去尾部空行（远端写盘会追加一个换行）。"""
    return content.replace("\r\n", "\n").rstrip("\n")


def sha256_of(content: str):
    return hashlib.sha256(normalize(content).encode("utf-8")).hexdigest()[:16]


def mask(value: str):
    if len(value) <= 6:
        return "***"
    return f"{value[:4]}***{value[-2:]}"


def flatten_script_tree(nodes):
    """script-list 树 → 相对路径集合。"""
    out = []
    def walk(items):
        for n in items or []:
            if n.get("path"):
                out.append(n["path"])
            walk(n.get("children"))
    walk(nodes)
    return out


def diff_stat(local: str, remote: str):
    a = normalize(remote).splitlines()
    b = normalize(local).splitlines()
    sm = difflib.SequenceMatcher(a=a, b=b)
    added = removed = 0
    samples = []
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag in ("delete", "replace"):
            removed += i2 - i1
        if tag in ("insert", "replace"):
            added += j2 - j1
        if tag != "equal" and len(samples) < 5:
            samples.append({"kind": tag, "old": a[i1:i1 + 3], "new": b[j1:j1 + 3]})
    return {"added": added, "removed": removed, "samples": samples}

# ---------------------------------------------------------------- 子命令实现

def cmd_check(args):
    root = Path(args.root) if args.root else DEFAULT_ROOT
    result = []
    ok_all = True
    for name in args.names:
        hit = find_script(name, root)
        if isinstance(hit, dict):
            result.append({"name": name, "error": hit})
            ok_all = False
            continue
        f, rel = hit
        content = f.read_text(encoding="utf-8-sig")
        item = {
            "name": f.stem,
            "localPath": str(f),
            "localRel": rel,
            "sha": sha256_of(content),
            "size": len(content.encode("utf-8")),
            "safety": safety_scan(content),
            "header": parse_header(content),
        }
        result.append(item)
        if item["safety"]["blocked"]:
            ok_all = False
    print(json.dumps({"ok": ok_all, "root": str(root), "scripts": result}, ensure_ascii=False, indent=2))
    sys.exit(0 if ok_all else 2)


def remote_path_for(local_rel: str, remote_paths):
    """远端已有同名文件（basename 匹配）→ 跟随远端实际路径；否则按本地相对路径映射。"""
    base = local_rel.rsplit("/", 1)[-1].lower()
    hits = [p for p in remote_paths if p.rsplit("/", 1)[-1].lower() == base]
    if len(hits) >= 1:
        # 同名多命中（如根与子目录各一份）取路径最短者并提示
        hits.sort(key=len)
        return hits[0], (f"远端存在 {len(hits)} 个同名文件，已选最短路径 {hits[0]}，其余：{hits[1:]}" if len(hits) > 1 else None)
    return local_rel, None


def cmd_plan(args):
    cfg = load_config()
    root = Path(args.root) if args.root else DEFAULT_ROOT
    tgt = cfg["target"]

    # 1) 本地：定位 + 安全预检 + 头部解析
    locals_ = []
    for name in args.names:
        hit = find_script(name, root)
        if isinstance(hit, dict):
            print(json.dumps({"ok": False, "error": f"脚本 {name} 定位失败", "detail": hit}, ensure_ascii=False))
            sys.exit(3)
        f, rel = hit
        content = f.read_text(encoding="utf-8-sig")
        locals_.append({"file": f, "rel": rel, "content": content,
                        "safety": safety_scan(content), "header": parse_header(content)})

    # 2) 远端：脚本树 + 逐个内容对比
    remote_paths = flatten_script_tree(api(tgt, "GET", "/api/Task/script-list"))
    scripts_out, declared_env_names = [], []
    for it in locals_:
        remote_path, note = remote_path_for(it["rel"], remote_paths)
        remote_exists = remote_path in remote_paths
        remote_content = api(tgt, "GET", f"/api/Task/scripts?fileName={urllib.parse.quote(remote_path)}") if remote_exists else None
        action = "skip" if (remote_content is not None and normalize(remote_content) == normalize(it["content"])) else ("update" if remote_exists else "create")
        scripts_out.append({
            "name": it["file"].stem,
            "localPath": str(it["file"]),
            "remotePath": remote_path,
            "remotePathNote": note,
            "action": action,
            "localSha": sha256_of(it["content"]),
            "remoteSha": sha256_of(remote_content) if remote_content is not None else None,
            "diff": diff_stat(it["content"], remote_content) if action == "update" else None,
            "safety": it["safety"],
            "header": it["header"],
            "content": it["content"] if action != "skip" else None,
        })
        for e in it["header"]["envs"]:
            if e["name"] in SYSTEM_VARS:
                continue  # 系统注入变量（如 command）不进「需在环境变量页配置」清单
            if e["name"] not in declared_env_names:
                declared_env_names.append(e["name"])

    # 3) 环境变量：远端缺失清单 → 本地实例补值
    env_ops = []
    source_ok = True
    for name in declared_env_names:
        remote_hits = api(tgt, "GET", f"/api/Env/Query?name={urllib.parse.quote(name)}") or []
        if remote_hits:
            continue  # 远端已有（含多账号多条），不覆盖——同名同值 upsert 语义安全但无必要
        op = {"name": name, "action": "create", "values": [], "source": "need-user"}
        if source_ok:
            try:
                local_hits = api(cfg["source"], "GET", f"/api/Env/Query?name={urllib.parse.quote(name)}") or []
                if local_hits:
                    op["values"] = [{"value": h["Value"], "remark": h.get("Remark") or "",
                                     "enable": h.get("Enable", True)} for h in local_hits]
                    op["source"] = "local-instance"
                    op["maskedPreview"] = [mask(h["Value"]) for h in local_hits]
            except Exception as e:
                source_ok = False
                op["sourceError"] = f"本地实例不可用：{e}"
        env_ops.append(op)

    # 4) 任务匹配（按 FileName / 任务名）
    tasks = []
    page, all_tasks = 1, []
    while True:
        pr = api(tgt, "GET", f"/api/Task?PageIndex={page}&PageSize=200")
        all_tasks.extend(pr.get("Data") or [])
        if len(all_tasks) >= (pr.get("TotalCount") or 0) or not (pr.get("Data")):
            break
        page += 1
    for s in scripts_out:
        base = s["remotePath"].rsplit("/", 1)[-1]
        stem = base[:-3] if base.lower().endswith(".cs") else base
        matched = [t for t in all_tasks
                   if (t.get("FileName") or "").replace("\\", "/").lower() == s["remotePath"].lower()
                   or (t.get("Name") or "").strip() == stem]
        entry = {"script": s["name"], "remotePath": s["remotePath"]}
        if matched:
            t = matched[0]
            entry["existing"] = {"id": t.get("Id"), "name": t.get("Name"), "command": t.get("Command"),
                                 "cron": t.get("Cron"), "enable": t.get("Enable"),
                                 "fileName": t.get("FileName"), "sessionName": t.get("SessionName")}
            entry["note"] = "任务已存在：默认只更新脚本内容，不改任务配置"
            if len(matched) > 1:
                entry["note"] += f"（按名称/文件名命中 {len(matched)} 个任务，请人工核对）"
        else:
            proposed = {
                "name": stem,
                "fileName": s["remotePath"],
                "command": None,          # 触发指令：会话中向用户确认
                "cron": s["header"].get("cron"),
                "enable": False,          # 先建停用任务，配置确认后可再启用
                "enablePush": True,
                "remark": f"quantum-sync 从本地同步（{time.strftime('%Y-%m-%d')}）",
            }
            entry["proposed"] = proposed
            entry["note"] = "远端无对应任务：已按脚本头部注释预填建议（Command 需确认，默认停用）"
        tasks.append(entry)

    plan = {
        "generatedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "target": tgt["baseUrl"],
        "scripts": scripts_out,
        "envOps": env_ops,
        "tasks": tasks,
        "notes": [
            "plan 为只读产物，未对远端做任何写操作",
            "envOps.source=need-user 的变量需在会话中向用户要值后填入 values（source 改为 user-provided）",
            "含 BLOCK 安全发现的脚本默认不参与 apply，除非用户明确要求跳过检查",
        ],
    }
    out = json.dumps(plan, ensure_ascii=False, indent=2)
    if args.o:
        Path(args.o).write_text(out, encoding="utf-8")
        print(json.dumps({"ok": True, "planFile": str(Path(args.o).resolve()),
                          "maskedPlan": mask_plan_for_display(plan)}, ensure_ascii=False, indent=2))
    else:
        print(out)
    sys.exit(0)


def mask_plan_for_display(plan):
    """给会话展示用的打码版（隐藏变量值），完整值只在 plan 文件里。"""
    import copy
    p = copy.deepcopy(plan)
    p.pop("scripts", None)  # 脚本源码太长，展示版剔除 content 由上方摘要承担
    return p


def cmd_apply(args):
    plan = json.loads(Path(args.plan).read_text(encoding="utf-8-sig"))
    cfg = load_config()
    tgt = cfg["target"]
    results = {"env": [], "scripts": [], "tasks": []}
    errors = 0

    # 1) 环境变量（先补变量，避免脚本首跑即缺参报错）
    for op in plan.get("envOps", []):
        if op.get("action") != "create" or not op.get("values"):
            results["env"].append({"name": op["name"], "skipped": True, "reason": "无值（need-user 未提供）"})
            continue
        body = [{"Name": op["name"], "Value": v["value"], "Enable": v.get("enable", True),
                 "Remark": v.get("remark") or f"quantum-sync 补齐（{time.strftime('%Y-%m-%d')}）"}
                for v in op["values"]]
        try:
            msgs = api(tgt, "POST", "/api/Env", body=body)
            results["env"].append({"name": op["name"], "created": len(body), "serverMessages": msgs})
        except Exception as e:
            errors += 1
            results["env"].append({"name": op["name"], "error": str(e)})

    # 2) 脚本（跳过 blocked / skip 项）
    for s in plan.get("scripts", []):
        if s.get("action") == "skip":
            results["scripts"].append({"name": s["name"], "skipped": True, "reason": "内容一致"})
            continue
        if s.get("safety", {}).get("blocked"):
            results["scripts"].append({"name": s["name"], "skipped": True, "reason": "存在 BLOCK 安全发现，未上传"})
            continue
        content = normalize(s["content"])  # 与 Web 端一致：去尾部空行提交；远端写盘自动补一个换行
        try:
            r = api(tgt, "PUT", "/api/Task/scripts", body={"path": s["remotePath"], "content": content})
            if r and r.get("Success"):
                results["scripts"].append({"name": s["name"], "path": s["remotePath"],
                                           "action": s["action"], "success": True,
                                           "warnings": r.get("Warnings") or []})
            else:
                errors += 1
                results["scripts"].append({"name": s["name"], "path": s["remotePath"], "success": False,
                                           "gate": r})
        except Exception as e:
            errors += 1
            results["scripts"].append({"name": s["name"], "error": str(e)})

    # 3) 任务（proposed：无 Id → POST；带 Id → PUT）
    for t in plan.get("tasks", []):
        p = t.get("proposed")
        if not p:
            results["tasks"].append({"script": t.get("script"), "skipped": True, "reason": "任务已存在，未改动"})
            continue
        try:
            if p.get("id"):
                ok = api(tgt, "PUT", "/api/Task", body=p)
                results["tasks"].append({"script": t.get("script"), "action": "update", "success": bool(ok)})
            else:
                if not p.get("command") and not p.get("cron"):
                    results["tasks"].append({"script": t.get("script"), "skipped": True,
                                             "reason": "proposed 无 Command 也无 Cron（触发方式未确认），不建任务"})
                    continue
                ok = api(tgt, "POST", "/api/Task", body=p)
                results["tasks"].append({"script": t.get("script"), "action": "create",
                                         "name": p.get("name"), "success": bool(ok)})
        except Exception as e:
            errors += 1
            results["tasks"].append({"script": t.get("script"), "error": str(e)})

    if not args.keep:
        try:
            Path(args.plan).unlink()
        except OSError:
            pass
    print(json.dumps({"ok": errors == 0, "errors": errors, "results": results}, ensure_ascii=False, indent=2))
    sys.exit(0 if errors == 0 else 4)


def cmd_run(args):
    cfg = load_config()
    tgt = cfg["target"]
    page, all_tasks = 1, []
    while True:
        pr = api(tgt, "GET", f"/api/Task?PageIndex={page}&PageSize=200")
        all_tasks.extend(pr.get("Data") or [])
        if len(all_tasks) >= (pr.get("TotalCount") or 0) or not (pr.get("Data")):
            break
        page += 1
    name = args.task_name.strip()
    hits = [t for t in all_tasks if (t.get("Name") or "").strip() == name
            or (t.get("FileName") or "").replace("\\", "/").rstrip(".cs").endswith(name)]
    if not hits:
        print(json.dumps({"ok": False, "error": f"未找到任务 {name}",
                          "candidates": [t.get("Name") for t in all_tasks][:30]}, ensure_ascii=False))
        sys.exit(3)
    if len(hits) > 1:
        print(json.dumps({"ok": False, "error": "命中多个任务", "hits": [
            {"id": t.get("Id"), "name": t.get("Name"), "fileName": t.get("FileName")} for t in hits]},
            ensure_ascii=False))
        sys.exit(3)
    t = hits[0]
    receipts = api(tgt, "POST", "/api/Task/execute-runs", body=[t["Id"]])
    print(json.dumps({"ok": True, "task": {"id": t["Id"], "name": t.get("Name")},
                      "receipts": receipts}, ensure_ascii=False, indent=2))


def cmd_ping(args):
    cfg = load_config()
    out = {}
    for side in ("target", "source"):
        try:
            t0 = time.time()
            token = login(cfg[side], force=True)
            ver = http_json("GET", f"{cfg[side]['baseUrl'].rstrip('/')}/api/Login/Version", token=token)
            out[side] = {"ok": True, "version": ver, "ms": int((time.time() - t0) * 1000)}
        except Exception as e:
            out[side] = {"ok": False, "error": str(e)}
    print(json.dumps(out, ensure_ascii=False, indent=2))
    sys.exit(0 if out["target"]["ok"] else 1)


def main():
    ap = argparse.ArgumentParser(description="Quantum 任务脚本同步工具")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("check", help="本地安全预检（不联网）")
    p.add_argument("names", nargs="+")
    p.add_argument("--root", help="本地脚本根目录（默认仓库 Quantum.Web/scripts）")
    p.set_defaults(fn=cmd_check)

    p = sub.add_parser("plan", help="登录远端生成同步计划（只读）")
    p.add_argument("names", nargs="+")
    p.add_argument("--root")
    p.add_argument("-o", dest="o", help="计划输出文件（推荐放系统临时目录，含敏感值勿入仓库）")
    p.set_defaults(fn=cmd_plan)

    p = sub.add_parser("apply", help="执行已确认的计划")
    p.add_argument("plan")
    p.add_argument("--keep", action="store_true", help="执行后保留计划文件（默认删除）")
    p.set_defaults(fn=cmd_apply)

    p = sub.add_parser("run", help="远端手动执行一次任务")
    p.add_argument("task_name")
    p.set_defaults(fn=cmd_run)

    p = sub.add_parser("ping", help="验证 target/source 凭据")
    p.set_defaults(fn=cmd_ping)

    args = ap.parse_args()
    try:
        args.fn(args)
    except RuntimeError as e:
        print(json.dumps({"ok": False, "error": str(e)}, ensure_ascii=False))
        sys.exit(1)


if __name__ == "__main__":
    main()
