package com.quickremote.app.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.RemoteFrameProtocol
import com.quickremote.app.services.RemoteSessionManager
import com.quickremote.app.ui.components.RemoteDisplayView
import com.quickremote.app.ui.components.StatusColor
import com.quickremote.app.ui.components.StatusIndicator
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
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
                            if (videoWidth > 0) "$videoWidth x $videoHeight" else "远程桌面",
                            style = MaterialTheme.typography.bodySmall,
                            color = TextMuted
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
                        onClick = { viewModel.toggleKeyboard() }
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
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
                .padding(padding)
        ) {
            // 渲染视图（MediaCodec 解码渲染目标）
            AndroidView(
                modifier = Modifier.fillMaxSize(),
                factory = { ctx ->
                    RemoteDisplayView(context = ctx).apply {
                        onSurfaceChanged = { surface, _, _ ->
                            viewModel.setSurface(surface)
                        }
                        onMouseMove = { x, y -> sendMouseAction(viewModel, 0, x, y) }
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
                    // 分辨率变化时更新坐标映射
                    if (videoWidth > 0 && videoHeight > 0) {
                        view.remoteWidth = videoWidth
                        view.remoteHeight = videoHeight
                    }
                }
            )

            // 状态覆盖层（连接中/失败时显示）
            if (state != RemoteSessionManager.SessionState.CONNECTED) {
                SessionOverlay(
                    state = state,
                    tunnel = tunnel,
                    errorMessage = errorMessage
                )
            }
        }
    }
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

@Composable
private fun SessionOverlay(
    state: RemoteSessionManager.SessionState,
    tunnel: com.quickremote.app.data.models.TunnelResponse?,
    errorMessage: String
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
