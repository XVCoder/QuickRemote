package com.quickremote.app.data.local

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 设备备注规范化 + 备注表 JSON 编解码（v1.0.77）。
 *
 * 备注是「仅本机可见」的私有数据：存 DataStore，不上传服务端、不改设备名。
 * DataStore 的 Preferences 没有 Map 类型，故备注表序列化成 JSON 存在单个字符串键里 ——
 * 编解码与规范化都是纯逻辑，必须被测试锁住（脏数据不能让设备列表整体崩掉）。
 */
class DeviceRemarkCodecTest {

    @Test
    fun `规范化去除首尾空白`() {
        assertEquals("书房主机", normalizeRemark("  书房主机  "))
    }

    @Test
    fun `规范化截断到 64 字符`() {
        assertEquals(MAX_REMARK_LENGTH, normalizeRemark("A".repeat(100)).length)
    }

    @Test
    fun `纯空白规范化后为空串（表示删除该条备注）`() {
        assertEquals("", normalizeRemark("   "))
    }

    @Test
    fun `编解码往返一致`() {
        val map = mapOf("id1" to "书房主机", "id2" to "客厅小主机")
        assertEquals(map, decodeRemarks(encodeRemarks(map)))
    }

    @Test
    fun `空表编码为空串且解码回来仍是空表`() {
        assertEquals("", encodeRemarks(emptyMap()))
        assertTrue(decodeRemarks("").isEmpty())
    }

    @Test
    fun `脏数据不抛异常而是返回空表`() {
        assertTrue(decodeRemarks("{不是合法 json").isEmpty())
        assertTrue(decodeRemarks("   ").isEmpty())
    }
}
