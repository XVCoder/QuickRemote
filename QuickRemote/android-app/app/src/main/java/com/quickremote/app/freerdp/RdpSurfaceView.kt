package com.quickremote.app.freerdp

import android.content.Context
import android.graphics.Matrix
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import com.quickremote.app.services.Logger

/**
 * RDP 渲染视图。
 *
 * 继承 SurfaceView，管理 Surface 生命周期，处理触摸手势：
 * - 单指移动 = 鼠标移动
 * - 单指点击 = 左键点击
 * - 单指长按 = 右键点击
 * - 双指点击 = 右键点击
 * - 双指缩放 = 画面缩放
 * - 双指拖动 = 滚轮
 *
 * @param client FreeRDP 客户端
 * @param remoteWidth 远程桌面宽度（用于坐标转换）
 * @param remoteHeight 远程桌面高度
 */
class RdpSurfaceView(
    context: Context,
    private val client: FreeRdpClient,
    private val logger: Logger = Logger()
) : SurfaceView(context), SurfaceHolder.Callback {

    /** 远程桌面分辨率。 */
    var remoteWidth: Int = 1280
    var remoteHeight: Int = 720

    /** 缩放比例。 */
    private var scaleFactor = 1f

    /** 触摸坐标到远程坐标的转换矩阵。 */
    private val transformMatrix = Matrix()

    /** 手势检测器。 */
    private val gestureDetector = GestureDetector(context, GestureListener())
    private val scaleDetector = ScaleGestureDetector(context, ScaleListener())

    init {
        holder.addCallback(this)
        // 透明背景，让 FreeRDP 绘制的内容可见
        setZOrderOnTop(true)
        holder.setFormat(android.graphics.PixelFormat.OPAQUE)
    }

    // ==================== Surface 生命周期 ====================

    override fun surfaceCreated(holder: SurfaceHolder) {
        logger.info("RdpSurfaceView: surface created")
        client.setSurface(holder.surface)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        logger.info("RdpSurfaceView: surface changed ${width}x${height}")
        updateTransform(width, height)
        client.setSurface(holder.surface)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        logger.info("RdpSurfaceView: surface destroyed")
        client.setSurface(null)
    }

    /** 更新坐标转换矩阵：将本地触摸坐标映射到远程桌面坐标。 */
    private fun updateTransform(viewWidth: Int, viewHeight: Int) {
        if (viewWidth <= 0 || viewHeight <= 0 || remoteWidth <= 0 || remoteHeight <= 0) return

        // 计算缩放比例（保持宽高比，适配视图）
        val scaleX = viewWidth.toFloat() / remoteWidth
        val scaleY = viewHeight.toFloat() / remoteHeight
        scaleFactor = minOf(scaleX, scaleY)

        // 居中偏移
        val offsetX = (viewWidth - remoteWidth * scaleFactor) / 2f
        val offsetY = (viewHeight - remoteHeight * scaleFactor) / 2f

        transformMatrix.reset()
        transformMatrix.postTranslate(-offsetX, -offsetY)
        transformMatrix.postScale(1f / scaleFactor, 1f / scaleFactor)

        logger.info("Transform updated: scale=$scaleFactor offset=($offsetX,$offsetY)")
    }

    /** 将视图坐标转换为远程桌面坐标。 */
    private fun viewToRemote(x: Float, y: Float): Pair<Int, Int> {
        val pts = floatArrayOf(x, y)
        transformMatrix.mapPoints(pts)
        return Pair(
            pts[0].toInt().coerceIn(0, remoteWidth - 1),
            pts[1].toInt().coerceIn(0, remoteHeight - 1)
        )
    }

    // ==================== 触摸事件 ====================

    override fun onTouchEvent(event: MotionEvent): Boolean {
        scaleDetector.onTouchEvent(event)
        gestureDetector.onTouchEvent(event)

        // 处理双指拖动（滚轮）
        if (event.pointerCount == 2 && event.actionMasked == MotionEvent.ACTION_MOVE) {
            // 简单实现：双指垂直移动差值作为滚轮
            val dy = event.getY(1) - event.getY(0)
            if (kotlin.math.abs(dy) > 10) {
                client.sendWheel(-dy.toInt() / 10)
            }
        }

        return true
    }

    /** 手势监听器。 */
    private inner class GestureListener : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(e: MotionEvent): Boolean = true

        override fun onSingleTapUp(e: MotionEvent): Boolean {
            val (rx, ry) = viewToRemote(e.x, e.y)
            client.sendLeftClick(rx, ry, true)
            client.sendLeftClick(rx, ry, false)
            return true
        }

        override fun onLongPress(e: MotionEvent) {
            // 长按 = 右键
            val (rx, ry) = viewToRemote(e.x, e.y)
            client.sendRightClick(rx, ry, true)
            client.sendRightClick(rx, ry, false)
        }

        override fun onScroll(
            e1: MotionEvent?, e2: MotionEvent, distanceX: Float, distanceY: Float
        ): Boolean {
            // 单指拖动 = 鼠标移动
            if (e1 != null && e2.pointerCount == 1) {
                val (rx, ry) = viewToRemote(e2.x, e2.y)
                client.sendMouseMove(rx, ry)
            }
            return true
        }

        override fun onDoubleTap(e: MotionEvent): Boolean {
            // 双击 = 左键按下+释放（双击打开）
            val (rx, ry) = viewToRemote(e.x, e.y)
            client.sendLeftClick(rx, ry, true)
            client.sendLeftClick(rx, ry, false)
            client.sendLeftClick(rx, ry, true)
            client.sendLeftClick(rx, ry, false)
            return true
        }
    }

    /** 缩放手势监听器。 */
    private inner class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScale(detector: ScaleGestureDetector): Boolean {
            // 缩放手势由视图层处理（可选：通过 Matrix 缩放绘制内容）
            // 当前实现：缩放手势不直接发送到 FreeRDP
            return true
        }
    }
}
