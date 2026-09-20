package com.quickremote.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * [RelayAddress] 的单元测试。
 *
 * 覆盖两类回归：
 * 1. 不再要求手写 `http://` —— 裸 `host:port` 必须能连（且不会被错误补成 https）。
 * 2. 配对载荷里的 PC 控制端口必须换算成 Android 的 HTTP/API 端口（8444 → 8443）。
 */
class RelayAddressTest {

    // ---------- parse ----------

    @Test
    fun `裸 host 无端口`() {
        val p = RelayAddress.parse("relay.example.com")!!
        assertNull(p.scheme)
        assertEquals("relay.example.com", p.host)
        assertNull(p.port)
        assertEquals("", p.path)
    }

    @Test
    fun `host 加端口`() {
        val p = RelayAddress.parse("relay.example.com:8444")!!
        assertNull(p.scheme)
        assertEquals("relay.example.com", p.host)
        assertEquals(8444, p.port)
    }

    @Test
    fun `带 scheme 与路径`() {
        val p = RelayAddress.parse("https://relay.example.com:8443/api")!!
        assertEquals("https", p.scheme)
        assertEquals("relay.example.com", p.host)
        assertEquals(8443, p.port)
        assertEquals("/api", p.path)
    }

    @Test
    fun `IPv6 字面量`() {
        val p = RelayAddress.parse("[::1]:8443")!!
        assertEquals("[::1]", p.host)
        assertEquals(8443, p.port)
    }

    @Test
    fun `非法输入返回 null`() {
        assertNull(RelayAddress.parse(""))
        assertNull(RelayAddress.parse("   "))
        assertNull(RelayAddress.parse("host:notaport"))
        assertNull(RelayAddress.parse("host:99999"))
        assertNull(RelayAddress.parse("http://"))
    }

    // ---------- baseUrl ----------

    @Test
    fun `裸 host 补 http 与默认端口 8443`() {
        assertEquals("http://relay.example.com:8443", RelayAddress.baseUrl("relay.example.com"))
    }

    @Test
    fun `裸 host 加端口不被补 scheme 为 https`() {
        // 回归：此前无 scheme 会被补成 https://，导致「必须手写 http:// 才能连」
        assertEquals("http://relay.example.com:8443", RelayAddress.baseUrl("relay.example.com:8443"))
    }

    @Test
    fun `保留显式 scheme 与端口`() {
        assertEquals("https://relay.example.com:8443", RelayAddress.baseUrl("https://relay.example.com:8443"))
        assertEquals("http://relay.example.com:9000", RelayAddress.baseUrl("http://relay.example.com:9000"))
    }

    @Test
    fun `保留路径并去掉尾部斜杠`() {
        assertEquals("http://relay.example.com:8443/api", RelayAddress.baseUrl("relay.example.com/api/"))
    }

    @Test
    fun `空地址返回空串`() {
        assertEquals("", RelayAddress.baseUrl("   "))
    }

    // ---------- forAndroid（配对导入端口换算） ----------

    @Test
    fun `控制端口换算为 HTTP 端口`() {
        assertEquals("relay.example.com:8443", RelayAddress.forAndroid("relay.example.com:8444"))
    }

    @Test
    fun `自定义控制端口同样减一`() {
        assertEquals("relay.example.com:9444", RelayAddress.forAndroid("relay.example.com:9445"))
    }

    @Test
    fun `已是 HTTP 端口则保持不变`() {
        assertEquals("relay.example.com:8443", RelayAddress.forAndroid("relay.example.com:8443"))
    }

    @Test
    fun `无端口时补默认 HTTP 端口`() {
        assertEquals("relay.example.com:8443", RelayAddress.forAndroid("relay.example.com"))
    }

    @Test
    fun `保留 https 前缀`() {
        assertEquals("https://relay.example.com:8443", RelayAddress.forAndroid("https://relay.example.com:8444"))
    }

    @Test
    fun `80 与 443 视为标准入口原样保留`() {
        assertEquals("relay.example.com:80", RelayAddress.forAndroid("relay.example.com:80"))
        assertEquals("relay.example.com:443", RelayAddress.forAndroid("relay.example.com:443"))
    }

    @Test
    fun `无法解析时回退原始输入`() {
        assertEquals("host:notaport", RelayAddress.forAndroid("host:notaport"))
    }

    // ---------- 端到端：PC 载荷 → Android 地址 ----------

    @Test
    fun `配对载荷端到端换算`() {
        val url = PairingPayload.encode("relay.example.com:8444", "psk", "书房主机")
        val info = PairingPayload.parse(url).getOrThrow()
        assertEquals("relay.example.com:8443", RelayAddress.forAndroid(info.addr))
        // 换算后必须能被 baseUrl 正确规范化
        assertEquals("http://relay.example.com:8443", RelayAddress.baseUrl(RelayAddress.forAndroid(info.addr)))
    }
}
