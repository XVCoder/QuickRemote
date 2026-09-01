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

    /** 解码 JPEG 并等比例 height-fit 渲染到 Surface。返回是否成功。 */
    fun decodeToSurface(data: ByteArray, surface: Surface?, videoW: Int, videoH: Int): Boolean {
        val surf = surface ?: return false
        if (videoW <= 0 || videoH <= 0) return false
        return try {
            val bmp = BitmapFactory.decodeByteArray(data, 0, data.size) ?: return false
            val canvas = surf.lockHardwareCanvas()
            // 等比例 height-fit：高度=buffer 高，宽度=videoW*(buffer高/videoH)，居中
            val baseScale = canvas.height.toFloat() / videoH
            val dispW = videoW * baseScale
            val cx = (canvas.width - dispW) / 2f
            canvas.drawColor(android.graphics.Color.BLACK)
            canvas.drawBitmap(bmp, null, Rect(cx.toInt(), 0, (cx + dispW).toInt(), canvas.height), null)
            surf.unlockCanvasAndPost(canvas)
            bmp.recycle()
            true
        } catch (e: Exception) {
            logger.warn("JPEG decode failed: ${e.message}")
            false
        }
    }
}
