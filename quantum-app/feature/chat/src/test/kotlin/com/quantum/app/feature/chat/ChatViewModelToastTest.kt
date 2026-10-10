package com.quantum.app.feature.chat

import com.quantum.app.core.common.ApiException
import com.quantum.app.core.media.PlayerManager
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.network.ws.AppWsClient
import com.quantum.app.core.network.ws.WsGateway
import com.quantum.app.core.network.ws.WsState
import com.quantum.app.core.storage.db.ChatMessageDao
import com.quantum.app.core.storage.db.NotificationDao
import com.quantum.app.core.storage.prefs.PrefsStore
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * F4 回归（对抗审计）：`ChatViewModel.toast` 此前无消费方，发送失败/限流/删除失败一律不可见；
 * 失败的 outbox 行也无重发入口（`resendFailed()` 无调用方）。
 * 本组用例锁定 ViewModel 契约：失败必须置 toast（可被 Snackbar 消费）且标记可重发，
 * `consumeToast()` 清除；`resendFailed()` 走统一发送核并正确清标记。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class ChatViewModelToastTest {

    private val dispatcher = StandardTestDispatcher()

    private lateinit var chatRepository: ChatRepository
    private lateinit var wsClient: AppWsClient
    private lateinit var sessionManager: SessionManager
    private lateinit var adminApi: AdminApi
    private lateinit var notificationDao: NotificationDao
    private lateinit var prefsStore: PrefsStore
    private lateinit var imageLoader: coil.ImageLoader
    private lateinit var playerManager: PlayerManager

    @Before
    fun setUp() {
        Dispatchers.setMain(dispatcher)
        chatRepository = mockk(relaxed = true)
        wsClient = mockk(relaxed = true)
        sessionManager = mockk(relaxed = true)
        adminApi = mockk(relaxed = true)
        notificationDao = mockk(relaxed = true)
        prefsStore = mockk(relaxed = true)
        imageLoader = mockk(relaxed = true)
        playerManager = mockk(relaxed = true)

        // ChatViewModel 构造期会挂帧流/查询会话标题/拉快捷指令：给真值避免收集器挂起
        every { wsClient.frames } returns MutableSharedFlow()
        every { wsClient.state } returns MutableStateFlow(WsState.Disconnected)
        coEvery { adminApi.taskDetail(any()) } returns EnvelopeDto(code = 200, data = null)
        coEvery { adminApi.commands(any(), any(), any()) } returns EnvelopeDto(code = 200, data = null)
        coEvery { notificationDao.byMsgIds(any()) } returns flowOf(emptyList())
        coEvery { chatRepository.syncAll() } returns 0L
        coEvery { chatRepository.sessionMessages(any(), any(), any()) } returns flowOf(emptyList())
        coEvery { chatRepository.sessionDisplayTitle(any()) } returns null
        coEvery { chatRepository.sessionMaxSeq(any()) } returns null
        coEvery { chatRepository.unreadCount() } returns 0L
    }

    @After
    fun tearDown() {
        Dispatchers.resetMain()
    }

    private fun viewModel(sessionId: String = "task-9"): ChatViewModel =
        ChatViewModel(
            chatRepository, wsClient, sessionManager, adminApi, notificationDao, prefsStore,
            androidx.lifecycle.SavedStateHandle(mapOf("sessionId" to sessionId)),
            imageLoader, playerManager
        )

    @Test
    fun `send failure sets toast and marks resendable`() = runTest(dispatcher) {
        coEvery { chatRepository.sendCommand(any(), any(), any(), any(), any()) } returns "发送失败，请检查网络后重试"
        val vm = viewModel()

        vm.send("查看任务")
        advanceUntilIdle()

        assertEquals("失败文案必须写入 toast（UI 消费）", "发送失败，请检查网络后重试", vm.toast.value)
        assertTrue("失败后必须提供重发入口", vm.sendFailed.value)
    }

    @Test
    fun `send success clears failure mark and leaves toast null`() = runTest(dispatcher) {
        coEvery { chatRepository.sendCommand(any(), any(), any(), any(), any()) } returns null
        val vm = viewModel()

        vm.send("查看任务")
        advanceUntilIdle()

        assertNull("成功不得弹提示", vm.toast.value)
        assertFalse(vm.sendFailed.value)
    }

    @Test
    fun `send exception maps ApiException message to toast`() = runTest(dispatcher) {
        coEvery { chatRepository.sendCommand(any(), any(), any(), any(), any()) } throws ApiException(500, "请求频繁，请稍后重试！")
        val vm = viewModel()

        vm.send("连点指令")
        advanceUntilIdle()

        assertEquals("请求频繁，请稍后重试！", vm.toast.value)
        assertTrue(vm.sendFailed.value)
    }

    @Test
    fun `consumeToast clears toast`() = runTest(dispatcher) {
        coEvery { chatRepository.sendCommand(any(), any(), any(), any(), any()) } returns "发送失败，请检查网络后重试"
        val vm = viewModel()
        vm.send("x")
        advanceUntilIdle()

        vm.consumeToast()

        assertNull("consumeToast 后 Snackbar 不再重复展示", vm.toast.value)
    }

    @Test
    fun `ws error frame surfaces server rejection text`() = runTest(dispatcher) {
        // 服务端拒收/限流帧（error）：必须让用户看见，不能当发送成功
        val frames = MutableSharedFlow<com.quantum.app.core.network.ws.WsFrame>()
        every { wsClient.frames } returns frames
        val vm = viewModel()
        advanceUntilIdle() // 先让 ViewModel 的 frames 收集器订阅上（SharedFlow 无 replay）

        frames.emit(com.quantum.app.core.network.ws.WsFrame(type = "error", content = "请求频繁，请稍后重试！"))
        advanceUntilIdle()

        assertEquals("请求频繁，请稍后重试！", vm.toast.value)
    }

    @Test
    fun `deleteSession failure sets toast`() = runTest(dispatcher) {
        coEvery { chatRepository.deleteSession(any()) } throws ApiException(500, "删除失败，请重试")
        val vm = viewModel()

        vm.deleteSession()
        advanceUntilIdle()

        assertEquals("删除失败，请重试", vm.toast.value)
    }

    @Test
    fun `default session delete sets guidance toast without repository call`() = runTest(dispatcher) {
        val vm = viewModel(sessionId = "")

        vm.deleteSession()
        advanceUntilIdle()

        assertEquals("默认会话不可删除，可使用「清空」清理聊天记录", vm.toast.value)
        coVerify(exactly = 0) { chatRepository.deleteSession(any()) }
    }

    // ---------------------------------------------------------------- resendFailed（F4：此前无 UI 入口）

    @Test
    fun `resendFailed resends and clears failure mark on success`() = runTest(dispatcher) {
        coEvery { chatRepository.sendCommand(any(), any(), any(), any(), any()) } returns "发送失败，请检查网络后重试"
        coEvery { chatRepository.resendFailed(any()) } returns null
        val vm = viewModel()
        vm.send("待重发")
        advanceUntilIdle()
        assertTrue("前置：失败标记已置位", vm.sendFailed.value)

        vm.resendFailed()
        advanceUntilIdle()

        coVerify { chatRepository.resendFailed("task-9") }
        assertFalse("重发成功应清除失败标记（Snackbar 动作收起）", vm.sendFailed.value)
    }

    @Test
    fun `resendFailed failure keeps mark and toasts`() = runTest(dispatcher) {
        coEvery { chatRepository.resendFailed(any()) } returns "发送失败，请检查网络后重试"
        val vm = viewModel()

        vm.resendFailed()
        advanceUntilIdle()

        assertEquals("发送失败，请检查网络后重试", vm.toast.value)
        assertTrue("重发仍失败应保持可重发", vm.sendFailed.value)
    }

    @Test
    fun `resendFailed with nothing pending is a no-op`() = runTest(dispatcher) {
        coEvery { chatRepository.resendFailed(any()) } returns null
        val vm = viewModel()

        vm.resendFailed()
        advanceUntilIdle()

        assertNull("无失败项时不得弹提示", vm.toast.value)
        assertFalse(vm.sendFailed.value)
    }
}
