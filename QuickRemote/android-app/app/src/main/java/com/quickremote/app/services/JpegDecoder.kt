package com.quickremote.app.services

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Rect
import android.view.Surface

/**
 * JPEG 帧解码器（BitmapFactory）。
 * 系统无 H.264 解码能力或不适用时的回退方案，直接把 JPEG 位图画到 Surface。
 *
 * View 已由 Compose 按远程画面宽高比等比布局（高度拉满），Surface buffer 与视频
 * 同比例，位图直接铺满画布即可无变形。pan/scale 完全由 View 变换实现，
 * 画布内不再叠加变换（避免双重平移/缩放）。
 */
class JpegDecoder(private val logger: Logger = Logger()) {

    /**
     * 解码 JPEG 并铺满 Surface 渲染（与 MediaCodec SCALE_TO_FIT 行为一致）。
     */
    fun decodeToSurface(data: ByteArray, surface: Surface?): Boolean {
        val surf = surface ?: return false
        return try {
            val bmp = BitmapFactory.decodeByteArray(data, 0, data.size) ?: return false
            val canvas = surf.lockHardwareCanvas()
            canvas.drawColor(android.graphics.Color.BLACK)
            canvas.drawBitmap(bmp, null, Rect(0, 0, canvas.width, canvas.height), null)
            surf.unlockCanvasAndPost(canvas)
            bmp.recycle()
            true
        } catch (e: Exception) {
            logger.warn("JPEG decode failed: ${e.message}")
            false
        }
    }
}
