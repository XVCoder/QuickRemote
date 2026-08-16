package com.quickremote.app.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.quickremote.app.data.models.Device
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Border
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.ui.theme.Warning

/**
 * 设备卡片组件：机器名、操作系统、在线状态、最后心跳。
 */
@Composable
fun DeviceCard(
    device: Device,
    onClick: (Device) -> Unit,
    modifier: Modifier = Modifier
) {
    val isOnline = device.status == "online"
    val statusColor = if (isOnline) StatusColor.GREEN else StatusColor.YELLOW

    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(BgCard)
            .border(1.dp, Border, RoundedCornerShape(8.dp))
            .clickable { onClick(device) }
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        // 设备图标
        Box(
            modifier = Modifier
                .size(32.dp)
                .clip(RoundedCornerShape(6.dp))
                .background(Color(0xFF1E3A5F)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = Icons.Filled.Computer,
                contentDescription = null,
                tint = TextPrimary,
                modifier = Modifier.size(18.dp)
            )
        }

        Spacer(modifier = Modifier.width(12.dp))

        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = device.hostname.ifBlank { device.device_id },
                    style = MaterialTheme.typography.titleSmall,
                    color = TextPrimary,
                    fontWeight = FontWeight.Medium
                )
                Spacer(modifier = Modifier.width(8.dp))
                StatusIndicator(color = statusColor, size = 6.dp)
            }
            Spacer(modifier = Modifier.height(2.dp))
            Text(
                text = buildString {
                    append(device.os.ifBlank { "Unknown OS" })
                    if (device.version.isNotBlank()) {
                        append(" · v")
                        append(device.version)
                    }
                },
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted
            )
        }

        Column(horizontalAlignment = Alignment.End) {
            Text(
                text = if (isOnline) "在线" else "离线",
                style = MaterialTheme.typography.labelMedium,
                color = if (isOnline) Success else Warning,
                fontWeight = FontWeight.Medium
            )
            Spacer(modifier = Modifier.height(2.dp))
            Text(
                text = formatLastSeen(device.last_seen),
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted
            )
        }
    }
}

/** 格式化最后心跳时间为简短显示。 */
private fun formatLastSeen(lastSeen: String): String {
    if (lastSeen.isBlank()) return "--"
    // last_seen 是 RFC3339 字符串，简单截取时间部分展示。
    return try {
        // 形如 2026-08-01T12:34:56Z 或带时区
        val t = lastSeen.indexOf('T')
        if (t > 0) lastSeen.substring(t + 1, minOf(t + 9, lastSeen.length)) else lastSeen.take(8)
    } catch (_: Exception) {
        lastSeen.take(8)
    }
}
