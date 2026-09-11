package com.quickremote.app.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.util.Base64

/**
 * 配对载荷编解码，与 PC 端 Interop/PairingPayload.cs 共享同一契约。
 *
 * 格式：quickremote://pair?d=<base64url_no_padding(UTF-8 JSON)>
 * JSON：{"v":1,"addr":"host:port","psk":"...","name":"设备名"}
 *
 * 两端实现必须逐字节一致。改动任一端务必同步另一端，并跑通两端各自的
 * PairingPayload 单元测试。
 *
 * 注：使用 java.util.Base64（而非 android.util.Base64）—— 后者在本地 JVM
 * 单元测试中是未实现的 stub，会抛 "Method not mocked"。minSdk 26 已支持 java.util.Base64。
 */
object PairingPayload {

    const val SCHEME = "quickremote"
    const val HOST = "pair"
    const val VERSION = 1

    @Serializable
    data class PairInfo(
        val v: Int = VERSION,
        val addr: String = "",
        val psk: String = "",
        val name: String = ""
    )

    private val json = Json { ignoreUnknownKeys = true }

    /** 生成完整配对 URL（二维码内容与明文配置串都是它）。 */
    fun encode(addr: String, psk: String, name: String): String {
        val raw = json.encodeToString(PairInfo.serializer(), PairInfo(VERSION, addr, psk, name))
        return "$SCHEME://$HOST?d=${encodeRaw(raw)}"
    }

    /** 仅编码原始 JSON 串（供测试构造非法载荷用）。 */
    fun encodeRaw(rawJson: String): String =
        Base64.getUrlEncoder().withoutPadding()
            .encodeToString(rawJson.toByteArray(Charsets.UTF_8))

    /** 解析配对 URL。失败返回 Result.failure 并带原因，绝不抛异常。 */
    fun parse(url: String?): Result<PairInfo> {
        return try {
            if (url.isNullOrBlank()) return Result.failure(IllegalArgumentException("空输入"))

            val trimmed = url.trim()
            if (!trimmed.startsWith("$SCHEME://", ignoreCase = true)) {
                return Result.failure(IllegalArgumentException("不是 QuickRemote 配对链接"))
            }

            val query = trimmed.substringAfter('?', "")
            if (query.isEmpty()) return Result.failure(IllegalArgumentException("缺少参数"))

            val d = query.split('&')
                .map { it.split('=', limit = 2) }
                .firstOrNull { it.size == 2 && it[0] == "d" }
                ?.get(1)
                ?: return Result.failure(IllegalArgumentException("缺少 d 参数"))

            val bytes = Base64.getUrlDecoder().decode(d)
            val rawText = String(bytes, Charsets.UTF_8)
            val info = json.decodeFromString(PairInfo.serializer(), rawText)

            when {
                info.v != VERSION -> Result.failure(IllegalArgumentException("版本不支持：${info.v}"))
                info.addr.isBlank() || !info.addr.contains(':') ->
                    Result.failure(IllegalArgumentException("服务器地址无效"))
                info.psk.isBlank() -> Result.failure(IllegalArgumentException("预共享密钥为空"))
                else -> Result.success(info)
            }
        } catch (e: Exception) {
            Result.failure(e)
        }
    }
}
