// 行级 diff（LCS 动态规划，无第三方依赖）：脚本版本对比与 AI 修复提案 diff 共用。
// 性能约定：先剥离公共前后缀，仅对差异中段做 DP；中段规模超过 MAX_DP_CELLS 时退化为
// 「整块删除 + 整块新增」，保证超大脚本也不会因 DP 表失控（内存 = 4 字节 × 格子数）。

const MAX_DP_CELLS = 2000000

// 按行切分：统一换行符，并忽略「文件末尾多一个换行」造成的空尾行
//（在线保存落盘会补一个换行，不忽略会到处都是假差异）
export function splitLines(text) {
    const normalized = String(text == null ? '' : text).replace(/\r\n/g, '\n').replace(/\r/g, '\n')
    if (normalized === '') {
        return []
    }
    const lines = normalized.split('\n')
    if (lines[lines.length - 1] === '') {
        lines.pop()
    }
    return lines
}

function lcsRows(midA, midB, offset) {
    const n = midA.length
    const m = midB.length
    const width = m + 1
    const dp = new Uint32Array((n + 1) * width)
    for (let i = n - 1; i >= 0; i--) {
        for (let j = m - 1; j >= 0; j--) {
            dp[i * width + j] =
                midA[i] === midB[j]
                    ? dp[(i + 1) * width + (j + 1)] + 1
                    : Math.max(dp[(i + 1) * width + j], dp[i * width + (j + 1)])
        }
    }
    const rows = []
    let i = 0
    let j = 0
    while (i < n && j < m) {
        if (midA[i] === midB[j]) {
            rows.push({ kind: 'same', oldLine: offset + i + 1, newLine: offset + j + 1, text: midA[i] })
            i++
            j++
        } else if (dp[(i + 1) * width + j] >= dp[i * width + (j + 1)]) {
            rows.push({ kind: 'del', oldLine: offset + i + 1, newLine: null, text: midA[i] })
            i++
        } else {
            rows.push({ kind: 'add', oldLine: null, newLine: offset + j + 1, text: midB[j] })
            j++
        }
    }
    while (i < n) {
        rows.push({ kind: 'del', oldLine: offset + i + 1, newLine: null, text: midA[i] })
        i++
    }
    while (j < m) {
        rows.push({ kind: 'add', oldLine: null, newLine: offset + j + 1, text: midB[j] })
        j++
    }
    return rows
}

/**
 * 行级对比。
 * @returns {{rows: Array<{kind:'same'|'add'|'del', oldLine:number|null, newLine:number|null, text:string}>,
 *            added:number, removed:number, truncated:boolean}}
 */
export function diffLines(oldText, newText) {
    const a = splitLines(oldText)
    const b = splitLines(newText)

    let start = 0
    while (start < a.length && start < b.length && a[start] === b[start]) {
        start++
    }
    let endA = a.length
    let endB = b.length
    while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
        endA--
        endB--
    }

    const rows = []
    for (let i = 0; i < start; i++) {
        rows.push({ kind: 'same', oldLine: i + 1, newLine: i + 1, text: a[i] })
    }

    const midA = a.slice(start, endA)
    const midB = b.slice(start, endB)
    let truncated = false
    if (midA.length * midB.length > MAX_DP_CELLS) {
        truncated = true
        midA.forEach((text, i) => rows.push({ kind: 'del', oldLine: start + i + 1, newLine: null, text }))
        midB.forEach((text, i) => rows.push({ kind: 'add', oldLine: null, newLine: start + i + 1, text }))
    } else {
        rows.push(...lcsRows(midA, midB, start))
    }

    for (let i = endA, j = endB; i < a.length; i++, j++) {
        rows.push({ kind: 'same', oldLine: i + 1, newLine: j + 1, text: a[i] })
    }

    return {
        rows,
        added: rows.filter((n) => n.kind === 'add').length,
        removed: rows.filter((n) => n.kind === 'del').length,
        truncated
    }
}
