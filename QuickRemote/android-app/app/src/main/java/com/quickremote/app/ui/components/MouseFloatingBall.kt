package com.quickremote.app.ui.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.OpenWith
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.BorderLight
import com.quickremote.app.ui.theme.TextPrimary
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.roundToInt

/** 鼠标按键：输入帧的按下/释放动作码。 */
enum class PadButton(val actionDown: Int, val actionUp: Int) {
    /** 左键。 */
    LEFT(1, 2),

    /** 中键（浏览器新标签打开链接、关闭标签页等）。 */
    MIDDLE(5, 6),

    /** 右键（上下文菜单）。 */
    RIGHT(3, 4)
}

/**
 * 鼠标悬浮球（向日葵式精准操控）。
 *
 * - 收起态：46dp 圆球（正常不透明度 70%；5 秒未使用降至 50%，任意使用含拖动即恢复），
 *   拖动移位（松手保持原位，不自动贴边）
 * - 长按球拖动 = 远程光标相对移动（球高亮提示，屏幕一划约 1.8 屏宽；
 *   画面层绘制虚拟光标指示位置）
 * - 点击球 = 展开三键圆盘（球隐藏）：116dp 的圆，上 2/3 被两条竖缝三等分——
 *   左键（左弧块，形如"("）、中键（圆角矩形块，"三"形滚轮图标）、
 *   右键（右弧块，形如")"）；下 1/3 横向切割合并成整块光标控制区
 *   - 左键/右键：点击直接发送对应按键（坐标 = 当前光标位置）
 *   - 中键：点击 = 中键点击；按住上下滑动 = 鼠标滚轮（上滑往上滚、下滑往下滚）
 *   - 光标控制区：轻点 = 左键单击；按住拖动 = 远程光标相对移动
 *     （与长按球拖动同款，拖动中高亮）
 *   - 右上角小关闭按钮：收起圆盘回到悬浮球形态（不自动贴边）
 *   - 右下角小拖动按钮：按住拖动可调整圆盘位置（收起后悬浮球出现在该位置）
 *
 * 远程光标位置由外部持有（提升到 RemoteSessionScreen，画面层据此绘制虚拟光标）：
 * cursorX/Y < 0 表示尚未使用（视为屏幕中心）。
 * 展开/收起状态同样由外部持有（padExpanded/onPadExpandedChange）：
 * RemoteSessionScreen 据此在收缩态启用画面外空白区的鼠标手势层。
 * 根节点 fillMaxSize 铺满父容器但自身不拦截触摸，仅球与按键区域响应。
 */
@Composable
fun MouseFloatingBall(
    remoteWidth: Int,
    remoteHeight: Int,
    cursorX: Float,
    cursorY: Float,
    padExpanded: Boolean,
    onPadExpandedChange: (Boolean) -> Unit,
    onCursorMove: (x: Int, y: Int) -> Unit,
    onCursorChange: (x: Float, y: Float) -> Unit,
    onButtonClick: (button: PadButton, x: Int, y: Int) -> Unit,
    onWheel: (x: Int, y: Int, deltaV: Int, deltaH: Int) -> Unit,
    modifier: Modifier = Modifier
) {
    BoxWithConstraints(modifier = modifier) {
        val density = LocalDensity.current
        val parentWpx = constraints.maxWidth.toFloat()
        val parentHpx = constraints.maxHeight.toFloat()
        val scope = rememberCoroutineScope()

        val ballSizePx = with(density) { BallSize.toPx() }
        val edgeMarginPx = with(density) { EdgeMargin.toPx() }
        // 三键圆盘与角落按钮（展开容器 = PadSize + CloseSize）
        val padSizePx = with(density) { PadSize.toPx() }
        val closeSizePx = with(density) { CloseSize.toPx() }
        val expandSizePx = padSizePx + closeSizePx
        val padGapPx = with(density) { PadGap.toPx() }
        // 圆盘纵向分区：上 2/3 = 三键区（横缝居中于 2/3 切线，与竖缝同宽），下 1/3 = 光标控制区
        val keysAreaHpx = padSizePx * 2f / 3f - padGapPx / 2f
        val cursorPadYpx = padSizePx * 2f / 3f + padGapPx / 2f
        val cursorPadHpx = padSizePx / 3f - padGapPx / 2f
        // 中键滚轮手势参数（每格滚动的滑动距离）
        val wheelStepPx = with(density) { 16.dp.toPx() }

        val maxX = max(0f, parentWpx - ballSizePx)
        val maxY = max(0f, parentHpx - ballSizePx)
        // 展开态（圆盘+角落按钮的容器）完整可见时球原点的可动范围
        val expMinX = max(0f, (expandSizePx - ballSizePx) / 2f)
        val expMaxX = max(expMinX, parentWpx - (expandSizePx + ballSizePx) / 2f)
        val expMinY = max(0f, (expandSizePx - ballSizePx) / 2f)
        val expMaxY = max(expMinY, parentHpx - (expandSizePx + ballSizePx) / 2f)

        // 长按光标模式（球高亮提示）
        var cursorMode by remember { mutableStateOf(false) }

        // ---- 闲置透明度：5 秒未使用降至 50%，任意使用（含拖动过程）恢复 70% ----
        var idle by remember { mutableStateOf(false) }
        var usedTick by remember { mutableStateOf(0) }
        val bgAlpha by animateFloatAsState(
            targetValue = if (idle) 0.5f else 0.7f,
            animationSpec = tween(durationMillis = 200)
        )
        LaunchedEffect(usedTick) {
            idle = false
            delay(IDLE_TIMEOUT_MS)
            idle = true
        }

        /** 标记一次使用（重置闲置计时）。 */
        fun markUsed() {
            usedTick++
        }

        // 球位置（拖动松手保持原位）
        val ballPos = remember {
            Animatable(Offset(max(0f, parentWpx - ballSizePx - edgeMarginPx), parentHpx * 0.62f), Offset.VectorConverter)
        }

        // 远程光标位置（外部持有；< 0 = 尚未使用触摸板，视为屏幕中心）
        val effCursorX = if (cursorX >= 0) cursorX
            else if (remoteWidth > 0) remoteWidth / 2f else 960f
        val effCursorY = if (cursorY >= 0) cursorY
            else if (remoteHeight > 0) remoteHeight / 2f else 540f

        // pointerInput 的 lambda 不随外部状态变化重启，捕获的局部值会陈旧——经
        // rememberUpdatedState 中转读取最新值
        val latestCursorX by rememberUpdatedState(effCursorX)
        val latestCursorY by rememberUpdatedState(effCursorY)
        val latestRemoteW by rememberUpdatedState(max(1, remoteWidth))
        val latestRemoteH by rememberUpdatedState(max(1, remoteHeight))
        val latestOnCursorMove by rememberUpdatedState(onCursorMove)
        val latestOnCursorChange by rememberUpdatedState(onCursorChange)
        val latestOnButtonClick by rememberUpdatedState(onButtonClick)
        val latestOnWheel by rememberUpdatedState(onWheel)
        val latestOnPadExpandedChange by rememberUpdatedState(onPadExpandedChange)

        /** 长按拖动：光标相对移动（屏幕一划 ≈ 1.8 屏宽）。 */
        fun moveCursorBy(dx: Float, dy: Float) {
            val gain = 1.8f * latestRemoteW / parentWpx.coerceAtLeast(1f)
            val nx = (latestCursorX + dx * gain).coerceIn(0f, (latestRemoteW - 1).toFloat())
            val ny = (latestCursorY + dy * gain).coerceIn(0f, (latestRemoteH - 1).toFloat())
            latestOnCursorChange(nx, ny)
            latestOnCursorMove(nx.roundToInt(), ny.roundToInt())
        }

        // ============ 球本体（展开时隐藏，由三键圆盘替代） ============
        if (!padExpanded) {
            Box(
                modifier = Modifier
                    .offset {
                        IntOffset(
                            ballPos.value.x.coerceIn(0f, maxX).roundToInt(),
                            ballPos.value.y.coerceIn(0f, maxY).roundToInt()
                        )
                    }
                    .size(BallSize)
                    .clip(CircleShape)
                    .background((if (cursorMode) Accent else BgCard).copy(alpha = bgAlpha))
                    .border(1.dp, if (cursorMode) Accent else BorderLight, CircleShape)
                    // 手势按链序处理（前者优先消费）：
                    // 长按后拖动 = 移动光标；立即拖动 = 移动球（松手保持原位）；快速点按 = 展开圆盘
                    .pointerInput(parentWpx, parentHpx) {
                        detectDragGesturesAfterLongPress(
                            onDragStart = {
                                markUsed()
                                cursorMode = true
                            },
                            onDrag = { change, amount ->
                                change.consume()
                                markUsed()
                                moveCursorBy(amount.x, amount.y)
                            },
                            onDragEnd = { cursorMode = false },
                            onDragCancel = { cursorMode = false }
                        )
                    }
                    .pointerInput(parentWpx, parentHpx) {
                        detectDragGestures(
                            onDragStart = { markUsed() },
                            onDrag = { change, dragAmount ->
                                change.consume()
                                markUsed()
                                scope.launch {
                                    ballPos.snapTo(
                                        Offset(
                                            (ballPos.value.x + dragAmount.x).coerceIn(0f, maxX),
                                            (ballPos.value.y + dragAmount.y).coerceIn(0f, maxY)
                                        )
                                    )
                                }
                            }
                        )
                    }
                    .pointerInput(Unit) {
                        detectTapGestures(onTap = {
                            markUsed()
                            latestOnPadExpandedChange(true)
                        })
                    },
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    Icons.Filled.Mouse,
                    contentDescription = "鼠标悬浮球",
                    tint = if (cursorMode) Color.White else TextPrimary
                )
            }
        }

        // ============ 三键圆盘 + 关闭/拖动按钮（淡入淡出） ============
        // 容器中心 = 球中心（球位置收敛到容器完整可见的范围）
        AnimatedVisibility(
            visible = padExpanded,
            enter = fadeIn(animationSpec = tween(durationMillis = 150)),
            exit = fadeOut(animationSpec = tween(durationMillis = 150)),
            modifier = Modifier
                .offset {
                    val cx = ballPos.value.x.coerceIn(expMinX, expMaxX) + ballSizePx / 2f
                    val cy = ballPos.value.y.coerceIn(expMinY, expMaxY) + ballSizePx / 2f
                    IntOffset(
                        (cx - expandSizePx / 2f).roundToInt(),
                        (cy - expandSizePx / 2f).roundToInt()
                    )
                }
                .size(with(density) { expandSizePx.toDp() })
        ) {
            // 容器本身无手势（透传画面触摸），仅按键与角落按钮区域响应
            Box(modifier = Modifier.fillMaxSize()) {
                // 光标控制区拖动中（高亮提示，与球长按光标模式同款反馈）
                var cursorPadActive by remember { mutableStateOf(false) }

                // ---- 圆盘：上 2/3 三键区（两条竖缝三等分）+ 下 1/3 光标控制区 ----
                // 外层 CircleShape 裁剪命中与绘制；三键为竖条矩形，经圆裁剪后
                // 左块形如"("、中块为圆角矩形、右块形如")"；下 1/3 横切合并为整块
                Box(
                    modifier = Modifier
                        .align(Alignment.Center)
                        .size(with(density) { padSizePx.toDp() })
                        .clip(CircleShape)
                ) {
                    // ---- 左键（左 1/3 弧块） ----
                    Box(
                        modifier = Modifier
                            .size(
                                width = with(density) { (padSizePx / 3f - padGapPx / 2f).toDp() },
                                height = with(density) { keysAreaHpx.toDp() }
                            )
                            .background(BgCard.copy(alpha = bgAlpha))
                            .pointerInput(Unit) {
                                detectTapGestures {
                                    markUsed()
                                    latestOnButtonClick(
                                        PadButton.LEFT,
                                        latestCursorX.roundToInt(),
                                        latestCursorY.roundToInt()
                                    )
                                }
                            }
                    )

                    // ---- 中键（中间 1/3 圆角矩形块）：点击 = 中键；按住上下滑 = 滚轮 ----
                    Box(
                        modifier = Modifier
                            .offset {
                                IntOffset((padSizePx / 3f + padGapPx / 2f).roundToInt(), 0)
                            }
                            .size(
                                width = with(density) { (padSizePx / 3f - padGapPx).toDp() },
                                height = with(density) { keysAreaHpx.toDp() }
                            )
                            .background(BgCard.copy(alpha = bgAlpha))
                            // 手势按链序处理：垂直拖动 = 滚轮（官方检测器，超触摸阈值后
                            // 逐帧回调位移，每滑 wheelStepPx 触发一格）；轻点 = 中键点击。
                            // （旧实现用 positionChange() 取位移，事件被消费后恒返回零，
                            // 导致滑动完全无反应）
                            .pointerInput(Unit) {
                                var accum = 0f
                                detectVerticalDragGestures(
                                    onDragStart = { markUsed() },
                                    onVerticalDrag = { change, dragAmount ->
                                        change.consume()
                                        markUsed()
                                        accum += dragAmount
                                        // 与双指手势同向：上滑 deltaV 正（往上滚），下滑负
                                        while (abs(accum) >= wheelStepPx) {
                                            val deltaV = if (accum > 0) -1 else 1
                                            latestOnWheel(
                                                latestCursorX.roundToInt(),
                                                latestCursorY.roundToInt(),
                                                deltaV, 0
                                            )
                                            accum -= if (accum > 0) wheelStepPx else -wheelStepPx
                                        }
                                    },
                                    onDragEnd = { accum = 0f },
                                    onDragCancel = { accum = 0f }
                                )
                            }
                            .pointerInput(Unit) {
                                detectTapGestures {
                                    markUsed()
                                    latestOnButtonClick(
                                        PadButton.MIDDLE,
                                        latestCursorX.roundToInt(),
                                        latestCursorY.roundToInt()
                                    )
                                }
                            },
                        contentAlignment = Alignment.Center
                    ) {
                        // 滚轮纹理（类似"三"的三条横线）
                        Column(
                            verticalArrangement = Arrangement.spacedBy(4.dp),
                            horizontalAlignment = Alignment.CenterHorizontally
                        ) {
                            repeat(3) {
                                Box(
                                    Modifier
                                        .size(width = 18.dp, height = 2.5.dp)
                                        .clip(RoundedCornerShape(1.dp))
                                        .background(TextPrimary)
                                )
                            }
                        }
                    }

                    // ---- 右键（右 1/3 弧块） ----
                    Box(
                        modifier = Modifier
                            .offset {
                                IntOffset((padSizePx * 2f / 3f + padGapPx / 2f).roundToInt(), 0)
                            }
                            .size(
                                width = with(density) { (padSizePx / 3f - padGapPx / 2f).toDp() },
                                height = with(density) { keysAreaHpx.toDp() }
                            )
                            .background(BgCard.copy(alpha = bgAlpha))
                            .pointerInput(Unit) {
                                detectTapGestures {
                                    markUsed()
                                    latestOnButtonClick(
                                        PadButton.RIGHT,
                                        latestCursorX.roundToInt(),
                                        latestCursorY.roundToInt()
                                    )
                                }
                            }
                    )

                    // ---- 光标控制区（下 1/3 横切合并的整块，经圆裁剪呈弓形） ----
                    // 轻按住拖动 = 远程光标相对移动（与长按球拖动同款增益），
                    // 拖动中整块高亮提示；轻点 = 左键单击（坐标 = 当前光标位置）
                    Box(
                        modifier = Modifier
                            .offset { IntOffset(0, cursorPadYpx.roundToInt()) }
                            .size(
                                width = with(density) { padSizePx.toDp() },
                                height = with(density) { cursorPadHpx.toDp() }
                            )
                            .background((if (cursorPadActive) Accent else BgCard).copy(alpha = bgAlpha))
                            .pointerInput(parentWpx, parentHpx) {
                                detectDragGestures(
                                    onDragStart = {
                                        markUsed()
                                        cursorPadActive = true
                                    },
                                    onDrag = { change, dragAmount ->
                                        change.consume()
                                        markUsed()
                                        moveCursorBy(dragAmount.x, dragAmount.y)
                                    },
                                    onDragEnd = { cursorPadActive = false },
                                    onDragCancel = { cursorPadActive = false }
                                )
                            }
                            .pointerInput(Unit) {
                                detectTapGestures {
                                    markUsed()
                                    latestOnButtonClick(
                                        PadButton.LEFT,
                                        latestCursorX.roundToInt(),
                                        latestCursorY.roundToInt()
                                    )
                                }
                            }
                    )
                }

                // ---- 关闭按钮（圆盘右上角）：收起圆盘回到悬浮球（不自动贴边） ----
                Box(
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .size(CloseSize)
                        .clip(CircleShape)
                        .background(BgCard.copy(alpha = bgAlpha))
                        .border(1.dp, BorderLight, CircleShape)
                        .pointerInput(Unit) {
                                detectTapGestures {
                                    markUsed()
                                    latestOnPadExpandedChange(false)
                                }
                            },
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Filled.Close,
                        contentDescription = "收起按键",
                        tint = TextPrimary,
                        modifier = Modifier.size(12.dp)
                    )
                }

                // ---- 拖动按钮（圆盘右下角）：按住拖动调整圆盘位置 ----
                Box(
                    modifier = Modifier
                        .align(Alignment.BottomEnd)
                        .size(CloseSize)
                        .clip(CircleShape)
                        .background(BgCard.copy(alpha = bgAlpha))
                        .border(1.dp, BorderLight, CircleShape)
                        .pointerInput(parentWpx, parentHpx) {
                            detectDragGestures(
                                onDragStart = { markUsed() },
                                onDrag = { change, dragAmount ->
                                    change.consume()
                                    markUsed()
                                    scope.launch {
                                        ballPos.snapTo(
                                            Offset(
                                                (ballPos.value.x + dragAmount.x).coerceIn(expMinX, expMaxX),
                                                (ballPos.value.y + dragAmount.y).coerceIn(expMinY, expMaxY)
                                            )
                                        )
                                    }
                                }
                            )
                        },
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Filled.OpenWith,
                        contentDescription = "移动按键盘",
                        tint = TextPrimary,
                        modifier = Modifier.size(12.dp)
                    )
                }
            }
        }
    }
}

private val BallSize = 46.dp
/** 三键圆盘直径（比悬浮球大，约为球的两倍）。 */
private val PadSize = 116.dp
/** 三块之间的竖缝宽度。 */
private val PadGap = 6.dp
/** 展开态圆盘角落的关闭/拖动按钮尺寸。 */
private val CloseSize = 20.dp
private val EdgeMargin = 12.dp
/** 闲置判定时长：超过此时间未使用降至 50% 透明度。 */
private const val IDLE_TIMEOUT_MS = 5000L
