package com.quickremote.app.services

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import java.nio.ByteBuffer

/**
 * H.264 硬件解码器（MediaCodec）。
 * 输入 H.264 NAL unit 流，输出到 Surface（零拷贝渲染）。
 */
class H264Decoder(private val logger: Logger = Logger()) {

    private var decoder: MediaCodec? = null
    private var bufferInfo: MediaCodec.BufferInfo = MediaCodec.BufferInfo()

    /** 解码器是否已启动。 */
    val isRunning: Boolean get() = decoder != null

    /** 使用 PC 端上报的分辨率启动解码器。 */
    fun start(surface: Surface, width: Int, height: Int) {
        stop()
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
            decoder = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            decoder!!.configure(format, surface, null, 0)
            decoder!!.start()
            logger.info("H264 decoder started: ${width}x${height}")
        } catch (e: Exception) {
            logger.warn("H264 decoder start failed: ${e.message}")
            decoder = null
        }
    }

    /** 解码一帧 H.264 数据（可能包含 SPS/PPS/IDR 等 NAL unit）。 */
    fun decode(nalData: ByteArray) {
        val dec = decoder ?: return
        try {
            // 输入
            val inputIndex = dec.dequeueInputBuffer(10_000)
            if (inputIndex >= 0) {
                val inputBuffer: ByteBuffer = dec.getInputBuffer(inputIndex) ?: return
                inputBuffer.clear()
                inputBuffer.put(nalData)
                val ptsUs = System.nanoTime() / 1000

                // CSD 处理：SPS(7)/PPS(8) NAL 必须带 BUFFER_FLAG_CODEC_CONFIG，
                // 否则解码器无法初始化参数集（否则画面黑屏或解码失败）
                val nalType = detectNalType(nalData)
                val flags = if (nalType == 7 || nalType == 8) {
                    MediaCodec.BUFFER_FLAG_CODEC_CONFIG
                } else {
                    0
                }
                dec.queueInputBuffer(inputIndex, 0, nalData.size, ptsUs, flags)
            }

            // 输出（释放到 Surface 渲染）
            var outputIndex = dec.dequeueOutputBuffer(bufferInfo, 10_000)
            while (outputIndex >= 0) {
                dec.releaseOutputBuffer(outputIndex, true)
                outputIndex = dec.dequeueOutputBuffer(bufferInfo, 0)
            }
        } catch (e: Exception) {
            // 解码器可能已释放，忽略
        }
    }

    /** 检测 NAL unit 类型（7=SPS, 8=PPS），支持 00 00 00 01 / 00 00 01 / 4字节长度前缀。 */
    private fun detectNalType(data: ByteArray): Int {
        if (data.isEmpty()) return -1
        // 起始码后第一个字节
        var idx = 0
        if (data.size >= 4 &&
            data[0] == 0.toByte() && data[1] == 0.toByte() &&
            data[2] == 0.toByte() && data[3] == 1.toByte()) {
            idx = 4
        } else if (data.size >= 3 &&
            data[0] == 0.toByte() && data[1] == 0.toByte() && data[2] == 1.toByte()) {
            idx = 3
        }
        if (idx >= data.size) return -1
        return data[idx].toInt() and 0x1F
    }

    /** 停止解码器并释放资源。 */
    fun stop() {
        try {
            decoder?.stop()
        } catch (_: Exception) {
        }
        try {
            decoder?.release()
        } catch (_: Exception) {
        }
        decoder = null
    }
}
