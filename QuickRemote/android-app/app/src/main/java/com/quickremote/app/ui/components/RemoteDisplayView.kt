package com.quickremote.app.ui.components

import android.content.Context
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import com.quickremote.app.services.Logger

/**
 * 远程桌面渲染视图（截屏方案）。
 *
 * SurfaceView 管理 Surface 生命周期，是 MediaCodec 解码器的渲染目标。
 * 触摸事件通过 [touchListener] 回调给上层（阶段 5 转换为输入事件帧发送）。
 */
class RemoteDisplayView(
    context: Context,
    private val logger: Logger = Logger()
) : SurfaceView(context), SurfaceHolder.Callback {

    /** 触摸事件回调：类型 + 坐标（视图坐标）。 */
    var touchListener: ((event: MotionEvent, pointerCount: Int) -> Unit)? = null

    init {
        holder.addCallback(this)
        holder.setFormat(android.graphics.PixelFormat.OPAQUE)
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        logger.info("RemoteDisplayView: surface created")
        onSurfaceChanged?.invoke(holder.surface, holder.surfaceFrame.width(), holder.surfaceFrame.height())
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        logger.info("RemoteDisplayView: surface changed ${width}x${height}")
        onSurfaceChanged?.invoke(holder.surface, width, height)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        logger.info("RemoteDisplayView: surface destroyed")
        onSurfaceChanged?.invoke(null, 0, 0)
    }

    /** Surface 变化回调（surface 可能为 null 表示销毁）。 */
    var onSurfaceChanged: ((surface: android.view.Surface?, width: Int, height: Int) -> Unit)? = null

    override fun onTouchEvent(event: MotionEvent): Boolean {
        touchListener?.invoke(event, event.pointerCount)
        return true
    }
}
