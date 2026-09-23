package com.quantum.app.core.common

import java.net.URLEncoder

/**
 * 通知深链路由表（与服务端 AppPushService 约定一致）：
 * quantum://chat（默认回退）、quantum://notify、quantum://session/{taskId}（直达任务会话）、
 * quantum://task/{id}/log、quantum://mine/devices、quantum://ai（AI 助手会话列表）
 */
data class DeepLink(val route: String) {

    companion object {
        const val SCHEME = "quantum"
        const val CHAT = "quantum://chat"
        const val NOTIFY = "quantum://notify"
        const val AI = "quantum://ai"

        fun taskLog(taskId: String) = "quantum://task/$taskId/log"

        /** 解析 jump 深链为 NavHost 路由；未注册前缀一律回退会话页。 */
        fun resolve(jump: String?): DeepLink {
            if (jump.isNullOrBlank() || !jump.startsWith("$SCHEME://")) {
                return DeepLink(NAV_CHAT_ROUTE)
            }
            val known = listOf("chat", "notify", "task", "session", "mine", "ai")
            val body = jump.removePrefix("$SCHEME://")
            return if (known.any { body == it || body.startsWith("$it/") }) {
                DeepLink(routeFor(body))
            } else {
                DeepLink(NAV_CHAT_ROUTE)
            }
        }

        /** NavHost 路由（非深链原串）：chat/notify/task/{id}/log/mine/... */
        private const val NAV_CHAT_ROUTE = "chat"

        /** mine 域已注册子路由白名单（须与 AppRoot NavHost 注册同步）。 */
        private val KNOWN_MINE_ROUTES = setOf("mine/devices")

        /**
         * 只放行已知形态的 NavHost 路由；未知子路径（mine/x）、多段 taskId（task/a/b/log）、
         * 空 taskId（task//log）等一律回退会话页——未注册路由会让 navController.navigate
         * 抛 IllegalArgumentException（quantum:// 为 BROWSABLE，网页可远程触发崩溃）。
         */
        private fun routeFor(body: String): String {
            val segments = body.split('/')
            return when {
                body == "chat" || body == "notify" -> NAV_CHAT_ROUTE
                // AI 助手会话列表（底部「AI助手」tab 根页；站内通知「AI 已更新脚本…」点按直达，2026-09-21）
                body == "ai" -> "ai"
                // session/{sessionId}：本地通知点按直达对应会话（键须为单段非空）。
                // 键原文可能是中文会话名或含 / ? # % 等字符——必须编码进 route：
                // 未编码时非法字符会让 navigate 匹配失败/参数被 Uri 层误解；
                // Compose Navigation 取参走 Uri.getPathSegments（自动 decode），还原回原文
                segments.size == 2 && segments[0] == "session" && segments[1].isNotEmpty() ->
                    "chat/${encodeSegment(segments[1])}"
                // task/{taskId}/log：taskId 必须为单段非空
                segments.size == 3 && segments[0] == "task" && segments[2] == "log" && segments[1].isNotEmpty() ->
                    "task/${encodeSegment(segments[1])}/log"
                body in KNOWN_MINE_ROUTES -> body
                else -> NAV_CHAT_ROUTE
            }
        }

        /**
         * route 路径段编码（RFC 3986 百分号编码）：URLEncoder 是 form 编码（空格→+），
         * 换回 %20 后即为合法 path 段编码；decode 端由导航库 getPathSegments 统一完成。
         */
        private fun encodeSegment(raw: String): String =
            URLEncoder.encode(raw, Charsets.UTF_8.name()).replace("+", "%20")
    }
}
