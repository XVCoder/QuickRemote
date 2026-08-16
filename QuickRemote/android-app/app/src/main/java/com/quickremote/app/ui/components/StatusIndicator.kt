package com.quickremote.app.ui.components

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.quickremote.app.ui.theme.Danger
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.Warning

enum class StatusColor { GREEN, YELLOW, RED }

/**
 * 状态指示灯组件：彩色圆点 + 脉冲动画。
 */
@Composable
fun StatusIndicator(
    color: StatusColor,
    size: Dp = 8.dp,
    pulse: Boolean = true,
    modifier: Modifier = Modifier
) {
    val targetColor = when (color) {
        StatusColor.GREEN -> Success
        StatusColor.YELLOW -> Warning
        StatusColor.RED -> Danger
    }

    val transition = rememberInfiniteTransition(label = "status_pulse")
    val alpha by transition.animateFloat(
        initialValue = 1f,
        targetValue = 0.45f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1000),
            repeatMode = RepeatMode.Reverse
        ),
        label = "status_alpha"
    )

    Box(
        modifier = modifier
            .size(size)
            .clip(CircleShape)
            .background(targetColor)
            .then(if (pulse) Modifier.alpha(alpha) else Modifier)
    )
}
