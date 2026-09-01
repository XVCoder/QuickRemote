package com.quickremote.app.services

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Rect
import android.view.Surface

/**
 * JPEG 帧解码器（BitmapFactory）。
 * 系统无 H.264 解码能力或不适用时的回退方案，直接把 JPEG 位图画到 Surface。
 *
 * 等比例 height-fit 绘制：高度铺满 Surface buffer，宽度按视频比例（横屏视频宽度超出、
 * 左右居中裁切；宽度不足时左右留白），不依赖 View 布局尺寸，避免拉伸变形。
 */
class JpegDecoder(private val logger: Logger = Logger()) {

    /**
     * 解码 JPEG 并等比例 height-fit 渲染到 Surface，支持 pan/scale。
     * - 等比例 height-fit：基础缩放 baseScale = canvas.height / videoH
     * - 用户缩放 s = baseScale * scale（scale 由双指缩放控制）
     * - 平移 panX/panY 为屏幕像素偏移（用户单指拖动 dx 累加）
     * - 居中 + pan 后画 Rect，dst 超出 canvas 区域自动裁切
     */
    fun decodeToSurface(data: ByteArray, surface: Surface?, videoW: Int, videoH: Int, panX: Float = 0f, panY: Float = 0f, scale: Float = 1f): Boolean {
        val surf = surface ?: return false
        if (videoW <= 0 || videoH <= 0) return false
        return try {
            val bmp = BitmapFactory.decodeByteArray(data, 0, data.size) ?: return false
            val canvas = surf.lockHardwareCanvas()
            val baseScale = canvas.height.toFloat() / videoH
            val s = baseScale * scale
            val dispW = videoW * s
            val dispH = canvas.height.toFloat() * scale
            // 居中位置 + pan 偏移（baseScale 空间居中，即未缩放时的中心）
            val centerX = (canvas.width - videoW * baseScale) / 2f + panX
            val centerY = (canvas.height - canvas.height.toFloat() * scale) / 2f + panY
            canvas.drawColor(android.graphics.Color.BLACK)
            canvas.drawBitmap(bmp, null, Rect(centerX.toInt(), centerY.toInt(), (centerX + dispW).toInt(), (centerY + dispH).toInt()), null)
            surf.unlockCanvasAndPost(canvas)
            bmp.recycle()
            true
        } catch (e: Exception) {
            logger.warn("JPEG decode failed: ${e.message}")
            false
        }
    }
}
