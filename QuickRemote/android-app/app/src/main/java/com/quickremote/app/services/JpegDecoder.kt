package com.quickremote.app.services

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.view.Surface

/**
 * JPEG 帧解码器（BitmapFactory）。
 * 系统无 H.264 解码能力或不适用时的回退方案，直接把 JPEG 位图画到 Surface。
 */
class JpegDecoder(private val logger: Logger = Logger()) {

    /** 解码 JPEG 并渲染到 Surface。返回是否成功。 */
    fun decodeToSurface(nalData: ByteArray, surface: Surface?): Boolean {
        val surf = surface ?: return false
        return try {
            val bmp = BitmapFactory.decodeByteArray(nalData, 0, nalData.size) ?: return false
            val canvas = surf.lockHardwareCanvas()
            canvas.drawBitmap(bmp, 0f, 0f, null)
            surf.unlockCanvasAndPost(canvas)
            bmp.recycle()
            true
        } catch (e: Exception) {
            logger.warn("JPEG decode failed: ${e.message}")
            false
        }
    }
}
