package com.quantum.app.core.common

import org.junit.Assert.assertEquals
import org.junit.Test

/** 深链路由解析：session/{taskId}（本地通知直达任务会话）与既有回退规则。 */
class DeepLinkTest {

    @Test
    fun sessionDeepLink_MapsToChatDetailRoute() {
        assertEquals("chat/task-abc", DeepLink.resolve("quantum://session/task-abc").route)
    }

    @Test
    fun malformedSessionLinks_FallBackToChat() {
        assertEquals("chat", DeepLink.resolve("quantum://session/").route)
        assertEquals("chat", DeepLink.resolve("quantum://session/a/b").route)
    }

    @Test
    fun aiDeepLink_MapsToAiConversationList() {
        // 站内通知「AI 已更新脚本…」jump=quantum://ai 点按直达 AI 会话列表（2026-09-21）；
        // AI 助手已升级为底部 tab，路由不再挂 manage 前缀
        assertEquals("ai", DeepLink.resolve("quantum://ai").route)
    }

    @Test
    fun aiSubPath_NotRegistered_FallsBackToChat() {
        // 只注册了会话列表根：quantum://ai/xxx 未注册子路径回退会话页（防 navigate 抛未注册路由）
        assertEquals("chat", DeepLink.resolve("quantum://ai/x").route)
    }

    @Test
    fun legacyLinks_Unchanged() {
        assertEquals("chat", DeepLink.resolve("quantum://chat").route)
        assertEquals("chat", DeepLink.resolve("quantum://notify").route)
        assertEquals("chat", DeepLink.resolve(null).route)
        assertEquals("task/t1/log", DeepLink.resolve("quantum://task/t1/log").route)
        assertEquals("chat", DeepLink.resolve("quantum://unknown/x").route)
    }

    @Test
    fun chineseSessionName_IsPercentEncodedIntoRoute() {
        // 会话分组后键为中文会话名：须以百分号编码进 route（未编码非法字符会匹配失败），
        // 导航库 getPathSegments 取参时自动 decode 还原原文
        assertEquals(
            "chat/%E9%9F%B3%E4%B9%90%E6%90%9C%E7%B4%A2",
            DeepLink.resolve("quantum://session/音乐搜索").route
        )
    }

    @Test
    fun specialCharsInSessionName_AreEncoded() {
        // 空格→%20（不能是 +，form 编码语义在 path 段非法）；? # % 编码后不再被 Uri 层误解析。
        // 注意含 / 的键在 resolve 拆段时即回退 chat（jump 生成端约定键不含 /），不在此用例范围
        assertEquals(
            "chat/a%20b%3Fc%23d%25e",
            DeepLink.resolve("quantum://session/a b?c#d%e").route
        )
        // 含 % 的键编码后（%25…）不会被 getPathSegments decode 出原文以外的值
        assertEquals(
            "chat/%E7%99%BE%E5%88%86%E4%B9%8B%E7%99%BE",
            DeepLink.resolve("quantum://session/百分之百").route
        )
    }

    @Test
    fun taskRunDeepLink_MapsToRunDetailRoute() {
        // 告警通知（服务端 TaskAlertService）发 quantum://task/{taskId}/runs/{runId} → 直达执行详情
        assertEquals(
            "task/t1/runs/r1",
            DeepLink.resolve("quantum://task/t1/runs/r1").route
        )
    }

    @Test
    fun taskRunsListDeepLink_MapsToRunListRoute() {
        // 三段形态 = 该任务的最近执行列表页
        assertEquals("task/t1/runs", DeepLink.resolve("quantum://task/t1/runs").route)
    }

    @Test
    fun taskRunDeepLink_WithoutRunId_FallsBackToTaskLog() {
        // 解析不出 runId（空段/多余段）时回退该任务的日志页：通知本来就是「看这次任务怎么了」，
        // 甩回会话页等于把上下文丢了
        assertEquals("task/t1/log", DeepLink.resolve("quantum://task/t1/runs/").route)
        assertEquals("task/t1/log", DeepLink.resolve("quantum://task/t1/runs/r1/r2").route)
        // 第二段（runId 位）为空的畸形四段同样回退
        assertEquals("task/t1/log", DeepLink.resolve("quantum://task/t1/runs/").route)
    }

    @Test
    fun taskRunDeepLink_BlankTaskId_FallsBackToChat() {
        // taskId 本身非法（空段）时无法回退日志页（那也会命中未注册路由），一律会话页
        assertEquals("chat", DeepLink.resolve("quantum://task//runs/r1").route)
        assertEquals("chat", DeepLink.resolve("quantum://task//log").route)
    }

    @Test
    fun taskRunDeepLink_IdsArePercentEncodedIntoRoute() {
        // runId/taskId 含特殊字符须编码进 route（导航库 getPathSegments 取参时自动还原）
        assertEquals(
            "task/t%201/runs/r%232",
            DeepLink.resolve("quantum://task/t 1/runs/r#2").route
        )
    }

    @Test
    fun taskLogDeepLink_StillMapsToLogRoute() {
        // 既有 task/{id}/log 形态不受新增 runs 分支影响
        assertEquals("task/t1/log", DeepLink.resolve("quantum://task/t1/log").route)
        assertEquals("chat", DeepLink.resolve("quantum://task/t1/log/extra").route)
    }
}
