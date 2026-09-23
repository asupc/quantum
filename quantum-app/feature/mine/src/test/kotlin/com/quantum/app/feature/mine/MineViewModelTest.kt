package com.quantum.app.feature.mine

import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.network.dto.AppDeviceDto
import com.quantum.app.core.network.dto.NotifySettingDto
import com.quantum.app.core.network.dto.ScanLoginRequest
import com.quantum.app.core.network.dto.UnbindDeviceRequest
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.network.ws.AppWsClient
import com.quantum.app.core.network.ws.WsState
import com.quantum.app.core.storage.prefs.PrefsStore
import com.quantum.app.core.storage.prefs.TokenStore
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
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
 * §4-15：MineViewModel 状态机单元测试（JVM + mockk，无需设备/Robolectric）。
 * 覆盖设备列表、通知偏好的服务端成功/本地镜像兜底、保存先落本地再上报、
 * 解绑后重载、扫码授权成败、toast 消费等分支——正是 P7 报告里"缺测试护网"的判定逻辑。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class MineViewModelTest {

    private val dispatcher = StandardTestDispatcher()

    private lateinit var appApi: AppApi
    private lateinit var sessionManager: SessionManager
    private lateinit var tokenStore: TokenStore
    private lateinit var prefsStore: PrefsStore
    private lateinit var wsClient: AppWsClient

    @Before
    fun setUp() {
        Dispatchers.setMain(dispatcher)
        appApi = mockk(relaxed = true)
        sessionManager = mockk(relaxed = true)
        tokenStore = mockk(relaxed = true)
        prefsStore = mockk(relaxed = true)
        wsClient = mockk(relaxed = true)
        // 构造期 stateIn 会订阅这三个 Flow，给真值避免收集器挂起
        every { wsClient.state } returns MutableStateFlow(WsState.Disconnected)
        every { tokenStore.userName } returns flowOf("小明")
        every { prefsStore.biometricLock } returns flowOf(false)
    }

    @After
    fun tearDown() {
        Dispatchers.resetMain()
    }

    private fun viewModel() = MineViewModel(appApi, sessionManager, tokenStore, prefsStore, wsClient)

    private fun device(id: String) = AppDeviceDto(
        id = id,
        deviceId = id,
        deviceName = "dev-$id",
        lastSeenAt = "2026-09-19 00:00:00",
        createTime = "2026-09-19 00:00:00"
    )

    @Test
    fun `loadDeviceList success populates devices`() = runTest(dispatcher) {
        val list = listOf(device("d1"), device("d2"))
        coEvery { appApi.devices() } returns EnvelopeDto(code = 200, data = list)
        val vm = viewModel()
        vm.loadDeviceList()
        advanceUntilIdle()
        assertEquals(list, vm.devices.value)
        assertNull(vm.toast.value)
    }

    @Test
    fun `loadDeviceList failure sets toast from ApiException message`() = runTest(dispatcher) {
        coEvery { appApi.devices() } returns EnvelopeDto(code = 500, message = "服务开小差了")
        val vm = viewModel()
        vm.loadDeviceList()
        advanceUntilIdle()
        assertEquals("服务开小差了", vm.toast.value)
        assertTrue(vm.devices.value.isEmpty())
    }

    @Test
    fun `loadNotifyPrefs success uses server value`() = runTest(dispatcher) {
        val server = NotifySettingDto(taskPush = false, systemPush = true, securityPush = false, dndStart = "23:00", dndEnd = "07:00")
        coEvery { appApi.notifySetting() } returns EnvelopeDto(code = 200, data = server)
        val vm = viewModel()
        vm.loadNotifyPrefs()
        advanceUntilIdle()
        assertEquals(server, vm.notifyPrefs.value)
        assertNull(vm.toast.value)
    }

    @Test
    fun `loadNotifyPrefs failure falls back to local mirror and toasts`() = runTest(dispatcher) {
        coEvery { appApi.notifySetting() } returns EnvelopeDto(code = 500, message = "x")
        every { prefsStore.taskPush } returns flowOf(false)
        every { prefsStore.systemPush } returns flowOf(true)
        every { prefsStore.securityPush } returns flowOf(false)
        every { prefsStore.dndStart } returns flowOf("22:30")
        every { prefsStore.dndEnd } returns flowOf("06:15")
        val vm = viewModel()
        vm.loadNotifyPrefs()
        advanceUntilIdle()
        val prefs = vm.notifyPrefs.value
        assertFalse(prefs.taskPush)
        assertTrue(prefs.systemPush)
        assertFalse(prefs.securityPush)
        assertEquals("22:30", prefs.dndStart)
        assertEquals("06:15", prefs.dndEnd)
        assertEquals("通知偏好读取失败，已展示本地生效的设置", vm.toast.value)
    }

    @Test
    fun `saveNotifyPrefs writes local mirror first then uploads`() = runTest(dispatcher) {
        val uploaded = NotifySettingDto(taskPush = true, systemPush = false, securityPush = true, dndStart = "23:00", dndEnd = null)
        val cap = slot<NotifySettingDto>()
        coEvery { appApi.updateNotifySetting(capture(cap)) } returns EnvelopeDto(code = 200, data = uploaded)
        val vm = viewModel()
        vm.saveNotifyPrefs(uploaded)
        advanceUntilIdle()
        // 界面立即反映
        assertEquals(uploaded, vm.notifyPrefs.value)
        // 本地镜像已写
        coVerify {
            prefsStore.saveNotifyPrefs(
                task = true,
                system = false,
                security = true,
                dndStart = "23:00",
                dndEnd = null
            )
        }
        // 服务端已上报，且 toast 提示生效
        assertEquals(uploaded, cap.captured)
        assertEquals("通知偏好已保存，免打扰立即生效", vm.toast.value)
    }

    @Test
    fun `unbindDevice reloads list on success`() = runTest(dispatcher) {
        coEvery { appApi.unbindDevice(any()) } returns EnvelopeDto(code = 200, data = true)
        coEvery { appApi.devices() } returns EnvelopeDto(code = 200, data = listOf(device("kept")))
        val vm = viewModel()
        vm.unbindDevice("gone")
        advanceUntilIdle()
        coVerify { appApi.unbindDevice(UnbindDeviceRequest("gone")) }
        // 解绑成功后自动重载 → devices 更新
        assertEquals(listOf(device("kept")), vm.devices.value)
    }

    @Test
    fun `authorizeScan returns true on success`() = runTest(dispatcher) {
        coEvery { appApi.scanLogin(any()) } returns EnvelopeDto(code = 200, data = true)
        val vm = viewModel()
        assertTrue(vm.authorizeScan("ticket-1"))
        coVerify { appApi.scanLogin(ScanLoginRequest("ticket-1")) }
    }

    @Test
    fun `authorizeScan returns false and toasts on business failure`() = runTest(dispatcher) {
        coEvery { appApi.scanLogin(any()) } returns EnvelopeDto(code = 500, message = "票据已过期")
        val vm = viewModel()
        assertFalse(vm.authorizeScan("stale"))
        assertEquals("票据已过期", vm.toast.value)
    }

    @Test
    fun `authorizeScan maps AuthException message`() = runTest(dispatcher) {
        coEvery { appApi.scanLogin(any()) } returns EnvelopeDto(code = 401, message = "请重新登录")
        val vm = viewModel()
        assertFalse(vm.authorizeScan("x"))
        assertEquals("请重新登录", vm.toast.value)
    }

    @Test
    fun `consumeToast clears toast`() = runTest(dispatcher) {
        coEvery { appApi.devices() } returns EnvelopeDto(code = 500, message = "boom")
        val vm = viewModel()
        vm.loadDeviceList()
        advanceUntilIdle()
        assertEquals("boom", vm.toast.value)
        vm.consumeToast()
        assertNull(vm.toast.value)
    }

    @Test
    fun `setBiometricLock persists to prefsStore`() = runTest(dispatcher) {
        val vm = viewModel()
        vm.setBiometricLock(true)
        advanceUntilIdle()
        coVerify { prefsStore.setBiometricLock(true) }
    }

    @Test
    fun `logout delegates to sessionManager`() = runTest(dispatcher) {
        val vm = viewModel()
        vm.logout()
        advanceUntilIdle()
        coVerify { sessionManager.logout() }
    }

    @Test
    fun `unwrap throws ApiException on null data with code 200`() = runTest(dispatcher) {
        coEvery { appApi.devices() } returns EnvelopeDto(code = 200, data = null)
        val vm = viewModel()
        vm.loadDeviceList()
        advanceUntilIdle()
        // 契约违背路径：200 但 Data 为空 → unwrap 抛 ApiException → toast 展示其 message
        assertEquals("响应数据为空", vm.toast.value)
        assertTrue(vm.devices.value.isEmpty())
    }

    @Suppress("unused")
    private fun sanityApiExceptionMessage() {
        // 确认 ApiException.message 透传（toast 断言依赖它）
        assertEquals("hi", ApiException(500, "hi").message)
    }
}
