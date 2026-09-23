package com.quantum.app.feature.mine

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Matrix
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.hilt.navigation.compose.hiltViewModel
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumTextField
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import java.util.concurrent.Executors
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * 扫码授权页：调用摄像头实时识别 Web 登录页展示的二维码（CameraX 取景 + ZXing 本地解码，
 * 不依赖 GMS），识别到票据后调 scan-login 完成授权。摄像头被拒/不可用时提供手动输入兜底。
 */
@Composable
fun ScanScreen(
    onBack: () -> Unit,
    viewModel: MineViewModel = hiltViewModel()
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()

    var granted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }
    var showManualInput by remember { mutableStateOf(false) }
    var manualTicket by remember { mutableStateOf("") }
    // 已识别待授权：防止同一帧/后续帧重复解码与重复提交
    var recognized by remember { mutableStateOf<String?>(null) }
    var authorized by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val toast by viewModel.toast.collectAsState()

    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        granted = it
    }
    LaunchedEffect(Unit) {
        if (!granted) {
            permissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    val analyzerExecutor = remember { Executors.newSingleThreadExecutor() }
    DisposableEffect(Unit) {
        onDispose { analyzerExecutor.shutdown() }
    }

    // 相机绑定：PreviewView 常驻（进组合即建），授权后绑定一次；离开页面时解绑
    val previewView = remember { PreviewView(context).apply { scaleType = PreviewView.ScaleType.FILL_CENTER } }
    LaunchedEffect(granted) {
        if (!granted) {
            return@LaunchedEffect
        }
        val provider = awaitProvider(context)
        val preview = Preview.Builder().build().also {
            it.setSurfaceProvider(previewView.surfaceProvider)
        }
        val analysis = ImageAnalysis.Builder()
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .build()
        analysis.setAnalyzer(analyzerExecutor) { image ->
            // 已识别：后续帧直接丢弃，不再走 ZXing 解码（省 CPU）；失败复位 recognized 后自动恢复扫码
            if (recognized != null) {
                image.close()
                return@setAnalyzer
            }
            val ticket = decodeQrOrNull(image)
            image.close()
            if (ticket != null) {
                recognized = ticket
            }
        }
        runCatching {
            provider.unbindAll()
            provider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
        }.onFailure { e ->
            errorMessage = "相机启动失败：${e.message ?: "未知错误"}"
        }
        try {
            awaitCancellation()
        } finally {
            runCatching { provider.unbindAll() }
        }
    }

    Box(modifier = Modifier.fillMaxSize().background(Color.Black)) {
        if (granted) {
            AndroidView(factory = { previewView }, modifier = Modifier.fillMaxSize())
        } else {
            Column(
                modifier = Modifier.fillMaxSize().padding(QuantumSpacing.Xl),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    "需要相机权限才能扫码",
                    color = Color.White,
                    style = MaterialTheme.typography.titleSmall,
                    textAlign = TextAlign.Center
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Md))
                QuantumButton(
                    text = "授予权限",
                    onClick = { permissionLauncher.launch(Manifest.permission.CAMERA) },
                    variant = QuantumButtonVariant.Tonal
                )
            }
        }

        // 取景框与提示（识别到票据后切换为授权中/已完成状态）
        BoxWithConstraints(
            modifier = Modifier.align(Alignment.Center).fillMaxWidth()
        ) {
            val frame = maxWidth * 0.62f
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .width(frame)
                    .aspectRatio(1f)
                    .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.9f), RoundedCornerShape(QuantumRadius.Lg))
                    .clip(RoundedCornerShape(QuantumRadius.Lg))
            )
            when {
                recognized == null -> Text(
                    text = errorMessage ?: "对准 Web 登录页展示的二维码",
                    color = Color.White.copy(alpha = 0.85f),
                    style = MaterialTheme.typography.labelMedium,
                    modifier = Modifier
                        .align(Alignment.Center)
                        .padding(top = frame + QuantumSpacing.Xl)
                )
                !authorized -> Text(
                    text = "已识别，正在授权…",
                    color = MaterialTheme.colorScheme.primary,
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.align(Alignment.Center)
                )
                else -> Text(
                    text = "已授权，请回到 Web 页查看登录结果",
                    color = MaterialTheme.colorScheme.tertiary,
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.align(Alignment.Center)
                )
            }
        }

        // 顶栏
        Row(
            modifier = Modifier
                .align(Alignment.TopStart)
                .fillMaxWidth()
                .padding(horizontal = QuantumSpacing.Sm, vertical = QuantumSpacing.Sm),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack) {
                Icon(
                    Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = "返回",
                    tint = Color.White
                )
            }
            Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
            Text("扫码授权", color = Color.White, style = MaterialTheme.typography.titleMedium)
        }

        // 底部兜底：手动输入票据（摄像头不可用/二维码破损时）
        Row(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = QuantumSpacing.Xxl),
            horizontalArrangement = Arrangement.Center
        ) {
            QuantumButton(
                text = "无法扫码？手动输入票据",
                onClick = { showManualInput = true },
                variant = QuantumButtonVariant.Ghost,
                size = QuantumButtonSize.Sm,
                icon = Icons.Filled.Edit
            )
        }

        // 授权失败的服务端文案（过期/已使用）短暂展示
        if (!authorized && toast != null) {
            Text(
                text = toast.orEmpty(),
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(bottom = QuantumSpacing.Lg)
            )
        }
    }

    // 识别到票据 → 授权 → 成功提示后返回
    LaunchedEffect(recognized) {
        val ticket = recognized ?: return@LaunchedEffect
        authorized = viewModel.authorizeScan(ticket)
        if (authorized) {
            delay(1_400)
            onBack()
        } else {
            // 失败（过期/已用）允许重新扫：复位识别状态
            delay(1_600)
            recognized = null
        }
    }

    if (showManualInput) {
        AlertDialog(
            onDismissRequest = { showManualInput = false },
            title = { Text("手动输入票据") },
            text = {
                QuantumTextField(
                    value = manualTicket,
                    onValueChange = { manualTicket = it },
                    label = "Web 登录页展示的票据",
                    singleLine = true
                )
            },
            confirmButton = {
                QuantumButton(
                    text = "授权",
                    onClick = {
                        showManualInput = false
                        val ticket = manualTicket.trim()
                        if (ticket.isNotEmpty()) {
                            scope.launch {
                                if (viewModel.authorizeScan(ticket)) {
                                    authorized = true
                                    delay(1_400)
                                    onBack()
                                } else {
                                    recognized = null
                                }
                            }
                        }
                    },
                    variant = QuantumButtonVariant.Primary,
                    size = QuantumButtonSize.Sm
                )
            },
            dismissButton = {
                QuantumButton(
                    text = "取消",
                    onClick = { showManualInput = false },
                    variant = QuantumButtonVariant.Ghost,
                    size = QuantumButtonSize.Sm
                )
            }
        )
    }
}

/** 单帧二维码解码（失败返回 null；ZXing 本地解码，无 GMS 依赖）。 */
private fun decodeQrOrNull(image: ImageProxy): String? = runCatching {
    val bitmap = image.toBitmapCompat()
    val pixels = IntArray(bitmap.width * bitmap.height)
    bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
    val source = RGBLuminanceSource(bitmap.width, bitmap.height, pixels)
    QRCodeReader().decode(
        BinaryBitmap(HybridBinarizer(source)),
        mapOf(DecodeHintType.TRY_HARDER to true)
    ).text
}.getOrNull()

/** YUV → Bitmap 并按帧方向转正（竖屏后置相机需要旋转才能解出）。 */
private fun ImageProxy.toBitmapCompat(): Bitmap {
    val source = toBitmap()
    val degrees = imageInfo.rotationDegrees.toFloat()
    if (degrees == 0f) {
        return source
    }
    val matrix = Matrix().apply { postRotate(degrees) }
    return Bitmap.createBitmap(source, 0, 0, source.width, source.height, matrix, true)
}

/** ProcessCameraProvider.getInstance 的挂起封装。 */
private suspend fun awaitProvider(context: android.content.Context): ProcessCameraProvider =
    suspendCancellableCoroutine { cont ->
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener(
            {
                try {
                    cont.resume(future.get())
                } catch (e: Exception) {
                    cont.resumeWithException(e)
                }
            },
            ContextCompat.getMainExecutor(context)
        )
    }
