package com.quickremote.app.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// 服务器配置
@Serializable
data class ServerConfig(
    val address: String = "",
    val preSharedKey: String = ""
)

// 在线 PC 设备（字段与 relay-server registry.Device 对齐）
@Serializable
data class Device(
    val device_id: String = "",
    val machine_id: String = "",
    val hostname: String = "",
    val os: String = "",
    val lan_ip: String = "",
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
    val autoUpdate: Boolean = true,
    /** 图像质量百分比（20-100），压缩率设置。 */
    val qualityPercent: Int = 80,
    /** 画面外空白区触摸板（单指滑动移光标/轻点左键/双指右键与滚动），默认启用。 */
    val blankTouchpad: Boolean = true,
    /** 触摸板光标速度（百分比 50-300，100 = 与悬浮球长按同速）。 */
    val touchpadSpeed: Int = 100,
    /** 触摸板双击拖动（单指快速双击后按住拖动 = 左键按住拖动）。 */
    val touchpadDoubleTapDrag: Boolean = true,
    /** 触摸板三指手势（上滑多任务/下滑显示桌面/左右滑切换应用/轻点搜索）。 */
    val touchpadThreeFinger: Boolean = true,
    /** 触摸板四指手势（左右滑切换虚拟桌面/轻点通知中心）。 */
    val touchpadFourFinger: Boolean = true
)

enum class ResolutionMode { AUTO, ORIGINAL, CUSTOM }
