package com.quickremote.app.ui.components

import android.content.Context
import android.view.GestureDetector
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
 * SurfaceView 管理 Surface 生命周期（MediaCodec 渲染目标），
 * 并处理触摸手势转换为远程坐标事件：
 * - 单指移动 = 鼠标移动
 * - 单指点击 = 左键
 * - 长按 = 右键
 * - 双指垂直移动 = 滚轮
 *
 * 回调均返回远程桌面坐标（通过 [remoteWidth]/[remoteHeight] 映射）。
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

    /** 左键点击（远程坐标）。 */
    var onLeftClick: ((Int, Int) -> Unit)? = null

    /** 右键点击（远程坐标）。 */
    var onRightClick: ((Int, Int) -> Unit)? = null

    /** 滚轮（远程坐标 + 滚动量）。 */
    var onWheel: ((Int, Int, Int) -> Unit)? = null

    private var viewWidth = 0
    private var viewHeight = 0

    private val gestureDetector = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(e: MotionEvent): Boolean = true

        override fun onSingleTapUp(e: MotionEvent): Boolean {
            val (rx, ry) = mapToRemote(e.x, e.y)
            onLeftClick?.invoke(rx, ry)
            return true
        }

        override fun onLongPress(e: MotionEvent) {
            val (rx, ry) = mapToRemote(e.x, e.y)
            onRightClick?.invoke(rx, ry)
        }

        override fun onScroll(
            e1: MotionEvent?, e2: MotionEvent, distanceX: Float, distanceY: Float
        ): Boolean {
            // 单指拖动 = 鼠标移动
            if (e2.pointerCount == 1) {
                val (rx, ry) = mapToRemote(e2.x, e2.y)
                onMouseMove?.invoke(rx, ry)
            }
            return true
        }
    })

    private val scaleDetector = ScaleGestureDetector(context, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScale(detector: ScaleGestureDetector): Boolean = true
    })

    init {
        holder.addCallback(this)
        holder.setFormat(android.graphics.PixelFormat.OPAQUE)
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
        scaleDetector.onTouchEvent(event)
        gestureDetector.onTouchEvent(event)

        // 双指垂直移动 = 滚轮
        if (event.pointerCount == 2 && event.actionMasked == MotionEvent.ACTION_MOVE) {
            val dy = event.getY(1) - event.getY(0)
            if (abs(dy) > 10) {
                val (rx, ry) = mapToRemote(
                    (event.getX(0) + event.getX(1)) / 2f,
                    (event.getY(0) + event.getY(1)) / 2f
                )
                onWheel?.invoke(rx, ry, -dy.toInt() / 10)
            }
        }

        return true
    }

    /** 视图坐标 → 远程桌面坐标（保持宽高比，居中映射）。 */
    private fun mapToRemote(vx: Float, vy: Float): Pair<Int, Int> {
        if (viewWidth <= 0 || viewHeight <= 0 || remoteWidth <= 0 || remoteHeight <= 0) {
            return Pair(0, 0)
        }
        val scale = min(
            viewWidth.toFloat() / remoteWidth,
            viewHeight.toFloat() / remoteHeight
        )
        val offsetX = (viewWidth - remoteWidth * scale) / 2f
        val offsetY = (viewHeight - remoteHeight * scale) / 2f
        val rx = ((vx - offsetX) / scale).toInt().coerceIn(0, remoteWidth - 1)
        val ry = ((vy - offsetY) / scale).toInt().coerceIn(0, remoteHeight - 1)
        return Pair(rx, ry)
    }
}
