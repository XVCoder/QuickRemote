package com.quickremote.app.services

import androidx.camera.core.ImageProxy
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer

/**
 * 配对二维码解码：CameraX 的 YUV 帧 → 配置串文本。
 *
 * 只识别 QR 码（配对载荷只可能是 QR），并开 TRY_HARDER。
 * **不做旋转预处理**：QR 的定位图形对平面旋转不敏感，横屏/竖屏取景都能识别，
 * 省掉一次整帧旋转的开销。
 *
 * 与 `PairingPayload` 的分工：这里只负责"图像 → 文本"，文本的合法性校验
 * 交给 `PairingPayload.parse`（扫码与粘贴走同一条校验路径）。
 */
object QrCodeDecoder {

    private val hints = mapOf(
        DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
        DecodeHintType.TRY_HARDER to true
    )

    /**
     * 从 CameraX 单帧解出二维码文本；未识别返回 null。
     * 不负责关闭帧 —— 由调用方在 finally 中 close，保持帧生命周期归属清晰。
     */
    fun decode(frame: ImageProxy): String? {
        // YUV_420_888 的 Y 平面就是灰度图，直接当亮度源用，无需彩色转换
        val plane = frame.planes.firstOrNull() ?: return null
        val buffer = plane.buffer
        val data = ByteArray(buffer.remaining())
        buffer.get(data)

        // rowStride 常大于 width（每行末尾有填充字节）。把填充也算进 dataWidth 交给 ZXing
        // 按 stride 索引，再裁回真实画面宽度 —— 否则图像会斜掉，识别率骤降。
        val pixelStride = plane.pixelStride.coerceAtLeast(1)
        val rowPadding = (plane.rowStride - pixelStride * frame.width).coerceAtLeast(0)
        val dataWidth = frame.width + rowPadding / pixelStride

        return decodeYPlane(data, dataWidth, frame.height, frame.width, frame.height)
    }

    /**
     * 纯算法部分（可在本地 JVM 单测中直接调用）：
     * 亮度平面 + 数据尺寸 + 有效画面区域 → 二维码文本。
     *
     * @param yPlane 8 位亮度数据，按 dataWidth 逐行排列（可含行填充）
     * @param dataWidth 数据行宽（含填充）
     * @param dataHeight 数据行数
     * @param cropWidth 有效画面宽度（≤ dataWidth）
     * @param cropHeight 有效画面高度（≤ dataHeight）
     */
    fun decodeYPlane(
        yPlane: ByteArray,
        dataWidth: Int,
        dataHeight: Int,
        cropWidth: Int,
        cropHeight: Int
    ): String? {
        if (yPlane.isEmpty() || dataWidth <= 0 || dataHeight <= 0) return null
        if (cropWidth <= 0 || cropHeight <= 0) return null
        if (cropWidth > dataWidth || cropHeight > dataHeight) return null

        val source = PlanarYUVLuminanceSource(
            yPlane, dataWidth, dataHeight, 0, 0, cropWidth, cropHeight, false
        )
        val reader = MultiFormatReader()
        return try {
            reader.setHints(hints)
            reader.decodeWithState(BinaryBitmap(HybridBinarizer(source))).text
        } catch (e: Exception) {
            // NotFoundException 是常态（绝大多数帧里没有码），不视为异常路径
            null
        } finally {
            reader.reset()
        }
    }
}
