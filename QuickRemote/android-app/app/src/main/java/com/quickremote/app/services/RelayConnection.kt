package com.quickremote.app.services

import com.quickremote.app.data.api.ApiException
import com.quickremote.app.data.api.RelayApi
import com.quickremote.app.data.models.Device
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.data.models.TunnelResponse

/**
 * 管理与中转服务器的连接状态：认证获取 token、获取设备列表、请求隧道。
 *
 * 注意：所有网络方法均为阻塞式，调用方需在协程中（IO 调度器）执行。
 */
class RelayConnection(
    private val api: RelayApi = RelayApi(),
    private val logger: Logger = Logger()
) {

    @Volatile
    var token: String = ""
        private set

    @Volatile
    var serverConfig: ServerConfig = ServerConfig()
        private set

    /** 最近一次操作的错误信息（成功时为空）。用于 UI 显示具体失败原因。 */
    @Volatile
    var lastError: String = ""
        private set

    /** 认证并缓存 token。成功返回 true。 */
    fun authenticate(config: ServerConfig): Boolean {
        serverConfig = config
        return try {
            val resp = api.authenticate(config.address, config.preSharedKey)
            token = resp.token
            lastError = ""
            logger.info("Authenticated OK, token length=${token.length}, expires=${resp.expires}")
            true
        } catch (e: ApiException) {
            token = ""
            lastError = formatApiError(e, "认证失败")
            logger.warn("Authenticate failed: $lastError")
            false
        } catch (e: Exception) {
            token = ""
            lastError = formatNetworkError(e, config.address)
            logger.warn("Authenticate failed: $lastError")
            false
        }
    }

    /** 测试连接（认证），不影响已缓存状态。成功返回 true。 */
    fun testConnection(config: ServerConfig): Boolean {
        return try {
            val resp = api.authenticate(config.address, config.preSharedKey)
            lastError = ""
            logger.info("Test connection OK, expires=${resp.expires}")
            true
        } catch (e: ApiException) {
            lastError = formatApiError(e, "认证失败")
            logger.warn("Test connection failed: $lastError")
            false
        } catch (e: Exception) {
            lastError = formatNetworkError(e, config.address)
            logger.warn("Test connection failed: $lastError")
            false
        }
    }

    /** 获取在线设备列表。若 token 为空会先认证。 */
    fun getDevices(): List<Device> {
        if (token.isEmpty()) {
            val ok = authenticate(serverConfig)
            if (!ok) throw IllegalStateException("未认证")
        }
        return api.getDevices(serverConfig.address, token).devices
    }

    /** 请求建立到指定设备的隧道。 */
    fun requestTunnel(deviceId: String): TunnelResponse {
        if (token.isEmpty()) {
            val ok = authenticate(serverConfig)
            if (!ok) throw IllegalStateException("未认证")
        }
        logger.info("Requesting tunnel for device=$deviceId")
        return api.requestTunnel(serverConfig.address, token, deviceId)
    }

    /** 上传日志。 */
    fun uploadLogs(deviceId: String, logs: String, level: String): Boolean {
        if (token.isEmpty()) {
            lastError = "未认证，请先在服务器配置页测试连接"
            return false
        }
        return try {
            api.uploadLogs(serverConfig.address, token, deviceId, logs, level)
            lastError = ""
            true
        } catch (e: ApiException) {
            lastError = formatApiError(e, "上传日志失败")
            logger.warn("Upload logs failed: $lastError")
            false
        } catch (e: Exception) {
            lastError = formatNetworkError(e, serverConfig.address)
            logger.warn("Upload logs failed: $lastError")
            false
        }
    }

    /** 清除认证状态。 */
    fun reset() {
        token = ""
        serverConfig = ServerConfig()
        lastError = ""
    }

    /** 格式化 API 业务错误（HTTP 非 2xx）。 */
    private fun formatApiError(e: ApiException, prefix: String): String {
        // 从 message 中提取服务器返回的响应体（格式为 "xxx失败: <body>"）
        val raw = e.message.orEmpty()
        val body = raw.substringAfter(": ", "").trim()
        val bodyLower = body.lowercase()
        return when {
            e.code == 401 && bodyLower.contains("invalid_key") ->
                "预共享密钥不匹配（HTTP 401）"
            e.code == 401 && bodyLower.contains("unauthorized") ->
                "未授权：Token 无效或已过期（HTTP 401）"
            e.code == 401 ->
                "认证失败（HTTP 401）：$body"
            e.code == 404 ->
                "接口不存在：请确认服务器地址和端口正确（HTTP 404）"
            e.code == 405 ->
                "请求方法不被允许（HTTP 405）"
            e.code in 500..599 ->
                "服务器内部错误（HTTP ${e.code}）"
            else -> "$prefix（HTTP ${e.code}）：$body"
        }
    }

    /** 格式化网络/协议错误（连接失败、SSL、超时等）。 */
    private fun formatNetworkError(e: Exception, address: String): String {
        val msg = e.message.orEmpty()
        return when {
            msg.contains("Unable to resolve host", ignoreCase = true) ||
            msg.contains("UnknownHost", ignoreCase = true) ->
                "无法解析服务器域名：$address"
            msg.contains("Connection refused", ignoreCase = true) ->
                "连接被拒绝：服务器端口未开放或服务未启动"
            msg.contains("Connection timed out", ignoreCase = true) ||
            msg.contains("timeout", ignoreCase = true) ->
                "连接超时：服务器无响应或网络不通"
            msg.contains("SSL", ignoreCase = true) ||
            msg.contains("certificate", ignoreCase = true) ->
                "SSL/证书错误：$msg"
            msg.contains("Failed to connect", ignoreCase = true) ->
                "无法连接到服务器：$msg"
            msg.contains("unexpected end of stream", ignoreCase = true) ->
                "服务器响应异常（可能端口协议不匹配，如用 HTTPS 连接了明文 HTTP 端口）"
            else -> "网络错误：$msg"
        }
    }
}
