package com.quickremote.app.data.models

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 设备展示标题的回退链与在线判定（v1.0.77 抽出为纯逻辑以便测试）。
 *
 * 背景：Android 端原先只用 hostname 作标题，而服务端早就下发了 display_name
 * （PC 端「远程设备」列表一直用的就是它）。两边显示不一致，且 display_name
 * 此前被 Json{ignoreUnknownKeys} 静默丢弃。改为与 PC 端 RemoteDeviceInfo.DisplayName
 * 相同的三级回退链后，用测试锁住语义。
 */
class DeviceDisplayTitleTest {

    @Test
    fun `优先使用服务端 display_name`() {
        val d = Device(device_id = "id1", display_name = "书房主机", hostname = "DESKTOP-ABC")
        assertEquals("书房主机", d.displayTitle)
    }

    @Test
    fun `display_name 为空时回退 hostname`() {
        val d = Device(device_id = "id1", display_name = "", hostname = "DESKTOP-ABC")
        assertEquals("DESKTOP-ABC", d.displayTitle)
    }

    @Test
    fun `display_name 与 hostname 都为空时回退 device_id`() {
        val d = Device(device_id = "id1", display_name = "", hostname = "")
        assertEquals("id1", d.displayTitle)
    }

    @Test
    fun `isOnline 跟随 status 字段`() {
        assertTrue(Device(device_id = "a", status = "online").isOnline)
        assertFalse(Device(device_id = "b", status = "offline").isOnline)
    }
}
