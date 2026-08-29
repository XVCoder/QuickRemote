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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
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
 * 设备列表页（主页）：服务器状态、在线设备卡片、下拉刷新、设置入口。
 *
 * 所有设备统一走截屏方案连接（经中继隧道），不再有内网 RDP 直连
 * （曾导致 PC 端黑屏，相关实现已彻底移除）。
 */
@Composable
fun DeviceListScreen(
    viewModel: MainViewModel,
    onDeviceClick: (Device) -> Unit,
    onSettingsClick: () -> Unit
) {
    val devices by viewModel.devices.collectAsState()
    val connectionState by viewModel.connectionState.collectAsState()
    val isRefreshing by viewModel.isRefreshing.collectAsState()
    val serverConfig by viewModel.serverConfig.collectAsState()

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
        if (serverConfig.address.isNotBlank() && devices.isEmpty() && connectionState != ConnectionState.CONNECTING) {
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
        Box(modifier = Modifier.fillMaxSize().padding(padding)) {
            if (devices.isEmpty() && !isRefreshing) {
                EmptyState()
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(
                        horizontal = 16.dp,
                        vertical = 12.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    item {
                        Text(
                            "在线设备 (${devices.size})",
                            style = MaterialTheme.typography.labelMedium,
                            color = TextMuted,
                            modifier = Modifier.padding(start = 2.dp, bottom = 4.dp)
                        )
                    }
                    items(devices, key = { it.device_id }) { device ->
                        DeviceCard(device = device, onClick = onDeviceClick)
                    }
                }
            }
        }
    }

    ToastHost(viewModel)
}

@Composable
private fun EmptyState() {
    Column(
        modifier = Modifier.fillMaxSize().padding(32.dp),
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
        Text("暂无在线设备", style = MaterialTheme.typography.titleSmall, color = TextPrimary)
        Spacer(modifier = Modifier.height(6.dp))
        Text(
            "请确认 PC 客户端已连接到中转服务器",
            style = MaterialTheme.typography.bodyMedium,
            color = TextMuted
        )
    }
}

@Composable
private fun ToastHost(viewModel: MainViewModel) {
    val toast by viewModel.toast.collectAsState()
    LaunchedEffect(toast) {
        if (toast != null) viewModel.consumeToast()
    }
}
