package com.quickremote.app.services

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ClipboardChunkerTest {

    @Test
    fun `短文本单片`() {
        val c = ClipboardChunker.split("hello")
        assertEquals(1, c.size)
        assertEquals(0, c[0].seq)
        assertEquals(1, c[0].total)
        assertEquals("hello", c[0].text)
    }

    @Test
    fun `超长文本分多片且拼接后完全一致`() {
        // 15 字节/组 × 8000 = 120000 字节，落在 64KB~256KB 之间 → 应分多片
        val text = "中文abc混合".repeat(8000)
        val c = ClipboardChunker.split(text)
        assertTrue("应分多片，实际 ${c.size}", c.size > 1)
        assertEquals(c.size, c[0].total)
        assertEquals(text, c.sortedBy { it.seq }.joinToString("") { it.text })
    }

    @Test
    fun `每片 UTF-8 字节数不超过上限`() {
        val text = "中".repeat(50000)
        ClipboardChunker.split(text).forEach {
            assertTrue(
                "分片过大：${it.text.toByteArray(Charsets.UTF_8).size}",
                it.text.toByteArray(Charsets.UTF_8).size <= ClipboardChunker.MAX_CHUNK_BYTES
            )
        }
    }

    @Test
    fun `切分不产生替换字符`() {
        val text = "汉字".repeat(30000)
        ClipboardChunker.split(text).forEach {
            assertTrue("出现 U+FFFD，说明切断了码点", !it.text.contains('\uFFFD'))
        }
    }

    @Test
    fun `正好等于单片的边界`() {
        val text = "a".repeat(ClipboardChunker.MAX_CHUNK_BYTES)
        val c = ClipboardChunker.split(text)
        assertEquals(1, c.size)
        assertEquals(text, c[0].text)
    }

    @Test
    fun `超过总量上限返回空列表`() {
        val text = "a".repeat(ClipboardChunker.MAX_TOTAL_BYTES + 1)
        assertTrue(ClipboardChunker.split(text).isEmpty())
    }

    @Test
    fun `空文本单片返回`() {
        val c = ClipboardChunker.split("")
        assertEquals(1, c.size)
        assertEquals("", c[0].text)
    }

    @Test
    fun `组装器收齐后返回全文`() {
        val asm = ClipboardAssembler()
        val text = "中文abc混合".repeat(8000)
        val parts = ClipboardChunker.split(text)
        var result: String? = null
        parts.forEach { result = asm.add("id1", it.seq, it.total, it.text) }
        assertEquals(text, result)
    }

    @Test
    fun `组装器乱序收齐也正确`() {
        val asm = ClipboardAssembler()
        val text = "abcd".repeat(40000)
        val parts = ClipboardChunker.split(text).reversed()
        var result: String? = null
        parts.forEach { result = asm.add("id2", it.seq, it.total, it.text) }
        assertEquals(text, result)
    }

    @Test
    fun `缺片时不返回结果`() {
        val asm = ClipboardAssembler()
        assertNull(asm.add("id3", 0, 3, "a"))
        assertNull(asm.add("id3", 2, 3, "c"))
    }

    @Test
    fun `超时后丢弃整组`() {
        val asm = ClipboardAssembler()
        assertNull(asm.add("id4", 0, 2, "a", nowMs = 0))
        assertNull(asm.add("id4", 1, 2, "b", nowMs = 10_000))
    }
}
