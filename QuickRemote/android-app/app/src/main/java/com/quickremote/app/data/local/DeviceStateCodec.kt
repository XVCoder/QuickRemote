package com.quickremote.app.data.local

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** 备注最大长度（与 PC 端 InputDialogWindow 的 64 字符上限一致）。 */
const val MAX_REMARK_LENGTH = 64

/** 规范化备注：去首尾空白 + 超长截断；结果为空串表示「删除该条备注」。 */
fun normalizeRemark(raw: String): String = raw.trim().take(MAX_REMARK_LENGTH)

private val remarksJson = Json { ignoreUnknownKeys = true }

/**
 * 备注表 → JSON 字符串。
 *
 * DataStore 的 Preferences 没有 Map 类型，因此备注表序列化后存在单个字符串键里；
 * 空表统一编码为空串，避免存一个 "{}" 让后续判空逻辑变复杂。
 */
fun encodeRemarks(map: Map<String, String>): String =
    if (map.isEmpty()) "" else remarksJson.encodeToString(map)

/** JSON 字符串 → 备注表；脏数据/空串一律返回空表，绝不让设备列表整体崩掉。 */
fun decodeRemarks(raw: String): Map<String, String> {
    if (raw.isBlank()) return emptyMap()
    return try {
        remarksJson.decodeFromString<Map<String, String>>(raw)
    } catch (_: Exception) {
        emptyMap()
    }
}
