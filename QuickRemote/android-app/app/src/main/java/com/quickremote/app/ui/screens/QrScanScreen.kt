package com.quickremote.app.ui.screens

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import android.util.Size
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.core.content.ContextCompat
import com.quickremote.app.services.QrCodeDecoder
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BorderLight
import com.quickremote.app.ui.theme.Danger
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/** 取景框边长（正方形，QR 码居中即可）。 */
private val ViewfinderSize = 240.dp

/**
 * 扫码导入页：App 内置的配对二维码扫描器。
 *
 * 为什么必须内置：PC 端二维码内容是 `quickremote://pair?...`，系统相机与第三方扫码器
 * 都不处理自定义 scheme，扫了不会有任何反应（这正是此前"扫码没反应"的原因）。
 *
 * 识别成功把文本交给调用方（[onScanned]），由调用方走与「粘贴配置导入」相同的
 * 解析 / 校验 / 写入路径，保证两条入口行为一致。
 */
@Composable
fun QrScanScreen(
    onScanned: (String) -> Unit,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    var hasPermission by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED
        )
    }
    // 区分"还没问过"与"问过被拒"，被拒才展示引导面板
    var asked by remember { mutableStateOf(false) }
    var cameraError by remember { mutableStateOf<String?>(null) }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        hasPermission = granted
        asked = true
    }

    LaunchedEffect(Unit) {
        if (hasPermission) asked = true else permissionLauncher.launch(Manifest.permission.CAMERA)
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black)
    ) {
        when {
            hasPermission -> CameraPreview(
                onDecoded = onScanned,
                onError = { cameraError = it },
                modifier = Modifier.fillMaxSize()
            )

            // 授权前：留黑避免闪白；被拒后：给出可执行的下一步
            asked -> PermissionDeniedPanel(
                onOpenSettings = {
                    val intent = Intent(
                        Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                        Uri.fromParts("package", context.packageName, null)
                    )
                    context.startActivity(intent)
                },
                onBack = onBack
            )
        }

        // 取景遮罩 + 框：只有画面真的在跑时才叠加，避免盖住权限面板
        if (hasPermission && cameraError == null) {
            ViewfinderOverlay()
        }

        // 顶栏浮在上层，任何状态下都能退出
        ScanTopBar(onBack = onBack)

        val error = cameraError
        if (error != null) {
            CameraErrorPanel(message = error, onBack = onBack)
        }
    }
}

/** 相机取景 + 逐帧解码。CameraX 绑定在 [LocalLifecycleOwner] 上，离开页面自动释放。 */
@Composable
private fun CameraPreview(
    onDecoded: (String) -> Unit,
    onError: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current

    val previewView = remember {
        PreviewView(context).apply {
            // 背景全黑且在预览上叠了遮罩，SurfaceView 在部分机型上会盖住遮罩或黑屏，
            // 用 TextureView 实现（COMPATIBLE）保证叠层稳定
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
            scaleType = PreviewView.ScaleType.FILL_CENTER
        }
    }
    val analysisExecutor = remember { Executors.newSingleThreadExecutor() }
    val mainExecutor = remember { ContextCompat.getMainExecutor(context) }
    // 命中第一帧后停止解码：否则同一码会连续回调多次，触发重复导入
    val handled = remember { AtomicBoolean(false) }
    val providerRef = remember { AtomicReference<ProcessCameraProvider?>(null) }

    AndroidView(factory = { previewView }, modifier = modifier)

    DisposableEffect(lifecycleOwner) {
        var analysis: ImageAnalysis? = null

        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            try {
                val provider = future.get()
                providerRef.set(provider)

                // 720p 足够解 QR：分辨率越高，每帧亮度转换 + 解码越慢
                val analyzer = ImageAnalysis.Builder()
                    .setResolutionSelector(
                        ResolutionSelector.Builder()
                            .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
                            .setResolutionStrategy(
                                ResolutionStrategy(
                                    Size(1280, 720),
                                    ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER
                                )
                            )
                            .build()
                    )
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()
                analyzer.setAnalyzer(analysisExecutor) { frame ->
                    try {
                        if (!handled.get()) {
                            QrCodeDecoder.decode(frame)?.let { text ->
                                if (handled.compareAndSet(false, true)) {
                                    mainExecutor.execute { onDecoded(text) }
                                }
                            }
                        }
                    } finally {
                        // 帧必须归还，否则分析管线会饿死（表现为预览卡住）
                        frame.close()
                    }
                }
                analysis = analyzer

                val preview = Preview.Builder().build().also {
                    it.setSurfaceProvider(previewView.surfaceProvider)
                }

                provider.unbindAll()
                provider.bindToLifecycle(
                    lifecycleOwner,
                    CameraSelector.DEFAULT_BACK_CAMERA,
                    preview,
                    analyzer
                )
            } catch (e: Exception) {
                onError(e.message ?: e.javaClass.simpleName)
            }
        }, mainExecutor)

        onDispose {
            // 顺序有讲究：先摘掉分析器再解绑，最后关线程池。
            // 若先关线程池，CameraX 提交分析任务时会撞上 RejectedExecutionException。
            mainExecutor.execute {
                analysis?.clearAnalyzer()
                providerRef.get()?.unbindAll()
                analysisExecutor.shutdown()
            }
        }
    }
}

/** 取景遮罩：四块半透明黑边 + 中间留空的 Accent 取景框，底部放操作提示。 */
@Composable
private fun ViewfinderOverlay() {
    val scrim = Color.Black.copy(alpha = 0.62f)

    Column(modifier = Modifier.fillMaxSize()) {
        Box(modifier = Modifier.fillMaxWidth().weight(1f).background(scrim))

        Row(modifier = Modifier.fillMaxWidth().height(ViewfinderSize)) {
            Box(modifier = Modifier.weight(1f).fillMaxHeight().background(scrim))
            Box(
                modifier = Modifier
                    .size(ViewfinderSize)
                    .clip(RoundedCornerShape(14.dp))
                    .border(2.dp, Accent, RoundedCornerShape(14.dp))
            )
            Box(modifier = Modifier.weight(1f).fillMaxHeight().background(scrim))
        }

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f)
                .background(scrim)
                .navigationBarsPadding()
                .padding(horizontal = 28.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Spacer(modifier = Modifier.height(24.dp))
            Text(
                "将 PC 端「手机配对」弹窗里的二维码放入框内",
                style = MaterialTheme.typography.bodyMedium,
                color = TextPrimary,
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                "识别成功会自动填入服务器地址与预共享密钥",
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted,
                textAlign = TextAlign.Center
            )
        }
    }
}

@Composable
private fun ScanTopBar(onBack: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .windowInsetsPadding(WindowInsets.statusBars)
            .padding(horizontal = 8.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        IconButton(onClick = onBack) {
            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回", tint = TextPrimary)
        }
        Text(
            "扫码导入",
            style = MaterialTheme.typography.titleMedium,
            color = TextPrimary,
            fontWeight = FontWeight.Medium
        )
    }
}

/** 相机权限被拒：说明为什么需要 + 直达系统设置（拒绝后系统不再弹窗，只能去设置里开）。 */
@Composable
private fun PermissionDeniedPanel(onOpenSettings: () -> Unit, onBack: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            modifier = Modifier.padding(horizontal = 32.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                "需要相机权限",
                style = MaterialTheme.typography.titleMedium,
                color = TextPrimary
            )
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                "扫码需要调用相机。也可返回后用「粘贴配置导入」，效果一样。",
                style = MaterialTheme.typography.bodySmall,
                color = TextSecondary,
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(20.dp))
            OutlinedButton(
                onClick = onOpenSettings,
                modifier = Modifier.fillMaxWidth().height(44.dp),
                shape = RoundedCornerShape(4.dp),
                border = androidx.compose.foundation.BorderStroke(1.dp, BorderLight)
            ) {
                Text("去系统设置开启", color = TextPrimary, style = MaterialTheme.typography.labelMedium)
            }
            Spacer(modifier = Modifier.height(10.dp))
            OutlinedButton(
                onClick = onBack,
                modifier = Modifier.fillMaxWidth().height(44.dp),
                shape = RoundedCornerShape(4.dp),
                border = androidx.compose.foundation.BorderStroke(1.dp, BorderLight)
            ) {
                Text("返回", color = TextSecondary, style = MaterialTheme.typography.labelMedium)
            }
        }
    }
}

/** 相机启动失败（无摄像头 / 被其他应用占用）：给出原因，不静默黑屏。 */
@Composable
private fun CameraErrorPanel(message: String, onBack: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            modifier = Modifier.padding(horizontal = 32.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Text("无法启动相机", style = MaterialTheme.typography.titleMedium, color = Danger)
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                message,
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted,
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(20.dp))
            OutlinedButton(
                onClick = onBack,
                modifier = Modifier.width(160.dp).height(44.dp),
                shape = RoundedCornerShape(4.dp),
                border = androidx.compose.foundation.BorderStroke(1.dp, BorderLight)
            ) {
                Text("返回", color = TextPrimary, style = MaterialTheme.typography.labelMedium)
            }
        }
    }
}
