package com.quickremote.app.freerdp

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Rect
import android.view.Surface
import com.freerdp.freerdpcore.services.LibFreeRDP
import com.quickremote.app.services.Logger

/**
 * FreeRDP 客户端封装。
 *
 * 对接真实 libfreerdp-android.so 的 JNI 接口（见 [LibFreeRDP]）。
 * 采用软件 GDI 渲染模型：native 将远程桌面绘制到内部 Bitmap，
 * 我们通过 [LibFreeRDP.updateGraphics] 填充 Bitmap，再绘制到 Surface。
 *
 * 当 .so 库未加载时，所有操作降级为空操作并记录日志。
 */
class FreeRdpClient(
    private val context: Context,
    private val logger: Logger = Logger()
) : LibFreeRDP.EventListener, LibFreeRDP.UIEventListener {

    companion object {
        private const val TAG = "FreeRdpClient"

        /** 鼠标按钮标志位（参考 MS-RDPBCGR 2.2.8.1.1.3.1.1） */
        const val PTR_FLAGS_DOWN = 0x8000
        const val PTR_FLAGS_BUTTON1 = 0x1000  // 左键
        const val PTR_FLAGS_BUTTON2 = 0x2000  // 右键
        const val PTR_FLAGS_MOVE = 0x0800
        const val PTR_FLAGS_WHEEL = 0x0200
        const val PTR_FLAGS_HWHEEL = 0x0400
        const val WHEEL_NEGATIVE = 0x0100
    }

    /** 连接配置。 */
    data class ConnectConfig(
        val hostname: String,
        val port: Int = 3389,
        val username: String = "",
        val password: String = "",
        val domain: String = "",
        val width: Int = 1280,
        val height: Int = 720,
        val colorDepth: Int = 32
    )

    /** 事件监听器。 */
    interface Listener {
        fun onConnecting() {}
        fun onConnected() {}
        fun onDisconnected(error: String?) {}
        fun onGraphicsUpdated(x: Int, y: Int, width: Int, height: Int) {}
    }

    var listener: Listener? = null

    /** FreeRDP 实例指针。0 表示未创建。 */
    private var instance: Long = 0L

    /** 渲染目标 Surface。 */
    private var surface: Surface? = null

    /** 远程桌面位图（native 填充）。 */
    private var bitmap: Bitmap? = null

    private val renderLock = Any()

    /** .so 库是否可用。 */
    val isAvailable: Boolean get() = LibFreeRDP.isLoaded()

    /** .so 库加载失败时的真实异常信息（用于定位问题）。 */
    val loadError: String? get() = LibFreeRDP.getLoadError()

    /**
     * 连接到远程桌面。
     * @param config 连接配置
     * @param surface 渲染目标 Surface（可为 null）
     * @return true 表示连接已启动
     */
    fun connect(config: ConnectConfig, surface: Surface?): Boolean {
        if (!LibFreeRDP.isLoaded()) {
            logger.warn("FreeRDP .so not loaded, cannot connect")
            listener?.onDisconnected("FreeRDP 库未加载，无法建立远程连接")
            return false
        }

        this.surface = surface
        LibFreeRDP.listener = this
        LibFreeRDP.uiListener = this

        if (instance != 0L) {
            logger.warn("Instance already exists, releasing first")
            release()
        }

        logger.info("Creating FreeRDP instance: ${config.hostname}:${config.port} " +
            "${config.width}x${config.height}@${config.colorDepth}")

        instance = LibFreeRDP.newInstance(context)
        if (instance == 0L) {
            logger.error("Failed to create FreeRDP instance")
            listener?.onDisconnected("创建 FreeRDP 实例失败")
            return false
        }

        val args = buildArguments(config)
        if (!LibFreeRDP.parseArguments(instance, args)) {
            logger.error("Failed to parse FreeRDP arguments")
            LibFreeRDP.freeInstance(instance)
            instance = 0L
            listener?.onDisconnected("FreeRDP 参数解析失败")
            return false
        }

        listener?.onConnecting()
        logger.info("Connecting to ${config.hostname}:${config.port}")
        val ok = LibFreeRDP.connect(instance)
        if (!ok) {
            logger.error("FreeRDP connect failed immediately")
            LibFreeRDP.freeInstance(instance)
            instance = 0L
            listener?.onDisconnected("连接启动失败")
            return false
        }
        return true
    }

    /** 构建 FreeRDP 命令行参数数组。 */
    private fun buildArguments(config: ConnectConfig): Array<String> {
        val args = ArrayList<String>()
        args.add(TAG)
        args.add("/gdi:sw")
        args.add("/v:" + config.hostname)
        args.add("/port:" + config.port)
        if (config.username.isNotEmpty()) args.add("/u:" + config.username)
        if (config.password.isNotEmpty()) args.add("/p:" + config.password)
        if (config.domain.isNotEmpty()) args.add("/d:" + config.domain)
        args.add("/size:" + config.width + "x" + config.height)
        args.add("/bpp:" + config.colorDepth)
        args.add("/clipboard")
        args.add("/cert:ignore")
        // 降低 TLS 安全级别，兼容 Windows RDP 服务较弱的 TLS 密码套件/版本，
        // 修复 ERRCONNECT_TLS_CONNECT_FAILED（"the connection failed at tls connect"）。
        // 注意：FreeRDP 3.x 的 /sec 只接受 rdp|tls|nla|ext，不接受 negotiate
        //（安全协商本就是默认行为），传 /sec:negotiate 会导致参数解析失败。
        args.add("/tls:seclevel:0")
        // 详细日志：便于定位 TLS 握手失败的确切原因（输出到 logcat）。
        args.add("/log-level:DEBUG")
        return args.toTypedArray()
    }

    /** 断开连接。 */
    fun disconnect() {
        if (instance == 0L) return
        logger.info("Disconnecting FreeRDP instance")
        try {
            if (LibFreeRDP.isLoaded()) {
                LibFreeRDP.disconnect(instance)
            }
        } catch (e: Exception) {
            logger.warn("Disconnect error: ${e.message}")
        }
    }

    /** 释放资源。必须在 UI 销毁时调用。 */
    fun release() {
        if (instance == 0L) return
        logger.info("Releasing FreeRDP instance")
        try {
            if (LibFreeRDP.isLoaded()) {
                LibFreeRDP.freeInstance(instance)
            }
        } catch (e: Exception) {
            logger.warn("Release error: ${e.message}")
        }
        instance = 0L
        LibFreeRDP.listener = null
        LibFreeRDP.uiListener = null
        synchronized(renderLock) {
            bitmap = null
        }
        surface = null
    }

    /** 更新渲染 Surface。 */
    fun setSurface(surface: Surface?) {
        this.surface = surface
        // 新 Surface 就绪后重绘当前缓存帧
        renderBitmap()
    }

    // ==================== 输入事件 ====================

    /** 发送鼠标移动事件。 */
    fun sendMouseMove(x: Int, y: Int) {
        if (instance != 0L && LibFreeRDP.isLoaded()) {
            LibFreeRDP.sendCursorEvent(instance, x, y, PTR_FLAGS_MOVE)
        }
    }

    /** 发送鼠标左键点击。 */
    fun sendLeftClick(x: Int, y: Int, down: Boolean) {
        if (instance != 0L && LibFreeRDP.isLoaded()) {
            val flags = PTR_FLAGS_BUTTON1 or if (down) PTR_FLAGS_DOWN else 0
            LibFreeRDP.sendCursorEvent(instance, x, y, flags)
        }
    }

    /** 发送鼠标右键点击。 */
    fun sendRightClick(x: Int, y: Int, down: Boolean) {
        if (instance != 0L && LibFreeRDP.isLoaded()) {
            val flags = PTR_FLAGS_BUTTON2 or if (down) PTR_FLAGS_DOWN else 0
            LibFreeRDP.sendCursorEvent(instance, x, y, flags)
        }
    }

    /** 发送键盘事件。 */
    fun sendKey(keycode: Int, down: Boolean) {
        if (instance != 0L && LibFreeRDP.isLoaded()) {
            LibFreeRDP.sendKeyEvent(instance, keycode, down)
        }
    }

    /** 发送 Unicode 字符。 */
    fun sendUnicodeChar(unicode: Int, down: Boolean) {
        if (instance != 0L && LibFreeRDP.isLoaded()) {
            LibFreeRDP.sendUnicodeKeyEvent(instance, unicode, down)
        }
    }

    /** 发送鼠标滚轮事件。delta > 0 向上滚动，delta < 0 向下滚动。 */
    fun sendWheel(delta: Int) {
        if (instance != 0L && LibFreeRDP.isLoaded()) {
            val flags = if (delta >= 0) {
                PTR_FLAGS_WHEEL or (delta.coerceIn(0, 0xFF))
            } else {
                PTR_FLAGS_WHEEL or WHEEL_NEGATIVE or ((-delta).coerceIn(0, 0xFF))
            }
            LibFreeRDP.sendCursorEvent(instance, 0, 0, flags)
        }
    }

    // ==================== 渲染 ====================

    /** 将当前位图绘制到 Surface。 */
    private fun renderBitmap() {
        val bmp = synchronized(renderLock) { bitmap } ?: return
        val s = surface ?: return
        var canvas: Canvas? = null
        try {
            canvas = s.lockCanvas(null)
            if (canvas != null) {
                val w = canvas.width
                val h = canvas.height
                if (w > 0 && h > 0) {
                    canvas.drawBitmap(bmp, null, Rect(0, 0, w, h), null)
                } else {
                    canvas.drawBitmap(bmp, 0f, 0f, null)
                }
            }
        } catch (e: Exception) {
            logger.warn("renderBitmap error: ${e.message}")
        } finally {
            try {
                if (canvas != null) s.unlockCanvasAndPost(canvas)
            } catch (_: Exception) {
                // 忽略锁屏/解锁失败
            }
        }
    }

    // ==================== JNI 生命周期回调（EventListener） ====================

    override fun OnPreConnect(inst: Long) {
        // 连接前回调，无需处理
    }

    override fun OnConnectionSuccess(inst: Long) {
        logger.info("FreeRDP connection success")
        listener?.onConnected()
    }

    override fun OnConnectionFailure(inst: Long) {
        val err = runCatching { LibFreeRDP.getLastErrorString(inst) }.getOrDefault("")
        logger.error("FreeRDP connection failed: $err")
        val friendly = when {
            err.contains("tls", ignoreCase = true) ->
                "TLS 握手失败。可能原因：PC 端 RDP 服务未启用（请在 PC 客户端点「一键启用 RDP」），" +
                    "或 Windows RDP 密码套件与当前 FreeRDP 不兼容"
            err.contains("timeout", ignoreCase = true) ->
                "连接超时，请检查网络是否可达、隧道端口(8445)是否开放"
            err.contains("refused", ignoreCase = true) ->
                "连接被拒绝：PC 端 RDP 服务(3389)未监听或隧道未建立"
            err.isBlank() -> "未知错误"
            else -> err
        }
        // 附加完整错误码，便于精确定位（如 ERRCONNECT_TLS_CONNECT_FAILED）
        val detail = if (err.isNotBlank()) "\n\n原始错误：$err" else ""
        listener?.onDisconnected("连接失败：$friendly$detail")
    }

    override fun OnDisconnecting(inst: Long) {
        // 断开中
    }

    override fun OnDisconnected(inst: Long) {
        logger.info("FreeRDP disconnected")
        listener?.onDisconnected(null)
    }

    // ==================== JNI UI 回调（UIEventListener） ====================

    override fun OnGraphicsResize(inst: Long, width: Int, height: Int, bpp: Int) {
        logger.info("FreeRDP graphics resize: ${width}x${height}@$bpp")
        synchronized(renderLock) {
            if (width <= 0 || height <= 0) return
            bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        }
    }

    override fun OnGraphicsUpdate(inst: Long, x: Int, y: Int, width: Int, height: Int) {
        val bmp = synchronized(renderLock) { bitmap } ?: return
        if (LibFreeRDP.isLoaded()) {
            LibFreeRDP.updateGraphics(inst, bmp, x, y, width, height)
        }
        renderBitmap()
        listener?.onGraphicsUpdated(x, y, width, height)
    }

    override fun OnSettingsChanged(inst: Long, width: Int, height: Int, bpp: Int) {
        // 可选：根据设置更新分辨率
    }

    override fun OnAuthenticate(
        inst: Long, username: StringBuilder, domain: StringBuilder, password: StringBuilder
    ): Boolean = false

    override fun OnGatewayAuthenticate(
        inst: Long, username: StringBuilder, domain: StringBuilder, password: StringBuilder
    ): Boolean = false

    override fun OnVerifyCertificateEx(
        inst: Long, host: String, port: Long, commonName: String, subject: String,
        issuer: String, fingerprint: String, flags: Long
    ): Int = 1 // 信任证书（与 /cert:ignore 一致）

    override fun OnVerifyChangedCertificateEx(
        inst: Long, host: String, port: Long, commonName: String, subject: String,
        issuer: String, fingerprint: String, oldSubject: String, oldIssuer: String,
        oldFingerprint: String, flags: Long
    ): Int = 1

    override fun OnExperimentalFeature(inst: Long, feature: Int): Boolean = true

    override fun OnRemoteClipboardChanged(inst: Long, data: String) {
        // 剪切板文本暂不处理
    }

    override fun OnRemoteClipboardImageChanged(inst: Long, data: ByteArray) {
        // 剪切板图片暂不处理
    }

    override fun OnPointerSet(inst: Long, pixels: IntArray, width: Int, height: Int, hotX: Int, hotY: Int) {
        // 自定义指针暂不处理
    }

    override fun OnPointerSetNull(inst: Long) {
    }

    override fun OnPointerSetDefault(inst: Long) {
    }

    override fun OnRailWindowUpdate(inst: Long, windowId: Long, width: Int, height: Int, pixels: IntArray) {
        // RemoteApp 窗口更新暂不处理
    }

    override fun OnRailWindowMove(inst: Long, windowId: Long, x: Int, y: Int, w: Int, h: Int) {
    }

    override fun OnRailWindowHide(inst: Long, windowId: Long) {
    }

    override fun OnRailWindowDestroy(inst: Long, windowId: Long) {
    }

    override fun OnRailSessionEnd(inst: Long) {
    }

    override fun OnRailMonitoredDesktop(inst: Long, windowIds: LongArray, activeWindowId: Long) {
    }
}