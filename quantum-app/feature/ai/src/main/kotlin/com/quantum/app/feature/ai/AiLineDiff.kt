package com.quantum.app.feature.ai

/**
 * 行级 LCS diff（Kotlin 移植 Web quantum-web/src/utils/lineDiff.js，语义逐条对齐，单测同款口径）：
 * - splitLines：统一换行符、忽略末尾空行、空串→空列表、"\n"→[""]
 * - 剥公共前后缀 → 中段 LCS DP（回溯需要完整 DP 表，Int 每格；规模受 MAX_DP_CELLS 封顶）
 * - 中段格子数超 2,000,000 退化为整块删除+新增（truncated=true）
 */
object AiLineDiff {

    /** 中段 DP 规模上限（与 Web MAX_DP_CELLS 一致）：超出即退化整块替换。 */
    const val MAX_DP_CELLS: Long = 2_000_000L

    enum class RowKind { Same, Add, Del }

    data class DiffRow(
        val kind: RowKind,
        /** 1-based 旧侧行号（Same/Del 有值）。 */
        val oldLine: Int?,
        /** 1-based 新侧行号（Same/Add 有值）。 */
        val newLine: Int?,
        val text: String
    )

    data class DiffResult(
        val rows: List<DiffRow>,
        val added: Int,
        val removed: Int,
        val truncated: Boolean
    )

    fun splitLines(text: String): List<String> {
        if (text.isEmpty()) return emptyList()
        val normalized = text.replace("\r\n", "\n").replace('\r', '\n')
        val parts = normalized.split('\n').toMutableList()
        // 末尾空行不算差异：尾部换行 pop 掉（"\n" → [""]）
        if (parts.isNotEmpty() && parts.last().isEmpty()) {
            parts.removeAt(parts.size - 1)
        }
        return parts
    }

    fun diffLines(base: String, new: String): DiffResult {
        val a = splitLines(base)
        val b = splitLines(new)

        // 剥公共前缀
        var start = 0
        while (start < a.size && start < b.size && a[start] == b[start]) start++
        // 剥公共后缀（不越过前缀）
        var endA = a.size
        var endB = b.size
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) {
            endA--
            endB--
        }

        val rows = ArrayList<DiffRow>(maxOf(a.size, b.size))
        for (i in 0 until start) {
            rows += DiffRow(RowKind.Same, i + 1, i + 1, a[i])
        }

        val midA = a.subList(start, endA)
        val midB = b.subList(start, endB)
        var truncated = false
        var added = 0
        var removed = 0

        if (midA.size.toLong() * midB.size > MAX_DP_CELLS) {
            // 超规模退化：整块删除 + 整块新增
            for (i in midA.indices) {
                rows += DiffRow(RowKind.Del, start + i + 1, null, midA[i])
            }
            for (j in midB.indices) {
                rows += DiffRow(RowKind.Add, null, start + j + 1, midB[j])
            }
            truncated = true
        } else {
            val back = lcsBacktrack(midA, midB)
            // 回溯产出的是中段逆序行，倒着取出；行号是中段内 0-based 索引，+1 换整段 1-based
            for (row in back.reversed()) {
                rows += when (row.kind) {
                    RowKind.Same -> DiffRow(RowKind.Same, start + row.oldLine!! + 1, start + row.newLine!! + 1, row.text)
                    RowKind.Del -> DiffRow(RowKind.Del, start + row.oldLine!! + 1, null, row.text)
                    RowKind.Add -> DiffRow(RowKind.Add, null, start + row.newLine!! + 1, row.text)
                }
            }
        }

        // 公共后缀行：旧侧 1-based = i+1；新侧行号要补上中段两侧的长度差（插入/删除导致的整体错位）
        val suffixOffset = endB - endA
        for (i in endA until a.size) {
            rows += DiffRow(RowKind.Same, i + 1, i + suffixOffset + 1, a[i])
        }

        for (row in rows) {
            when (row.kind) {
                RowKind.Add -> added++
                RowKind.Del -> removed++
                RowKind.Same -> Unit
            }
        }
        return DiffResult(rows, added, removed, truncated)
    }

    /**
     * 中段 LCS 回溯：dp[i][j] = midA[0..i) 与 midB[0..j) 的 LCS 长度。
     * 返回行序为 (m,n) 回溯到 (0,0) 的**逆序**（调用方 reversed），行号是中段内 0-based，
     * 由调用方换算为整段 1-based。
     */
    private fun lcsBacktrack(midA: List<String>, midB: List<String>): List<DiffRow> {
        val m = midA.size
        val n = midB.size
        val dp = IntArray((m + 1) * (n + 1))
        for (i in 1..m) {
            val rowBase = i * (n + 1)
            val prevBase = (i - 1) * (n + 1)
            for (j in 1..n) {
                dp[rowBase + j] = if (midA[i - 1] == midB[j - 1]) {
                    dp[prevBase + j - 1] + 1
                } else {
                    maxOf(dp[prevBase + j], dp[rowBase + j - 1])
                }
            }
        }
        val out = ArrayList<DiffRow>(m + n)
        var i = m
        var j = n
        while (i > 0 && j > 0) {
            when {
                midA[i - 1] == midB[j - 1] -> {
                    out += DiffRow(RowKind.Same, i - 1, j - 1, midA[i - 1])
                    i--
                    j--
                }
                // 优先删旧行（与 Web 的回溯方向一致，保证相同输入产出相同 diff）
                dp[(i - 1) * (n + 1) + j] >= dp[i * (n + 1) + j - 1] -> {
                    out += DiffRow(RowKind.Del, i - 1, null, midA[i - 1])
                    i--
                }
                else -> {
                    out += DiffRow(RowKind.Add, null, j - 1, midB[j - 1])
                    j--
                }
            }
        }
        while (i > 0) {
            out += DiffRow(RowKind.Del, i - 1, null, midA[i - 1])
            i--
        }
        while (j > 0) {
            out += DiffRow(RowKind.Add, null, j - 1, midB[j - 1])
            j--
        }
        return out
    }
}
