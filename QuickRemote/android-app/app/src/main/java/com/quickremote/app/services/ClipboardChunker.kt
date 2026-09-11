package com.quickremote.app.services

/**
 * 剪贴板文本分片器。
 *
 * 切割点必须落在**字符边界**（码点边界），否则会产生 U+FFFD 损坏内容 ——
 * 这是本模块唯一真正容易写错的地方，故按码点推进而不是按 UTF-16 char 推进。
 *
 * 与 PC 端 Interop/ClipboardChunker.cs 共享同一套常量与规则，改动需同步两端。
 */
object ClipboardChunker {

    /** 单片上限（UTF-8 字节）。 */
    const val MAX_CHUNK_BYTES = 64 * 1024

    /** 单次同步总量上限（UTF-8 字节），超出直接放弃而非截断。 */
    const val MAX_TOTAL_BYTES = 256 * 1024

    data class Chunk(val seq: Int, val total: Int, val text: String)

    /**
     * 分片。总量超限时返回空列表（调用方应放弃本次同步）。
     * 空文本返回单片，保证"复制了空内容"也能正常同步。
     */
    fun split(text: String): List<Chunk> {
        val totalBytes = text.toByteArray(Charsets.UTF_8).size
        if (totalBytes > MAX_TOTAL_BYTES) return emptyList()

        val pieces = mutableListOf<String>()
        var pieceStart = 0
        var bytesInPiece = 0

        var i = 0
        while (i < text.length) {
            val codePoint = text.codePointAt(i)
            val charCount = Character.charCount(codePoint)
            val segBytes = utf8Length(codePoint)

            if (bytesInPiece + segBytes > MAX_CHUNK_BYTES && bytesInPiece > 0) {
                pieces.add(text.substring(pieceStart, i))
                pieceStart = i
                bytesInPiece = 0
            }
            bytesInPiece += segBytes
            i += charCount
        }
        pieces.add(text.substring(pieceStart))

        val total = pieces.size
        return pieces.mapIndexed { idx, s -> Chunk(idx, total, s) }
    }

    /** UTF-8 编码后该码点占用的字节数（免去为每个码点分配临时字符串）。 */
    private fun utf8Length(codePoint: Int): Int = when {
        codePoint < 0x80 -> 1
        codePoint < 0x800 -> 2
        codePoint < 0x10000 -> 3
        else -> 4
    }
}

/**
 * 剪贴板分片组装器。
 *
 * 按 id 归组，收齐后按 seq 顺序拼接；超过 [timeoutMs] 仍未收齐的组会被丢弃，
 * 避免断流后残留的半个载荷永久占内存或被后续分片误拼接。
 */
class ClipboardAssembler(private val timeoutMs: Long = 5_000) {

    private class Pending(
        val total: Int,
        val parts: MutableMap<Int, String>,
        val startedAt: Long
    )

    private val pending = mutableMapOf<String, Pending>()

    /** 加入一片。集齐返回全文，否则返回 null。 */
    fun add(
        id: String,
        seq: Int,
        total: Int,
        text: String,
        nowMs: Long = System.currentTimeMillis()
    ): String? {
        purgeExpired(nowMs)

        if (total <= 0 || seq < 0 || seq >= total) return null

        val existing = pending.getOrPut(id) { Pending(total, mutableMapOf(), nowMs) }
        // 同 id 但 total 不一致 → 视为对端开了新一组，丢弃旧的
        if (existing.total != total) {
            pending[id] = Pending(total, mutableMapOf(), nowMs)
        }

        val entry = pending[id]!!
        entry.parts[seq] = text

        if (entry.parts.size < entry.total) return null

        val full = StringBuilder()
        for (i in 0 until entry.total) {
            val part = entry.parts[i] ?: return null
            full.append(part)
        }
        pending.remove(id)
        return full.toString()
    }

    private fun purgeExpired(nowMs: Long) {
        val iterator = pending.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next().value
            if (nowMs - entry.startedAt > timeoutMs) iterator.remove()
        }
    }
}
