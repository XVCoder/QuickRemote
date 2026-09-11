package com.quickremote.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingPayloadTest {

    @Test
    fun `往返编解码保持一致`() {
        val url = PairingPayload.encode("relay.example.com:8444", "abc123", "书房主机")
        val info = PairingPayload.parse(url).getOrThrow()
        assertEquals(1, info.v)
        assertEquals("relay.example.com:8444", info.addr)
        assertEquals("abc123", info.psk)
        assertEquals("书房主机", info.name)
    }

    @Test
    fun `载荷不含 base64 填充等号`() {
        val url = PairingPayload.encode("a:1", "b", "c")
        val d = url.substringAfter("d=")
        assertTrue("不应含 '='：$d", !d.contains('='))
        assertTrue("不应含 '+' 或 '/'：$d", !d.contains('+') && !d.contains('/'))
    }

    @Test
    fun `非 quickremote scheme 被拒绝`() {
        val r = PairingPayload.parse("https://pair?d=eyJ2IjoxfQ")
        assertTrue(r.isFailure)
    }

    @Test
    fun `版本号不为 1 被拒绝`() {
        val raw = """{"v":2,"addr":"a:1","psk":"b","name":"c"}"""
        val url = "quickremote://pair?d=" + PairingPayload.encodeRaw(raw)
        assertTrue(PairingPayload.parse(url).isFailure)
    }

    @Test
    fun `addr 缺少端口被拒绝`() {
        val raw = """{"v":1,"addr":"nohost","psk":"b","name":"c"}"""
        val url = "quickremote://pair?d=" + PairingPayload.encodeRaw(raw)
        assertTrue(PairingPayload.parse(url).isFailure)
    }

    @Test
    fun `psk 为空被拒绝`() {
        val raw = """{"v":1,"addr":"a:1","psk":"","name":"c"}"""
        val url = "quickremote://pair?d=" + PairingPayload.encodeRaw(raw)
        assertTrue(PairingPayload.parse(url).isFailure)
    }

    @Test
    fun `base64 损坏被拒绝且不抛异常`() {
        assertTrue(PairingPayload.parse("quickremote://pair?d=!!!notbase64!!!").isFailure)
    }

    @Test
    fun `缺少 d 参数被拒绝`() {
        assertTrue(PairingPayload.parse("quickremote://pair").isFailure)
    }

    @Test
    fun `无填充 base64url 也能解出`() {
        assertTrue(PairingPayload.parse(PairingPayload.encode("a:1", "b", "")).isSuccess)
    }
}
