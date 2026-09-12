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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.quickremote.app.data.models.Device
import com.quickremote.app.data.models.DeviceListItem
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Border
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary

/**
 * 设备卡片：设备名 / 系统与版本 / 本机备注 / 连接类型 / 在线状态 / 最近心跳。
 *
 * 右侧操作列为「设置备注」（常驻，在线离线都可用）与「移除」（仅离线设备显示 ——
 * 在线设备不可移除，与 PC 端语义一致）。
 */
@Composable
fun DeviceCard(
    item: DeviceListItem,
    onClick: (Device) -> Unit,
    onEditRemark: (Device) -> Unit,
    onRemove: (Device) -> Unit,
    modifier: Modifier = Modifier
) {
    val device = item.device
    val isOnline = device.isOnline
    val statusColor = if (isOnline) StatusColor.GREEN else StatusColor.YELLOW
    val isLan = isOnline && com.quickremote.app.services.LanUtils.isSameSubnet(device.lan_ip)

    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(BgCard)
            .border(1.dp, Border, RoundedCornerShape(8.dp))
            .clickable { onClick(device) }
            .padding(start = 14.dp, end = 6.dp, top = 12.dp, bottom = 12.dp),
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
                    text = device.displayTitle,
                    style = MaterialTheme.typography.titleSmall,
                    color = if (isOnline) TextPrimary else TextSecondary,
                    fontWeight = FontWeight.Medium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
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
                color = TextMuted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            // 本机备注：仅非空时占一行（仅本机可见，不改设备自身名称）
            if (item.remark.isNotBlank()) {
                Spacer(modifier = Modifier.height(2.dp))
                Text(
                    text = item.remark,
                    style = MaterialTheme.typography.bodySmall,
                    color = Accent,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }

        Column(horizontalAlignment = Alignment.End) {
            // 连接类型标注：局域网（同网段） / 公网（经中继） / 离线
            Text(
                text = when {
                    !isOnline -> "离线"
                    isLan -> "局域网"
                    else -> "公网"
                },
                style = MaterialTheme.typography.labelSmall,
                color = TextMuted,
                fontWeight = FontWeight.Medium
            )
            Spacer(modifier = Modifier.height(2.dp))
            Text(
                text = if (isOnline) "在线" else "离线",
                style = MaterialTheme.typography.labelMedium,
                color = if (isOnline) Success else TextMuted,
                fontWeight = FontWeight.Medium
            )
            Spacer(modifier = Modifier.height(2.dp))
            Text(
                text = formatLastSeen(device.last_seen),
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted
            )
        }

        // 操作列：备注常驻；移除只在离线设备上出现
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            CompactIconButton(
                icon = Icons.Filled.Edit,
                contentDescription = "设置备注",
                tint = TextMuted,
                size = 32.dp,
                iconSize = 18.dp,
                onClick = { onEditRemark(device) }
            )
            if (!isOnline) {
                CompactIconButton(
                    icon = Icons.Filled.Close,
                    contentDescription = "移除设备",
                    tint = TextMuted,
                    size = 32.dp,
                    iconSize = 18.dp,
                    onClick = { onRemove(device) }
                )
            }
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
