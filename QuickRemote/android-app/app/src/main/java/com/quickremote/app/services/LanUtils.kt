package com.quickremote.app.services

/**
 * 局域网工具：判断远程设备 IP 是否与本机同一网段（IPv4 前 3 段相同，按 /24 网段）。
 */
object LanUtils {

    /** PC 端局域网直连监听端口（与 pc-client LanListener.DefaultPort 一致）。 */
    const val LAN_PORT = 8447

    /**
     * 判断远程 IP 是否与当前设备处于同一局域网。
     * 遍历本机所有私有 IPv4（Wi-Fi/以太网等），任意一个与远程 IP 同 /24 网段即认为同局域网
     * （避免枚举顺序拿到蜂窝 rmnet/热点接口 IP 导致误判为公网）。
     */
    fun isSameSubnet(remoteIp: String): Boolean {
        if (remoteIp.isBlank()) return false
        return localPrivateIps().any { sameIpv4Prefix(it, remoteIp) }
    }

    /** 获取本机所有私有 IPv4 地址（所有 up 且非回环的接口）。 */
    fun localPrivateIps(): List<String> {
        val result = mutableListOf<String>()
        return try {
            val interfaces = java.net.NetworkInterface.getNetworkInterfaces()
            for (ni in interfaces) {
                if (!ni.isUp || ni.isLoopback) continue
                for (addr in ni.inetAddresses) {
                    val ip = addr.hostAddress ?: continue
                    if (ip.contains('.') && !ip.startsWith("127.") && isPrivateIp(ip)) {
                        result.add(ip)
                    }
                }
            }
            result
        } catch (_: Exception) {
            result
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
