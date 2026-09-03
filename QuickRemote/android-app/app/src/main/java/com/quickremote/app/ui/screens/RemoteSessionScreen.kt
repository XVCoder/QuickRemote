package com.quickremote.app.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.ScreenRotation
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.KeyMapper
import com.quickremote.app.services.RemoteFrameProtocol
import com.quickremote.app.services.RemoteSessionManager
import com.quickremote.app.ui.components.RemoteDisplayView
import com.quickremote.app.ui.components.StatusColor
import com.quickremote.app.ui.components.StatusIndicator
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.ui.theme.Warning
import com.quickremote.app.viewmodels.SessionViewModel
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 远程桌面会话页（截屏方案）。
 *
 * - 顶部工具栏: 返回、设备名、断开连接
 * - 中间: RemoteDisplayView（MediaCodec 渲染目标）
 * - 底部工具栏: 键盘切换、全屏切换
 *
 * 触摸事件转换为输入帧发送（阶段 5 完整实现）。
 */
@Composable
fun RemoteSessionScreen(
    device: Device,
    viewModel: SessionViewModel,
    onBack: () -> Unit
) {
    val state by viewModel.state.collectAsState()
    val tunnel by viewModel.tunnel.collectAsState()
    val errorMessage by viewModel.errorMessage.collectAsState()
    val isFullscreen by viewModel.isFullscreen.collectAsState()
    val isKeyboardVisible by viewModel.isKeyboardVisible.collectAsState()
    val videoWidth by viewModel.videoWidth.collectAsState()
    val videoHeight by viewModel.videoHeight.collectAsState()
    val connectionMode by viewModel.connectionMode.collectAsState()
    val pcLocked by viewModel.pcLocked.collectAsState()
    val pcUnlockError by viewModel.pcUnlockError.collectAsState()

    // 进入页面自动开始截屏远程会话（无需凭据）
    LaunchedEffect(device.device_id) {
        viewModel.startSession(device)
    }

    // 会话页沉浸全屏（隐藏系统栏），退出时恢复。
    // 屏幕方向不自动横屏：由工具栏「旋转」按钮手动切换横竖屏。
    val activity = LocalContext.current.findActivity()
    var isLandscape by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        activity?.window?.let { win ->
            androidx.core.view.WindowInsetsControllerCompat(
                win, win.decorView
            ).apply {
                hide(androidx.core.view.WindowInsetsCompat.Type.systemBars())
                systemBarsBehavior =
                    androidx.core.view.WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        }
    }
    DisposableEffect(Unit) {
        onDispose {
            activity?.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            activity?.window?.let { win ->
                androidx.core.view.WindowInsetsControllerCompat(
                    win, win.decorView
                ).show(androidx.core.view.WindowInsetsCompat.Type.systemBars())
            }
        }
    }

    /** 切换横屏/竖屏。 */
    fun toggleOrientation() {
        isLandscape = !isLandscape
        activity?.requestedOrientation = if (isLandscape) {
            android.content.pm.ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        } else {
            android.content.pm.ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
    }

    // 隐藏键盘输入框引用（软键盘字符/物理键盘按键捕获）
    var keyInput by remember { mutableStateOf<android.widget.EditText?>(null) }
    var lastKeyText by remember { mutableStateOf("") }

    /** 发送键盘事件帧：[vkCode 2B][down 1B]。 */
    fun sendKeyRaw(vk: Int, down: Boolean) {
        val data = ByteBuffer.allocate(3).order(ByteOrder.LITTLE_ENDIAN)
            .putShort(vk.toShort())
            .put(if (down) 1 else 0)
            .array()
        viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_KEY, data)
    }

    /** 发送一次按键（含 Shift 组合与按下/释放）。 */
    fun sendKeyPress(vk: Int, needShift: Boolean) {
        if (needShift) sendKeyRaw(0x10, true)  // VK_SHIFT down
        sendKeyRaw(vk, true)
        sendKeyRaw(vk, false)
        if (needShift) sendKeyRaw(0x10, false)
    }

    /** 物理键盘按键按下：映射 VK 并发送（含修饰键状态）。 */
    fun handleAndroidKeyDown(keyCode: Int, event: android.view.KeyEvent): Boolean {
        // 修饰键单独处理（维持按下状态）
        KeyMapper.androidModifierToVk(keyCode)?.let { vk ->
            sendKeyRaw(vk, true)
            return true
        }
        val vk = KeyMapper.androidKeyToVk(keyCode) ?: return false
        val needShift = event.isShiftPressed
        if (needShift) sendKeyRaw(0x10, true)
        sendKeyRaw(vk, true)
        if (needShift) sendKeyRaw(0x10, false)
        return true
    }

    /** 物理键盘按键释放。 */
    fun handleAndroidKeyUp(keyCode: Int, event: android.view.KeyEvent): Boolean {
        KeyMapper.androidModifierToVk(keyCode)?.let { vk ->
            sendKeyRaw(vk, false)
            return true
        }
        val vk = KeyMapper.androidKeyToVk(keyCode) ?: return false
        sendKeyRaw(vk, false)
        return true
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            if (!isFullscreen) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface)
                        .windowInsetsPadding(WindowInsets.statusBars)
                        .padding(horizontal = 8.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = onBack) {
                        Icon(
                            Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "返回",
                            tint = TextPrimary
                        )
                    }
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            device.hostname.ifBlank { device.device_id },
                            style = MaterialTheme.typography.titleSmall,
                            color = TextPrimary,
                            fontWeight = FontWeight.Medium
                        )
                        Text(
                            if (videoWidth > 0) {
                                "$videoWidth x $videoHeight · " +
                                    if (connectionMode == RemoteSessionManager.ConnectionMode.LAN) "局域网直连" else "公网中继"
                            } else "远程桌面",
                            style = MaterialTheme.typography.bodySmall,
                            color = if (connectionMode == RemoteSessionManager.ConnectionMode.LAN) Success else TextMuted
                        )
                    }
                    IconButton(onClick = { viewModel.disconnect(); onBack() }) {
                        Icon(Icons.Filled.LinkOff, contentDescription = "断开连接", tint = Warning)
                    }
                }
            }
        },
        bottomBar = {
            if (!isFullscreen) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface)
                        .windowInsetsPadding(WindowInsets.navigationBars)
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    horizontalArrangement = Arrangement.SpaceEvenly,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    ToolBarButton(
                        icon = Icons.Filled.Keyboard,
                        label = "键盘",
                        active = isKeyboardVisible,
                        onClick = {
                            // 先根据当前状态决定动作，再翻转（避免读到旧状态）
                            val willShow = !isKeyboardVisible
                            viewModel.toggleKeyboard()
                            val ime = activity?.getSystemService(Context.INPUT_METHOD_SERVICE)
                                    as? android.view.inputmethod.InputMethodManager
                            if (willShow) {
                                keyInput?.requestFocus()
                                ime?.showSoftInput(keyInput, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT)
                            } else {
                                ime?.hideSoftInputFromWindow(keyInput?.windowToken, 0)
                                keyInput?.clearFocus()
                            }
                        }
                    )
                    ToolBarButton(
                        icon = Icons.Filled.ScreenRotation,
                        label = "旋转",
                        active = isLandscape,
                        onClick = { toggleOrientation() }
                    )
                    ToolBarButton(
                        icon = if (isFullscreen) Icons.Filled.FullscreenExit else Icons.Filled.Fullscreen,
                        label = "全屏",
                        active = false,
                        onClick = { viewModel.toggleFullscreen() }
                    )
                }
            }
        }
    ) { padding ->
        BoxWithConstraints(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
                .padding(padding)
        ) {
            val density = LocalDensity.current
            val parentWpx = constraints.maxWidth
            val parentHpx = constraints.maxHeight

            // 高度拉满模式（类似相册缩放打开的照片）：缩放比 = 父高/远程高，
            // 画面高度铺满、宽度同比例（横屏远程宽度超出屏幕，View 超出父边界可左右拖动）。
            // 用 requiredSize 强制子项尺寸（Compose 会忽略子 View 内部设置的 layoutParams）。
            val (coverWdp, coverHdp) = remember(videoWidth, videoHeight, parentWpx, parentHpx, density) {
                if (videoWidth > 0 && videoHeight > 0 && parentWpx > 0 && parentHpx > 0) {
                    val baseScale = parentHpx.toFloat() / videoHeight
                    val coverW = (videoWidth * baseScale).toInt().coerceAtLeast(1)
                    val coverH = (videoHeight * baseScale).toInt().coerceAtLeast(1)
                    with(density) { coverW.toDp() to coverH.toDp() }
                } else {
                    with(density) { parentWpx.toDp() to parentHpx.toDp() }
                }
            }

            // 渲染视图（MediaCodec 解码渲染目标）
            AndroidView(
                modifier = Modifier
                    .align(Alignment.Center)
                    .requiredSize(coverWdp, coverHdp),
                factory = { ctx ->
                    RemoteDisplayView(context = ctx).apply {
                        onSurfaceChanged = { surface, _, _ ->
                            viewModel.setSurface(surface)
                        }
                        onLeftClick = { x, y ->
                            sendMouseAction(viewModel, 1, x, y)  // 左按下
                            sendMouseAction(viewModel, 2, x, y)  // 左释放
                        }
                        onRightClick = { x, y ->
                            sendMouseAction(viewModel, 3, x, y)  // 右按下
                            sendMouseAction(viewModel, 4, x, y)  // 右释放
                        }
                        onWheel = { x, y, delta -> sendWheel(viewModel, x, y, delta) }
                    }
                },
                update = { view ->
                    // 可视区域（clamp 依赖）与远程分辨率变化时同步
                    view.setViewport(parentWpx, parentHpx)
                    if (videoWidth > 0 && videoHeight > 0) {
                        view.setRemoteSize(videoWidth, videoHeight)
                    }
                }
            )

            // 状态覆盖层（连接中/失败时显示）
            if (state != RemoteSessionManager.SessionState.CONNECTED) {
                SessionOverlay(
                    state = state,
                    tunnel = tunnel,
                    errorMessage = errorMessage,
                    onReconnect = { viewModel.startSession(device) }
                )
            }

            // PC 锁屏提示条（连接保持；点击可输入 Windows 密码远程解锁）
            if (pcLocked && state == RemoteSessionManager.SessionState.CONNECTED) {
                var showUnlockDialog by remember { mutableStateOf(false) }
                Column(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .padding(top = 24.dp)
                        .clip(RoundedCornerShape(20.dp))
                        .background(BgCard)
                        .clickable { showUnlockDialog = true }
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        StatusIndicator(color = StatusColor.YELLOW, size = 8.dp)
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(
                            "PC 已锁屏，点击输入密码解锁",
                            style = MaterialTheme.typography.bodySmall,
                            color = TextPrimary
                        )
                    }
                    pcUnlockError?.let { err ->
                        Spacer(modifier = Modifier.height(4.dp))
                        Text(
                            err,
                            style = MaterialTheme.typography.labelSmall,
                            color = Warning
                        )
                    }
                }
                if (showUnlockDialog) {
                    UnlockDialog(
                        onConfirm = { password ->
                            showUnlockDialog = false
                            viewModel.sendUnlock(password)
                        },
                        onDismiss = { showUnlockDialog = false }
                    )
                }
            }

            // 隐藏键盘输入框：捕获软键盘文本与物理键盘按键，映射为 VK 码发送
            AndroidView(
                modifier = Modifier.size(1.dp),
                factory = { ctx ->
                    android.widget.EditText(ctx).apply {
                        setBackgroundColor(android.graphics.Color.TRANSPARENT)
                        isCursorVisible = false
                        isFocusableInTouchMode = true
                        textSize = 1f
                        // 物理键盘：直接转发按键事件
                        setOnKeyListener { _, keyCode, event ->
                            when (event.action) {
                                android.view.KeyEvent.ACTION_DOWN ->
                                    handleAndroidKeyDown(keyCode, event)
                                android.view.KeyEvent.ACTION_UP ->
                                    handleAndroidKeyUp(keyCode, event)
                            }
                            true
                        }
                        // 软键盘：文本变化 → 逐字符映射发送
                        addTextChangedListener(object : android.text.TextWatcher {
                            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                            override fun afterTextChanged(s: android.text.Editable?) {
                                val newText = s?.toString().orEmpty()
                                val old = lastKeyText
                                if (newText.length > old.length) {
                                    // 新增字符（可能一次提交多个，如中文输入法）
                                    newText.substring(old.length).forEach { ch ->
                                        KeyMapper.charToVk(ch)?.let { (vk, shift) ->
                                            sendKeyPress(vk, shift)
                                        }
                                    }
                                } else if (newText.length < old.length) {
                                    // 删除 → 退格
                                    repeat(old.length - newText.length) { sendKeyPress(0x08, false) }
                                }
                                lastKeyText = newText
                            }
                        })
                        keyInput = this
                    }
                }
            )
        }
    }
}

/** 从 Context 向上查找宿主 Activity。 */
private fun Context.findActivity(): Activity? {
    var ctx = this
    while (ctx is ContextWrapper) {
        if (ctx is Activity) return ctx
        ctx = ctx.baseContext
    }
    return null
}

/** 发送鼠标事件帧：[action 1B][x 2B][y 2B]（远程坐标）。 */
private fun sendMouseAction(
    viewModel: SessionViewModel,
    action: Int,
    x: Int,
    y: Int
) {
    val data = ByteBuffer.allocate(5).order(ByteOrder.LITTLE_ENDIAN)
        .put(action.toByte())
        .putShort(x.toShort())
        .putShort(y.toShort())
        .array()
    viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_MOUSE, data)
}

/** 发送滚轮事件帧：[delta 2B(有符号)][x 2B][y 2B]（远程坐标）。 */
private fun sendWheel(
    viewModel: SessionViewModel,
    x: Int,
    y: Int,
    delta: Int
) {
    val data = ByteBuffer.allocate(6).order(ByteOrder.LITTLE_ENDIAN)
        .putShort(delta.toShort())
        .putShort(x.toShort())
        .putShort(y.toShort())
        .array()
    viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_WHEEL, data)
}

@Composable
private fun ToolBarButton(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    active: Boolean,
    onClick: () -> Unit
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier
            .clip(RoundedCornerShape(8.dp))
            .background(if (active) BgCard else Color.Transparent)
            .padding(horizontal = 24.dp, vertical = 6.dp)
    ) {
        IconButton(onClick = onClick) {
            Icon(icon, contentDescription = label, tint = if (active) Success else TextPrimary)
        }
        Text(label, style = MaterialTheme.typography.labelSmall, color = if (active) Success else TextMuted)
    }
}

/** 远程解锁对话框：输入 Windows 登录密码，发送到 PC 端锁屏桌面注入解锁。 */
@Composable
private fun UnlockDialog(
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit
) {
    var password by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text("远程解锁", style = MaterialTheme.typography.titleMedium, color = TextPrimary)
        },
        text = {
            Column {
                Text(
                    "输入 PC 端的 Windows 登录密码，将在锁屏界面自动输入并解锁。",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextSecondary
                )
                Spacer(modifier = Modifier.height(12.dp))
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                    placeholder = { Text("Windows 登录密码", color = TextSecondary) },
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { if (password.isNotEmpty()) onConfirm(password) },
                enabled = password.isNotEmpty()
            ) {
                Text("解锁", color = if (password.isNotEmpty()) Accent else TextSecondary)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("取消", color = TextSecondary)
            }
        },
        containerColor = BgCard
    )
}

@Composable
private fun SessionOverlay(
    state: RemoteSessionManager.SessionState,
    tunnel: com.quickremote.app.data.models.TunnelResponse?,
    errorMessage: String,
    onReconnect: () -> Unit = {}
) {
    val (color, text) = when (state) {
        RemoteSessionManager.SessionState.CONNECTING -> StatusColor.YELLOW to "连接中…"
        RemoteSessionManager.SessionState.CONNECTED -> StatusColor.GREEN to "隧道已建立"
        RemoteSessionManager.SessionState.FAILED -> StatusColor.RED to "连接失败"
        RemoteSessionManager.SessionState.DISCONNECTED -> StatusColor.RED to "已断开"
        RemoteSessionManager.SessionState.IDLE -> StatusColor.YELLOW to "等待中…"
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        if (state == RemoteSessionManager.SessionState.CONNECTING) {
            CircularProgressIndicator(modifier = Modifier.size(36.dp), strokeWidth = 3.dp, color = TextPrimary)
            Spacer(modifier = Modifier.height(16.dp))
        } else {
            StatusIndicator(color = color, size = 12.dp)
            Spacer(modifier = Modifier.height(12.dp))
        }
        Text(text, style = MaterialTheme.typography.titleSmall, color = TextPrimary, fontWeight = FontWeight.Medium)

        if (state == RemoteSessionManager.SessionState.FAILED && errorMessage.isNotBlank()) {
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                errorMessage,
                style = MaterialTheme.typography.bodySmall,
                color = Warning
            )
        }

        // 断连/失败时提供重连入口
        if (state == RemoteSessionManager.SessionState.FAILED ||
            state == RemoteSessionManager.SessionState.DISCONNECTED
        ) {
            Spacer(modifier = Modifier.height(20.dp))
            Button(onClick = onReconnect) {
                Icon(Icons.Filled.Refresh, contentDescription = null, tint = TextPrimary)
                Spacer(modifier = Modifier.width(6.dp))
                Text("重新连接", color = TextPrimary)
            }
        }

        tunnel?.let { t ->
            Spacer(modifier = Modifier.height(20.dp))
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(BgCard)
                    .padding(16.dp)
            ) {
                Text("隧道信息", style = MaterialTheme.typography.labelMedium, color = TextMuted)
                Spacer(modifier = Modifier.height(8.dp))
                InfoRow("会话 ID", t.session_id)
                InfoRow("隧道主机", t.tunnel_host.ifBlank { "-" })
                InfoRow("隧道端口", if (t.tunnel_port > 0) t.tunnel_port.toString() else "待分配")
            }
        }
    }
}

@Composable
private fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 3.dp),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, style = MaterialTheme.typography.bodySmall, color = TextMuted)
        Spacer(modifier = Modifier.weight(1f))
        Text(
            value,
            style = MaterialTheme.typography.bodySmall,
            color = TextPrimary,
            fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace
        )
    }
}
