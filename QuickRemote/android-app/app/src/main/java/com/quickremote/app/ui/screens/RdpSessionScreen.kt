package com.quickremote.app.ui.screens

import android.view.SurfaceHolder
import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
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
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.quickremote.app.data.models.Device
import com.quickremote.app.freerdp.RdpSurfaceView
import com.quickremote.app.services.RdpSessionManager
import com.quickremote.app.ui.components.StatusColor
import com.quickremote.app.ui.components.StatusIndicator
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.ui.theme.Warning
import com.quickremote.app.viewmodels.RdpSessionViewModel

/**
 * RDP 远程桌面会话页。
 *
 * - 顶部工具栏: 返回、设备名、断开连接
 * - 中间: RdpSurfaceView（FreeRDP 渲染目标）
 * - 底部工具栏: 键盘切换、全屏切换
 *
 * FreeRDP .so 库可用时，实际渲染远程桌面画面；
 * 不可用时，降级显示连接状态和隧道信息。
 */
@Composable
fun RdpSessionScreen(
    device: Device,
    viewModel: RdpSessionViewModel,
    onBack: () -> Unit
) {
    val state by viewModel.state.collectAsState()
    val tunnel by viewModel.tunnel.collectAsState()
    val errorMessage by viewModel.errorMessage.collectAsState()
    val isFullscreen by viewModel.isFullscreen.collectAsState()
    val isKeyboardVisible by viewModel.isKeyboardVisible.collectAsState()
    val isFreeRdpAvailable = viewModel.isFreeRdpAvailable

    // 凭据输入状态
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var domain by remember { mutableStateOf("") }
    var saveCredentials by remember { mutableStateOf(false) }
    val savedCred by viewModel.savedCredentials.collectAsState()

    // 进入页面时加载该设备已保存的凭据
    LaunchedEffect(device.device_id) {
        viewModel.loadCredentials(device.device_id)
    }

    // 已保存凭据加载完成后预填表单（仅在字段为空时填充，避免覆盖用户输入）
    LaunchedEffect(savedCred) {
        savedCred?.let { cred ->
            if (username.isBlank() && password.isBlank()) {
                username = cred.username
                password = cred.password
                domain = cred.domain
            }
            saveCredentials = true
        }
    }

    // 会话未开始或失败时展示凭据输入表单（NLA 认证需要 Windows 用户名/密码）
    val showCredentialForm = state == RdpSessionManager.SessionState.IDLE ||
        state == RdpSessionManager.SessionState.FAILED

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
                            device.os.ifBlank { "Remote Desktop" },
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
            // FreeRDP 渲染视图（始终渲染，Surface 创建时自动传递给 FreeRDP）
            AndroidView(
                modifier = Modifier.fillMaxSize(),
                factory = { ctx ->
                    RdpSurfaceView(
                        context = ctx,
                        client = viewModel.getFreeRdpClient()
                    ).apply {
                        holder.addCallback(object : SurfaceHolder.Callback {
                            override fun surfaceCreated(holder: SurfaceHolder) {
                                viewModel.setSurface(holder.surface)
                            }
                            override fun surfaceChanged(
                                holder: SurfaceHolder, format: Int, width: Int, height: Int
                            ) {
                                viewModel.setSurface(holder.surface)
                            }
                            override fun surfaceDestroyed(holder: SurfaceHolder) {
                                viewModel.setSurface(null)
                            }
                        })
                    }
                }
            )

            // 凭据输入表单（会话未开始时显示）
            if (showCredentialForm) {
                CredentialForm(
                    device = device,
                    username = username,
                    password = password,
                    domain = domain,
                    saveCredentials = saveCredentials,
                    errorMessage = errorMessage,
                    onUsernameChange = { username = it },
                    onPasswordChange = { password = it },
                    onDomainChange = { domain = it },
                    onSaveCredentialsChange = { saveCredentials = it },
                    onConnect = {
                        viewModel.startSession(device, username, password, domain)
                        viewModel.saveCredentials(
                            device.device_id, username, password, domain, saveCredentials
                        )
                    }
                )
            } else if (state != RdpSessionManager.SessionState.CONNECTED || !isFreeRdpAvailable) {
                // 状态覆盖层（连接中/失败/FreeRDP 不可用时显示）
                SessionOverlay(
                    state = state,
                    tunnel = tunnel,
                    errorMessage = errorMessage,
                    isFreeRdpAvailable = isFreeRdpAvailable
                )
            }

            // 已连接但长时间未收到画面（可能停在 Windows 登录/认证界面）
            val waitingForFirstFrame by viewModel.waitingForFirstFrame.collectAsState()
            if (state == RdpSessionManager.SessionState.CONNECTED && waitingForFirstFrame) {
                Surface(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .fillMaxWidth()
                        .padding(12.dp),
                    shape = RoundedCornerShape(8.dp),
                    color = Color.Black.copy(alpha = 0.75f)
                ) {
                    Text(
                        "连接已建立，但暂未收到画面。\n若一直无画面，请确认 Windows 登录凭据正确（PC 处于锁屏/登录界面时需通过 NLA 认证）。",
                        style = MaterialTheme.typography.bodySmall,
                        color = TextPrimary,
                        modifier = Modifier.padding(12.dp)
                    )
                }
            }
        }
    }
}

@Composable
private fun CredentialForm(
    device: Device,
    username: String,
    password: String,
    domain: String,
    saveCredentials: Boolean,
    errorMessage: String,
    onUsernameChange: (String) -> Unit,
    onPasswordChange: (String) -> Unit,
    onDomainChange: (String) -> Unit,
    onSaveCredentialsChange: (Boolean) -> Unit,
    onConnect: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.85f))
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text(
            device.hostname.ifBlank { device.device_id },
            style = MaterialTheme.typography.titleMedium,
            color = TextPrimary,
            fontWeight = FontWeight.SemiBold
        )
        Spacer(modifier = Modifier.height(4.dp))
        Text(
            "请输入 Windows 登录凭据以建立远程连接",
            style = MaterialTheme.typography.bodySmall,
            color = TextMuted
        )

        // 连接失败时展示错误详情
        if (errorMessage.isNotBlank()) {
            Spacer(modifier = Modifier.height(12.dp))
            Text(
                errorMessage,
                style = MaterialTheme.typography.bodySmall,
                color = Warning
            )
        }

        Spacer(modifier = Modifier.height(20.dp))

        OutlinedTextField(
            value = username,
            onValueChange = onUsernameChange,
            label = { Text("用户名") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(modifier = Modifier.height(12.dp))
        OutlinedTextField(
            value = password,
            onValueChange = onPasswordChange,
            label = { Text("密码") },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(modifier = Modifier.height(12.dp))
        OutlinedTextField(
            value = domain,
            onValueChange = onDomainChange,
            label = { Text("域（可选）") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth()
        )

        // 保存用户名和密码选项
        Spacer(modifier = Modifier.height(8.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Checkbox(
                checked = saveCredentials,
                onCheckedChange = onSaveCredentialsChange
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                "保存用户名和密码",
                style = MaterialTheme.typography.bodyMedium,
                color = TextSecondary
            )
        }

        Spacer(modifier = Modifier.height(16.dp))

        Button(
            onClick = onConnect,
            enabled = username.isNotBlank() && password.isNotBlank(),
            modifier = Modifier.fillMaxWidth().height(48.dp)
        ) {
            Text("连接远程桌面", fontWeight = FontWeight.Medium)
        }
        Spacer(modifier = Modifier.height(8.dp))
        Text(
            "提示：Windows 默认开启 NLA 认证，必须输入正确的用户名和密码。",
            style = MaterialTheme.typography.bodySmall,
            color = TextMuted
        )
    }
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
    state: RdpSessionManager.SessionState,
    tunnel: com.quickremote.app.data.models.TunnelResponse?,
    errorMessage: String,
    isFreeRdpAvailable: Boolean
) {
    val (color, text) = when (state) {
        RdpSessionManager.SessionState.CONNECTING -> StatusColor.YELLOW to "RDP 连接中…"
        RdpSessionManager.SessionState.CONNECTED -> StatusColor.GREEN to "隧道已建立"
        RdpSessionManager.SessionState.FAILED -> StatusColor.RED to "连接失败"
        RdpSessionManager.SessionState.DISCONNECTED -> StatusColor.RED to "已断开"
        RdpSessionManager.SessionState.IDLE -> StatusColor.YELLOW to "等待中…"
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        if (state == RdpSessionManager.SessionState.CONNECTING) {
            CircularProgressIndicator(modifier = Modifier.size(36.dp), strokeWidth = 3.dp, color = TextPrimary)
            Spacer(modifier = Modifier.height(16.dp))
        } else {
            StatusIndicator(color = color, size = 12.dp)
            Spacer(modifier = Modifier.height(12.dp))
        }
        Text(text, style = MaterialTheme.typography.titleSmall, color = TextPrimary, fontWeight = FontWeight.Medium)

        // 失败时展示错误详情
        if (state == RdpSessionManager.SessionState.FAILED && errorMessage.isNotBlank()) {
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                errorMessage,
                style = MaterialTheme.typography.bodySmall,
                color = Warning
            )
        }

        // 隧道信息卡片
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

        // FreeRDP 不可用提示（仅 .so 库未加载时显示）
        if (!isFreeRdpAvailable && state == RdpSessionManager.SessionState.CONNECTED) {
            Spacer(modifier = Modifier.height(24.dp))
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(Warning.copy(alpha = 0.1f))
                    .border(1.dp, Warning.copy(alpha = 0.4f), RoundedCornerShape(8.dp))
                    .padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    "FreeRDP 库未加载",
                    style = MaterialTheme.typography.titleSmall,
                    color = Warning,
                    fontWeight = FontWeight.SemiBold
                )
                Spacer(modifier = Modifier.height(6.dp))
                Text(
                    if (errorMessage.startsWith("FreeRDP 库加载失败")) {
                        errorMessage
                    } else {
                        "远程桌面渲染需要 libfreerdp-android.so 库。" +
                        "请从 FreeRDP APK 提取 .so 文件放置到 app/src/main/jniLibs/<abi>/ 目录。"
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )
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
        Text(label, style = MaterialTheme.typography.bodySmall, color = TextSecondary)
        Spacer(modifier = Modifier.width(16.dp))
        Text(
            value,
            style = MaterialTheme.typography.bodySmall,
            color = TextPrimary,
            fontFamily = FontFamily.Monospace
        )
    }
}
