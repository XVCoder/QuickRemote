package com.quickremote.app.ui.screens

import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.pullrefresh.PullRefreshIndicator
import androidx.compose.material.pullrefresh.pullRefresh
import androidx.compose.material.pullrefresh.rememberPullRefreshState
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.quickremote.app.data.local.MAX_REMARK_LENGTH
import com.quickremote.app.data.models.Device
import com.quickremote.app.ui.components.DeviceCard
import com.quickremote.app.ui.components.StatusColor
import com.quickremote.app.ui.components.StatusIndicator
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.viewmodels.ConnectionState
import com.quickremote.app.viewmodels.MainViewModel

/**
 * 设备列表页（主页）：服务器状态、在线/离线两段设备卡片、下拉刷新、设置入口。
 *
 * 设备数据来自服务端全量列表（含离线）；本机备注与软删除在 ViewModel 层装配完成，
 * 本页只负责展示，以及两个对话框（设置备注 / 移除离线设备）。
 *
 * 所有设备统一走截屏方案连接（经中继隧道），不再有内网 RDP 直连
 * （曾导致 PC 端黑屏，相关实现已彻底移除）。
 */
@OptIn(androidx.compose.material.ExperimentalMaterialApi::class)
@Composable
fun DeviceListScreen(
    viewModel: MainViewModel,
    onDeviceClick: (Device) -> Unit,
    onSettingsClick: () -> Unit
) {
    val deviceList by viewModel.deviceList.collectAsState()
    val connectionState by viewModel.connectionState.collectAsState()
    val isRefreshing by viewModel.isRefreshing.collectAsState()
    val serverConfig by viewModel.serverConfig.collectAsState()

    // 对话框目标：非空即弹出对应对话框
    var remarkTarget by remember { mutableStateOf<Device?>(null) }
    var removeTarget by remember { mutableStateOf<Device?>(null) }

    val statusColor = when (connectionState) {
        ConnectionState.CONNECTED -> StatusColor.GREEN
        ConnectionState.CONNECTING -> StatusColor.YELLOW
        ConnectionState.ERROR -> StatusColor.RED
        ConnectionState.DISCONNECTED -> StatusColor.RED
    }
    val statusText = when (connectionState) {
        ConnectionState.CONNECTED -> "已连接"
        ConnectionState.CONNECTING -> "连接中"
        ConnectionState.ERROR -> "连接错误"
        ConnectionState.DISCONNECTED -> "未连接"
    }

    // 首次进入若已配置则自动拉取设备
    LaunchedEffect(serverConfig.address) {
        if (serverConfig.address.isNotBlank() &&
            deviceList.isEmpty &&
            connectionState != ConnectionState.CONNECTING
        ) {
            viewModel.refreshDevices()
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
                    .padding(horizontal = 16.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            "QuickRemote",
                            style = MaterialTheme.typography.titleMedium,
                            color = Accent,
                            fontWeight = FontWeight.SemiBold
                        )
                        Text(
                            " · 设备",
                            style = MaterialTheme.typography.titleMedium,
                            color = TextSecondary
                        )
                    }
                    Spacer(modifier = Modifier.height(2.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        StatusIndicator(color = statusColor, size = 7.dp)
                        Spacer(modifier = Modifier.size(6.dp))
                        Text(statusText, style = MaterialTheme.typography.bodySmall, color = TextSecondary)
                        if (serverConfig.address.isNotBlank()) {
                            Text(
                                " · ${serverConfig.address}",
                                style = MaterialTheme.typography.bodySmall,
                                color = TextMuted
                            )
                        }
                    }
                }
                IconButton(onClick = { viewModel.refreshDevices() }, enabled = !isRefreshing) {
                    if (isRefreshing) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(18.dp),
                            strokeWidth = 2.dp,
                            color = TextPrimary
                        )
                    } else {
                        Icon(Icons.Filled.Refresh, contentDescription = "刷新", tint = TextPrimary)
                    }
                }
                IconButton(onClick = onSettingsClick) {
                    Icon(Icons.Filled.Settings, contentDescription = "设置", tint = TextPrimary)
                }
            }
        }
    ) { padding ->
        // 下拉刷新（列表滚动到顶再下拉触发；空列表区域同样支持）
        val pullRefreshState = rememberPullRefreshState(
            refreshing = isRefreshing,
            onRefresh = { viewModel.refreshDevices() }
        )
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .pullRefresh(pullRefreshState)
        ) {
            if (deviceList.isEmpty && !isRefreshing) {
                // verticalScroll 让空态区域也能产生嵌套滚动，否则下拉手势无法触发
                EmptyState(modifier = Modifier.verticalScroll(rememberScrollState()))
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    if (deviceList.online.isNotEmpty()) {
                        item { SectionHeader("在线设备 (${deviceList.online.size})") }
                        items(deviceList.online, key = { it.device.device_id }) { listItem ->
                            DeviceCard(
                                item = listItem,
                                onClick = { device -> onDeviceClick(device) },
                                onEditRemark = { remarkTarget = it },
                                onRemove = { removeTarget = it }
                            )
                        }
                    }
                    if (deviceList.offline.isNotEmpty()) {
                        item { SectionHeader("离线设备 (${deviceList.offline.size})") }
                        items(deviceList.offline, key = { it.device.device_id }) { listItem ->
                            DeviceCard(
                                item = listItem,
                                // 离线设备不发起连接：服务端会直接回 503 device_offline，
                                // 与其白跑一趟不如当场说清楚原因
                                onClick = {
                                    viewModel.showToast(
                                        "设备「${listItem.device.displayTitle}」当前离线，无法远程控制"
                                    )
                                },
                                onEditRemark = { remarkTarget = it },
                                onRemove = { removeTarget = it }
                            )
                        }
                    }
                }
            }
            PullRefreshIndicator(
                refreshing = isRefreshing,
                state = pullRefreshState,
                modifier = Modifier.align(Alignment.TopCenter),
                backgroundColor = BgCard,
                contentColor = Accent,
                scale = true
            )
        }
    }

    remarkTarget?.let { device ->
        val currentRemark = (deviceList.online + deviceList.offline)
            .firstOrNull { it.device.device_id == device.device_id }
            ?.remark
            .orEmpty()
        RemarkDialog(
            device = device,
            currentRemark = currentRemark,
            onDismiss = { remarkTarget = null },
            onConfirm = { text ->
                viewModel.setDeviceRemark(device.device_id, text)
                remarkTarget = null
            }
        )
    }

    removeTarget?.let { device ->
        RemoveDialog(
            device = device,
            onDismiss = { removeTarget = null },
            onConfirm = {
                viewModel.removeOfflineDevice(device.device_id)
                removeTarget = null
            }
        )
    }

    ToastHost(viewModel)
}

@Composable
private fun SectionHeader(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.labelMedium,
        color = TextMuted,
        modifier = Modifier.padding(start = 2.dp, bottom = 4.dp)
    )
}

/** 设备备注编辑对话框：仅本机可见，最长 [MAX_REMARK_LENGTH] 字符；清空即删除备注。 */
@Composable
private fun RemarkDialog(
    device: Device,
    currentRemark: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit
) {
    var text by remember(device.device_id) { mutableStateOf(currentRemark) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("设备备注", color = TextPrimary) },
        text = {
            Column {
                Text(
                    "为「${device.displayTitle}」设置备注（仅本机可见，最长 $MAX_REMARK_LENGTH 字符；清空则删除备注）",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextSecondary
                )
                Spacer(modifier = Modifier.height(10.dp))
                OutlinedTextField(
                    value = text,
                    onValueChange = { if (it.length <= MAX_REMARK_LENGTH) text = it },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    placeholder = { Text("如：书房主机", color = TextMuted) }
                )
                Spacer(modifier = Modifier.height(4.dp))
                Text(
                    "${text.length} / $MAX_REMARK_LENGTH",
                    style = MaterialTheme.typography.labelSmall,
                    color = TextMuted
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(text) }) { Text("保存", color = Accent) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("取消", color = TextMuted) }
        },
        containerColor = BgCard,
        titleContentColor = TextPrimary,
        textContentColor = TextPrimary
    )
}

/** 移除离线设备确认对话框：仅本机隐藏（软删除），设备再次上线自动恢复。 */
@Composable
private fun RemoveDialog(
    device: Device,
    onDismiss: () -> Unit,
    onConfirm: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("移除设备", color = TextPrimary) },
        text = {
            Text(
                "确定将「${device.displayTitle}」从列表移除？\n\n" +
                    "仅在当前手机隐藏（软删除），不影响其它设备；该设备再次上线后将自动恢复显示。",
                style = MaterialTheme.typography.bodyMedium,
                color = TextSecondary
            )
        },
        confirmButton = {
            TextButton(onClick = onConfirm) { Text("移除", color = Accent) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("取消", color = TextMuted) }
        },
        containerColor = BgCard,
        titleContentColor = TextPrimary,
        textContentColor = TextPrimary
    )
}

@Composable
private fun EmptyState(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxSize().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Box(
            modifier = Modifier
                .size(64.dp)
                .clip(RoundedCornerShape(16.dp))
                .background(BgCard),
            contentAlignment = Alignment.Center
        ) {
            Text("🖥", color = TextMuted)
        }
        Spacer(modifier = Modifier.height(16.dp))
        Text("暂无设备", style = MaterialTheme.typography.titleSmall, color = TextPrimary)
        Spacer(modifier = Modifier.height(6.dp))
        Text(
            "请确认 PC 客户端已连接到中转服务器",
            style = MaterialTheme.typography.bodyMedium,
            color = TextMuted
        )
    }
}

/**
 * 展示 ViewModel 的瞬时提示。
 *
 * 注意：这里必须真的渲染出来 —— 原先只调用 consumeToast() 把消息丢掉，
 * 导致「认证失败 / 备注已保存 / 设备离线」这类反馈全部静默。沿用 MainActivity
 * 处理配对提示的同一写法（系统 Toast），不引入新的 Snackbar 体系。
 */
@Composable
private fun ToastHost(viewModel: MainViewModel) {
    val toast by viewModel.toast.collectAsState()
    val context = LocalContext.current
    LaunchedEffect(toast) {
        val message = toast ?: return@LaunchedEffect
        Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
        viewModel.consumeToast()
    }
}
