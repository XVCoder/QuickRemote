package com.quickremote.app.services

/**
 * 远程桌面帧协议（与 PC 端一致）。
 * 帧格式：[类型 1字节][长度 4字节(小端)][载荷 N字节]
 */
object RemoteFrameProtocol {
    // ============ 帧类型 ============

    /** 视频帧（H.264 NAL unit），PC→Android。 */
    const val TYPE_VIDEO_FRAME: Byte = 0x01

    /** 鼠标事件，Android→PC。 */
    const val TYPE_INPUT_MOUSE: Byte = 0x02

    /** 键盘事件，Android→PC。 */
    const val TYPE_INPUT_KEY: Byte = 0x03

    /** 滚轮事件，Android→PC。 */
    const val TYPE_INPUT_WHEEL: Byte = 0x04

    /** 控制帧（握手/分辨率/帧率），双向。 */
    const val TYPE_CONTROL: Byte = 0x05

    /** 心跳，双向。 */
    const val TYPE_HEARTBEAT: Byte = 0x06

    /** 局域网直连认证，Android→PC：[auth_key 64B hex ASCII]。 */
    const val TYPE_AUTH: Byte = 0x07

    /** 帧头长度：1 类型 + 4 长度。 */
    const val HEADER_SIZE = 5

    /** 单帧最大载荷（16MB）。 */
    const val MAX_PAYLOAD = 16 * 1024 * 1024

    /** 编码帧头。 */
    fun makeHeader(type: Byte, length: Int): ByteArray {
        val header = ByteArray(HEADER_SIZE)
        header[0] = type
        header[1] = (length and 0xFF).toByte()
        header[2] = ((length shr 8) and 0xFF).toByte()
        header[3] = ((length shr 16) and 0xFF).toByte()
        header[4] = ((length shr 24) and 0xFF).toByte()
        return header
    }

    /** 解析帧头载荷长度（header 至少 5 字节）。 */
    fun decodeLength(header: ByteArray): Int {
        return (header[1].toInt() and 0xFF) or
            ((header[2].toInt() and 0xFF) shl 8) or
            ((header[3].toInt() and 0xFF) shl 16) or
            ((header[4].toInt() and 0xFF) shl 24)
    }
}
