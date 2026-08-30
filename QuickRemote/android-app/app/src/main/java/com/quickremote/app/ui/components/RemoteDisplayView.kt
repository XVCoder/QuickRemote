package com.quickremote.app.ui.components

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.SurfaceHolder
import android.view.SurfaceView
import com.quickremote.app.services.Logger
import kotlin.math.abs
import kotlin.math.min

/**
 * 远程桌面渲染视图（截屏方案）。
 *
 * SurfaceView 管理 Surface 生命周期（MediaCodec 渲染目标），并处理触摸输入：
 * - 单指拖动 = 鼠标移动（流式处理，即时响应，不依赖手势判定阈值）
 * - 单指轻点 = 左键点击
 * - 长按 = 右键
 * - 双指缩放 = 本地画面缩放（1x~5x，围绕屏幕中心）
 * - 双指拖动 = 平移缩放后的画面
 * - 双指垂直滑动 = 滚轮
 *
 * 触摸坐标经 [mapToRemote] 映射为远程桌面坐标（含缩放/平移逆变换）。
 */
class RemoteDisplayView(
    context: Context,
    private val logger: Logger = Logger()
) : SurfaceView(context), SurfaceHolder.Callback {

    /** 远程桌面分辨率（收到控制帧后由上层设置）。 */
    var remoteWidth: Int = 0
    var remoteHeight: Int = 0

    /** Surface 变化回调（surface 可能为 null 表示销毁）。 */
    var onSurfaceChanged: ((android.view.Surface?, Int, Int) -> Unit)? = null

    /** 鼠标移动（远程坐标）。 */
    var onMouseMove: ((Int, Int) -> Unit)? = null

    /** 左键点击（远程坐标，上层负责按下+释放）。 */
    var onLeftClick: ((Int, Int) -> Unit)? = null

    /** 右键点击（远程坐标）。 */
    var onRightClick: ((Int, Int) -> Unit)? = null

    /** 滚轮（远程坐标 + 滚动量）。 */
    var onWheel: ((Int, Int, Int) -> Unit)? = null

    private var viewWidth = 0
    private var viewHeight = 0

    // ============ 显示变换（双指缩放/平移） ============
    private var displayScale = 1f
    private var displayTransX = 0f
    private var displayTransY = 0f

    // ============ 触摸状态 ============
    private val handler = Handler(Looper.getMainLooper())
    private var downX = 0f
    private var downY = 0f
    private var downTime = 0L
    private var moved = false
    private var lastMouseX = Int.MIN_VALUE
    private var lastMouseY = Int.MIN_VALUE

    // 双指捏合
    private var pinchStartDistance = 0f
    private var pinchStartScale = 1f
    private var lastPinchCenterX = 0f
    private var lastPinchCenterY = 0f
    private var pinchActive = false

    // 双指滚轮累计
    private var wheelAccumY = 0f

    private val longPressRunnable = Runnable {
        val (rx, ry) = mapToRemote(downX, downY)
        onRightClick?.invoke(rx, ry)
    }

    private val scaleDetector = ScaleGestureDetector(
        context,
        object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                // 以屏幕中心为基准缩放
                applyScale(displayScale * detector.scaleFactor)
                return true
            }
        }
    )

    init {
        holder.addCallback(this)
        holder.setFormat(android.graphics.PixelFormat.OPAQUE)
        // 确保能收到触摸事件
        isClickable = true
        isFocusable = true
    }

    // ==================== Surface 生命周期 ====================

    override fun surfaceCreated(holder: SurfaceHolder) {
        logger.info("RemoteDisplayView: surface created")
        viewWidth = holder.surfaceFrame.width()
        viewHeight = holder.surfaceFrame.height()
        onSurfaceChanged?.invoke(holder.surface, viewWidth, viewHeight)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        logger.info("RemoteDisplayView: surface changed ${width}x${height}")
        viewWidth = width
        viewHeight = height
        onSurfaceChanged?.invoke(holder.surface, width, height)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        logger.info("RemoteDisplayView: surface destroyed")
        onSurfaceChanged?.invoke(null, 0, 0)
    }

    // ==================== 触摸事件 ====================

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                downTime = System.currentTimeMillis()
                moved = false
                pinchActive = false
                wheelAccumY = 0f
                handler.postDelayed(longPressRunnable, LONG_PRESS_MS)
                return true
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                if (event.pointerCount == 2) {
                    handler.removeCallbacks(longPressRunnable)
                    pinchActive = true
                    pinchStartDistance = distance(event)
                    pinchStartScale = displayScale
                    lastPinchCenterX = centerX(event)
                    lastPinchCenterY = centerY(event)
                    wheelAccumY = 0f
                }
            }

            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount == 1 && !pinchActive) {
                    // 单指：鼠标移动（流式，立即响应）
                    handler.removeCallbacks(longPressRunnable)
                    moved = true
                    val (rx, ry) = mapToRemote(event.x, event.y)
                    if (rx != lastMouseX || ry != lastMouseY) {
                        lastMouseX = rx
                        lastMouseY = ry
                        onMouseMove?.invoke(rx, ry)
                    }
                } else if (event.pointerCount >= 2) {
                    handler.removeCallbacks(longPressRunnable)
                    moved = true
                    scaleDetector.onTouchEvent(event)

                    // 双指平移
                    val cx = centerX(event)
                    val cy = centerY(event)
                    if (pinchActive) {
                        displayTransX += cx - lastPinchCenterX
                        displayTransY += cy - lastPinchCenterY
                        lastPinchCenterX = cx
                        lastPinchCenterY = cy
                        applyTransform()
                    }

                    // 双指垂直滑动 = 滚轮（间距变化小、整体位移大时触发）
                    val dist = distance(event)
                    val dy = cy - lastPinchCenterY
                    if (pinchActive && dist > 0 && abs(dist - pinchStartDistance) < pinchStartDistance * 0.15f) {
                        wheelAccumY += dy
                        if (abs(wheelAccumY) >= WHEEL_THRESHOLD) {
                            val (rx, ry) = mapToRemote(cx, cy)
                            val delta = (-wheelAccumY / WHEEL_THRESHOLD).toInt()
                            onWheel?.invoke(rx, ry, delta)
                            wheelAccumY = 0f
                        }
                    }
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                if (event.pointerCount == 2) {
                    pinchActive = false
                }
            }

            MotionEvent.ACTION_UP -> {
                handler.removeCallbacks(longPressRunnable)
                if (event.pointerCount == 1) {
                    if (!moved) {
                        // 轻点 = 左键（按下+释放）
                        val (rx, ry) = mapToRemote(event.x, event.y)
                        onLeftClick?.invoke(rx, ry)
                    }
                }
                resetTouchState()
                return true
            }

            MotionEvent.ACTION_CANCEL -> {
                handler.removeCallbacks(longPressRunnable)
                resetTouchState()
            }
        }
        return true
    }

    private fun resetTouchState() {
        moved = false
        pinchActive = false
        wheelAccumY = 0f
        lastMouseX = Int.MIN_VALUE
        lastMouseY = Int.MIN_VALUE
    }

    // ============ 显示变换 ============

    private fun applyScale(newScale: Float) {
        val clamped = newScale.coerceIn(1f, 5f)
        if (abs(clamped - displayScale) < 0.001f) return
        // 围绕屏幕中心缩放
        val cx = viewWidth / 2f
        val cy = viewHeight / 2f
        val ratio = clamped / displayScale
        displayTransX = (displayTransX - cx) * ratio + cx
        displayTransY = (displayTransY - cy) * ratio + cy
        displayScale = clamped
        applyTransform()
    }

    private fun applyTransform() {
        scaleX = displayScale
        scaleY = displayScale
        pivotX = 0f
        pivotY = 0f
        translationX = displayTransX
        translationY = displayTransY
    }

    /** 重置缩放/平移（会话重连或分辨率变化时）。 */
    fun resetTransform() {
        displayScale = 1f
        displayTransX = 0f
        displayTransY = 0f
        scaleX = 1f
        scaleY = 1f
        translationX = 0f
        translationY = 0f
    }

    // ============ 坐标映射 ============

    /** 视图坐标 → 远程桌面坐标（先逆变换缩放/平移，再按宽高比居中映射）。 */
    private fun mapToRemote(vx: Float, vy: Float): Pair<Int, Int> {
        if (viewWidth <= 0 || viewHeight <= 0 || remoteWidth <= 0 || remoteHeight <= 0) {
            return Pair(0, 0)
        }
        // 逆变换：恢复未缩放/未平移的视图坐标
        val cx = viewWidth / 2f
        val cy = viewHeight / 2f
        val nx = (vx - displayTransX - cx) / displayScale + cx
        val ny = (vy - displayTransY - cy) / displayScale + cy

        val scale = min(
            viewWidth.toFloat() / remoteWidth,
            viewHeight.toFloat() / remoteHeight
        )
        val offsetX = (viewWidth - remoteWidth * scale) / 2f
        val offsetY = (viewHeight - remoteHeight * scale) / 2f
        val rx = ((nx - offsetX) / scale).toInt().coerceIn(0, remoteWidth - 1)
        val ry = ((ny - offsetY) / scale).toInt().coerceIn(0, remoteHeight - 1)
        return Pair(rx, ry)
    }

    // ============ 双指几何 ============

    private fun distance(event: MotionEvent): Float {
        val dx = event.getX(0) - event.getX(1)
        val dy = event.getY(0) - event.getY(1)
        return kotlin.math.sqrt(dx * dx + dy * dy)
    }

    private fun centerX(event: MotionEvent): Float = (event.getX(0) + event.getX(1)) / 2f

    private fun centerY(event: MotionEvent): Float = (event.getY(0) + event.getY(1)) / 2f

    companion object {
        private const val LONG_PRESS_MS = 550L
        private const val WHEEL_THRESHOLD = 40f
    }
}
