package com.quickremote.app.services

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ReconnectPolicyTest {

    @Test
    fun `退避序列为 1 2 4 8 15 15 15 15 秒`() {
        val expected = longArrayOf(1000, 2000, 4000, 8000, 15000, 15000, 15000, 15000)
        expected.forEachIndexed { i, ms ->
            assertEquals("第 $i 次", ms, ReconnectPolicy.delayFor(i))
        }
    }

    @Test
    fun `总等待时间约 75 秒`() {
        var sum = 0L
        for (i in 0 until ReconnectPolicy.MAX_ATTEMPTS) sum += ReconnectPolicy.delayFor(i)!!
        assertEquals(75_000L, sum)
    }

    @Test
    fun `超出上限返回 null`() {
        assertNull(ReconnectPolicy.delayFor(ReconnectPolicy.MAX_ATTEMPTS))
        assertNull(ReconnectPolicy.delayFor(8))
        assertNull(ReconnectPolicy.delayFor(-1))
    }

    @Test
    fun `网络与看门狗断开自动重连`() {
        assertTrue(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.Network))
        assertTrue(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.Watchdog))
    }

    @Test
    fun `用户主动断开与鉴权失败不自动重连`() {
        assertFalse(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.UserInitiated))
        assertFalse(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.AuthFailed))
        assertFalse(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.ServerRejected))
    }
}
