package com.quickremote.app.ui.screens

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quickremote.app.BuildConfig
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.ResolutionMode
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Border
import com.quickremote.app.ui.theme.BorderLight
import com.quickremote.app.ui.theme.Danger
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.viewmodels.MainViewModel

/**
 * 设置页：服务器地址、显示分辨率、颜色深度、音频重定向、版本检查、更新记录、日志上传、关于。
 */
@Composable
fun SettingsScreen(
    viewModel: MainViewModel,
    onBack: () -> Unit
) {
    val appSettings by viewModel.appSettings.collectAsState()
    val serverConfig by viewModel.serverConfig.collectAsState()
    val lastError by viewModel.lastError.collectAsState()
    val toast by viewModel.toast.collectAsState()
    val updateInfo by viewModel.updateInfo.collectAsState()

    var serverAddress by remember(serverConfig.address) { mutableStateOf(serverConfig.address) }
    var pskInput by remember(serverConfig.preSharedKey) { mutableStateOf(serverConfig.preSharedKey) }
    var settings by remember(appSettings) { mutableStateOf(appSettings) }
    var isCheckingUpdate by remember { mutableStateOf(false) }
    var isUploadingLogs by remember { mutableStateOf(false) }
    // 重置预共享密钥确认对话框
    var showResetPsk by remember { mutableStateOf(false) }
    // 清空日志确认对话框
    var showClearLogs by remember { mutableStateOf(false) }
    // 更新记录对话框（从 APK 内置资源读取）
    val context = LocalContext.current
    var showChangelog by remember { mutableStateOf(false) }
    var changelogContent by remember { mutableStateOf("") }
    // 查看日志对话框
    var showLogs by remember { mutableStateOf(false) }
    var logContent by remember { mutableStateOf("") }
    // 本地保存最近一次操作结果消息（独立于 toast 的即时消费）
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var isError by remember { mutableStateOf(false) }

    // toast 变化时：保存消息到本地状态、重置 loading、延迟清除消息
    LaunchedEffect(toast) {
        val msg = toast
        if (msg != null) {
            isError = msg.contains("失败") || msg.contains("未配置") || msg.contains("无法")
            // 错误时优先显示 lastError（更详细），成功时显示 toast 消息
            statusMessage = if (isError && lastError.isNotBlank()) lastError else msg
            isCheckingUpdate = false
            isUploadingLogs = false
            viewModel.consumeToast()
            // 4 秒后自动清除消息
            kotlinx.coroutines.delay(4000)
            statusMessage = null
        }
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surface)
                    .windowInsetsPadding(WindowInsets.statusBars)
                    .padding(horizontal = 8.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回", tint = TextPrimary)
                }
                Text("设置", style = MaterialTheme.typography.titleMedium, color = TextPrimary, fontWeight = FontWeight.Medium)
            }
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 12.dp)
        ) {
            // 服务器
            SectionTitle("服务器")
            SettingCard {
                Text("服务器地址", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                Spacer(modifier = Modifier.height(6.dp))
                OutlinedTextField(
                    value = serverAddress,
                    onValueChange = { serverAddress = it },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(4.dp),
                    textStyle = MaterialTheme.typography.bodyMedium
                )
                Spacer(modifier = Modifier.height(10.dp))
                Text("预共享密钥", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                Spacer(modifier = Modifier.height(6.dp))
                OutlinedTextField(
                    value = pskInput,
                    onValueChange = { pskInput = it },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(4.dp),
                    textStyle = MaterialTheme.typography.bodyMedium,
                    visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(),
                    placeholder = { Text("未设置，请输入", style = MaterialTheme.typography.bodyMedium, color = TextMuted) }
                )
                Spacer(modifier = Modifier.height(10.dp))
                OutlinedButton(
                    onClick = { showResetPsk = true },
                    modifier = Modifier.fillMaxWidth().height(40.dp),
                    shape = RoundedCornerShape(4.dp),
                    border = BorderStroke(1.dp, Danger.copy(alpha = 0.5f))
                ) {
                    Text("重置预共享密钥", color = Danger, style = MaterialTheme.typography.labelMedium)
                }
            }

            Spacer(modifier = Modifier.height(16.dp))

            // 显示
            SectionTitle("显示")
            SettingCard {
                Text("显示分辨率", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                Spacer(modifier = Modifier.height(8.dp))
                ResolutionMode.entries.forEach { mode ->
                    RadioRow(
                        label = when (mode) {
                            ResolutionMode.AUTO -> "自适应"
                            ResolutionMode.ORIGINAL -> "原分辨率"
                            ResolutionMode.CUSTOM -> "指定分辨率"
                        },
                        selected = settings.resolutionMode == mode,
                        onClick = { settings = settings.copy(resolutionMode = mode) }
                    )
                }
                if (settings.resolutionMode == ResolutionMode.CUSTOM) {
                    Spacer(modifier = Modifier.height(8.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        NumberField(
                            value = settings.customWidth,
                            onValueChange = { settings = settings.copy(customWidth = it) },
                            label = "宽",
                            modifier = Modifier.weight(1f)
                        )
                        Text("×", color = TextMuted)
                        NumberField(
                            value = settings.customHeight,
                            onValueChange = { settings = settings.copy(customHeight = it) },
                            label = "高",
                            modifier = Modifier.weight(1f)
                        )
                    }
                }

                Divider()
                Text("颜色深度", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                Spacer(modifier = Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Chip(
                        label = "16 bit",
                        selected = settings.colorDepth == 16,
                        onClick = { settings = settings.copy(colorDepth = 16) }
                    )
                    Chip(
                        label = "32 bit",
                        selected = settings.colorDepth == 32,
                        onClick = { settings = settings.copy(colorDepth = 32) }
                    )
                }

                Divider()
                Text("图像质量（压缩率）", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                Spacer(modifier = Modifier.height(4.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("更低带宽", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                    Text(
                        "${settings.qualityPercent}%",
                        style = MaterialTheme.typography.titleSmall,
                        color = TextPrimary,
                        fontWeight = FontWeight.Medium
                    )
                    Text("更高画质", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                }
                Slider(
                    value = settings.qualityPercent.toFloat(),
                    onValueChange = { settings = settings.copy(qualityPercent = it.toInt()) },
                    valueRange = 20f..100f,
                    steps = 15
                )
                Text(
                    "公网连接建议降低（20-60%），局域网可保持 80-100%",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )
            }

            Spacer(modifier = Modifier.height(16.dp))

            // 音频
            SectionTitle("音频")
            SettingCard {
                ToggleRow(
                    label = "音频重定向",
                    checked = settings.audioRedirect,
                    onCheckedChange = { settings = settings.copy(audioRedirect = it) }
                )
            }

            Spacer(modifier = Modifier.height(16.dp))

            // 更新
            SectionTitle("更新")
            SettingCard {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = {
                            isCheckingUpdate = true
                            viewModel.checkUpdate()
                        },
                        enabled = !isCheckingUpdate,
                        modifier = Modifier.weight(1f).height(42.dp),
                        shape = RoundedCornerShape(4.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = Accent, contentColor = TextPrimary)
                    ) {
                        if (isCheckingUpdate) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(16.dp),
                                strokeWidth = 2.dp,
                                color = TextPrimary
                            )
                        } else {
                            Text("检查更新", style = MaterialTheme.typography.labelMedium)
                        }
                    }
                    OutlinedButton(
                        onClick = {
                            changelogContent = try {
                                context.assets.open("changelog.txt").bufferedReader().use { it.readText() }
                            } catch (e: Exception) {
                                "未找到更新记录（assets/changelog.txt）"
                            }
                            showChangelog = true
                        },
                        modifier = Modifier.weight(1f).height(42.dp),
                        shape = RoundedCornerShape(4.dp),
                        border = BorderStroke(1.dp, BorderLight)
                    ) {
                        Text("更新记录", color = TextPrimary, style = MaterialTheme.typography.labelMedium)
                    }
                }
                Spacer(modifier = Modifier.height(10.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = {
                            isUploadingLogs = true
                            viewModel.uploadLogs(deviceId = "android")
                        },
                        enabled = !isUploadingLogs,
                        modifier = Modifier.weight(1f).height(42.dp),
                        shape = RoundedCornerShape(4.dp),
                        border = BorderStroke(1.dp, BorderLight)
                    ) {
                        if (isUploadingLogs) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(16.dp),
                                strokeWidth = 2.dp,
                                color = TextPrimary
                            )
                        } else {
                            Text("上传日志", color = TextPrimary, style = MaterialTheme.typography.labelMedium)
                        }
                    }
                    OutlinedButton(
                        onClick = {
                            logContent = viewModel.readLogContent()
                            showLogs = true
                        },
                        modifier = Modifier.weight(1f).height(42.dp),
                        shape = RoundedCornerShape(4.dp),
                        border = BorderStroke(1.dp, BorderLight)
                    ) {
                        Text("查看日志", color = TextPrimary, style = MaterialTheme.typography.labelMedium)
                    }
                    OutlinedButton(
                        onClick = { showClearLogs = true },
                        modifier = Modifier.weight(1f).height(42.dp),
                        shape = RoundedCornerShape(4.dp),
                        border = BorderStroke(1.dp, Danger.copy(alpha = 0.5f))
                    ) {
                        Text("清空日志", color = Danger, style = MaterialTheme.typography.labelMedium)
                    }
                }
                // 操作结果显示区（成功或失败的消息）
                statusMessage?.let { msg ->
                    Spacer(modifier = Modifier.height(8.dp))
                    val msgColor = if (isError) Danger else Success
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(4.dp))
                            .background(msgColor.copy(alpha = 0.08f))
                            .padding(horizontal = 10.dp, vertical = 8.dp)
                    ) {
                        Text(
                            msg,
                            style = MaterialTheme.typography.bodySmall,
                            color = msgColor
                        )
                    }
                }
            }

            Spacer(modifier = Modifier.height(16.dp))

            // 关于
            SectionTitle("关于")
            SettingCard {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text("版本", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                    Text("v${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodyMedium, color = TextPrimary)
                }
            }

            Spacer(modifier = Modifier.height(24.dp))

            // 保存按钮
            Button(
                onClick = {
                    viewModel.saveAndContinue(
                        com.quickremote.app.data.models.ServerConfig(
                            address = serverAddress.trim(),
                            preSharedKey = pskInput.trim()
                        )
                    )
                    viewModel.updateSettings(settings)
                    onBack()
                },
                modifier = Modifier.fillMaxWidth().height(46.dp),
                shape = RoundedCornerShape(4.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Accent, contentColor = TextPrimary)
            ) {
                Text("保存设置", fontWeight = FontWeight.SemiBold)
            }
            Spacer(modifier = Modifier.height(16.dp))
        }

        // 更新记录对话框（从 APK 内置 assets/changelog.txt 读取）
        if (showChangelog) {
            AlertDialog(
                onDismissRequest = { showChangelog = false },
                title = { Text("更新记录", color = TextPrimary) },
                text = {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(max = 420.dp)
                    ) {
                        ChangelogMarkdown(
                            changelogContent,
                            modifier = Modifier
                                .verticalScroll(rememberScrollState())
                                .padding(vertical = 4.dp)
                        )
                    }
                },
                confirmButton = {
                    TextButton(onClick = { showChangelog = false }) {
                        Text("关闭", color = Accent)
                    }
                },
                containerColor = BgCard,
                titleContentColor = TextPrimary,
                textContentColor = TextPrimary
            )
        }

        // 查看日志对话框（显示本地日志 + 复制按钮）
        if (showLogs) {
            AlertDialog(
                onDismissRequest = { showLogs = false },
                title = { Text("查看日志", color = TextPrimary) },
                text = {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(max = 420.dp)
                    ) {
                        Text(
                            logContent.ifBlank { "暂无日志" },
                            style = MaterialTheme.typography.bodySmall,
                            color = TextPrimary,
                            fontFamily = FontFamily.Monospace,
                            modifier = Modifier
                                .verticalScroll(rememberScrollState())
                                .padding(vertical = 4.dp)
                        )
                    }
                },
                confirmButton = {
                    TextButton(onClick = {
                        val clipboard = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
                            as android.content.ClipboardManager
                        clipboard.setPrimaryClip(
                            android.content.ClipData.newPlainText("QuickRemote 日志", logContent)
                        )
                        statusMessage = "日志已复制到剪贴板"
                        isError = false
                    }) {
                        Text("复制", color = Accent)
                    }
                },
                dismissButton = {
                    TextButton(onClick = { showLogs = false }) {
                        Text("关闭", color = TextSecondary)
                    }
                },
                containerColor = BgCard,
                titleContentColor = TextPrimary,
                textContentColor = TextPrimary
            )
        }

        // 重置预共享密钥确认对话框
        if (showResetPsk) {
            AlertDialog(
                onDismissRequest = { showResetPsk = false },
                title = { Text("重置预共享密钥", color = TextPrimary) },
                text = {
                    Text(
                        "将清除已保存的密钥和登录令牌，并断开设备列表。\n\n重置后请输入新密钥并保存，重新连接服务器。",
                        style = MaterialTheme.typography.bodySmall,
                        color = TextSecondary
                    )
                },
                confirmButton = {
                    TextButton(onClick = {
                        showResetPsk = false
                        viewModel.resetPreSharedKey()
                    }) {
                        Text("确认重置", color = Danger)
                    }
                },
                dismissButton = {
                    TextButton(onClick = { showResetPsk = false }) {
                        Text("取消", color = TextSecondary)
                    }
                },
                containerColor = BgCard,
                titleContentColor = TextPrimary,
                textContentColor = TextPrimary
            )
        }

        // 清空日志确认对话框
        if (showClearLogs) {
            AlertDialog(
                onDismissRequest = { showClearLogs = false },
                title = { Text("清空日志", color = TextPrimary) },
                text = {
                    Text(
                        "将删除本地全部日志文件（含最近 7 天），此操作不可恢复。\n\n如需保留用于排查，请先「上传日志」或「查看日志」复制。",
                        style = MaterialTheme.typography.bodySmall,
                        color = TextSecondary
                    )
                },
                confirmButton = {
                    TextButton(onClick = {
                        showClearLogs = false
                        viewModel.clearLogs()
                    }) {
                        Text("确认清空", color = Danger)
                    }
                },
                dismissButton = {
                    TextButton(onClick = { showClearLogs = false }) {
                        Text("取消", color = TextSecondary)
                    }
                },
                containerColor = BgCard,
                titleContentColor = TextPrimary,
                textContentColor = TextPrimary
            )
        }

        // 发现新版本对话框（展示更新内容 + 确认更新按钮）
        updateInfo?.let { info ->
            AlertDialog(
                onDismissRequest = { viewModel.dismissUpdate() },
                title = {
                    Text("发现新版本 v${info.latestVersion}", color = TextPrimary, fontWeight = FontWeight.SemiBold)
                },
                text = {
                    Column {
                        Text("当前版本 v${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodySmall, color = TextMuted)
                        Spacer(modifier = Modifier.height(8.dp))
                        if (info.changelog.isNotBlank()) {
                            Text("本次更新内容：", style = MaterialTheme.typography.bodySmall, color = TextSecondary)
                            Spacer(modifier = Modifier.height(4.dp))
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .heightIn(max = 260.dp)
                                    .clip(RoundedCornerShape(4.dp))
                                    .background(androidx.compose.ui.graphics.Color.White.copy(alpha = 0.03f))
                                    .padding(horizontal = 10.dp, vertical = 8.dp)
                            ) {
                                Text(
                                    info.changelog,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = TextPrimary,
                                    modifier = Modifier.verticalScroll(rememberScrollState())
                                )
                            }
                        }
                    }
                },
                confirmButton = {
                    Button(
                        onClick = {
                            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(info.downloadUrl))
                            context.startActivity(intent)
                            viewModel.dismissUpdate()
                        },
                        enabled = info.downloadUrl.isNotBlank(),
                        colors = ButtonDefaults.buttonColors(containerColor = Accent, contentColor = TextPrimary)
                    ) {
                        Text("立即更新", fontWeight = FontWeight.SemiBold)
                    }
                },
                dismissButton = {
                    TextButton(onClick = { viewModel.dismissUpdate() }) {
                        Text("以后再说", color = TextMuted)
                    }
                },
                containerColor = BgCard,
                titleContentColor = TextPrimary,
                textContentColor = TextPrimary
            )
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.labelMedium,
        color = TextMuted,
        modifier = Modifier.padding(start = 2.dp, bottom = 8.dp)
    )
}

@Composable
private fun SettingCard(content: @Composable () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(BgCard)
            .border(1.dp, Border, RoundedCornerShape(8.dp))
            .padding(14.dp)
    ) {
        content()
    }
}

@Composable
private fun Divider() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 10.dp)
            .height(1.dp)
            .background(BorderLight)
    )
}

@Composable
private fun RadioRow(label: String, selected: Boolean, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(16.dp)
                .clip(CircleShape)
                .background(if (selected) Accent else androidx.compose.ui.graphics.Color.Transparent)
                .border(1.dp, if (selected) Accent else BorderLight, CircleShape)
        )
        Spacer(modifier = Modifier.size(10.dp))
        Text(label, style = MaterialTheme.typography.bodyMedium, color = TextPrimary)
    }
}

@Composable
private fun Chip(label: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(4.dp))
            .background(if (selected) Accent else BgCard)
            .border(1.dp, if (selected) Accent else BorderLight, RoundedCornerShape(4.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp)
    ) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = if (selected) TextPrimary else TextSecondary)
    }
}

@Composable
private fun ToggleRow(label: String, checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = TextPrimary,
                checkedTrackColor = Accent,
                uncheckedThumbColor = TextMuted,
                uncheckedTrackColor = BgCard
            )
        )
    }
}

@Composable
private fun NumberField(
    value: Int,
    onValueChange: (Int) -> Unit,
    label: String,
    modifier: Modifier = Modifier
) {
    var text by remember(value) { mutableStateOf(value.toString()) }
    OutlinedTextField(
        value = text,
        onValueChange = {
            text = it.filter { c -> c.isDigit() }
            text.toIntOrNull()?.let(onValueChange)
        },
        singleLine = true,
        label = { Text(label) },
        modifier = modifier,
        shape = RoundedCornerShape(4.dp),
        textStyle = MaterialTheme.typography.bodyMedium
    )
}

/**
 * 轻量 Markdown 渲染：支持标题、版本号行、列表项、分隔线。
 * 用于「更新记录」弹窗，把 changelog 原文渲染成预览效果。
 */
@Composable
private fun ChangelogMarkdown(content: String, modifier: Modifier = Modifier) {
    val lines = content.replace("\r\n", "\n").split("\n")
    Column(modifier = modifier) {
        lines.forEach { rawLine ->
            val line = rawLine.trimEnd()
            when {
                line.isBlank() ->
                    Spacer(modifier = Modifier.height(6.dp))

                line.matches(Regex("^=+$")) || line.matches(Regex("^-{3,}$")) ->
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(1.dp)
                            .background(BorderLight)
                    )

                line.startsWith("### ") ->
                    Text(line.removePrefix("### ").trim(), fontWeight = FontWeight.Bold, fontSize = 14.sp, color = Accent)

                line.startsWith("## ") ->
                    Text(line.removePrefix("## ").trim(), fontWeight = FontWeight.Bold, fontSize = 15.sp, color = Accent)

                line.startsWith("# ") ->
                    Text(line.removePrefix("# ").trim(), fontWeight = FontWeight.Bold, fontSize = 16.sp, color = Accent)

                line.matches(Regex("^v?\\d+\\.\\d+(\\.\\d+)?.*")) ->
                    Text(
                        line,
                        fontWeight = FontWeight.Bold,
                        fontSize = 14.sp,
                        color = Accent,
                        modifier = Modifier.padding(top = 8.dp, bottom = 2.dp)
                    )

                line.startsWith("- ") || line.startsWith("* ") ->
                    Text(
                        "•  " + line.removePrefix("- ").removePrefix("* ").trim(),
                        style = MaterialTheme.typography.bodySmall,
                        color = TextPrimary,
                        modifier = Modifier.padding(start = 10.dp, top = 2.dp, bottom = 2.dp)
                    )

                else ->
                    Text(line, style = MaterialTheme.typography.bodySmall, color = TextPrimary)
            }
        }
    }
}
