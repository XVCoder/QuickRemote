package com.quickremote.app.services

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer

/**
 * H.264 硬件解码器（MediaCodec）。
 * 输入 H.264 NAL unit 流（Annex-B 起始码格式），输出到 Surface（零拷贝渲染）。
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

    /**
     * 解码一帧 H.264 数据。
     * PC 端编码器单次输出可能包含 SPS+PPS+IDR 多个 NAL unit：
     * 必须拆分后分别入队——SPS(7)/PPS(8) 合并为 CODEC_CONFIG，
     * 其余（IDR/P 帧）作为普通输入，否则整块被当配置数据、IDR 帧丢失导致黑屏。
     */
    fun decode(nalData: ByteArray) {
        val dec = decoder ?: return
        try {
            val nals = splitNalUnits(nalData)
            if (nals.isEmpty()) return

            // 1. SPS/PPS 合并为一个 CSD 缓冲（带起始码，Annex-B）
            val csd = ByteArrayOutputStream()
            for (nal in nals) {
                val t = nalType(nal)
                if (t == 7 || t == 8) csd.write(nal)
            }
            if (csd.size() > 0) {
                queueInput(csd.toByteArray(), MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
            }

            // 2. 非 SPS/PPS 的 NAL 按普通帧入队（每个 NAL 单独入队）
            for (nal in nals) {
                val t = nalType(nal)
                if (t != 7 && t != 8) {
                    queueInput(nal, 0)
                }
            }

            // 3. 输出（释放到 Surface 渲染）
            drainOutputs()
        } catch (e: Exception) {
            // 解码器可能已释放，忽略
        }
    }

    /** 入队一个缓冲（带重试，避免偶发取不到输入缓冲导致丢帧破坏参考链）。 */
    private fun queueInput(data: ByteArray, flags: Int) {
        val dec = decoder ?: return
        var attempts = 0
        while (attempts++ < 5) {
            val inputIndex = dec.dequeueInputBuffer(10_000)
            if (inputIndex >= 0) {
                val inputBuffer: ByteBuffer = dec.getInputBuffer(inputIndex) ?: return
                inputBuffer.clear()
                inputBuffer.put(data)
                val ptsUs = System.nanoTime() / 1000
                dec.queueInputBuffer(inputIndex, 0, data.size, ptsUs, flags)
                return
            }
        }
    }

    /** 拉取并渲染所有可用的解码输出。 */
    private fun drainOutputs() {
        val dec = decoder ?: return
        var outputIndex = dec.dequeueOutputBuffer(bufferInfo, 10_000)
        while (outputIndex >= 0) {
            dec.releaseOutputBuffer(outputIndex, true)
            outputIndex = dec.dequeueOutputBuffer(bufferInfo, 0)
        }
    }

    /** 按 Annex-B 起始码（00 00 01 / 00 00 00 01）拆分为独立 NAL（保留起始码）。 */
    private fun splitNalUnits(data: ByteArray): List<ByteArray> {
        val result = mutableListOf<ByteArray>()
        // 找出所有起始码位置（00 00 01）
        val starts = mutableListOf<Int>()
        var i = 0
        while (i < data.size - 3) {
            if (data[i] == 0.toByte() && data[i + 1] == 0.toByte() && data[i + 2] == 1.toByte()) {
                starts.add(i)
                i += 3
            } else {
                i++
            }
        }
        if (starts.isEmpty()) {
            // 无起始码：可能是 4 字节长度前缀格式，整块当单个 NAL 处理
            if (data.isNotEmpty()) result.add(data)
            return result
        }
        for (j in starts.indices) {
            // NAL 载荷从起始码（00 00 01）之后开始
            val payloadStart = starts[j] + 3
            // 结束 = 下一个起始码前（排除属于下一个 4 字节起始码的前导 0）
            var end = if (j + 1 < starts.size) starts[j + 1] else data.size
            if (j + 1 < starts.size && end > 0 && data[end - 1] == 0.toByte()) {
                // 起始码是 4 字节（00 00 00 01）时，前一个 0 属于下一个起始码
                end--
            }
            if (end <= payloadStart) continue
            // 从起始码（含前导 0，保持 00 00 00 01 完整）复制到结束
            val segStart = if (starts[j] > 0 && data[starts[j] - 1] == 0.toByte()) starts[j] - 1 else starts[j]
            if (segStart < end) {
                result.add(data.copyOfRange(segStart, end))
            }
        }
        return result
    }

    /** NAL unit 类型（起始码后第一个字节低 5 位）：7=SPS, 8=PPS, 5=IDR, 1=非 IDR。 */
    private fun nalType(nal: ByteArray): Int {
        if (nal.size < 4) return -1
        // 跳过 00 00 00 01 或 00 00 01 起始码
        val idx = if (nal[2] == 1.toByte()) 3 else 4
        if (idx >= nal.size) return -1
        return nal[idx].toInt() and 0x1F
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
