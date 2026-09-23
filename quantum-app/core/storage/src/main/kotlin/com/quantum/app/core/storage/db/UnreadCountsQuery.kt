package com.quantum.app.core.storage.db

import androidx.sqlite.db.SimpleSQLiteQuery

/**
 * 未读聚合查询构造器（2.2）：把「会话键 → 本地已读水位」映射编译成一条 SQL，
 * 供 [ChatMessageDao.unreadCounts] 以 @RawQuery 执行——未读刷新从「每会话一条 COUNT」
 * 的 N+1 降为一条聚合查询，且 (sessionId, seq) 联合索引（v6）让 JOIN 内层走索引范围扫。
 *
 * SQL 形态（水位以绑定参数成对传入，键值不经字符串拼接，无注入面）：
 * ```sql
 * SELECT m.sessionId AS sessionId, COUNT(*) AS unread
 * FROM chat_message m
 * JOIN (SELECT ? AS sid, ? AS wl UNION ALL SELECT ?, ? ...) w
 *      ON m.sessionId = w.sid
 * WHERE m.pending = 0 AND m.direction = 1 AND m.seq > w.wl
 * GROUP BY m.sessionId
 * ```
 * 与逐会话 `sessionUnread`（COUNT WHERE sessionId=? AND direction=1 AND seq>? AND pending=0）
 * 完全同口径；结果只含未读 ≥ 1 的会话，miss 视为 0。
 */
object UnreadCountsQuery {

    /**
     * 单条聚合查询最多承载的水位条数（§4-13）：每条水位绑定 sid+wl 两个参数，
     * 400 条 = 800 参数，稳妥低于 SQLite 默认 SQLITE_MAX_VARIABLE_NUMBER=999——
     * 会话数超过该阈值时上层按此分块、多次查询再合并，避免参数超限直接报错。
     */
    const val MAX_WATERLINES_PER_QUERY = 400

    /**
     * 水位分块（§4-13）：按迭代序把映射切成每块 ≤ [MAX_WATERLINES_PER_QUERY] 条的有序子映射。
     * 不超限返回单元素列表；空映射返回空列表（调用方短路，不发查询）。
     */
    fun chunks(waterlines: Map<String, Long>): List<Map<String, Long>> {
        if (waterlines.isEmpty()) {
            return emptyList()
        }
        if (waterlines.size <= MAX_WATERLINES_PER_QUERY) {
            return listOf(waterlines)
        }
        val result = ArrayList<Map<String, Long>>()
        var current = LinkedHashMap<String, Long>()
        for ((sid, wl) in waterlines) {
            if (current.size == MAX_WATERLINES_PER_QUERY) {
                result += current
                current = LinkedHashMap()
            }
            current[sid] = wl
        }
        if (current.isNotEmpty()) {
            result += current
        }
        return result
    }

    /** 聚合 SQL（水位条数 = count；参数按 sid,wl,sid,wl... 顺序绑定）。 */
    fun sql(count: Int): String {
        require(count >= 1) { "空水位映射由调用方短路，不发查询" }
        val pairs = buildString {
            append("SELECT ? AS sid, ? AS wl")
            repeat(count - 1) { append(" UNION ALL SELECT ?, ?") }
        }
        return "SELECT m.sessionId AS sessionId, COUNT(*) AS unread " +
            "FROM chat_message m JOIN ($pairs) w ON m.sessionId = w.sid " +
            "WHERE m.pending = 0 AND m.direction = 1 AND m.seq > w.wl " +
            "GROUP BY m.sessionId"
    }

    /** 构造可执行查询（waterlines 迭代序即参数序，LinkedHashMap 保证稳定）。 */
    fun build(waterlines: Map<String, Long>): SimpleSQLiteQuery {
        val args = ArrayList<Any?>(waterlines.size * 2)
        waterlines.forEach { (sid, wl) ->
            args.add(sid)
            args.add(wl)
        }
        return SimpleSQLiteQuery(sql(waterlines.size), args.toTypedArray())
    }

    /**
     * 会话键 × 已读水位映射合并：水位存于 DataStore（sessionReadSeqs），会话行存于 chat_session 表，
     * 两侧取并集——只在会话列表里的键缺省水位 0（该会话全部机器人消息算未读，与逐会话查询口径一致），
     * 只在水位里的键（消息行已删但水位未清） JOIN 不到行，自然不出结果。
     */
    fun waterlines(sessionKeys: Iterable<String>, readSeqs: Map<String, Long>): Map<String, Long> {
        val merged = LinkedHashMap<String, Long>()
        sessionKeys.forEach { merged[it] = 0L }
        readSeqs.forEach { (sid, seq) -> merged[sid] = seq }
        return merged
    }
}
