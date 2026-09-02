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
    val decoder: H264Decoder = H264Decoder(logger),
    private val jpegDecoder: JpegDecoder = JpegDecoder(logger)
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

    /** 当前视频编码格式（从控制帧解析）：h264 或 jpeg。 */
    @Volatile
    var codec: String = "h264"
        private set

    /** 当前渲染 Surface。 */
    @Volatile
    var surface: Surface? = null
        private set

    /** 图像质量百分比（20-100），连接后通知 PC 调整压缩率。 */
    @Volatile
    var qualityPercent: Int = 80
        private set

    /** 连接模式：lan=局域网直连，relay=公网中继。 */
    enum class ConnectionMode { LAN, RELAY }

    @Volatile
    var connectionMode: ConnectionMode = ConnectionMode.RELAY
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
     * 启动远程会话。连接策略：
     * 1. 优先局域网直连：设备 lan_ip 与本机同网段时，直连 PC 的 LAN_PORT（低延迟）
     * 2. 直连失败/不同网段：回退中继隧道（公网）
     *
     * @param qualityPercent 图像质量百分比（20-100），连接后通过控制帧通知 PC 调整压缩率
     */
    fun start(
        deviceId: String,
        hostname: String,
        lanIp: String,
        qualityPercent: Int,
        config: ServerConfig
    ) {
        this.deviceId = deviceId
        this.errorMessage = ""
        this.state = SessionState.CONNECTING
        this.qualityPercent = qualityPercent.coerceIn(20, 100)
        logger.info("Remote session starting: device=$deviceId host=$hostname lan=$lanIp quality=$qualityPercent%")
        listener?.onStateChanged(state)

        Thread {
            try {
                // 0. 局域网直连优先（同网段）
                if (lanIp.isNotBlank() && LanUtils.isSameSubnet(lanIp)) {
                    logger.info("Device in same subnet, trying LAN direct: $lanIp:${LanUtils.LAN_PORT}")
                    if (tryLanDirect(lanIp, config)) return@Thread
                    logger.warn("LAN direct failed (${errorMessage}), fallback to relay")
                    closeSocket()
                    errorMessage = ""
                    state = SessionState.CONNECTING
                    listener?.onStateChanged(state)
                } else {
                    logger.info("Device not in same subnet (lan=$lanIp), use relay")
                }

                // 1. 认证（中继）
                if (relay.token.isEmpty()) {
                    if (!relay.authenticate(config)) {
                        fail("认证失败：${relay.lastError}")
                        return@Thread
                    }
                }

                // 2. 请求隧道
                val t = relay.requestTunnel(deviceId)
                this.tunnel = t
                logger.info("Tunnel established: session=${t.session_id} host=${t.tunnel_host} port=${t.tunnel_port}")

                // 3. 连接隧道服务器 + 握手 [0x02] + session_id
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

                // 4. 发送压缩率控制帧（让 PC 端按设置调整编码质量）
                sendQualityControl()

                // 5. 启动接收线程
                running = true
                connectionMode = ConnectionMode.RELAY
                state = SessionState.CONNECTED
                logger.info("Remote session connected (relay)")
                listener?.onStateChanged(state)
                receiveLoop()
            } catch (e: Exception) {
                fail("连接失败：${e.message}")
            }
        }.apply { isDaemon = true }.start()
    }

    /** 发送压缩率控制帧：{action:"quality", percent:N}。 */
    private fun sendQualityControl() {
        try {
            val out = output ?: return
            val json = "{\"action\":\"quality\",\"percent\":$qualityPercent}"
            val data = json.toByteArray(Charsets.UTF_8)
            synchronized(out) {
                out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_CONTROL, data.size))
                out.write(data)
                out.flush()
            }
            logger.info("Quality control sent: $qualityPercent%")
        } catch (_: Exception) {
        }
    }

    /**
     * 局域网直连：直连 PC 的 LAN_PORT，发送 TYPE_AUTH 认证帧。
     * 成功进入 receiveLoop 并返回 true；失败清理并返回 false（由上层回退中继）。
     */
    private fun tryLanDirect(lanIp: String, config: ServerConfig): Boolean {
        return try {
            val sock = Socket(lanIp, LanUtils.LAN_PORT)
            sock.tcpNoDelay = true
            this.tunnelSocket = sock
            input = DataInputStream(sock.getInputStream())
            val out = DataOutputStream(sock.getOutputStream())
            output = out

            // 认证帧：TYPE_AUTH + auth_key(SHA-256 hex 小写)
            val authKey = sha256Hex(config.preSharedKey)
            val keyBytes = authKey.toByteArray(Charsets.UTF_8)
            out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_AUTH, keyBytes.size))
            out.write(keyBytes)
            out.flush()
            logger.info("LAN auth sent, waiting for video stream")

            // 压缩率控制帧（认证后即告知 PC 编码质量）
            sendQualityControl()

            running = true
            connectionMode = ConnectionMode.LAN
            state = SessionState.CONNECTED
            logger.info("Remote session connected (LAN direct)")
            listener?.onStateChanged(state)
            receiveLoop()
            true
        } catch (e: Exception) {
            errorMessage = e.message ?: "LAN direct failed"
            logger.warn("LAN direct exception: ${e.message}")
            closeSocket()
            false
        }
    }

    /** SHA-256 hex 小写（与 relay/PC 端 auth_key 算法一致）。 */
    private fun sha256Hex(input: String): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(input.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
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
                    RemoteFrameProtocol.TYPE_VIDEO_FRAME -> {
                        // 按编码格式分发：h264 → MediaCodec，jpeg → BitmapFactory 铺满画布
                        // （pan/scale 由 View 变换实现，画布内不叠加）
                        if (codec == "jpeg") {
                            jpegDecoder.decodeToSurface(data, surface)
                        } else {
                            decoder.decode(data)
                        }
                    }
                    RemoteFrameProtocol.TYPE_CONTROL -> handleControl(data)
                    RemoteFrameProtocol.TYPE_HEARTBEAT -> { /* 心跳，忽略 */ }
                    else -> logger.warn("Unknown frame type: 0x${type.toString(16)}")
                }
            }
        } catch (e: Exception) {
            // 读线程退出=连接断开，记录原因（EOF=对端正常关闭，Reset=对端异常关闭）
            logger.warn("Receive loop exited: ${e.javaClass.name}: ${e.message}")
        } finally {
            logger.warn("Receive loop ended, disconnecting (state was $state)")
            disconnect()
        }
    }

    /** 处理控制帧（分辨率/帧率/编码格式信息）。 */
    private fun handleControl(data: ByteArray) {
        try {
            val json = JSONObject(String(data, Charsets.UTF_8))
            val w = json.optInt("width", 1280)
            val h = json.optInt("height", 720)
            val c = json.optString("codec", "h264")
            val prevCodec = codec
            // 先保存分辨率（即使 surface 未就绪也不丢失），setSurface 时用最新值
            videoWidth = w
            videoHeight = h
            codec = c
            val surf = surface
            // 切到 jpeg：必须停止 H.264 解码器，否则 MediaCodec 占用 surface，
            // jpeg 的 lockHardwareCanvas 与之冲突会导致 native 崩溃（无 Java 异常）
            if (prevCodec != "jpeg" && c == "jpeg") {
                decoder.stop()
                logger.info("Control: codec switched to jpeg, H264 decoder stopped to free surface")
            }
            if (surf != null && c != "jpeg") {
                // JPEG 不需要预启动解码器（BitmapFactory 直接画）；H.264 需要 MediaCodec
                decoder.start(surf, w, h)
                logger.info("Control: resolution=$w x $h, codec=$c, decoder started on existing surface")
            } else {
                logger.info("Control: resolution=$w x $h, codec=$c saved (surface not ready or jpeg)")
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
            // surface 就绪：H.264 模式才启动 MediaCodec；jpeg 模式用 lockHardwareCanvas 直绘，
            // 误启动 H264 会占用 surface 导致 jpeg 渲染崩溃
            if (videoWidth > 0 && videoHeight > 0 && codec != "jpeg") {
                decoder.start(surface, videoWidth, videoHeight)
                logger.info("Surface ready, H264 decoder started: ${videoWidth}x${videoHeight}")
            } else if (codec == "jpeg") {
                logger.info("Surface ready, jpeg mode (no H264 decoder): ${videoWidth}x${videoHeight}")
            } else {
                logger.info("Surface ready but no resolution yet (waiting CONTROL frame)")
            }
        }
    }

    /** 发送输入事件（阶段 5 使用）。 */
    fun sendInput(type: Byte, data: ByteArray) {
        val out = output ?: run {
            logger.warn("sendInput: no output stream (not connected?)")
            return
        }
        try {
            synchronized(out) {
                out.write(RemoteFrameProtocol.makeHeader(type, data.size))
                out.write(data)
                out.flush()
            }
            logger.info("Input sent: type=0x${type.toString(16)} len=${data.size}")
        } catch (e: Exception) {
            // 注意：SocketException 等异常 message 可能为 null，必须带类名定位
            logger.warn("sendInput failed: ${e.javaClass.name}: ${e.message}")
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
