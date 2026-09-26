// 执行记录展示口径（纯函数，vitest 直测）：
// 状态色只描述「执行终态」，绝不代表 HTTP 请求是否成功——UI 不得用颜色反推脚本结果。

export const RunStatusMeta = {
    Succeeded: { label: '成功', color: 'var(--ok)', tone: 'ok' },
    Failed: { label: '执行异常', color: 'var(--danger)', tone: 'danger' },
    Rejected: { label: '已拒绝执行', color: 'var(--danger)', tone: 'danger' },
    Canceled: { label: '已取消', color: 'var(--warn)', tone: 'warn' },
    Interrupted: { label: '中断/未知', color: 'var(--warn)', tone: 'warn' },
    Running: { label: '执行中', color: 'var(--accent)', tone: 'accent' },
    Pending: { label: '待执行', color: 'var(--muted)', tone: 'muted' }
}

export const TriggerSourceLabel = {
    Manual: '手动',
    Cron: '定时',
    Command: '指令',
    OpenTrigger: '外触',
    Shadow: 'AI 试运行',
    Retry: '自动重试'
}

export function runStatusMeta(status) {
    return RunStatusMeta[status] || { label: status || '未知', color: 'var(--muted)', tone: 'muted' }
}

export function triggerSourceLabel(source) {
    return TriggerSourceLabel[source] || source || '-'
}

// 耗时：毫秒级保两位小数，超过 1 分钟按 分+秒 展示；起止时间缺失时不给 0（避免把「未知」画成「0 秒」）
export function formatElapsed(ms) {
    if (ms === null || ms === undefined) return '-'
    const n = Number(ms)
    if (!Number.isFinite(n) || n < 0) return '-'
    if (n < 1000) return `${n} 毫秒`
    if (n < 60000) return `${(n / 1000).toFixed(2)} 秒`
    const minutes = Math.floor(n / 60000)
    const seconds = ((n % 60000) / 1000).toFixed(0)
    return `${minutes} 分 ${seconds} 秒`
}

// UTC → 本地时间串（后端一律持久化 UTC，界面按客户端时区显示）
export function formatUtcToLocal(value) {
    if (!value) return '-'
    const date = new Date(value.endsWith('Z') ? value : `${value}Z`)
    if (Number.isNaN(date.getTime())) return String(value)
    const pad = (n) => String(n).padStart(2, '0')
    return `${date.getFullYear()}/${pad(date.getMonth() + 1)}/${pad(date.getDate())} ` +
        `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

// 失败摘要兜底：没有摘要时只说终态，不猜测原因
export function summaryOr(status, safeSummary) {
    if (safeSummary && safeSummary.trim()) return safeSummary.trim()
    return runStatusMeta(status).label
}

// 执行链是否仍未收口（决定执行记录抽屉要不要继续自动刷新）：活动行 = Pending/Running；
// 已排定下一次重试的失败行以 NextAttemptAtUtc 非空为标记。注意服务端「判失败」与「排定重试」
// 是先后两次写入，刚落库的一瞬可能两头都不满足，故调用方须连续多轮判空才停轮询。
export function hasOpenChain(runs) {
    return (runs || []).some(r => r && (r.Status === 'Pending' || r.Status === 'Running' || !!r.NextAttemptAtUtc))
}

// 策略输入的服务端 clamp 口径在前端同样实现一份（仅用于即时反馈，真值仍以后端返回为准）
export const PolicyLimits = {
    RetryCount: [0, 3],
    BackoffSeconds: [30, 3600],
    AlertAfterConsecutiveFailures: [1, 10],
    CooldownMinutes: [0, 1440]
}

export function clampPolicy(policy) {
    const out = { ...(policy || {}) }
    Object.entries(PolicyLimits).forEach(([key, [min, max]]) => {
        const value = Number(out[key])
        out[key] = Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : min
    })
    out.SendRecovery = !!out.SendRecovery
    out.Enabled = !!out.Enabled
    return out
}

// 重试退避预览：第 n 次尝试按 基数 × 2^(n-1)，封顶 3600 秒
export function backoffSecondsFor(base, attempt) {
    const b = Math.min(PolicyLimits.BackoffSeconds[1], Math.max(PolicyLimits.BackoffSeconds[0], Number(base) || 0))
    const n = Math.max(1, Number(attempt) || 1)
    return Math.min(PolicyLimits.BackoffSeconds[1], b * Math.pow(2, n - 1))
}
