package com.quickremote.app.services

/**
 * 局域网工具：判断远程设备 IP 是否与本机同一网段（IPv4 前 3 段相同，按 /24 网段）。
 */
object LanUtils {

    /** PC 端局域网直连监听端口（与 pc-client LanListener.DefaultPort 一致）。 */
    const val LAN_PORT = 8447

    /** 判断远程 IP 是否与当前设备处于同一局域网。 */
    fun isSameSubnet(remoteIp: String): Boolean {
        if (remoteIp.isBlank()) return false
        val localIp = currentLocalIp() ?: return false
        return sameIpv4Prefix(localIp, remoteIp)
    }

    /** 获取本机当前 IPv4 地址（优先 Wi-Fi/移动网络/以太网接口）。 */
    fun currentLocalIp(): String? {
        return try {
            val interfaces = java.net.NetworkInterface.getNetworkInterfaces()
            for (ni in interfaces) {
                if (!ni.isUp || ni.isLoopback) continue
                val name = ni.name.lowercase()
                if (name.contains("wlan") || name.contains("eth") || name.contains("rmnet") ||
                    name.contains("wifi") || name.contains("usb") || name.contains("en0") ||
                    name.contains("ap") || name.contains("radio")
                ) {
                    for (addr in ni.inetAddresses) {
                        val ip = addr.hostAddress ?: continue
                        if (ip.contains('.') && !ip.startsWith("127.")) {
                            return ip
                        }
                    }
                }
            }
            // 兜底：遍历所有非回环接口
            val interfaces2 = java.net.NetworkInterface.getNetworkInterfaces()
            for (ni in interfaces2) {
                if (!ni.isUp || ni.isLoopback) continue
                for (addr in ni.inetAddresses) {
                    val ip = addr.hostAddress ?: continue
                    if (ip.contains('.') && !ip.startsWith("127.") && isPrivateIp(ip)) {
                        return ip
                    }
                }
            }
            null
        } catch (_: Exception) {
            null
        }
    }

    private fun isPrivateIp(ip: String): Boolean {
        return ip.startsWith("10.") ||
                ip.startsWith("192.168.") ||
                (ip.startsWith("172.") && ip.split('.').getOrNull(1)?.toIntOrNull()?.let { it in 16..31 } == true)
    }

    /** 比较两个 IPv4 地址是否同一 /24 网段（前三段相同）。 */
    fun sameIpv4Prefix(a: String, b: String): Boolean {
        val pa = a.split('.')
        val pb = b.split('.')
        if (pa.size < 3 || pb.size < 3) return false
        return pa[0] == pb[0] && pa[1] == pb[1] && pa[2] == pb[2]
    }
}
