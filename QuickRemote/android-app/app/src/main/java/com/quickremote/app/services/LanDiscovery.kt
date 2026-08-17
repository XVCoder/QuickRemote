package com.quickremote.app.services

import com.quickremote.app.data.models.Device
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * 局域网发现：监听 PC 端的 UDP 广播（端口 8446），
 * 发现同一局域网内的 QuickRemote 主机（内网 RDP 直连模式）。
 *
 * 用 StateFlow 暴露设备列表，多观察者安全；生命周期独立于页面
 * （stop() 只停监听不清列表，避免页面切换丢失已发现设备）。
 */
object LanDiscovery {

    /** 广播端口（与 PC 端一致）。 */
    const val PORT = 8446

    /** 内网发现的主机。 */
    @Serializable
    data class LanDevice(
        val deviceId: String,
        val hostname: String,
        val ip: String,
        val rdpPort: Int
    ) {
        /** 转换为设备模型（供 RDP 会话页使用）。 */
        fun toDevice(): Device = Device(
            device_id = deviceId,
            hostname = hostname,
            os = "内网直连",
            rdp_port = rdpPort,
            status = "online"
        )
    }

    @Volatile
    private var running = false
    private var thread: Thread? = null

    private val _devices = MutableStateFlow<List<LanDevice>>(emptyList())

    /** 当前发现的主机列表（去重，按 device_id），多观察者安全。 */
    val devices: StateFlow<List<LanDevice>> = _devices.asStateFlow()

    /** 启动监听（后台线程）。 */
    fun start() {
        if (running) return
        running = true
        thread = Thread {
            try {
                val socket = DatagramSocket(PORT)
                socket.soTimeout = 2000
                val buffer = ByteArray(1024)
                val packet = DatagramPacket(buffer, buffer.size)
                while (running) {
                    try {
                        socket.receive(packet)
                        val data = String(packet.data, 0, packet.length, Charsets.UTF_8)
                        parseAndAdd(data, packet.address)
                    } catch (e: java.net.SocketTimeoutException) {
                        // 超时继续监听
                    } catch (_: Exception) {
                        // 忽略单包错误
                    }
                }
                socket.close()
            } catch (_: Exception) {
                // 端口占用或权限问题，静默退出
            }
        }.apply { isDaemon = true }
        thread?.start()
    }

    /** 停止监听（保留已发现设备，供返回页面时继续显示）。 */
    fun stop() {
        running = false
        thread = null
    }

    private fun parseAndAdd(data: String, source: InetAddress) {
        try {
            val json = JSONObject(data)
            if (json.optString("t") != "qr_discover") return
            val deviceId = json.optString("id")
            if (deviceId.isBlank()) return
            val device = LanDevice(
                deviceId = deviceId,
                hostname = json.optString("host", deviceId),
                ip = source.hostAddress ?: return,
                rdpPort = json.optInt("port", 3389)
            )
            synchronized(this) {
                val current = _devices.value
                if (current.none { it.deviceId == deviceId }) {
                    _devices.value = current + device
                }
            }
        } catch (_: Exception) {
            // 解析失败忽略
        }
    }
}
