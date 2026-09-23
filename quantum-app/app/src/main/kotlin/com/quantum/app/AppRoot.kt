package com.quantum.app

import android.Manifest
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Chat
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Key
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Login
import androidx.compose.material.icons.filled.Person as PersonFilled
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.TaskAlt
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import com.quantum.app.core.common.ui.components.BottomBarItem
import com.quantum.app.core.common.ui.components.QuantumBottomBar
import com.quantum.app.core.common.ui.components.QuantumCard
import com.quantum.app.core.common.ui.components.QuantumStatusBadge
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.common.DeepLink
import com.quantum.app.core.common.R as CommonR
import com.quantum.app.core.common.ui.theme.QuantumTheme
import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.LoginRequest
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.storage.prefs.CredentialCipher
import com.quantum.app.core.storage.prefs.PrefsStore
import com.quantum.app.core.storage.prefs.RememberedCredentialsPolicy
import com.quantum.app.core.storage.prefs.TokenStore
import com.quantum.app.feature.admin.EnvScreen
import com.quantum.app.feature.admin.SystemSettingScreen
import com.quantum.app.feature.ai.AiChatScreen
import com.quantum.app.feature.ai.AiConversationsScreen
import com.quantum.app.feature.ai.AiDiffScreen
import com.quantum.app.feature.chat.ChatBadgeViewModel
import com.quantum.app.feature.chat.ChatScreen
import com.quantum.app.feature.chat.ConversationListScreen
import com.quantum.app.feature.docker.DockerLogsScreen
import com.quantum.app.feature.docker.DockerScreen
import com.quantum.app.feature.mine.DevicesScreen
import com.quantum.app.feature.mine.MineScreen
import com.quantum.app.feature.mine.ScanScreen
import com.quantum.app.feature.notify.NotifyScreen
import com.quantum.app.feature.task.LogDetailScreen
import com.quantum.app.feature.task.LogsScreen
import com.quantum.app.feature.task.TaskScreen
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import javax.inject.Inject

/** 骨架状态机：服务器地址 → 登录 → 主界面（含深链入口、登出事件跳转）。 */
@HiltViewModel
class ShellViewModel @Inject constructor(
    val tokenStore: TokenStore,
    private val prefsStore: PrefsStore,
    private val sessionManager: SessionManager,
    private val appApi: AppApi,
    private val pushHandler: AppPushHandler,
    private val credentialCipher: CredentialCipher
) : ViewModel() {

    /** 登录页「记住密码」预填载荷。 */
    data class RememberedLogin(
        val userName: String,
        val password: String?,
        val rememberPassword: Boolean
    )

    /** 生物识别门禁（P2）：纯本地开关，冷启/回前台验证指纹或面容。 */
    val biometricLock: StateFlow<Boolean> = prefsStore.biometricLock
        .stateIn(viewModelScope, kotlinx.coroutines.flow.SharingStarted.Eagerly, false)

    private val _probeMessage = MutableStateFlow<String?>(null)
    val probeMessage: StateFlow<String?> = _probeMessage.asStateFlow()

    private val _loggedIn = MutableStateFlow<Boolean?>(null)
    val loggedIn: StateFlow<Boolean?> = _loggedIn.asStateFlow()

    /**
     * 服务器地址快照：由 tokenStore.baseUrl 流预热，登录页组合期只读此值（非阻塞）。
     * 替代组合期 runBlocking 的 currentBaseUrlBlocking()——冷启首帧 DataStore 未就绪时会卡渲染线程。
     */
    private val _baseUrl = MutableStateFlow("")
    val baseUrl: StateFlow<String> = _baseUrl.asStateFlow()

    init {
        viewModelScope.launch {
            tokenStore.loggedIn.collect { _loggedIn.value = it }
        }
        viewModelScope.launch {
            tokenStore.baseUrl.collect { _baseUrl.value = it.orEmpty() }
        }
        viewModelScope.launch {
            sessionManager.events.collect { event ->
                if (event == SessionManager.Event.LoggedOut) {
                    _loggedIn.value = false
                }
            }
        }
    }

    /** 保存服务器地址并探活（GET /api/SystemConfig/footer，匿名）；返回 null=成功。 */
    suspend fun saveBaseUrl(url: String): String? {
        val normalized = url.trim().trimEnd('/')
        val scheme = normalized.substringBefore("://", "").lowercase()
        if (scheme != "http" && scheme != "https") {
            return "服务器地址需以 http:// 或 https:// 开头"
        }
        tokenStore.saveBaseUrl(normalized)
        sessionManager.updateBaseUrl(normalized)
        return probe()
    }

    /**
     * 探活客户端：只建一次（每次点击新建会各自带一套连接池/线程池）。
     * 不走 DI 里那个 OkHttpClient —— 那条链挂了 baseUrl/auth/envelope 三个拦截器，
     * 而探活要的是「这个地址通不通」的裸判断，不该被鉴权与信封解包逻辑影响。
     */
    private val probeClient by lazy {
        okhttp3.OkHttpClient.Builder()
            .callTimeout(java.time.Duration.ofSeconds(10))
            .build()
    }

    /**
     * 探活（GET /api/SystemConfig/footer，匿名）。
     * 必须切到 IO 线程：execute() 是阻塞调用，在主线程会抛 NetworkOnMainThreadException
     * （该异常不带 message），页面表现为恒定的「连接失败：null」——与服务器是否可达无关，
     * 换地址也不会变，曾因此把「服务端不可达」和「探活本身坏了」两件事混在一起。
     */
    suspend fun probe(): String? = withContext(Dispatchers.IO) {
        try {
            val request = okhttp3.Request.Builder()
                .url("${sessionManager.currentBaseUrl}/api/SystemConfig/footer")
                .build()
            probeClient.newCall(request).execute().use { response ->
                if (response.isSuccessful) "连接成功" else "服务返回 HTTP ${response.code}"
            }
        } catch (e: Exception) {
            // message 允许为空（NetworkOnMainThreadException 即无 message），兜底到异常类名
            "连接失败：${e.message ?: e::class.java.simpleName}"
        }.also { _probeMessage.value = it }
    }

    /**
     * App 登录（POST api/AppAuth/login）：存三元组 → 上报厂商注册 id → 镜像通知偏好 →
     * 按勾选状态落盘记住密码（成功才保存；失败不动旧存值）。
     * 返回 null=成功；非 null=错误文案。
     */
    suspend fun login(userName: String, password: String, rememberPassword: Boolean = true): String? = try {
        val baseUrl = tokenStore.currentBaseUrl().orEmpty()
        if (baseUrl.isBlank()) {
            "请先保存服务器地址"
        } else {
            val deviceId = tokenStore.currentDeviceId()
            val result = appApi.login(
                LoginRequest(
                    userName = userName,
                    password = password,
                    deviceId = deviceId,
                    deviceName = android.os.Build.MODEL,
                    platform = "android"
                )
            ).unwrap()
            sessionManager.onLoginSuccess(
                baseUrl = baseUrl,
                userName = userName,
                accessToken = result.accessToken,
                refreshToken = result.refreshToken,
                refreshExpiresAtRaw = result.refreshExpiresAt
            )
            // 建连/补拉必须紧随登录成功同步发起（此后到 return 之间不得有挂起点）：
            // onLoginSuccess 落库会令 loggedIn=true 触发 UI 切主界面、登录页离开组合，
            // 本协程（登录页 rememberCoroutineScope）随时被取消——下方 saveRemembered/mirrorNotifyPrefs
            // 的挂起点抛 CancellationException 时曾把 onLogin() 一起跳过，表现为
            // 「首次登录后 WS 永不连接、会话列表空，重启进程才恢复」
            pushHandler.onLogin()
            tokenStore.saveRemembered(
                userName = userName,
                passwordCipher = RememberedCredentialsPolicy.cipherToStore(rememberPassword, password) {
                    credentialCipher.encrypt(it)
                },
                remember = rememberPassword
            )
            pushHandler.mirrorNotifyPrefs()
            null
        }
    } catch (e: Exception) {
        // CancellationException 是协程取消（UI 离开组合），不是登录失败——必须重抛，
        // 否则取消被吞后协程以「正常返回」收尾，取消传播语义被破坏
        if (e is kotlinx.coroutines.CancellationException) throw e
        (e as? ApiException)?.message ?: "登录失败：${e.message}"
    }

    /**
     * 登录页预填（记住密码）：密文解密失败（换机迁移/Keystore 密钥被删）自愈清除按未保存处理。
     */
    fun loadRememberedLogin(): RememberedLogin {
        val userName = tokenStore.rememberedUserNameBlocking().orEmpty()
        val remember = tokenStore.rememberPasswordBlocking()
        val cipher = tokenStore.rememberedPasswordCipherBlocking()
        val password = if (remember && cipher != null) {
            credentialCipher.decrypt(cipher) ?: run {
                viewModelScope.launch { tokenStore.clearRememberedPassword() }
                null
            }
        } else {
            null
        }
        return RememberedLogin(userName, password, remember)
    }
}

@Composable
fun AppRoot(initialJump: String?) {
    QuantumTheme {
        // 全局深色兜底：未自行绘制背景的页面（登录页/锁定页）不再透出系统窗口底色
        Surface(
            modifier = Modifier.fillMaxSize(),
            color = MaterialTheme.colorScheme.background
        ) {
            AppRootContent(initialJump)
        }
    }
}

/** 骨架状态机内容（背景由 [AppRoot] 的 Surface 统一提供）。 */
@Composable
private fun AppRootContent(initialJump: String?) {
    val shellViewModel: ShellViewModel = hiltViewModel()
    val loggedIn by shellViewModel.loggedIn.collectAsState()
    val biometricLock by shellViewModel.biometricLock.collectAsState()
    val navController = rememberNavController()
    var unlockedThisSession by remember { mutableStateOf(false) }

    val gateContent: @Composable () -> Unit = {
        when (loggedIn) {
            null -> Unit
            false -> ServerAndLoginScreen(shellViewModel)
            true -> {
                // 后台本地通知的硬前提：Android 13+ 通知权限默认拒绝，SystemNotifier 未授权时静默不弹
                NotificationPermissionRequester()
                MainScaffold(shellViewModel, navController, initialJump)
            }
        }
    }

    if (loggedIn == true && biometricLock && !unlockedThisSession) {
        val context = LocalContext.current
        Column(
            modifier = Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                "已锁定",
                style = MaterialTheme.typography.headlineMedium
            )
            Spacer(modifier = Modifier.height(12.dp))
            Button(onClick = { showBiometricPrompt(context) { unlockedThisSession = true } }) {
                Text("使用指纹/面容解锁")
            }
        }
        LaunchedEffect(Unit) {
            showBiometricPrompt(context) { unlockedThisSession = true }
        }
    } else {
        gateContent()
    }
}

/** BiometricPrompt 门禁（需 FragmentActivity；取消/失败保持锁定，可点按钮重试）。 */
private fun showBiometricPrompt(context: android.content.Context, onUnlocked: () -> Unit) {
    val activity = context as? androidx.fragment.app.FragmentActivity ?: return
    val executor = androidx.core.content.ContextCompat.getMainExecutor(activity)
    val prompt = androidx.biometric.BiometricPrompt(
        activity,
        executor,
        object : androidx.biometric.BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: androidx.biometric.BiometricPrompt.AuthenticationResult) {
                onUnlocked()
            }
        }
    )
    val info = androidx.biometric.BiometricPrompt.PromptInfo.Builder()
        .setTitle("解锁量子助手")
        .setAllowedAuthenticators(
            androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_WEAK
                or androidx.biometric.BiometricManager.Authenticators.DEVICE_CREDENTIAL
        )
        .build()
    prompt.authenticate(info)
}

/** 进入主界面即申请通知权限（Android 13+ 默认拒绝；拒绝过系统会记住，不会反复弹窗打扰）。 */
@Composable
private fun NotificationPermissionRequester() {
    val context = LocalContext.current
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    LaunchedEffect(Unit) {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            launcher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }
}

/** 首启「服务器地址」页 + 登录页（凭据 = appsettings 管理员账号，改密在 Web 管理端）。 */
@Composable
private fun ServerAndLoginScreen(shellViewModel: ShellViewModel) {
    val scope = rememberCoroutineScope()
    val persistedBaseUrl by shellViewModel.baseUrl.collectAsState()
    var serverUrl by remember { mutableStateOf(persistedBaseUrl) }
    // 冷启首帧 DataStore 未就绪时快照为空，地址流预热到值后回填一次（用户已输入则不覆盖）
    LaunchedEffect(persistedBaseUrl) {
        if (serverUrl.isBlank() && persistedBaseUrl.isNotBlank()) {
            serverUrl = persistedBaseUrl
        }
    }
    // 记住账号密码预填（密文经 Keystore 解密；解密失败已在 ViewModel 侧自愈清除）
    val remembered by remember { mutableStateOf(shellViewModel.loadRememberedLogin()) }
    var userName by remember { mutableStateOf(remembered.userName) }
    var password by remember { mutableStateOf(remembered.password.orEmpty()) }
    var rememberPassword by remember { mutableStateOf(remembered.rememberPassword) }
    var message by remember { mutableStateOf<String?>(null) }
    val probeMessage by shellViewModel.probeMessage.collectAsState()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Spacer(modifier = Modifier.height(16.dp))

        // 品牌 Hero 区域
        Box(
            modifier = Modifier
                .size(72.dp)
                .clip(RoundedCornerShape(18.dp))
                .background(MaterialTheme.colorScheme.surfaceVariant),
            contentAlignment = Alignment.Center
        ) {
            Image(
                painter = painterResource(id = CommonR.drawable.ic_quantum_logo),
                contentDescription = "量子助手 Logo",
                modifier = Modifier
                    .size(56.dp)
                    .clip(RoundedCornerShape(14.dp))
            )
        }
        Spacer(modifier = Modifier.height(12.dp))
        Text(
            "量子助手",
            style = MaterialTheme.typography.headlineMedium,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onBackground
        )
        Text(
            "QUANTUM · 自动化管理中枢",
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.primary,
            modifier = Modifier.padding(top = 2.dp)
        )

        Spacer(modifier = Modifier.height(28.dp))

        // 服务器配置卡片
        QuantumCard(modifier = Modifier.fillMaxWidth()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        Icons.Default.Dns,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(18.dp)
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(
                        "服务器节点",
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold
                    )
                }
                probeMessage?.let {
                    val isOk = it.contains("成功")
                    QuantumStatusBadge(
                        text = if (isOk) "在线" else "连接异常",
                        active = isOk
                    )
                }
            }

            Spacer(modifier = Modifier.height(10.dp))
            OutlinedTextField(
                value = serverUrl,
                onValueChange = { serverUrl = it },
                label = { Text("服务地址 (http(s)://…)") },
                leadingIcon = {
                    Icon(Icons.Default.Language, contentDescription = null, modifier = Modifier.size(18.dp))
                },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                shape = RoundedCornerShape(10.dp)
            )

            Spacer(modifier = Modifier.height(10.dp))
            Button(
                onClick = { scope.launch { message = shellViewModel.saveBaseUrl(serverUrl) } },
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(10.dp),
                colors = ButtonDefaults.buttonColors(
                    containerColor = MaterialTheme.colorScheme.surfaceVariant,
                    contentColor = MaterialTheme.colorScheme.onSurface
                )
            ) {
                Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(modifier = Modifier.width(6.dp))
                Text("保存并测试连接")
            }
            probeMessage?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.labelSmall,
                    color = if (it.contains("成功")) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(top = 6.dp)
                )
            }
        }

        Spacer(modifier = Modifier.height(16.dp))

        // 账号登录卡片
        QuantumCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Default.Lock,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(18.dp)
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    "身份凭据",
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold
                )
            }

            Spacer(modifier = Modifier.height(10.dp))
            OutlinedTextField(
                value = userName,
                onValueChange = { userName = it },
                label = { Text("用户名") },
                leadingIcon = {
                    Icon(Icons.Default.PersonFilled, contentDescription = null, modifier = Modifier.size(18.dp))
                },
                // 账号按 ASCII 处理：关掉输入法联想/自动更正，避免输入法把账号改成它「觉得对」的样子
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Text,
                    autoCorrectEnabled = false,
                    capitalization = KeyboardCapitalization.None
                ),
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                shape = RoundedCornerShape(10.dp)
            )

            Spacer(modifier = Modifier.height(10.dp))
            OutlinedTextField(
                value = password,
                onValueChange = { password = it },
                label = { Text("密码") },
                leadingIcon = {
                    Icon(Icons.Default.Key, contentDescription = null, modifier = Modifier.size(18.dp))
                },
                visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(),
                // 必须显式声明为密码输入：只加 PasswordVisualTransformation 只影响显示，
                // 输入法仍按普通文本处理（联想/自动更正/首字母大写会静默改掉内容，而圆点显示
                // 让用户完全看不出来），是「同一口令 Web 能登、App 报用户名密码错误」的典型成因。
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Password,
                    autoCorrectEnabled = false,
                    capitalization = KeyboardCapitalization.None
                ),
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                shape = RoundedCornerShape(10.dp)
            )

            // 记住密码（勾选状态本身持久化；密码经 Keystore 加密落盘，成功登录才保存）
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 2.dp)
            ) {
                Checkbox(
                    checked = rememberPassword,
                    onCheckedChange = { rememberPassword = it }
                )
                Text(
                    "记住密码",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(modifier = Modifier.height(16.dp))
            Button(
                onClick = {
                    scope.launch {
                        message = shellViewModel.login(userName, password, rememberPassword)
                    }
                },
                modifier = Modifier.fillMaxWidth().height(48.dp),
                shape = RoundedCornerShape(10.dp)
            ) {
                Icon(Icons.Default.Login, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(modifier = Modifier.width(8.dp))
                Text("登 录", fontWeight = FontWeight.Bold)
            }

            message?.let {
                Text(
                    it,
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 10.dp)
                )
            }
        }

        Spacer(modifier = Modifier.height(20.dp))
        Text(
            "提示：使用 appsettings 配置的管理员账号密码登录；平台未开放自助注册。",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(modifier = Modifier.height(16.dp))
    }
}

/** 底部导航项：QQ 风格平铺底栏渲染 + 目标路由。 */
private data class BottomTab(val item: BottomBarItem, val route: String)

/** 底部五项（会话/AI助手/任务/管理/我的）：描边图标样式见 core:common 的 QuantumBottomBar。 */
private val BottomTabs = listOf(
    BottomTab(BottomBarItem(label = "会话", icon = Icons.AutoMirrored.Outlined.Chat), "chats"),
    BottomTab(BottomBarItem(label = "AI助手", icon = Icons.Outlined.AutoAwesome), "ai"),
    BottomTab(BottomBarItem(label = "任务", icon = Icons.Outlined.TaskAlt), "manage/tasks"),
    BottomTab(BottomBarItem(label = "管理", icon = Icons.Outlined.Settings), "manage"),
    BottomTab(BottomBarItem(label = "我的", icon = Icons.Outlined.Person), "mine")
)

@Composable
private fun MainScaffold(
    shellViewModel: ShellViewModel,
    navController: NavHostController,
    initialJump: String?
) {
    val context = LocalContext.current
    val snackbar = remember { SnackbarHostState() }
    // 底栏「会话」红点：任意会话有超水位未读即点亮（QQ 式；数据与会话列表同源）
    val badgeViewModel: ChatBadgeViewModel = hiltViewModel()
    val chatsTabHasUnread by badgeViewModel.hasUnread.collectAsState()
    val installedVersionCode = remember {
        runCatching {
            context.packageManager.getPackageInfo(context.packageName, 0).longVersionCode.toInt()
        }.getOrDefault(1)
    }

    LaunchedEffect(initialJump) {
        // 通知深链（点按推送进入；onNewIntent 亦经此转发，值变化时重新跳转）
        initialJump?.let { navigateSafely(navController, DeepLink.resolve(it).route) }
    }

    // 会话流通知卡片点按：jump 深链与推送进入走同一解析入口（未注册路由一律回退会话页）
    val openJump: (String) -> Unit = { jump -> navigateSafely(navController, DeepLink.resolve(jump).route) }

    // 当前路由与沉浸页判定：底栏显隐（bottomBar）与悬浮播放层的抬升量（content）共用
    val current = navController.currentBackStackEntryAsState().value?.destination?.route
    // 会话详情页隐藏底部导航（沉浸会话，返回靠页面头部）；其余页面（含会话列表）保留
    val inSessionDetail = current == "chat" || current == "notify" ||
        (current != null && current.startsWith("chat/"))
    // 管理子页与日志/容器详情页同样沉浸（2026-09-20）：进入子页后底部功能按钮隐藏，
    // 返回靠各页统一返回头；任务列表（manage/tasks）是「任务」tab 根页面，保留底栏
    val inManageSubPage = current == "manage/env" || current == "manage/docker" ||
        current == "manage/setting" || current == "manage/logs" ||
        current == "task/{taskId}/log" ||
        (current != null && (current.startsWith("manage/logs/") || current.startsWith("manage/docker/logs/") ||
            current.startsWith("ai/")))

    // 悬浮播放控制（音频续播）：随主界面存活；登出/生物锁切换到锁定页时停止播放
    val miniPlayerViewModel: MiniPlayerViewModel = hiltViewModel()
    DisposableEffect(miniPlayerViewModel) {
        onDispose { miniPlayerViewModel.playerManager.stop() }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            if (!inSessionDetail && !inManageSubPage) QuantumBottomBar(
                    // 「会话」tab 带未读红点，其余 tab 无角标
                    items = BottomTabs.mapIndexed { index, tab ->
                        if (index == 0) tab.item.copy(showBadge = chatsTabHasUnread) else tab.item
                    },
                // 子页归属其所属 tab（ai/chat → AI助手、manage/env → 管理、task/{id}/log → 任务、mine/devices → 我的）
                selectedIndex = when {
                    current == null -> 0
                    current.startsWith("ai") -> 1
                    current.startsWith("manage/tasks") || current.startsWith("task/") -> 2
                    current.startsWith("manage") -> 3
                    current.startsWith("mine") -> 4
                    else -> 0
                },
                // 重复点按当前 tab 根页面：不再压入重复 entry——新 entry 会按 NavBackStackEntry
                // 重建 VM 整页重拉并重放转场，观感即「页面刷新再次进入」；tab 子页（mine/devices 等）
                // 仍可点按回 tab 根。launchSingleTop 兜底同帧极速双击重复压栈。
                onSelect = { index ->
                    val tab = BottomTabs[index]
                    if (current != tab.route) {
                        navController.navigate(tab.route) { launchSingleTop = true }
                    }
                }
            )
        }
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = "chats",
            modifier = Modifier.fillMaxSize().padding(padding),
            // 页面转场（C 档）：新页由右侧轻滑入 + 淡入，返回反向；旧页只淡出，避免两页同时位移的杂乱
            enterTransition = {
                slideInHorizontally(animationSpec = tween(260)) { width -> width / 6 } + fadeIn(tween(200))
            },
            exitTransition = { fadeOut(tween(160)) },
            popEnterTransition = { fadeIn(tween(200)) },
            popExitTransition = {
                slideOutHorizontally(animationSpec = tween(240)) { width -> width / 6 } + fadeOut(tween(200))
            }
        ) {
            // 会话列表（默认首页）：按脚本分会话；点按进入会话详情（底部导航随之隐藏）。
            // 默认会话（空 sessionId）必须走无参路由："chat/" 的空段匹配不上 chat/{sessionId}，
            // navigate 抛的异常又被 runCatching 吞掉——会话打不开、已读水位永不推进，
            // 默认会话的未读只增不减恒 99+（2026-09-18 用户实测复现的根因）。
            // 会话键须编码进 route：会话分组后会键是中文会话名（或含 / ? # % 等字符的自定义名），
            // 未编码的非法字符会让 navigate 匹配失败；导航库取参走 getPathSegments 自动 decode 还原原文
            composable("chats") {
                ConversationListScreen(onOpenSession = { sessionId ->
                    navigateSafely(navController, if (sessionId.isEmpty()) "chat" else "chat/" + Uri.encode(sessionId))
                })
            }
            // 会话详情：底部导航隐藏，返回头退回列表；空 sessionId = 默认会话（深链 quantum://chat 兼容）
            // 跨会话触发迁移自动切换（2026-09-18 批次）：与列表点开同款路由（空键走无参 "chat"、键编码）
            val switchSession: (String) -> Unit = { sessionId ->
                navigateSafely(navController, if (sessionId.isEmpty()) "chat" else "chat/" + Uri.encode(sessionId))
            }
            composable("chat/{sessionId}") { ChatScreen(onOpenJump = openJump, onBack = { navController.popBackStack() }, onSwitchSession = switchSession) }
            composable("chat") { ChatScreen(onOpenJump = openJump, onBack = { navController.popBackStack() }, onSwitchSession = switchSession) }
            composable("notify") { ChatScreen(onOpenJump = openJump, onBack = { navController.popBackStack() }, onSwitchSession = switchSession) }
            composable("manage") { ManageHub(navController) }
            composable("mine") {
                MineScreen(
                    installedVersionCode = installedVersionCode,
                    onOpenDevices = { navController.navigate("mine/devices") },
                    onOpenScan = { navController.navigate("mine/scan") }
                )
            }
            composable("mine/devices") { DevicesScreen() }
            composable("mine/scan") {
                ScanScreen(onBack = { navController.popBackStack() })
            }
            composable("manage/env") { EnvScreen(onBack = { navController.popBackStack() }) }
            composable("manage/setting") {
                SystemSettingScreen(onBack = { navController.popBackStack() })
            }
            composable("manage/tasks") { TaskScreen(onOpenLog = { id -> navController.navigate("task/$id/log") }) }
            composable("manage/logs") {
                LogsScreen(
                    onBack = { navController.popBackStack() },
                    onOpenDetails = { logId -> navController.navigate("manage/logs/$logId") }
                )
            }
            composable("manage/docker") {
                DockerScreen(
                    onBack = { navController.popBackStack() },
                    onOpenLogs = { container ->
                        val name = container.names.firstOrNull()?.removePrefix("/").orEmpty()
                        navController.navigate("manage/docker/logs/${container.id}/${Uri.encode(name)}")
                    }
                )
            }
            // AI 助手（feature:ai，2026-09-21 由管理宫格升级为底部 tab，与会话平级）；
            // 站内通知 quantum://ai 深链直达会话列表；
            // 会话 Id/提案 Id 含特殊字符一律 Uri.encode 进 route（取参侧 getPathSegments 自动还原）
            composable("ai") {
                AiConversationsScreen(
                    onOpen = { id -> navigateSafely(navController, "ai/chat/" + Uri.encode(id)) }
                )
            }
            composable("ai/chat/{conversationId}") { entry ->
                AiChatScreen(
                    conversationId = entry.arguments?.getString("conversationId").orEmpty(),
                    onBack = { navController.popBackStack() },
                    onOpenDiff = { proposalId -> navigateSafely(navController, "ai/diff/" + Uri.encode(proposalId)) },
                    onOpenLog = { logId -> navigateSafely(navController, "manage/logs/$logId") }
                )
            }
            composable("ai/diff/{proposalId}") { entry ->
                AiDiffScreen(
                    proposalId = entry.arguments?.getString("proposalId").orEmpty(),
                    onBack = { navController.popBackStack() }
                )
            }
            // 日志详情页（2026-09-20 由弹层改页面：可滚动 + 可复制）
            composable("manage/logs/{logId}") { entry ->
                LogDetailScreen(
                    logId = entry.arguments?.getString("logId").orEmpty(),
                    onBack = { navController.popBackStack() }
                )
            }
            // 容器日志页（2026-09-20 由弹层改页面）
            composable("manage/docker/logs/{containerId}/{containerName}") { entry ->
                DockerLogsScreen(
                    containerId = entry.arguments?.getString("containerId").orEmpty(),
                    containerName = entry.arguments?.getString("containerName").orEmpty(),
                    onBack = { navController.popBackStack() }
                )
            }
            // 任务日志（通知深链与任务行「日志」按钮共用）：按任务过滤的日志页。
            // 早先这里又挂了一个 TaskScreen —— hiltViewModel 按 NavBackStackEntry 作用域，
            // 新 entry 立刻新建 VM 并重拉第一页，表现就是「点一个任务整页刷新」
            composable("task/{taskId}/log") { entry ->
                LogsScreen(
                    taskId = entry.arguments?.getString("taskId"),
                    onBack = { navController.popBackStack() },
                    onOpenDetails = { logId -> navController.navigate("manage/logs/$logId") }
                )
            }
        }

        // 音频续播悬浮控制：与 NavHost 同层后写者盖上（悬浮层自身不消费未展开时的触摸）。
        // 底栏可见时吃 Scaffold 内边距自然位于底栏上方；沉浸页底栏隐藏需额外抬升——
        // 会话详情页避开聊天输入条，管理子页常规浮起
        MiniPlayerOverlay(
            viewModel = miniPlayerViewModel,
            modifier = Modifier.fillMaxSize(),
            bottomPadding = padding.calculateBottomPadding() + when {
                inSessionDetail -> 92.dp
                inManageSubPage -> 16.dp
                else -> 0.dp
            }
        )
    }
}

/** 深链跳转兜底：resolve 已回退已知路由，这里再包 runCatching 防未注册路由抛 IllegalArgumentException 崩溃。 */
private fun navigateSafely(navController: NavHostController, route: String) {
    runCatching { navController.navigate(route) }
        .onFailure { android.util.Log.w("AppRoot", "deep link route rejected: $route", it) }
}

@Composable
private fun ManageHub(navController: NavHostController) {
    val items = remember {
        listOf(
            // 任务调度入口已移除（2026-09-20 用户要求）：「任务」tab 即 manage/tasks 根页，宫格重复入口收掉
            // AI 助手入口已移除（2026-09-21 用户要求）：升级为底部 tab（与会话平级），宫格重复入口收掉
            ManageItem("日志中心", "系统与审计日志", "manage/logs", Icons.Default.Terminal, Color(0xFF6366F1)),
            ManageItem("环境变量", "凭据与配置参数", "manage/env", Icons.Default.Code, Color(0xFF10B981)),
            ManageItem("Docker", "容器与镜像控制", "manage/docker", Icons.Default.Dns, Color(0xFF0284C7)),
            ManageItem("系统设置", "全局参数与网络", "manage/setting", Icons.Default.Tune, Color(0xFF0284C7))
        )
    }

    LazyVerticalGrid(
        columns = GridCells.Fixed(2),
        modifier = Modifier.fillMaxSize().padding(horizontal = 14.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        item(span = { GridItemSpan(2) }) {
            Column(modifier = Modifier.padding(bottom = 6.dp, top = 4.dp)) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        "管理控制台",
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.Bold
                    )
                    QuantumStatusBadge(text = "系统管理员", active = true)
                }
                Text(
                    "核心服务调度与系统级管理面板",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }

        items(items, key = { it.route }) { item ->
            QuantumCard(
                onClick = { navController.navigate(item.route) },
                modifier = Modifier.fillMaxWidth()
            ) {
                Box(
                    modifier = Modifier
                        .size(40.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(item.color.copy(alpha = 0.15f)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = item.icon,
                        contentDescription = item.title,
                        tint = item.color,
                        modifier = Modifier.size(22.dp)
                    )
                }
                Spacer(modifier = Modifier.height(10.dp))
                Text(
                    item.title,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = MaterialTheme.colorScheme.onSurface
                )
                Text(
                    item.desc,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
    }
}

private data class ManageItem(
    val title: String,
    val desc: String,
    val route: String,
    val icon: androidx.compose.ui.graphics.vector.ImageVector,
    val color: Color
)
