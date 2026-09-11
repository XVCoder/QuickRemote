package com.quickremote.app.services

import android.view.Surface
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.data.models.TunnelResponse
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket

// 看门狗参数：检查间隔 5s；无任何数据超时 30s；无视频帧超时 30s。
// PC 端心跳 5s/次 + 静止桌面保底帧 1fps，正常会话不会触碰这两个阈值。
private const val WATCHDOG_CHECK_INTERVAL_MS = 5_000L
private const val WATCHDOG_NO_DATA_TIMEOUT_MS = 30_000L
private const val WATCHDOG_NO_VIDEO_TIMEOUT_MS = 30_000L

/**
 * 隧道 socket 的读超时。
 *
 * 必须设置：不设时 `read()` 会**永久阻塞**，半开连接（对端进程已死/网络已切走，
 * 但本端收不到 FIN）会让接收线程静静挂死 —— 看门狗只看状态为 CONNECTED 的连接，
 * 而状态一直是 CONNECTED，于是出现"没断但也没画面、点了没反应"的僵死态。
 *
 * 取值依据：PC 端会话循环每 5 秒发一个心跳帧（`RemoteSessionManager.SendHeartbeats`
 * 同源逻辑，锁屏等待期间也保持），20 秒 = 连续漏掉 4 个心跳，正常会话不可能触发。
 * 超时后按网络故障收尾 → 交给自动重连，比等 30 秒看门狗更快也更可靠。
 */
private const val SOCKET_READ_TIMEOUT_MS = 20_000

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

    /** 图像质量百分比（20-100），连接后通知 PC 调整压缩率。写入即夹取到合法区间。 */
    @Volatile
    var qualityPercent: Int = 80
        set(value) {
            field = value.coerceIn(20, 100)
        }

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

    // ============ 无数据看门狗（v1.0.50 公网黑屏防御） ============
    // 中继竞态等故障下隧道连接建立但 PC 端从未真正加入数据通道：
    // 连接显示成功、心跳全无、零视频帧 → 无限黑屏直到用户手动退出。
    // PC 端正常时至少每 5 秒一个心跳帧、每秒一个视频帧（静止保底），
    // 因此以下两个超时均不可能在正常会话中触发。

    /** 最近一次收到任意帧（视频/控制/心跳）的时间戳。 */
    @Volatile
    private var lastDataAt = 0L

    /** 最近一次收到视频帧的时间戳（0=本会话从未收到）。 */
    @Volatile
    private var lastVideoFrameAt = 0L

    /** PC 锁屏中：锁屏时画面合法暂停（心跳保持），看门狗不判定视频超时。 */
    @Volatile
    private var pcLocked = false

    /** 视频帧宽限期起点：连接建立时；PC 解锁时重置（画面恢复重新计宽限）。 */
    @Volatile
    private var connectedAt = 0L

    /** 会话代数：新会话启动时递增，旧看门狗线程据此自杀，避免误杀新会话。 */
    private val sessionGeneration = java.util.concurrent.atomic.AtomicInteger(0)

    /**
     * 输入事件发送线程。触摸回调在主线程触发，直接写 socket 会抛
     * NetworkOnMainThreadException（Android 禁止主线程网络 I/O），
     * 输入帧从未离开手机——表现为"点了没反应"。所有输入写入必须经此线程。
     */
    private val inputExecutor = java.util.concurrent.Executors.newSingleThreadExecutor { r ->
        Thread(r, "qr-input-sender").apply { isDaemon = true }
    }

    @Volatile
    private var running = false

    /**
     * App 是否处于前台。
     *
     * 看门狗只在**前台**判定超时：App 被切到后台/锁屏时，即使有保活锁，
     * 网络与线程调度也都不受我们控制，此刻判定"无数据"会把一条本来还活着的连接杀掉 ——
     * 这正是「一进后台就断线」的主因之一。回到前台时由 [setAppForeground] 给一段宽限期，
     * 让积压的数据先流进来再恢复判定。
     */
    @Volatile
    private var appForeground = true

    /** 事件监听器。 */
    interface Listener {
        fun onStateChanged(state: SessionState) {}
        fun onVideoFrame(w: Int, h: Int) {}

        /** PC 端锁屏状态通知（锁屏时连接保持、画面暂停）。 */
        fun onPcLockStatus(locked: Boolean) {}

        /** 远程解锁失败通知（如 PC 端未以管理员身份运行）。 */
        fun onPcUnlockFailed() {}

        /** 被控端要求访问验证码（UI 弹输入框，经 sendAuthCode 提交）。 */
        fun onAuthRequired() {}

        /** 验证码错误（UI 可重新输入；被控端累计 3 次失败或超时会断开）。 */
        fun onAuthFailed() {}

        /** 验证码通过（被控端开始建立会话，UI 关闭验证输入框）。 */
        fun onAuthOk() {}
    }

    /** 最近一次会话的设备信息与配置，供断线自动重连时复用。 */
    private var lastHostname: String = ""
    private var lastLanIp: String = ""
    private var lastServerConfig: ServerConfig? = null

    // ============ 剪贴板双向同步（v1.0.70） ============
    // 会话级状态：start() 每次重建 —— 复用旧组装器会让上次的半个载荷串味。
    /** 对端分片组装器（按 id 归组，5 秒超时丢弃残片）。 */
    private var clipboardAssembler: ClipboardAssembler = ClipboardAssembler()

    /** 最后一次由对端写入本机剪贴板的内容哈希：本机再上报时命中即跳过（切断回环）。 */
    @Volatile
    private var lastAppliedClipHash: String = ""

    /** 最后一次推送出去的本机剪贴板内容哈希：内容未变化时不重复推送。 */
    @Volatile
    private var lastSentClipHash: String = ""

    /**
     * 收到对端剪贴板全文（分片已组装完成）。由接收线程回调，
     * UI 层负责写入本机剪贴板 —— Android 10+ 剪贴板只能在前台读写。
     */
    @Volatile
    var onClipboardReceived: ((String) -> Unit)? = null

    /**
     * 最近一次结束会话的原因，供 [ReconnectPolicy] 判定是否值得自动重连。
     * 默认 UserInitiated：未经历过断线时不应被误判为"网络断了"。
     */
    @Volatile
    var lastDisconnectReason: ReconnectPolicy.Reason = ReconnectPolicy.Reason.UserInitiated
        internal set

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
        this.lastHostname = hostname
        this.lastLanIp = lanIp
        this.lastServerConfig = config
        this.errorMessage = ""
        this.state = SessionState.CONNECTING
        this.qualityPercent = qualityPercent.coerceIn(20, 100)
        // 剪贴板为会话级状态：重建组装器，避免重连后残留的未完成分片串味
        this.clipboardAssembler = ClipboardAssembler()
        this.lastAppliedClipHash = ""
        this.lastSentClipHash = ""
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
                        fail("认证失败：${relay.lastError}", ReconnectPolicy.Reason.AuthFailed)
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
                configureSocket(sock)
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
                connectedAt = System.currentTimeMillis()
                lastDataAt = connectedAt
                lastVideoFrameAt = 0L
                startWatchdog()
                logger.info("Remote session connected (relay)")
                listener?.onStateChanged(state)
                receiveLoop()
            } catch (e: Exception) {
                fail("连接失败：${e.message}")
            }
        }.apply { isDaemon = true }.start()
    }

    /**
     * 无数据看门狗：会话连接后监控数据流，防止"连接成功但无限黑屏"。
     * - 30 秒无任何帧（连心跳都没有）→ 链路中断或 PC 未加入隧道（中继竞态）
     * - 30 秒未收到任何视频帧（心跳正常）→ PC 推流异常（编码器故障等）
     * PC 锁屏时画面合法暂停（心跳保持），跳过视频超时判定。
     * 超时通过 fail() 主动断开并提示用户，而不是永远黑屏。
     */
    private fun startWatchdog() {
        val gen = sessionGeneration.incrementAndGet()
        Thread {
            while (running && sessionGeneration.get() == gen) {
                try { Thread.sleep(WATCHDOG_CHECK_INTERVAL_MS) } catch (_: InterruptedException) { return@Thread }
                if (!running || sessionGeneration.get() != gen || state != SessionState.CONNECTED) continue
                if (pcLocked) continue
                // 后台/锁屏期间不判超时：此刻的"没数据"不代表链路已死（见 appForeground 注释）。
                // 真正死掉的链路由 socket 读超时（SOCKET_READ_TIMEOUT_MS）兜底。
                if (!appForeground) continue
                val now = System.currentTimeMillis()
                if (now - lastDataAt > WATCHDOG_NO_DATA_TIMEOUT_MS) {
                    logger.warn("Watchdog: no data for ${(now - lastDataAt) / 1000}s, disconnecting")
                    fail(
                        "连接后${WATCHDOG_NO_DATA_TIMEOUT_MS / 1000}秒无任何数据（链路中断或隧道未建立），已自动断开，请重连",
                        ReconnectPolicy.Reason.Watchdog
                    )
                    break
                }
                if (lastVideoFrameAt == 0L && now - connectedAt > WATCHDOG_NO_VIDEO_TIMEOUT_MS) {
                    logger.warn("Watchdog: no video frame since connect (${(now - connectedAt) / 1000}s), disconnecting")
                    fail(
                        "连接后${WATCHDOG_NO_VIDEO_TIMEOUT_MS / 1000}秒未收到视频画面（远端推流异常），已自动断开，请重连",
                        ReconnectPolicy.Reason.Watchdog
                    )
                    break
                }
            }
        }.apply { isDaemon = true; name = "qr-watchdog" }.start()
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
     * 会话内调整画质：写入档位并立即下发控制帧，无需重建会话。
     * 走 inputExecutor 避免网络阻塞时拖累接收线程（与 keyframe 请求同策略）。
     *
     * 注意方法名不能叫 setQualityPercent —— 会与 qualityPercent 属性的 JVM setter 签名冲突。
     */
    fun applyQualityPercent(percent: Int) {
        qualityPercent = percent
        inputExecutor.execute { sendQualityControl() }
    }

    /**
     * 请求 PC 端立即输出 IDR 关键帧：{action:"keyframe"}。
     * 解码器启动可能晚于 PC 端首个关键帧（中继模式下 PC 先推流），
     * 错过 IDR 后全是 P 帧无法解码 → 黑屏；主动请求秒级出画。
     * 走 inputExecutor 避免网络阻塞时拖累接收线程。
     */
    private fun requestKeyframe() {
        val out = output ?: return
        val data = "{\"action\":\"keyframe\"}".toByteArray(Charsets.UTF_8)
        inputExecutor.execute {
            try {
                synchronized(out) {
                    out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_CONTROL, data.size))
                    out.write(data)
                    out.flush()
                }
                logger.info("Keyframe request sent")
            } catch (e: Exception) {
                logger.warn("Keyframe request failed: ${e.javaClass.name}: ${e.message}")
            }
        }
    }

    /**
     * 发送远程解锁控制帧：{action:"unlock", password:"..."}。
     * 密码由 PC 端 SYSTEM 辅助程序在锁屏安全桌面注入，实现向日葵式的远程解锁。
     */
    fun sendUnlockRequest(password: String) {
        if (password.isEmpty()) return
        val out = output ?: run {
            logger.warn("sendUnlockRequest: no output stream (not connected?)")
            return
        }
        val json = JSONObject().put("action", "unlock").put("password", password).toString()
        val data = json.toByteArray(Charsets.UTF_8)
        inputExecutor.execute {
            try {
                synchronized(out) {
                    out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_CONTROL, data.size))
                    out.write(data)
                    out.flush()
                }
                logger.info("Unlock request sent")
            } catch (e: Exception) {
                logger.warn("Unlock request failed: ${e.javaClass.name}: ${e.message}")
            }
        }
    }

    /**
     * 提交访问验证码：{action:"auth", code:"..."}。
     * 被控端开启验证保护时须先通过验证（auth_required 通知），否则不推流、超时断开。
     */
    fun sendAuthCode(code: String) {
        if (code.isEmpty()) return
        val out = output ?: run {
            logger.warn("sendAuthCode: no output stream (not connected?)")
            return
        }
        val json = JSONObject().put("action", "auth").put("code", code).toString()
        val data = json.toByteArray(Charsets.UTF_8)
        inputExecutor.execute {
            try {
                synchronized(out) {
                    out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_CONTROL, data.size))
                    out.write(data)
                    out.flush()
                }
                logger.info("Auth code sent")
            } catch (e: Exception) {
                logger.warn("Auth code send failed: ${e.javaClass.name}: ${e.message}")
            }
        }
    }

    /**
     * 局域网直连：直连 PC 的 LAN_PORT，发送 TYPE_AUTH 认证帧。
     * 成功进入 receiveLoop 并返回 true；失败清理并返回 false（由上层回退中继）。
     */
    private fun tryLanDirect(lanIp: String, config: ServerConfig): Boolean {
        return try {
            val sock = Socket(lanIp, LanUtils.LAN_PORT)
            configureSocket(sock)
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
            connectedAt = System.currentTimeMillis()
            lastDataAt = connectedAt
            lastVideoFrameAt = 0L
            startWatchdog()
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

                // 看门狗喂狗：任意帧到达即刷新数据活性时间戳
                lastDataAt = System.currentTimeMillis()

                when (type) {
                    RemoteFrameProtocol.TYPE_VIDEO_FRAME -> {
                        lastVideoFrameAt = lastDataAt
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
        } catch (e: java.net.SocketTimeoutException) {
            // 读超时 = 链路变哑（半开连接/网络切走）。PC 端正常时每 5 秒一个心跳，
            // 连续 20 秒读不到任何东西说明这条路已经死了，按网络故障收尾并交给自动重连。
            logger.warn("Receive loop timeout: no data for ${SOCKET_READ_TIMEOUT_MS / 1000}s (link dead)")
        } catch (e: Exception) {
            // 读线程退出=连接断开，记录原因（EOF=对端正常关闭，Reset=对端异常关闭）
            logger.warn("Receive loop exited: ${e.javaClass.name}: ${e.message}")
        } finally {
            // running 仍为 true ⇒ 不是我们主动收尾（fail()/disconnect() 都会先置 false），
            // 说明是对端断开或网络中断。此时**绝不能**走 disconnect()：
            // 那会把断线原因标成 UserInitiated，自动重连白名单直接放行失败。
            if (running) {
                logger.warn("Receive loop ended unexpectedly, treating as network failure")
                fail("连接已断开，正在尝试恢复…", ReconnectPolicy.Reason.Network)
            } else {
                logger.warn("Receive loop ended (state was $state)")
            }
        }
    }

    /** 处理控制帧（分辨率/帧率/编码格式信息）。 */
    private fun handleControl(data: ByteArray) {
        try {
            val json = JSONObject(String(data, Charsets.UTF_8))

            // 访问验证码交互（被控端 v1.1.54+ 开启验证保护时）：auth_required 要求输入 /
            // auth_failed 错误重试 / auth_ok 通过开始推流
            if (json.has("action")) {
                when (json.optString("action")) {
                    "auth_required" -> {
                        logger.info("Control: auth required by host")
                        listener?.onAuthRequired()
                    }
                    "auth_failed" -> {
                        logger.warn("Control: auth rejected by host")
                        listener?.onAuthFailed()
                    }
                    "auth_ok" -> {
                        logger.info("Control: auth accepted by host")
                        listener?.onAuthOk()
                    }
                    "clipboard" -> handleClipboardChunk(json)
                }
                return
            }

            // 状态通知（PC 锁屏/解锁/解锁失败）：不携带分辨率，仅更新提示状态
            if (json.has("status")) {
                when (json.optString("status")) {
                    "locked" -> {
                        pcLocked = true
                        listener?.onPcLockStatus(true)
                    }
                    "unlocked" -> {
                        pcLocked = false
                        // 解锁后画面恢复需要时间，重置视频帧宽限期起点
                        //（仅清 lastVideoFrameAt 会让看门狗拿旧 connectedAt 立即误判超时）
                        lastVideoFrameAt = 0L
                        connectedAt = System.currentTimeMillis()
                        listener?.onPcLockStatus(false)
                    }
                    "unlock_failed" -> listener?.onPcUnlockFailed()
                }
                logger.info("Control: PC status = ${json.optString("status")}")
                return
            }

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
                // 解码器刚启动：PC 端此前的 IDR 已错过（被丢弃），请求立即刷新
                requestKeyframe()
            } else {
                logger.info("Control: resolution=$w x $h, codec=$c saved (surface not ready or jpeg)")
            }
            listener?.onVideoFrame(w, h)
        } catch (e: Exception) {
            logger.warn("Control parse failed: ${e.message}")
        }
    }

    /**
     * 通知 App 前后台切换（由会话页在 ON_RESUME / ON_STOP 调用）。
     *
     * 回到前台时**重置数据活性时间戳**，给积压数据一段宽限期 ——
     * 否则看门狗会在恢复的第一时间就把这条"刚回来、数据还在路上"的连接判死，
     * 用户看到的就是"切出去再回来必然断开"。
     */
    fun setAppForeground(foreground: Boolean) {
        if (appForeground == foreground) return
        appForeground = foreground
        if (foreground) {
            val now = System.currentTimeMillis()
            lastDataAt = now
            connectedAt = now
            lastVideoFrameAt = 0L
            logger.info("App foregrounded: watchdog grace period restarted")
        } else {
            logger.info("App backgrounded: watchdog paused")
        }
    }

    /**
     * 隧道 socket 的通用配置：低延迟 + TCP keepalive + 读超时。
     *
     * 读超时必须设置，否则半开连接会让接收线程**永久阻塞**，
     * 表现为"没断但也没画面"的僵死态（详见 [SOCKET_READ_TIMEOUT_MS]）。
     */
    private fun configureSocket(sock: Socket) {
        try {
            sock.tcpNoDelay = true
            sock.keepAlive = true
            sock.soTimeout = SOCKET_READ_TIMEOUT_MS
        } catch (e: Exception) {
            logger.warn("Configure socket failed: ${e.javaClass.name}: ${e.message}")
        }
    }

    /**
     * 设置渲染 Surface（由 UI 层在 SurfaceView 创建/尺寸变化/销毁时调用）。
     *
     * ⚠️ 性能关键：**同一个 Surface 的尺寸变化绝不能重建解码器**。
     * SurfaceView 因布局变化（键盘弹起、旋转、进出全屏）改尺寸时会回调 surfaceChanged，
     * 但 Surface 实例不变、缓冲区依然有效，解码输出会由合成器按新尺寸自动缩放
     * （MediaCodec 的 configure 宽高只是提示，真实尺寸以 SPS 为准）。
     * 若照旧 restart，键盘动画期间每帧都会跑一遍 stop → createDecoderByType → configure
     * → start → 等 IDR，这正是「切输入法特别卡」的元凶。
     * 与 ExoPlayer 的做法一致：改尺寸不重启 codec，只有 Surface 换实例/分辨率变了才重建。
     */
    fun setSurface(surface: Surface?) {
        val previous = this.surface
        this.surface = surface

        // Surface 已销毁：必须停掉解码器。MediaCodec 继续向失效 Surface 输出会 native 崩溃；
        // 下次 surfaceCreated 时本方法会被重新调用并重启解码器。
        if (surface == null) {
            if (previous != null && decoder.isRunning) {
                decoder.stop()
                logger.info("Surface destroyed, H264 decoder stopped")
            }
            return
        }

        // 同一 Surface 实例（仅尺寸变化）且解码器仍在跑：无需任何操作，直接返回
        if (previous === surface && decoder.isRunning) {
            logger.info("Surface size-only change, decoder kept (no restart)")
            return
        }

        if (state != SessionState.CONNECTED) return

        // surface 就绪：H.264 模式才启动 MediaCodec；jpeg 模式用 lockHardwareCanvas 直绘，
        // 误启动 H264 会占用 surface 导致 jpeg 渲染崩溃
        if (videoWidth > 0 && videoHeight > 0 && codec != "jpeg") {
            decoder.start(surface, videoWidth, videoHeight)
            logger.info("Surface ready, H264 decoder started: ${videoWidth}x${videoHeight}")
            // surface 晚就绪期间的 IDR 已被丢弃，请求 PC 立即刷新关键帧
            requestKeyframe()
        } else if (codec == "jpeg") {
            logger.info("Surface ready, jpeg mode (no H264 decoder): ${videoWidth}x${videoHeight}")
        } else {
            logger.info("Surface ready but no resolution yet (waiting CONTROL frame)")
        }
    }

    /** 发送输入事件（触摸/键盘回调在主线程调用，写入转发到后台线程执行）。 */
    fun sendInput(type: Byte, data: ByteArray) {
        val out = output ?: run {
            logger.warn("sendInput: no output stream (not connected?)")
            return
        }
        inputExecutor.execute {
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
    }

    // ============ 剪贴板双向同步 ============

    /**
     * 收到一片对端剪贴板数据。集齐后回调 UI 写入本机剪贴板。
     *
     * 记下全文哈希：本机下一次上报（ON_RESUME）命中同一哈希即跳过 ——
     * 否则 PC 复制的内容会在两端来回推送，形成无限互刷。
     */
    private fun handleClipboardChunk(json: JSONObject) {
        val id = json.optString("id")
        val seq = json.optInt("seq", -1)
        val total = json.optInt("total", 0)
        val text = json.optString("text")

        val full = clipboardAssembler.add(id, seq, total, text) ?: return
        lastAppliedClipHash = sha256Hex(full)
        logger.info("Clipboard received from peer: ${full.length} chars")
        onClipboardReceived?.invoke(full)
    }

    /**
     * 把本机剪贴板文本推送到对端。
     *
     * 调用时机由 UI 层决定（回到前台 / 会话内面板操作后）—— Android 10+ 禁止后台读剪贴板，
     * 因此绝不做轮询。与"上次由对端写入本机"或"上次已推送"的内容相同则直接跳过。
     */
    fun syncLocalClipboard(text: String?) {
        if (text == null) return
        if (state != SessionState.CONNECTED) return

        val hash = sha256Hex(text)
        if (hash == lastAppliedClipHash || hash == lastSentClipHash) return

        val chunks = ClipboardChunker.split(text)
        if (chunks.isEmpty()) {
            logger.warn("Clipboard too large to sync (${text.length} chars), skipped")
            return
        }
        lastSentClipHash = hash

        val id = java.util.UUID.randomUUID().toString().replace("-", "").take(8)
        // 走 inputExecutor：主线程直接写 socket 会抛 NetworkOnMainThreadException
        inputExecutor.execute {
            try {
                val out = output ?: return@execute
                chunks.forEach { c ->
                    val frame = "{\"action\":\"clipboard\",\"id\":\"$id\",\"seq\":${c.seq}," +
                        "\"total\":${c.total},\"text\":${jsonString(c.text)}}"
                    val data = frame.toByteArray(Charsets.UTF_8)
                    synchronized(out) {
                        out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_CONTROL, data.size))
                        out.write(data)
                        out.flush()
                    }
                }
                logger.info("Clipboard sent to peer: ${text.length} chars in ${chunks.size} chunk(s)")
            } catch (e: Exception) {
                logger.warn("Clipboard send failed: ${e.javaClass.name}: ${e.message}")
            }
        }
    }

    /** 最小 JSON 字符串转义（只覆盖 JSON 必需项，不引入新依赖）。 */
    private fun jsonString(s: String): String {
        val sb = StringBuilder(s.length + 16)
        sb.append('"')
        for (ch in s) {
            when (ch) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (ch < ' ') sb.append("\\u%04x".format(ch.code)) else sb.append(ch)
            }
        }
        sb.append('"')
        return sb.toString()
    }

    private fun readExactly(input: DataInputStream, buf: ByteArray, count: Int) {
        var offset = 0
        while (offset < count) {
            val read = input.read(buf, offset, count - offset)
            if (read < 0) throw java.io.EOFException()
            offset += read
        }
    }

    private fun fail(message: String, reason: ReconnectPolicy.Reason = ReconnectPolicy.Reason.Network) {
        errorMessage = message
        state = SessionState.FAILED
        running = false
        lastDisconnectReason = reason
        logger.warn("Remote session failed: $message")
        listener?.onStateChanged(state)
        closeSocket()
    }

    /**
     * 重新连接上一次的设备。
     *
     * start() 内部会重建全部会话级资源（socket / 输入输出流 / 解码器 / 看门狗代数），
     * 所以这里直接复用它 —— **绝不可复用上一次会话的任何资源实例**。
     * 历史教训（提交 b837eb1）：复用被 CompleteAdding() 关闭的帧队列，
     * 导致第二次连接秒断，日志特征为 session started 紧接 Encode loop ended。
     */
    fun reconnect() {
        val config = lastServerConfig ?: return
        val id = deviceId
        if (id.isBlank()) return
        logger.info("Reconnecting: device=$id host=$lastHostname lan=$lastLanIp")
        start(id, lastHostname, lastLanIp, qualityPercent, config)
    }

    /** 断开会话。 */
    fun disconnect() {
        running = false
        lastDisconnectReason = ReconnectPolicy.Reason.UserInitiated
        decoder.stop()
        // FAILED（含看门狗超时）不降级为 DISCONNECTED：保留错误信息供 UI 展示断开原因
        if (state != SessionState.FAILED) {
            state = SessionState.DISCONNECTED
            listener?.onStateChanged(state)
        }
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
