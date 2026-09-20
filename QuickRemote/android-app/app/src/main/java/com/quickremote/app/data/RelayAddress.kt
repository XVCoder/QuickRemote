package com.quickremote.app.data

/**
 * 中继服务器地址的规范化，以及两端端口换算。
 *
 * 中继的端口约定（见 `relay-server/deploy/install.sh` 与 `cmd/server/main.go`）：
 * - **HTTP/API 端口**（默认 8443）：**只有 Android 用** —— 登录认证 / 设备列表 / 隧道请求
 * - **控制连接端口 = HTTP 端口 + 1**（默认 8444）：**只有 PC 用**
 * - 隧道数据端口（默认 8445）：两端共用
 *
 * 这里集中解决两件事：
 *
 * 1. **不再要求手写 `http://`**：中继 HTTP API 是明文 HTTP，此前无 scheme 时被默认补成
 *    `https://`，于是「只写 host:port 连不上、必须带 http:// 才行」。现在无 scheme → `http://`，
 *    无端口 → 8443（与 PC 端「无 scheme 即明文」的语义一致）。
 *
 * 2. **配对载荷的端口换算**：PC 端配对二维码/配置串里的地址是 **PC 自己用的控制连接端口**
 *    （如 `relay.example.com:8444`）。Android 只认 HTTP 端口，导入时必须换成 HTTP 端口（−1），
 *    否则扫码导入后必然连不上。规则来自服务端契约（控制端口 = HTTP 端口 + 1），
 *    且对**已经发出的旧二维码**同样生效 —— 无需 PC 端配合升级。
 */
object RelayAddress {

    /** 中继 HTTP/API 端口（Android 专用）。 */
    const val HTTP_API_PORT = 8443

    /** 中继控制连接端口（PC 专用，= HTTP 端口 + 1）。 */
    const val CONTROL_PORT = 8444

    /** 拆解后的地址成分；[path] 为空串表示不带路径。 */
    data class Parsed(
        val scheme: String?,
        val host: String,
        val port: Int?,
        val path: String
    )

    /**
     * 解析地址，接受 `host`、`host:port`、`http(s)://host:port/path` 与 IPv6 字面量 `[::1]:8443`。
     * 返回 null 表示无法解析（调用方应回退到原始字符串）。
     */
    fun parse(raw: String): Parsed? {
        val text = raw.trim()
        if (text.isEmpty()) return null

        var rest = text
        var scheme: String? = null
        val schemeSep = rest.indexOf("://")
        if (schemeSep > 0) {
            scheme = rest.substring(0, schemeSep).lowercase()
            rest = rest.substring(schemeSep + 3)
        }

        // 去掉 query / fragment，路径单独留下
        rest = rest.substringBefore('?').substringBefore('#')
        val slash = rest.indexOf('/')
        val authority = if (slash >= 0) rest.substring(0, slash) else rest
        val path = if (slash >= 0) rest.substring(slash).trimEnd('/') else ""
        if (authority.isEmpty()) return null

        // IPv6 字面量：[::1] 或 [::1]:8443
        if (authority.startsWith("[")) {
            val end = authority.indexOf(']')
            if (end < 0) return null
            val host = authority.substring(0, end + 1)
            val tail = authority.substring(end + 1)
            val port = tail.removePrefix(":").takeIf { it.isNotEmpty() }?.toIntOrNull()
            val badPort = tail.startsWith(":") && port == null
            if (badPort) return null
            return Parsed(scheme, host, port, path)
        }

        val colon = authority.lastIndexOf(':')
        if (colon >= 0) {
            val port = authority.substring(colon + 1).toIntOrNull() ?: return null
            if (port !in 1..65535) return null
            return Parsed(scheme, authority.substring(0, colon), port, path)
        }
        return Parsed(scheme, authority, null, path)
    }

    /**
     * 供 HTTP 调用使用：返回可直接拼 `/api/...` 的 base URL。
     * 无 scheme → `http://`；无端口 → [HTTP_API_PORT]；路径原样保留。
     */
    fun baseUrl(raw: String): String {
        val p = parse(raw) ?: return ""
        val scheme = p.scheme ?: "http"
        val port = p.port ?: HTTP_API_PORT
        return "$scheme://${p.host}:$port${p.path}"
    }

    /**
     * 供配对导入使用（扫码 / 粘贴 / 深链）：把载荷里的地址换成 Android 端可用的地址。
     *
     * 载荷里的端口是 PC 的控制连接端口，Android 要的是 HTTP/API 端口（= 控制端口 − 1）。
     * 输入原本带 scheme 时保留（`https://` 表示用户要求 TLS），不带则保持裸 `host:port`，
     * 避免设置页里凭空多出 `http://` 前缀。
     */
    fun forAndroid(raw: String): String {
        val p = parse(raw) ?: return raw.trim()
        val port = p.port?.let(::toHttpPort) ?: HTTP_API_PORT
        val prefix = p.scheme?.let { "$it://" } ?: ""
        return "$prefix${p.host}:$port"
    }

    /**
     * 控制连接端口 → HTTP/API 端口。
     *
     * - 服务端契约是「控制端口 = HTTP 端口 + 1」，故默认取 −1（8444 → 8443）。
     * - 80 / 443 视为已经落在标准入口（多半挂在反向代理后面），原样保留。
     * - 已经是 8443 时原样保留（防呆：有人把 HTTP 端口填进了 PC 端配置）。
     */
    private fun toHttpPort(pcPort: Int): Int = when {
        pcPort <= 1 -> HTTP_API_PORT
        pcPort == 80 || pcPort == 443 -> pcPort
        pcPort == HTTP_API_PORT -> pcPort
        else -> pcPort - 1
    }
}
