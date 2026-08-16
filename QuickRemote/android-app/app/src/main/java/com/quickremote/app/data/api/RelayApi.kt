package com.quickremote.app.data.api

import com.quickremote.app.data.models.AuthRequest
import com.quickremote.app.data.models.AuthResponse
import com.quickremote.app.data.models.DeviceListResponse
import com.quickremote.app.data.models.LogUploadRequest
import com.quickremote.app.data.models.LogUploadResponse
import com.quickremote.app.data.models.TunnelRequest
import com.quickremote.app.data.models.TunnelResponse
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.security.MessageDigest
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * 与中转服务器通信的 HTTP API 客户端。
 *
 * 协议对齐（与 relay-server internal/api 一致）：
 * - 认证: POST /api/auth, body {"pre_shared_key":"<sha256(secret) hex>"}
 * - 设备列表: GET /api/devices, Header Authorization: Bearer <token>
 * - 请求隧道: POST /api/tunnel/request, body {"device_id":"xxx"}
 * - 日志上传: POST /api/logs/upload, body {"client_type":"android","device_id":"xxx","logs":"base64","level":"error"}
 *
 * 所有请求忽略自签名证书验证（开发/内网环境）。
 */
class RelayApi {

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    private val client: OkHttpClient = buildUnsafeClient()

    /** 计算预共享密钥的 SHA-256 hex 小写哈希。 */
    fun hashPreSharedKey(preSharedKey: String): String {
        val md = MessageDigest.getInstance("SHA-256")
        val digest = md.digest(preSharedKey.toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it) }
    }

    /** 规范化服务器地址，返回带 scheme 的 base URL。 */
    private fun baseUrl(address: String): String {
        var addr = address.trim().trimEnd('/')
        if (addr.isEmpty()) return ""
        if (!addr.startsWith("http://") && !addr.startsWith("https://")) {
            addr = "https://$addr"
        }
        return addr
    }

    /** 认证获取 JWT token。 */
    fun authenticate(serverAddress: String, preSharedKey: String): AuthResponse {
        val base = baseUrl(serverAddress)
        val body = json.encodeToString(AuthRequest.serializer(), AuthRequest(hashPreSharedKey(preSharedKey)))
        val request = Request.Builder()
            .url("$base/api/auth")
            .post(body.toRequestBody(JSON_MEDIA))
            .build()
        client.newCall(request).execute().use { resp ->
            val respBody = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) {
                throw ApiException(resp.code, "认证失败: $respBody")
            }
            return json.decodeFromString(AuthResponse.serializer(), respBody)
        }
    }

    /** 获取在线设备列表。 */
    fun getDevices(serverAddress: String, token: String): DeviceListResponse {
        val base = baseUrl(serverAddress)
        val request = Request.Builder()
            .url("$base/api/devices")
            .header("Authorization", "Bearer $token")
            .get()
            .build()
        client.newCall(request).execute().use { resp ->
            val respBody = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) {
                throw ApiException(resp.code, "获取设备列表失败: $respBody")
            }
            return json.decodeFromString(DeviceListResponse.serializer(), respBody)
        }
    }

    /** 请求建立到指定设备的隧道。 */
    fun requestTunnel(serverAddress: String, token: String, deviceId: String): TunnelResponse {
        val base = baseUrl(serverAddress)
        val body = json.encodeToString(TunnelRequest.serializer(), TunnelRequest(deviceId))
        val request = Request.Builder()
            .url("$base/api/tunnel/request")
            .header("Authorization", "Bearer $token")
            .post(body.toRequestBody(JSON_MEDIA))
            .build()
        client.newCall(request).execute().use { resp ->
            val respBody = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) {
                throw ApiException(resp.code, "请求隧道失败: $respBody")
            }
            return json.decodeFromString(TunnelResponse.serializer(), respBody)
        }
    }

    /** 上传日志到服务器。logs 应为 Base64 编码的日志内容。 */
    fun uploadLogs(
        serverAddress: String,
        token: String,
        deviceId: String,
        logs: String,
        level: String
    ): LogUploadResponse {
        val base = baseUrl(serverAddress)
        val req = LogUploadRequest(
            client_type = "android",
            device_id = deviceId,
            logs = logs,
            level = level
        )
        val body = json.encodeToString(LogUploadRequest.serializer(), req)
        val request = Request.Builder()
            .url("$base/api/logs/upload")
            .header("Authorization", "Bearer $token")
            .post(body.toRequestBody(JSON_MEDIA))
            .build()
        client.newCall(request).execute().use { resp ->
            val respBody = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) {
                throw ApiException(resp.code, "上传日志失败: $respBody")
            }
            return json.decodeFromString(LogUploadResponse.serializer(), respBody)
        }
    }

    companion object {
        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

        /** 构造一个信任所有证书（含自签名）的 OkHttpClient。 */
        private fun buildUnsafeClient(): OkHttpClient {
            val trustAllManager = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
            }
            val sslContext = SSLContext.getInstance("TLS")
            sslContext.init(null, arrayOf<TrustManager>(trustAllManager), java.security.SecureRandom())
            val sslSocketFactory: SSLSocketFactory = sslContext.socketFactory
            val hostnameVerifier = HostnameVerifier { _, _ -> true }

            return OkHttpClient.Builder()
                .sslSocketFactory(sslSocketFactory, trustAllManager)
                .hostnameVerifier(hostnameVerifier)
                .connectTimeout(15, TimeUnit.SECONDS)
                .readTimeout(30, TimeUnit.SECONDS)
                .writeTimeout(30, TimeUnit.SECONDS)
                .build()
        }
    }
}

/** API 调用异常。 */
class ApiException(val code: Int, message: String) : Exception(message)
