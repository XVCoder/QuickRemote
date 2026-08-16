package com.quickremote.app.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// 服务器配置
@Serializable
data class ServerConfig(
    val address: String = "",
    val preSharedKey: String = ""
)

// 保存的 RDP 登录凭据（按设备维度存储）
data class Credentials(
    val username: String = "",
    val password: String = "",
    val domain: String = ""
)

// 在线 PC 设备（字段与 relay-server registry.Device 对齐）
@Serializable
data class Device(
    val device_id: String = "",
    val machine_id: String = "",
    val hostname: String = "",
    val os: String = "",
    val rdp_port: Int = 3389,
    val version: String = "",
    val status: String = "online",
    val last_seen: String = ""
)

// 设备列表响应
@Serializable
data class DeviceListResponse(
    val devices: List<Device> = emptyList()
)

// 认证请求
@Serializable
data class AuthRequest(
    val pre_shared_key: String
)

// 认证响应
@Serializable
data class AuthResponse(
    val token: String = "",
    val expires: Int = 0
)

// 隧道请求
@Serializable
data class TunnelRequest(
    val device_id: String
)

// 隧道响应
@Serializable
data class TunnelResponse(
    val session_id: String = "",
    val tunnel_host: String = "",
    val tunnel_port: Int = 0
)

// 日志上传请求
@Serializable
data class LogUploadRequest(
    val client_type: String = "android",
    val device_id: String,
    val logs: String,
    val level: String
)

// 日志上传响应
@Serializable
data class LogUploadResponse(
    val status: String = ""
)

// 通用错误响应
@Serializable
data class ErrorResponse(
    val error: String = ""
)

// quickdeploy manifest 各组件版本信息
@Serializable
data class ManifestData(
    @SerialName("android-app") val androidApp: ComponentManifest? = null,
    @SerialName("pc-client") val pcClient: ComponentManifest? = null,
    @SerialName("relay-server") val relayServer: ComponentManifest? = null
)

@Serializable
data class ComponentManifest(
    val latest_version: String = "",
    val changelog: String = "",
    val versions: Map<String, Map<String, String>> = emptyMap()
)

// 更新记录条目
@Serializable
data class ChangelogEntry(
    val version: String = "",
    val date: String = "",
    val changes: List<String> = emptyList()
)

// App 设置
@Serializable
data class AppSettings(
    val resolutionMode: ResolutionMode = ResolutionMode.AUTO,
    val customWidth: Int = 1920,
    val customHeight: Int = 1080,
    val colorDepth: Int = 32,
    val audioRedirect: Boolean = false,
    val autoUpdate: Boolean = true
)

enum class ResolutionMode { AUTO, ORIGINAL, CUSTOM }
