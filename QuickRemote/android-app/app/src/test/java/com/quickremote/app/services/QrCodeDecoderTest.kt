package com.quickremote.app.services

import com.google.zxing.BarcodeFormat
import com.google.zxing.common.BitMatrix
import com.google.zxing.qrcode.QRCodeWriter
import com.quickremote.app.data.PairingPayload
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 扫码解码的本地 JVM 测试。
 *
 * 思路：用 ZXing 的编码器合成二维码位图 → 转成相机那样的亮度平面 → 喂给 [QrCodeDecoder]，
 * 断言解出的文本与原文一致。这样"能不能扫出来"这件事在无设备环境下也有证据，
 * 而不是只能靠真机试。
 */
class QrCodeDecoderTest {

    /** 把 BitMatrix 渲染成 8 位亮度平面：黑=0、白=255（与相机输出的灰度语义一致）。 */
    private fun BitMatrix.toLuminancePlane(): ByteArray {
        val data = ByteArray(width * height)
        for (y in 0 until height) {
            for (x in 0 until width) {
                data[y * width + x] = if (get(x, y)) 0 else 255.toByte()
            }
        }
        return data
    }

    private fun encodeQr(text: String, size: Int = 360): BitMatrix =
        QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size)

    @Test
    fun `解出 PC 端生成的配对二维码`() {
        val payload = PairingPayload.encode("relay.example.com:8443", "psk-abcdef", "书房台式机")
        val matrix = encodeQr(payload)

        val decoded = QrCodeDecoder.decodeYPlane(
            matrix.toLuminancePlane(), matrix.width, matrix.height, matrix.width, matrix.height
        )

        assertEquals(payload, decoded)
        // 解出的文本必须能被配对解析器接受 —— 扫码入口的端到端契约
        assertEquals("relay.example.com:8443", PairingPayload.parse(decoded).getOrThrow().addr)
    }

    @Test
    fun `行填充不影响解码`() {
        val payload = PairingPayload.encode("10.0.0.5:8443", "psk-abcdef", "PC")
        val matrix = encodeQr(payload)
        val padding = 16
        val dataWidth = matrix.width + padding

        // 模拟 rowStride > width：每行末尾补 16 字节，仅前 width 列是真实画面
        val data = ByteArray(dataWidth * matrix.height) { 255.toByte() }
        for (y in 0 until matrix.height) {
            for (x in 0 until matrix.width) {
                data[y * dataWidth + x] = if (matrix.get(x, y)) 0 else 255.toByte()
            }
        }

        val decoded = QrCodeDecoder.decodeYPlane(
            data, dataWidth, matrix.height, matrix.width, matrix.height
        )

        assertEquals(payload, decoded)
    }

    @Test
    fun `纯白画面解不出内容`() {
        assertNull(QrCodeDecoder.decodeYPlane(ByteArray(240 * 240) { 255.toByte() }, 240, 240, 240, 240))
    }

    @Test
    fun `尺寸非法时安全返回 null`() {
        val data = ByteArray(100)
        assertNull(QrCodeDecoder.decodeYPlane(data, 0, 0, 0, 0))
        // 裁剪区大于数据区（脏参数）不应崩溃
        assertNull(QrCodeDecoder.decodeYPlane(data, 10, 10, 20, 20))
    }
}
