package com.quickremote.app.services

import android.content.Context
import android.view.Surface
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.data.models.TunnelResponse
import com.quickremote.app.freerdp.FreeRdpClient
import java.net.ServerSocket
import java.net.Socket

/**
 * 管理 RDP 远程会话。
 *
 * 流程：请求隧道 → 打开隧道 TCP 连接并发送 [0x02][session_id] 握手 →
 * 启动本地代理（FreeRDP 连接 127.0.0.1:本地端口，代理将字节桥接到隧道）→
 * FreeRDP 发起 RDP 连接。
 *
 * 由于 libfreerdp-android.so 会自行建立到目标地址的 TCP 连接，而我们的隧道
 * 需要先完成握手，因此使用本地代理隧道：FreeRDP 连接本地端口，代理把
 * FreeRDP 的字节流透传到已经完成握手的隧道连接。
 */
class RdpSessionManager(
    private val context: Context,
    private val relay: RelayConnection = RelayConnection(),
    private val logger: Logger = Logger(),
    val freeRdpClient: FreeRdpClient = FreeRdpClient(context, Logger())
) {

    enum class SessionState { IDLE, CONNECTING, CONNECTED, DISCONNECTED, FAILED }

    @Volatile
    var state: SessionState = SessionState.IDLE
        private set

    @Volatile
    var tunnel: TunnelResponse? = null
        private set

    @Volatile
    var deviceId: String = ""
        private set

    @Volatile
    var hostname: String = ""
        private set

    @Volatile
    var errorMessage: String = ""
        private set

    /** FreeRDP 是否可用（.so 库已加载）。 */
    val isFreeRdpAvailable: Boolean get() = freeRdpClient.isAvailable

    /** 当前连接的 Surface。 */
    @Volatile
    var surface: Surface? = null
        private set

    /** 已建立的隧道 Socket。 */
    private var tunnelSocket: Socket? = null

    /** 本地代理 ServerSocket。 */
    private var localProxyServer: ServerSocket? = null

    private val proxyThreads = ArrayList<Thread>()

    /** 事件监听器。 */
    interface Listener {
        fun onStateChanged(state: SessionState) {}
        fun onGraphicsUpdated(x: Int, y: Int, width: Int, height: Int) {}
    }

    var listener: Listener? = null

    /** 开始一个会话：请求隧道、建立握手并启动 FreeRDP 连接。
     *
     * @param username Windows 登录用户名（NLA 认证需要）
     * @param password Windows 登录密码
     * @param domain   可选域
     */
    fun start(
        deviceId: String,
        hostname: String,
        config: ServerConfig,
        username: String = "",
        password: String = "",
        domain: String = ""
    ) {
        this.deviceId = deviceId
        this.hostname = hostname
        this.errorMessage = ""
        this.state = SessionState.CONNECTING
        logger.info("RDP session starting: device=$deviceId host=$hostname")
        listener?.onStateChanged(state)

        try {
            // 0. 确保已认证（未认证时用传入的服务器配置认证）
            if (relay.token.isEmpty()) {
                if (!relay.authenticate(config)) {
                    this.errorMessage = relay.lastError
                    this.state = SessionState.FAILED
                    logger.warn("RDP session auth failed: ${relay.lastError}")
                    listener?.onStateChanged(state)
                    return
                }
            }

            // 1. 请求隧道
            val t = relay.requestTunnel(deviceId)
            this.tunnel = t
            logger.info("Tunnel established: session=${t.session_id} host=${t.tunnel_host} port=${t.tunnel_port}")

            // 2. FreeRDP 库不可用时降级为占位模式
            if (!freeRdpClient.isAvailable) {
                logger.warn("FreeRDP .so not loaded, running in placeholder mode")
                val loadErr = freeRdpClient.loadError
                this.errorMessage = if (loadErr.isNullOrBlank()) {
                    "FreeRDP 库未加载，仅展示隧道信息"
                } else {
                    "FreeRDP 库加载失败: $loadErr"
                }
                this.state = SessionState.CONNECTED
                listener?.onStateChanged(state)
                return
            }

            // 3. 打开隧道连接并发送握手，启动本地代理
            val localPort = openTunnelLocalProxy(t)
            logger.info("Local proxy started on 127.0.0.1:$localPort")

            // 4. 配置 FreeRDP 连接本地代理（由代理桥接到隧道）
            val rdpConfig = FreeRdpClient.ConnectConfig(
                hostname = "127.0.0.1",
                port = localPort,
                username = username,
                password = password,
                domain = domain,
                width = 1280,
                height = 720,
                colorDepth = 32
            )

            freeRdpClient.listener = object : FreeRdpClient.Listener {
                override fun onConnecting() {
                    logger.info("FreeRDP connecting")
                }

                override fun onConnected() {
                    logger.info("FreeRDP connected")
                    state = SessionState.CONNECTED
                    listener?.onStateChanged(state)
                }

                override fun onDisconnected(error: String?) {
                    logger.info("FreeRDP disconnected: $error")
                    state = if (error != null) {
                        errorMessage = error
                        SessionState.FAILED
                    } else {
                        SessionState.DISCONNECTED
                    }
                    listener?.onStateChanged(state)
                }

                override fun onGraphicsUpdated(x: Int, y: Int, width: Int, height: Int) {
                    listener?.onGraphicsUpdated(x, y, width, height)
                }
            }

            val ok = freeRdpClient.connect(rdpConfig, surface)
            if (!ok) {
                this.state = SessionState.FAILED
                // connect 内部已通过 onDisconnected 回调设置具体失败原因
                //（如"创建 FreeRDP 实例失败"/"FreeRDP 参数解析失败"），
                // 仅在未设置时才用兜底提示，避免覆盖真实错误。
                if (this.errorMessage.isBlank()) {
                    this.errorMessage = "FreeRDP 连接启动失败"
                }
                listener?.onStateChanged(state)
            }
        } catch (e: Exception) {
            this.errorMessage = e.message ?: "隧道建立失败"
            this.state = SessionState.FAILED
            logger.warn("RDP session failed: ${e.message}")
            listener?.onStateChanged(state)
        }
    }

    /**
     * 打开到隧道服务器的连接，发送 [0x02][session_id] 握手，
     * 并在 127.0.0.1 上启动本地代理，返回本地端口。
     */
    private fun openTunnelLocalProxy(t: TunnelResponse): Int {
        val host = t.tunnel_host
        if (host.isBlank()) throw IllegalStateException("隧道主机为空")
        val port = if (t.tunnel_port > 0) t.tunnel_port else 8445

        val sock = Socket(host, port)
        this.tunnelSocket = sock

        // 发送握手头：[0x02] + 37 字节 session_id
        val out = sock.getOutputStream()
        out.write(0x02)
        val idBytes = t.session_id.toByteArray(Charsets.US_ASCII)
        val header = ByteArray(37)
        System.arraycopy(idBytes, 0, header, 0, minOf(idBytes.size, 37))
        out.write(header)
        out.flush()

        // 启动本地代理
        val server = ServerSocket(0)
        server.reuseAddress = true
        this.localProxyServer = server

        val acceptThread = Thread {
            try {
                while (true) {
                    val local = server.accept()
                    bridge(local, sock)
                }
            } catch (_: Exception) {
                // 代理已关闭
            }
        }
        acceptThread.isDaemon = true
        acceptThread.start()
        proxyThreads.add(acceptThread)

        return server.localPort
    }

    /** 双向桥接两个 Socket 的字节流。 */
    private fun bridge(local: Socket, remote: Socket) {
        val t1 = Thread {
            try {
                local.getInputStream().copyTo(remote.getOutputStream())
            } catch (_: Exception) {
            }
            try {
                remote.close()
            } catch (_: Exception) {
            }
        }
        val t2 = Thread {
            try {
                remote.getInputStream().copyTo(local.getOutputStream())
            } catch (_: Exception) {
            }
            try {
                local.close()
            } catch (_: Exception) {
            }
        }
        t1.isDaemon = true
        t2.isDaemon = true
        t1.start()
        t2.start()
        proxyThreads.add(t1)
        proxyThreads.add(t2)
    }

    /** 清理本地代理与隧道连接。 */
    private fun closeProxy() {
        try {
            localProxyServer?.close()
        } catch (_: Exception) {
        }
        localProxyServer = null
        try {
            tunnelSocket?.close()
        } catch (_: Exception) {
        }
        tunnelSocket = null
        proxyThreads.clear()
    }

    /** 设置渲染 Surface。 */
    fun setSurface(surface: Surface?) {
        this.surface = surface
        freeRdpClient.setSurface(surface)
    }

    /** 断开会话。 */
    fun disconnect() {
        logger.info("RDP session disconnect: device=$deviceId")
        freeRdpClient.disconnect()
        closeProxy()
        this.state = SessionState.DISCONNECTED
        this.tunnel = null
        listener?.onStateChanged(state)
    }

    /** 释放资源。 */
    fun release() {
        freeRdpClient.release()
        closeProxy()
        this.state = SessionState.IDLE
        this.tunnel = null
        this.surface = null
    }

    /** 重置到空闲状态。 */
    fun reset() {
        release()
        this.deviceId = ""
        this.hostname = ""
        this.errorMessage = ""
        listener?.onStateChanged(state)
    }
}