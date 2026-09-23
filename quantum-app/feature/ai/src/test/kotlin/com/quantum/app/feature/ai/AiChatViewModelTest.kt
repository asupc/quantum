package com.quantum.app.feature.ai

import com.quantum.app.core.network.api.AdminApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicInteger

/**
 * AiChatViewModel 轮询闭环：Running→Succeeded/WaitingConfirm 停轮、乐观消息被服务端回执顶掉、
 * 连续 5 次失败停轮、apply 成功全量重拉刷新卡片（后端回写 Payload 语义）、cancel 幂等。
 * 假后端按 path 路由（启动期 4 个并行请求无固定顺序，不能用响应队列）。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class AiChatViewModelTest {

    private val scheduler = StandardTestDispatcher()
    private val testScope = TestScope(scheduler)

    /** 内存假后端：消息表（有序 Map 树）+ 可切换 run 状态；响应体由 toJson 直接产合法 JSON。 */
    private class FakeBackend : Dispatcher() {
        var runStatus = "Running"
        var failRuns = false
        /** runs/latest 是否返回 run（false = 会话无 run，Data=null）；默认 false 保持既有用例不触发恢复轮询。 */
        var latestRunPresent = false
        val messages = ConcurrentLinkedQueue<LinkedHashMap<String, Any?>>()
        val runRequests = AtomicInteger(0)
        val latestRunRequests = AtomicInteger(0)
        val stepsRequests = AtomicInteger(0)
        val cancelRequests = AtomicInteger(0)
        private val seqBase = AtomicInteger(0)

        private fun nextSeq(): Int = seqBase.incrementAndGet()

        private fun ok(data: Any?): MockResponse = MockResponse().setResponseCode(200)
            .setHeader("Content-Type", "application/json")
            .setBody("""{"Code":200,"Message":"Success","Data":${toJson(data)}}""")

        private fun toJson(value: Any?): String = when (value) {
            null -> "null"
            is String -> "\"" + value
                .replace("\\", "\\\\")
                .replace("\"", "\\\"")
                .replace("\n", "\\n")
                .replace("\r", "\\r")
                .replace("\t", "\\t") + "\""
            is Number, is Boolean -> value.toString()
            is Map<*, *> -> value.entries.joinToString(prefix = "{", postfix = "}") { (k, v) -> """"$k":${toJson(v)}""" }
            is List<*> -> value.joinToString(prefix = "[", postfix = "]", transform = ::toJson)
            else -> "null"
        }

        override fun dispatch(request: RecordedRequest): MockResponse {
            val path = request.requestUrl?.encodedPath ?: "/"
            return when {
                path == "/api/AiAgent/conversations" ->
                    ok(listOf(mapOf("Id" to "c1", "Title" to "测试会话", "AllowEnvValues" to false)))

                path == "/api/AiAgent/messages" ->
                    ok(messages.sortedBy { (it["Seq"] as Number).toLong() })

                path == "/api/AiAgent/chat" -> {
                    val content = runCatching {
                        Json.parseToJsonElement(request.body.readUtf8())
                            .jsonObject["Content"]!!.jsonPrimitive.content
                    }.getOrDefault("")
                    messages += linkedMapOf(
                        "Id" to "srv-user-1", "ConversationId" to "c1", "Seq" to nextSeq(),
                        "Role" to "user", "Kind" to "text", "Content" to content,
                        "CreateTime" to "2026-09-21 10:00:00"
                    )
                    ok(mapOf("RunId" to "r1", "MessageId" to "srv-user-1"))
                }

                path == "/api/AiAgent/runs/latest" -> {
                    latestRunRequests.incrementAndGet()
                    // Data=null 是合法契约（会话无 run），消费侧必须用 unwrapOrNull
                    if (latestRunPresent) {
                        ok(mapOf("Id" to "r1", "Status" to runStatus, "Rounds" to 2, "Progress" to "整理结论…"))
                    } else {
                        ok(null)
                    }
                }

                path == "/api/AiAgent/runs/r1/cancel" -> {
                    cancelRequests.incrementAndGet()
                    // cancel 已结束的 run 返回 Data=false（幂等正常态）
                    ok(runStatus == "Running")
                }

                path == "/api/AiAgent/runs/r1" -> {
                    runRequests.incrementAndGet()
                    if (failRuns) {
                        MockResponse().setResponseCode(200)
                            .setHeader("Content-Type", "application/json")
                            .setBody("""{"Code":500,"Message":"模拟抖动","Data":null}""")
                    } else {
                        ok(mapOf("Id" to "r1", "Status" to runStatus, "Rounds" to 2, "Progress" to "整理结论…"))
                    }
                }

                path == "/api/AiAgent/runs/r1/steps" -> {
                    stepsRequests.incrementAndGet()
                    ok(listOf(mapOf("Seq" to 1, "Kind" to "tool", "Name" to "read_script", "DurationMs" to 30)))
                }

                path == "/api/AiAgent/default-model" ->
                    ok(mapOf("ProviderName" to "中转", "ModelId" to "glm-5.3", "ContextWindow" to 128000, "SupportsTools" to 1))

                path == "/api/Task/script-list" -> ok(emptyList<Any>())

                path == "/api/AiAgent/proposals/p1/apply" -> {
                    // 模拟后端改动②：apply 回写 proposal 消息 Payload（状态→Applied）+ 追加结论文案
                    messages.removeAll { it["Id"] == "msg-proposal" }
                    messages += linkedMapOf(
                        "Id" to "msg-proposal", "ConversationId" to "c1", "Seq" to nextSeq(),
                        "Role" to "assistant", "Kind" to "proposal", "Content" to "修复提案：demo.cs",
                        "Payload" to """{"Id":"p1","FileName":"demo.cs","Summary":"修复","Status":"Applied","TestStatus":"Passed","AppliedVersionId":"abcdef1234567890"}""",
                        "CreateTime" to "2026-09-21 10:00:01"
                    )
                    messages += linkedMapOf(
                        "Id" to "msg-applied", "ConversationId" to "c1", "Seq" to nextSeq(),
                        "Role" to "assistant", "Kind" to "text", "Content" to "已应用 AI 提案并写入脚本 demo.cs",
                        "CreateTime" to "2026-09-21 10:00:02"
                    )
                    ok(mapOf("Success" to true))
                }

                path == "/api/AiAgent/proposals/p1/discard" -> {
                    messages.removeAll { it["Id"] == "msg-proposal" }
                    ok(true)
                }

                path == "/api/AiAgent/contract" -> ok("契约文本")

                else -> MockResponse().setResponseCode(404)
            }
        }
    }

    private lateinit var backend: FakeBackend
    private lateinit var server: MockWebServer
    private lateinit var viewModel: AiChatViewModel

    @Before
    fun setUp() {
        Dispatchers.setMain(scheduler)
        backend = FakeBackend()
        server = MockWebServer()
        server.dispatcher = backend
        server.start()
        val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
        val api = Retrofit.Builder()
            .baseUrl(server.url("/"))
            .client(OkHttpClient())
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()
            .create(AdminApi::class.java)
        viewModel = AiChatViewModel(api)
    }

    @After
    fun tearDown() {
        server.shutdown()
        Dispatchers.resetMain()
    }

    /** 虚拟时钟 + 真实本地 HTTP 混合泵：advanceUntilIdle 推进虚拟 delay，sleep 等网络回合。 */
    private fun pumpUntil(timeoutMs: Long = 8_000, condition: () -> Boolean) {
        val start = System.currentTimeMillis()
        while (!condition() && System.currentTimeMillis() - start < timeoutMs) {
            testScope.advanceUntilIdle()
            if (!condition()) Thread.sleep(15)
        }
        testScope.advanceUntilIdle()
    }

    /** 停轮验证：等一段真实时间，确认对后端的 run 请求数不再增长。 */
    private fun assertNoMoreRunRequests(runsAtStop: Int) {
        val start = System.currentTimeMillis()
        while (System.currentTimeMillis() - start < 300) {
            testScope.advanceUntilIdle()
            Thread.sleep(50)
        }
        assertEquals(runsAtStop, backend.runRequests.get())
    }

    private fun startLoaded() {
        viewModel.start("c1")
        pumpUntil { !viewModel.state.value.loading }
    }

    @Test
    fun send_pollsUntilSucceeded_stopsPolling_andOptimisticReplaced() {
        startLoaded()
        assertEquals(0, viewModel.state.value.messages.size)

        backend.runStatus = "Running"
        viewModel.send("修复 demo.cs", null)
        // 乐观消息立即出现（id=null）
        pumpUntil { viewModel.state.value.messages.isNotEmpty() }
        assertEquals(1, viewModel.state.value.messages.count { it.dto.id == null })

        // 轮询拾取服务端用户回执：乐观被顶掉，用户消息只剩服务端 1 条（按回执 Id 去重）
        pumpUntil { viewModel.state.value.messages.any { it.dto.id == "srv-user-1" } }
        assertEquals(1, viewModel.state.value.messages.count { it.dto.role == "user" })
        assertEquals(0, viewModel.state.value.messages.count { it.dto.id == null })

        // run 转 Succeeded + 追加助手结论 → 停轮、状态条隐藏、结论落齐
        backend.runStatus = "Succeeded"
        backend.messages += linkedMapOf(
            "Id" to "m-done", "ConversationId" to "c1", "Seq" to 99,
            "Role" to "assistant", "Kind" to "text", "Content" to "已修复",
            "CreateTime" to "2026-09-21 10:00:05"
        )
        pumpUntil { viewModel.state.value.run?.status == "Succeeded" }
        pumpUntil { viewModel.state.value.messages.any { it.dto.id == "m-done" } }
        assertFalse(viewModel.state.value.runActive)

        // 停轮后后端再变化也不应产生新的 run 请求
        assertNoMoreRunRequests(backend.runRequests.get())
    }

    @Test
    fun waitingConfirm_stopsPolling_immediately() {
        startLoaded()
        backend.runStatus = "Running"
        viewModel.send("生成提案", null)
        pumpUntil { viewModel.state.value.run?.status == "Running" }
        backend.runStatus = "WaitingConfirm"
        pumpUntil { viewModel.state.value.run?.status == "WaitingConfirm" }
        assertFalse(viewModel.state.value.runActive)
        assertNoMoreRunRequests(backend.runRequests.get())
    }

    @Test
    fun fiveConsecutiveFailures_stopPolling_withToast() {
        startLoaded()
        backend.failRuns = true
        backend.runStatus = "Running"
        viewModel.send("触发轮询", null)
        pumpUntil { viewModel.state.value.toast != null }
        assertEquals("刷新运行状态连续失败，已停止自动刷新", viewModel.state.value.toast)
        assertNull(viewModel.state.value.run)
        assertEquals(AiChatViewModel.POLL_FAIL_LIMIT, backend.runRequests.get())
        assertNoMoreRunRequests(backend.runRequests.get())
    }

    @Test
    fun applyProposal_success_reloadsAndCardShowsApplied() {
        // 预置一条 Pending 提案消息
        backend.messages += linkedMapOf(
            "Id" to "msg-proposal", "ConversationId" to "c1", "Seq" to 1,
            "Role" to "assistant", "Kind" to "proposal", "Content" to "修复提案：demo.cs",
            "Payload" to """{"Id":"p1","FileName":"demo.cs","Summary":"修复","Status":"Pending","TestStatus":"None"}""",
            "CreateTime" to "2026-09-21 10:00:00"
        )
        startLoaded()
        assertEquals("Pending", viewModel.state.value.messages.first().proposal?.status)

        viewModel.applyProposal("p1")
        // Payload 回写后全量重拉：卡片翻转为 Applied（含版本短号）
        pumpUntil { viewModel.state.value.messages.first().proposal?.status == "Applied" }
        assertEquals("abcdef1234567890".take(8), viewModel.state.value.messages.first().proposal?.appliedVersionId?.take(8))
        assertEquals("已应用 AI 提案并写入脚本 demo.cs", viewModel.state.value.messages.last().dto.content)
    }

    @Test
    fun cancelRun_postsCancel_andPollPicksTerminal() {
        startLoaded()
        backend.runStatus = "Running"
        viewModel.send("跑起来", null)
        pumpUntil { viewModel.state.value.run?.status == "Running" }

        backend.runStatus = "Cancelled"
        viewModel.cancelRun()
        pumpUntil { backend.cancelRequests.get() >= 1 }
        pumpUntil { viewModel.state.value.run?.status == "Cancelled" }
        assertFalse(viewModel.state.value.runActive)
    }

    // ---- latest-run 恢复（runs/latest 是运行态恢复的权威来源，取代扫消息找 runId） ----

    @Test
    fun latestRun_running_resumesPollingAndLiveSteps() {
        backend.runStatus = "Running"
        backend.latestRunPresent = true
        startLoaded()

        // 恢复为运行态：run 回填 + 轮询在跑（runs/{id} 请求数持续增长）
        pumpUntil { viewModel.state.value.run?.status == "Running" }
        val atResume = backend.runRequests.get()
        pumpUntil { backend.runRequests.get() > atResume }
        assertTrue(viewModel.state.value.runActive)
        // 实时过程轨道已建（运行中默认展开，轮询增量填充）
        val entry = viewModel.state.value.stepsByRun["r1"]
        assertNotNull(entry)
        assertTrue(entry!!.loaded)
        assertTrue(entry.expanded)

        backend.runStatus = "Succeeded"
        pumpUntil { viewModel.state.value.run?.status == "Succeeded" }
    }

    @Test
    fun latestRun_finished_backfillsRunWithoutPolling() {
        backend.runStatus = "Succeeded"
        backend.latestRunPresent = true
        startLoaded()

        // 非 Running：仅回填 run 供状态展示，不恢复轮询
        pumpUntil { viewModel.state.value.run?.status == "Succeeded" }
        assertFalse(viewModel.state.value.runActive)
        assertNoMoreRunRequests(backend.runRequests.get())
    }

    @Test
    fun latestRun_absent_clearsRunStateSilently() {
        backend.latestRunPresent = false
        startLoaded()

        // 会话无 run（Data=null 经 unwrapOrNull 折叠为 null）：运行态清空、不报错
        pumpUntil { !viewModel.state.value.loading }
        assertNull(viewModel.state.value.run)
    }

    // ---- 内联过程块：历史块首展懒加载、同 run 不重复拉 ----

    @Test
    fun toggleRunSteps_lazyLoadsOnce_andCachesByRun() {
        // 预置带 runId 的 assistant 结论消息（历史回看场景）
        backend.messages += linkedMapOf(
            "Id" to "m-hist", "ConversationId" to "c1", "Seq" to 1,
            "Role" to "assistant", "Kind" to "text", "Content" to "历史结论", "RunId" to "r1",
            "CreateTime" to "2026-09-21 10:00:00"
        )
        startLoaded()

        // 消息加载后即建缓存条目：默认折叠、未加载
        val entry = viewModel.state.value.stepsByRun["r1"]
        assertNotNull(entry)
        assertFalse(entry!!.loaded)
        assertFalse(entry.expanded)

        viewModel.toggleRunSteps("r1") // 展开 → 首次懒加载全量
        pumpUntil { viewModel.state.value.stepsByRun["r1"]?.loaded == true }
        assertEquals(1, viewModel.state.value.stepsByRun["r1"]?.steps?.size)
        assertEquals(1, backend.stepsRequests.get())

        viewModel.toggleRunSteps("r1") // 收起
        assertFalse(viewModel.state.value.stepsByRun["r1"]!!.expanded)
        viewModel.toggleRunSteps("r1") // 再展开：loaded=true 直接命中缓存，不重复拉
        assertTrue(viewModel.state.value.stepsByRun["r1"]!!.expanded)
        pumpUntil(800) { true }
        assertEquals(1, backend.stepsRequests.get())
    }
}
