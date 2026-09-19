package com.quickremote.app.ui.screens

import android.content.Context
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
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.runtime.rememberCoroutineScope
import androidx.core.content.FileProvider
import com.quickremote.app.BuildConfig
import com.quickremote.app.data.PairingPayload
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.QUALITY_PRESETS
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Border
import com.quickremote.app.ui.theme.BorderLight
import com.quickremote.app.ui.theme.Danger
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.viewmodels.FeedbackUiState
import com.quickremote.app.viewmodels.MainViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * 设置页：服务器地址、画质与触控、版本检查、更新记录、日志上传、关于。
 */
@Composable
fun SettingsScreen(
    viewModel: MainViewModel,
    scannedPairText: String?,
    onScannedConsumed: () -> Unit,
    onScanClick: () -> Unit,
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
    // 意见反馈对话框
    var showFeedback by remember { mutableStateOf(false) }
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
    // 配对导入结果：就近显示在服务器卡片的导入按钮下方。
    // 不走 statusMessage —— 那块渲染在「版本更新」卡片里，导入反馈出现在那里会让人找不到。
    var importNotice by remember { mutableStateOf<String?>(null) }
    var importFailed by remember { mutableStateOf(false) }

    // 导入提示 4 秒后自动消失
    LaunchedEffect(importNotice) {
        if (importNotice != null) {
            kotlinx.coroutines.delay(4000)
            importNotice = null
        }
    }

    /**
     * 解析配对文本（二维码内容 / 剪贴板配置串）→ 写入服务器地址与密钥。
     * 扫码与粘贴共用这一条路径，避免两条入口的行为逐渐漂移。
     */
    fun importPairingText(text: String) {
        PairingPayload.parse(text)
            .onSuccess { info ->
                // 同步输入框显示（服务器地址/密钥已随导入变更）
                serverAddress = info.addr
                pskInput = info.psk
                viewModel.importServerConfig(
                    ServerConfig(address = info.addr, preSharedKey = info.psk)
                )
                importFailed = false
                importNotice = if (info.name.isBlank()) "已导入配置" else "已导入配置：${info.name}"
            }
            .onFailure {
                importFailed = true
                importNotice = "导入失败：${it.message ?: "内容无效"}"
            }
    }

    // 扫码页回传的载荷：消费掉再应用，避免返回本页重建时重复导入
    LaunchedEffect(scannedPairText) {
        val text = scannedPairText
        if (text != null) {
            onScannedConsumed()
            importPairingText(text)
        }
    }

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

    // 发现新版本时不产生 toast（以弹窗代替），需单独复位检查按钮的 loading，
    // 否则弹窗点「以后再说」后按钮仍在转圈
    LaunchedEffect(updateInfo) {
        if (updateInfo != null) isCheckingUpdate = false
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
                Spacer(modifier = Modifier.height(8.dp))
                // 扫码 / 粘贴导入：两条入口共用 importPairingText
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    OutlinedButton(
                        onClick = onScanClick,
                        modifier = Modifier.weight(1f).height(40.dp),
                        shape = RoundedCornerShape(4.dp)
                    ) {
                        Text("扫码导入", style = MaterialTheme.typography.labelMedium)
                    }
                    OutlinedButton(
                        onClick = {
                            val clip = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
                                as android.content.ClipboardManager
                            val text = clip.primaryClip?.getItemAt(0)?.coerceToText(context)?.toString()
                            if (text.isNullOrBlank()) {
                                importFailed = true
                                importNotice = "剪贴板为空"
                            } else {
                                importPairingText(text)
                            }
                        },
                        modifier = Modifier.weight(1f).height(40.dp),
                        shape = RoundedCornerShape(4.dp)
                    ) {
                        Text("粘贴配置导入", style = MaterialTheme.typography.labelMedium)
                    }
                }
                val notice = importNotice
                if (notice != null) {
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        notice,
                        style = MaterialTheme.typography.bodySmall,
                        color = if (importFailed) Danger else Success
                    )
                }
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

            // 画面与触控
            SectionTitle("画面与触控")
            SettingCard {
                Text("画质", style = MaterialTheme.typography.bodyMedium, color = TextSecondary)
                Spacer(modifier = Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    QUALITY_PRESETS.forEach { preset ->
                        Chip(
                            label = preset.label,
                            selected = settings.qualityPercent == preset.percent,
                            onClick = { settings = settings.copy(qualityPercent = preset.percent) }
                        )
                    }
                }
                Spacer(modifier = Modifier.height(6.dp))
                Text(
                    "与远程会话内工具栏「画质」共用同一配置；公网建议流畅/标准，局域网可选高清/原画",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )

                Divider()
                ToggleRow(
                    label = "空白区触摸板",
                    checked = settings.blankTouchpad,
                    onCheckedChange = { settings = settings.copy(blankTouchpad = it) }
                )
                Text(
                    "画面外空白区域作为触摸板：单指滑动移动光标，轻点左键，双指轻点右键、双指滑动滚动；关闭后仅保留点击与滚轮",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )

                Divider()
                Text(
                    "光标速度 ${settings.touchpadSpeed}%",
                    style = MaterialTheme.typography.bodyMedium,
                    color = TextPrimary
                )
                Slider(
                    value = settings.touchpadSpeed.toFloat(),
                    onValueChange = {
                        settings = settings.copy(touchpadSpeed = (it.toInt() / 10 * 10).coerceIn(50, 300))
                    },
                    valueRange = 50f..300f,
                    steps = 24
                )
                Text(
                    "触摸板单指滑动移动光标的速度（100% 与悬浮球长按同速）",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )

                Divider()
                ToggleRow(
                    label = "双击拖动",
                    checked = settings.touchpadDoubleTapDrag,
                    onCheckedChange = {
                        settings = settings.copy(touchpadDoubleTapDrag = it)
                    }
                )
                Text(
                    "快速双击后第二下按住滑动 = 按住左键拖动：画面内直接拖动远程窗口（移动窗口、拖选、拖滑块），空白区触摸板同样生效",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )

                Divider()
                ToggleRow(
                    label = "三指手势",
                    checked = settings.touchpadThreeFinger,
                    onCheckedChange = {
                        settings = settings.copy(touchpadThreeFinger = it)
                    }
                )
                Text(
                    "上滑多任务视图 / 下滑显示桌面 / 左右滑切换应用 / 轻点搜索",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )

                Divider()
                ToggleRow(
                    label = "四指手势",
                    checked = settings.touchpadFourFinger,
                    onCheckedChange = {
                        settings = settings.copy(touchpadFourFinger = it)
                    }
                )
                Text(
                    "左右滑切换虚拟桌面 / 轻点通知中心",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
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

            // 意见反馈
            SectionTitle("意见反馈")
            SettingCard {
                Text(
                    "遇到问题或有建议？写下来直接提交给我们。",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )
                Spacer(modifier = Modifier.height(10.dp))
                OutlinedButton(
                    onClick = { showFeedback = true },
                    modifier = Modifier.fillMaxWidth().height(42.dp),
                    shape = RoundedCornerShape(4.dp),
                    border = BorderStroke(1.dp, BorderLight)
                ) {
                    Text("提交意见反馈", color = TextPrimary, style = MaterialTheme.typography.labelMedium)
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

        // 意见反馈对话框（内容上传到 QuickDeploy 的 quickremote/feedback 目录）
        if (showFeedback) {
            FeedbackDialog(
                viewModel = viewModel,
                onDismiss = {
                    showFeedback = false
                    viewModel.consumeFeedbackState()
                }
            )
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

        // 发现新版本对话框（展示更新内容 + App 内下载安装）
        updateInfo?.let { info ->
            var downloading by remember(info) { mutableStateOf(false) }
            var progress by remember(info) { mutableStateOf(0f) }
            var downloadError by remember(info) { mutableStateOf<String?>(null) }
            val scope = rememberCoroutineScope()
            AlertDialog(
                onDismissRequest = { if (!downloading) viewModel.dismissUpdate() },
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
                        if (downloading) {
                            Spacer(modifier = Modifier.height(12.dp))
                            LinearProgressIndicator(
                                progress = { progress },
                                modifier = Modifier.fillMaxWidth(),
                                color = Accent
                            )
                            Spacer(modifier = Modifier.height(4.dp))
                            Text(
                                "正在下载安装包… ${(progress * 100).toInt()}%",
                                style = MaterialTheme.typography.bodySmall,
                                color = TextSecondary
                            )
                        }
                        downloadError?.let { err ->
                            Spacer(modifier = Modifier.height(8.dp))
                            Text(err, style = MaterialTheme.typography.bodySmall, color = Danger)
                        }
                    }
                },
                confirmButton = {
                    Button(
                        onClick = {
                            downloading = true
                            downloadError = null
                            scope.launch {
                                try {
                                    val file = withContext(Dispatchers.IO) {
                                        downloadUpdateApk(context, info.downloadUrl, info.latestVersion) { p -> progress = p }
                                    }
                                    // 下载完成，直接唤起系统安装器（App 内更新，不经浏览器/文件管理器）
                                    openApkInstaller(context, file)
                                    downloading = false
                                } catch (e: Exception) {
                                    downloading = false
                                    downloadError = "下载失败：${e.message ?: "未知错误"}，请重试或改用浏览器下载"
                                }
                            }
                        },
                        enabled = info.downloadUrl.isNotBlank() && !downloading,
                        colors = ButtonDefaults.buttonColors(containerColor = Accent, contentColor = TextPrimary)
                    ) {
                        Text(if (downloading) "下载中…" else "立即更新", fontWeight = FontWeight.SemiBold)
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

/**
 * 意见反馈对话框：多行输入 + 「附带运行日志」开关 + 提交。
 *
 * 反馈内容会以 `设备id_反馈时间.log` 上传到 QuickDeploy 的 quickremote/feedback 目录；
 * 勾选附带日志时，把最近 1000 行运行日志拼在反馈内容下方一起提交。
 * 提交结果在本对话框内呈现（成功后可继续提交下一条，失败保留正文便于重试）。
 */
@Composable
private fun FeedbackDialog(viewModel: MainViewModel, onDismiss: () -> Unit) {
    val feedbackState by viewModel.feedbackState.collectAsState()
    val deviceId by viewModel.localDeviceId.collectAsState()

    var content by remember { mutableStateOf("") }
    var withLogs by remember { mutableStateOf(true) }

    val submitting = feedbackState is FeedbackUiState.Submitting
    val done = feedbackState as? FeedbackUiState.Done

    // 提交成功后清空正文，方便继续提交下一条
    LaunchedEffect(done) {
        if (done?.success == true) content = ""
    }

    AlertDialog(
        onDismissRequest = { if (!submitting) onDismiss() },
        title = { Text("意见反馈", color = TextPrimary) },
        text = {
            Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                Text(
                    "请描述遇到的问题或你的建议（越具体越容易定位）",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )
                Spacer(modifier = Modifier.height(8.dp))
                OutlinedTextField(
                    value = content,
                    onValueChange = { content = it },
                    modifier = Modifier.fillMaxWidth().height(160.dp),
                    shape = RoundedCornerShape(4.dp),
                    textStyle = MaterialTheme.typography.bodyMedium,
                    enabled = !submitting,
                    placeholder = {
                        Text(
                            "例如：手机连上电脑后画面偶尔卡住…",
                            style = MaterialTheme.typography.bodySmall,
                            color = TextMuted
                        )
                    }
                )
                Spacer(modifier = Modifier.height(12.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            "附带运行日志",
                            style = MaterialTheme.typography.bodyMedium,
                            color = TextSecondary
                        )
                        Text(
                            "勾选后把最近 1000 行日志附在反馈内容下方一并提交",
                            style = MaterialTheme.typography.bodySmall,
                            color = TextMuted
                        )
                    }
                    Spacer(modifier = Modifier.width(8.dp))
                    Switch(
                        checked = withLogs,
                        onCheckedChange = { withLogs = it },
                        enabled = !submitting
                    )
                }
                Spacer(modifier = Modifier.height(10.dp))
                Text(
                    "设备 ID：${deviceId.ifBlank { "生成中…" }}",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextMuted
                )
                done?.let { result ->
                    Spacer(modifier = Modifier.height(10.dp))
                    val msgColor = if (result.success) Success else Danger
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(4.dp))
                            .background(msgColor.copy(alpha = 0.08f))
                            .padding(horizontal = 10.dp, vertical = 8.dp)
                    ) {
                        Text(
                            result.message,
                            style = MaterialTheme.typography.bodySmall,
                            color = msgColor
                        )
                    }
                }
            }
        },
        confirmButton = {
            TextButton(
                onClick = { viewModel.submitFeedback(content, withLogs) },
                enabled = content.isNotBlank() && !submitting
            ) {
                if (submitting) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(16.dp),
                        strokeWidth = 2.dp,
                        color = Accent
                    )
                } else {
                    Text(
                        if (done?.success == true) "再提交一条" else "提交",
                        color = if (content.isBlank()) TextMuted else Accent
                    )
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text(if (done?.success == true) "完成" else "取消", color = TextSecondary)
            }
        },
        containerColor = MaterialTheme.colorScheme.background
    )
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

/**
 * App 内下载更新 APK 到应用外部私有目录（无需存储权限）。
 * 此前"立即更新"只是打开浏览器链接，用户需在下载目录手动找文件安装，
 * 连续两次装成旧版本（v1.0.39 装成 1.0.38、v1.0.41 装成 1.0.40），
 * 现改为 App 内直接下载并唤起系统安装器，全程不离开 App。
 *
 * v1.0.44 加固：v1.0.43 升级时曾出现"下载进度 100%、安装器显示成功，
 * 但实际装入的仍是旧版 APK"（同 versionCode 覆盖重装也会显示安装成功）。
 * 现下载前清除旧文件、URL 加时间戳防缓存污染、下载后校验 APK 内嵌
 * versionName 与期望版本一致，不一致直接拦截不再唤起安装器。
 */
private fun downloadUpdateApk(context: Context, url: String, expectedVersion: String, onProgress: (Float) -> Unit): File {
    val dir = context.getExternalFilesDir(null)
        ?: File(context.filesDir, "update").apply { mkdirs() }
    val file = File(dir, "QuickRemote-update.apk")
    // 防旧安装包残留干扰：每次下载前清除
    if (file.exists() && !file.delete()) {
        throw IOException("无法清除旧安装包缓存，请重试")
    }
    // 加时间戳参数防中间层缓存返回旧内容
    val cacheBustingUrl = if (url.contains("?")) "$url&_t=${System.currentTimeMillis()}" else "$url?_t=${System.currentTimeMillis()}"
    val conn = URL(cacheBustingUrl).openConnection() as HttpURLConnection
    conn.connectTimeout = 15000
    conn.readTimeout = 60000
    conn.instanceFollowRedirects = true
    conn.setRequestProperty("Cache-Control", "no-cache")
    try {
        if (conn.responseCode != 200) throw IOException("服务器返回 HTTP ${conn.responseCode}")
        val total = conn.contentLengthLong.toFloat()
        conn.inputStream.use { input ->
            FileOutputStream(file).use { out ->
                val buf = ByteArray(64 * 1024)
                var done = 0L
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    out.write(buf, 0, n)
                    done += n
                    if (total > 0) onProgress((done / total).coerceIn(0f, 1f))
                }
                // 流提前结束且未下满声明长度，视为下载不完整
                if (total > 0 && done < total.toLong()) {
                    throw IOException("下载中断（${done}/${total.toLong()} 字节）")
                }
            }
        }
    } finally {
        conn.disconnect()
    }
    // 简单完整性校验：APK 是 zip 包（PK 头）且体积应大于 1MB
    if (file.length() < 1_000_000) throw IOException("安装包不完整（${file.length()} 字节）")
    FileInputStream(file).use { it.read() == 0x50 && it.read() == 0x4B }.let { isZip ->
        if (!isZip) {
            file.delete()
            throw IOException("下载内容不是有效安装包")
        }
    }
    // 关键校验：读取 APK 内嵌版本号，防止下载被缓存污染后装入旧版
    //（旧场景：versionCode 相同的旧包覆盖重装，安装器同样显示"安装成功"）
    val pkgInfo = try {
        context.packageManager.getPackageArchiveInfo(file.absolutePath, 0)
    } catch (e: Exception) {
        null
    }
    if (pkgInfo == null) {
        file.delete()
        throw IOException("安装包解析失败，请重试")
    }
    val actualVersion = pkgInfo.versionName.orEmpty().trim().trimStart('v', 'V')
    if (actualVersion != expectedVersion.trim().trimStart('v', 'V')) {
        file.delete()
        throw IOException("下载到旧版本安装包（实际 v$actualVersion，应为 v$expectedVersion），已拦截。请稍后重试")
    }
    return file
}

/** 通过 FileProvider 唤起系统安装器安装指定 APK。失败时回退到浏览器打开下载页。 */
private fun openApkInstaller(context: Context, apk: File) {
    try {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", apk)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(intent)
    } catch (e: Exception) {
        // FileProvider 或安装器唤起失败（极老 ROM）：回退浏览器
        val fallback = Intent(Intent.ACTION_VIEW, Uri.parse("https://qd.solutionx.top/app/23dafeae-1f70-4d6c-8023-dc585b0f4366/about"))
        fallback.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(fallback)
    }
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
