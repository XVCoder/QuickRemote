package com.quickremote.app.services

import android.view.Surface
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.data.models.TunnelResponse
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket

/**
 * Android 端远程会话管理器（截屏方案，替代 FreeRDP）。
 *
 * 流程：认证 → 请求隧道 → 连接隧道服务器并发送 [0x02][session_id] 握手 →
 * 帧协议收发：接收 H.264 视频帧 → MediaCodec 解码渲染到 Surface；
 * 发送输入事件（阶段 5 接入触摸捕获）。
 */
class RemoteSessionManager(
    private val relay: RelayConnection = RelayConnection(),
    private val logger: Logger = Logger(),
    val decoder: H264Decoder = H264Decoder(logger)
) {

    enum class SessionState { IDLE, CONNECTING, CONNECTED, DISCONNECTED, FAILED }

    @Volatile
    var state: SessionState = SessionState.IDLE
        private set

    @Volatile
    var tunnel: TunnelResponse? = null
        private set

    @Volatile
    var errorMessage: String = ""
        private set

    @Volatile
    var videoWidth: Int = 1280
        private set

    @Volatile
    var videoHeight: Int = 720
        private set

    @Volatile
    var deviceId: String = ""
        private set

    /** 当前渲染 Surface。 */
    @Volatile
    var surface: Surface? = null
        private set

    /** 已建立的隧道 Socket。 */
    private var tunnelSocket: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null
    private var receiveThread: Thread? = null

    @Volatile
    private var running = false

    /** 事件监听器。 */
    interface Listener {
        fun onStateChanged(state: SessionState) {}
        fun onVideoFrame(w: Int, h: Int) {}
    }

    var listener: Listener? = null

    /**
     * 开始一个截屏远程会话（Surface 通过 [setSurface] 提前设置）。
     * 截屏方案不需要 Windows 凭据（不需要 NLA 认证）。
     */
    fun start(
        deviceId: String,
        hostname: String,
        config: ServerConfig
    ) {
        this.deviceId = deviceId
        this.errorMessage = ""
        this.state = SessionState.CONNECTING
        logger.info("Remote session starting: device=$deviceId host=$hostname")
        listener?.onStateChanged(state)

        Thread {
            try {
                // 0. 认证
                if (relay.token.isEmpty()) {
                    if (!relay.authenticate(config)) {
                        fail("认证失败：${relay.lastError}")
                        return@Thread
                    }
                }

                // 1. 请求隧道
                val t = relay.requestTunnel(deviceId)
                this.tunnel = t
                logger.info("Tunnel established: session=${t.session_id} host=${t.tunnel_host} port=${t.tunnel_port}")

                // 2. 连接隧道服务器 + 握手 [0x02] + session_id
                val host = t.tunnel_host.ifBlank { config.address.substringBefore(':') }
                val port = if (t.tunnel_port > 0) t.tunnel_port else 8445
                val sock = Socket(host, port)
                sock.tcpNoDelay = true
                this.tunnelSocket = sock
                input = DataInputStream(sock.getInputStream())
                val out = DataOutputStream(sock.getOutputStream())
                output = out

                // 握手头：[0x02] + 37 字节 session_id
                out.write(0x02)
                val idBytes = t.session_id.toByteArray(Charsets.US_ASCII)
                val header = ByteArray(37)
                System.arraycopy(idBytes, 0, header, 0, minOf(idBytes.size, 37))
                out.write(header)
                out.flush()
                logger.info("Tunnel handshake sent, proxy connected")

                // 3. 启动接收线程
                running = true
                state = SessionState.CONNECTED
                logger.info("Remote session connected")
                listener?.onStateChanged(state)
                receiveLoop()
            } catch (e: Exception) {
                fail("连接失败：${e.message}")
            }
        }.apply { isDaemon = true }.start()
    }

    /** 接收线程：解析帧协议，分发视频帧/控制帧。 */
    private fun receiveLoop() {
        try {
            val ins = input ?: return
            val header = ByteArray(RemoteFrameProtocol.HEADER_SIZE)
            while (running) {
                readExactly(ins, header, header.size)
                val type = header[0]
                val len = RemoteFrameProtocol.decodeLength(header)
                if (len < 0 || len > RemoteFrameProtocol.MAX_PAYLOAD) break

                val data = ByteArray(len)
                if (len > 0) readExactly(ins, data, len)

                when (type) {
                    RemoteFrameProtocol.TYPE_VIDEO_FRAME -> decoder.decode(data)
                    RemoteFrameProtocol.TYPE_CONTROL -> handleControl(data)
                    RemoteFrameProtocol.TYPE_HEARTBEAT -> { /* 心跳，忽略 */ }
                    else -> logger.warn("Unknown frame type: 0x${type.toString(16)}")
                }
            }
        } catch (_: Exception) {
            // 连接断开
        } finally {
            disconnect()
        }
    }

    /** 处理控制帧（分辨率/帧率信息）。 */
    private fun handleControl(data: ByteArray) {
        try {
            val json = JSONObject(String(data, Charsets.UTF_8))
            val w = json.optInt("width", 1280)
            val h = json.optInt("height", 720)
            // 先保存分辨率（即使 surface 未就绪也不丢失），setSurface 时用最新值
            videoWidth = w
            videoHeight = h
            val surf = surface
            if (surf != null) {
                decoder.start(surf, w, h)
                logger.info("Control: resolution=$w x $h, decoder started on existing surface")
            } else {
                // surface 未就绪：等 SurfaceView surfaceCreated → setSurface 时启动解码器
                logger.info("Control: resolution=$w x $h saved (surface not ready yet)")
            }
            listener?.onVideoFrame(w, h)
        } catch (e: Exception) {
            logger.warn("Control parse failed: ${e.message}")
        }
    }

    /** 设置渲染 Surface（由 UI 层在 SurfaceView 创建时调用）。 */
    fun setSurface(surface: Surface?) {
        this.surface = surface
        if (surface != null && state == SessionState.CONNECTED) {
            // 会话已连接时，surface 就绪即用已保存的分辨率启动解码器
            if (videoWidth > 0 && videoHeight > 0) {
                decoder.start(surface, videoWidth, videoHeight)
                logger.info("Surface ready, decoder started: ${videoWidth}x${videoHeight}")
            } else {
                logger.info("Surface ready but no resolution yet (waiting CONTROL frame)")
            }
        }
    }

    /** 发送输入事件（阶段 5 使用）。 */
    fun sendInput(type: Byte, data: ByteArray) {
        val out = output ?: return
        try {
            synchronized(out) {
                out.write(RemoteFrameProtocol.makeHeader(type, data.size))
                out.write(data)
                out.flush()
            }
        } catch (_: Exception) {
        }
    }

    private fun readExactly(input: DataInputStream, buf: ByteArray, count: Int) {
        var offset = 0
        while (offset < count) {
            val read = input.read(buf, offset, count - offset)
            if (read < 0) throw java.io.EOFException()
            offset += read
        }
    }

    private fun fail(message: String) {
        errorMessage = message
        state = SessionState.FAILED
        running = false
        logger.warn("Remote session failed: $message")
        listener?.onStateChanged(state)
        closeSocket()
    }

    /** 断开会话。 */
    fun disconnect() {
        running = false
        decoder.stop()
        state = SessionState.DISCONNECTED
        listener?.onStateChanged(state)
        closeSocket()
    }

    private fun closeSocket() {
        try { tunnelSocket?.close() } catch (_: Exception) {}
        tunnelSocket = null
        input = null
        output = null
        tunnel = null
    }

    /** 释放资源。 */
    fun release() {
        disconnect()
        surface = null
        state = SessionState.IDLE
    }

    /** 重置到空闲状态。 */
    fun reset() {
        release()
        deviceId = ""
        errorMessage = ""
        listener?.onStateChanged(state)
    }
}
