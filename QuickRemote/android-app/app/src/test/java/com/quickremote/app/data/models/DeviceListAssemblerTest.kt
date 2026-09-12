package com.quickremote.app.data.models

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 设备列表装配：在线/离线分组、备注附加、隐藏过滤、上线复活（v1.0.77）。
 *
 * 装配规则必须与 PC 端 pc-client/ViewModels/MainViewModel.cs 的 OnDeviceListUpdated
 * 逐条一致 —— 尤其是「复活判定在隐藏过滤之前、且只对在线设备成立」这个顺序，
 * 顺序反了就会出现「软删除的设备永远回不来」或「离线设备删不掉」。
 */
class DeviceListAssemblerTest {

    private fun dev(id: String, status: String, name: String = "") =
        Device(device_id = id, display_name = name, hostname = "host-$id", status = status)

    @Test
    fun `空输入产出空结果`() {
        val r = assembleDeviceList(emptyList())
        assertTrue(r.online.isEmpty())
        assertTrue(r.offline.isEmpty())
        assertTrue(r.revived.isEmpty())
        assertTrue(r.isEmpty)
    }

    @Test
    fun `按状态分成两段且保持输入顺序`() {
        val r = assembleDeviceList(
            listOf(dev("a", "online"), dev("b", "offline"), dev("c", "online"))
        )
        assertEquals(listOf("a", "c"), r.online.map { it.device.device_id })
        assertEquals(listOf("b"), r.offline.map { it.device.device_id })
        assertEquals(3, r.total)
    }

    @Test
    fun `备注按 device_id 附加 无备注为空串`() {
        val r = assembleDeviceList(
            listOf(dev("a", "online"), dev("b", "offline")),
            remarks = mapOf("b" to "客厅小主机")
        )
        assertEquals("", r.online[0].remark)
        assertEquals("客厅小主机", r.offline[0].remark)
    }

    @Test
    fun `隐藏的离线设备不展示且不触发复活`() {
        val r = assembleDeviceList(
            listOf(dev("a", "online"), dev("b", "offline")),
            hidden = setOf("b")
        )
        assertEquals(listOf("a"), r.online.map { it.device.device_id })
        assertTrue(r.offline.isEmpty())
        assertTrue(r.revived.isEmpty())
    }

    @Test
    fun `隐藏设备重新上线时复活并展示`() {
        val r = assembleDeviceList(
            listOf(dev("a", "online"), dev("b", "online")),
            hidden = setOf("b")
        )
        assertEquals(listOf("a", "b"), r.online.map { it.device.device_id })
        assertEquals(setOf("b"), r.revived)
    }

    @Test
    fun `device_id 为空的脏数据被丢弃`() {
        val r = assembleDeviceList(listOf(Device(device_id = ""), dev("a", "online")))
        assertEquals(listOf("a"), r.online.map { it.device.device_id })
        assertEquals(0, r.offline.size)
    }
}
