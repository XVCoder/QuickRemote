package com.quickremote.app.ui.components

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * 紧凑圆形图标按钮。
 *
 * 为什么不用 Material3 的 IconButton：它自带 48dp 最小触控尺寸，`Modifier.size()` 压不下去，
 * 在密集工具栏/列表行里会撑破布局。这里自写 Box + clickable 精确控制尺寸。
 *
 * 原为 RemoteSessionScreen 的私有实现；v1.0.77 设备卡片也要用同一规格，故抽到公共组件。
 */
@Composable
fun CompactIconButton(
    icon: ImageVector,
    contentDescription: String,
    tint: Color,
    size: Dp = 38.dp,
    iconSize: Dp = 22.dp,
    onClick: () -> Unit
) {
    Box(
        modifier = Modifier
            .size(size)
            .clip(CircleShape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            icon,
            contentDescription = contentDescription,
            tint = tint,
            modifier = Modifier.size(iconSize)
        )
    }
}
