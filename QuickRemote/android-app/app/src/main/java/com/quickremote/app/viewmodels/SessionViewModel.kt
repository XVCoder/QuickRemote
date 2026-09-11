package com.quickremote.app.viewmodels

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.Logger
import com.quickremote.app.services.ReconnectPolicy
import com.quickremote.app.services.RemoteSessionManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 管理截屏远程会话状态。封装 RemoteSessionManager 并暴露响应式状态。
 * 截屏方案不需要 Windows 凭据（远程画面直接传输，不走 NLA 认证）。
 */
class SessionViewModel(
    app: Application,
    private val settingsStore: SettingsStore,
    private val sessionManager: RemoteSessionManager = RemoteSessionManager(logger = Logger())
) : AndroidViewModel(app) {

    val state: StateFlow<RemoteSessionManager.SessionState> get() = _state
    private val _state = MutableStateFlow(RemoteSessionManager.SessionState.IDLE)

    val tunnel get() = _tunnel
    private val _tunnel = MutableStateFlow<com.quickremote.app.data.models.TunnelResponse?>(null)

    private val _device = MutableStateFlow<Device?>(null)
    val device: StateFlow<Device?> = _device.asStateFlow()

    private val _errorMessage = MutableStateFlow("")
    val errorMessage: StateFlow<String> = _errorMessage.asStateFlow()

    private val _isFullscreen = MutableStateFlow(false)
    val isFullscreen: StateFlow<Boolean> = _isFullscreen.asStateFlow()

    private val _isAutoFullscreen = MutableStateFlow(false)

    private val _isKeyboardVisible = MutableStateFlow(false)
    val isKeyboardVisible: StateFlow<Boolean> = _isKeyboardVisible.asStateFlow()

    /** 远程画面分辨率（收到控制帧后更新）。 */
    private val _videoWidth = MutableStateFlow(0)
    val videoWidth: StateFlow<Int> = _videoWidth.asStateFlow()

    private val _videoHeight = MutableStateFlow(0)
    val videoHeight: StateFlow<Int> = _videoHeight.asStateFlow()

    /** 连接模式：局域网直连 / 公网中继。 */
    private val _connectionMode = MutableStateFlow(RemoteSessionManager.ConnectionMode.RELAY)
    val connectionMode: StateFlow<RemoteSessionManager.ConnectionMode> = _connectionMode.asStateFlow()

    /** 会话内画质档位（20-100），供画质快捷面板高亮当前档位。 */
    private val _qualityPercent = MutableStateFlow(80)
    val qualityPercent: StateFlow<Int> = _qualityPercent.asStateFlow()

    /**
     * 断线自动重连状态。null 表示当前未在重连。
     * @param attempt 从 0 起的重连次数（展示时 +1）
     * @param nextRetryAtMs 下一次重试的绝对时间戳，用于倒计时
     */
    data class ReconnectState(val attempt: Int, val nextRetryAtMs: Long) {
        val displayAttempt: Int get() = attempt + 1
    }

    private val _reconnecting = MutableStateFlow<ReconnectState?>(null)
    val reconnecting: StateFlow<ReconnectState?> = _reconnecting.asStateFlow()

    /** 已安排的重连次数（从 0 起），连接成功或用户取消时归零。 */
    private var reconnectAttempt = 0
    private var reconnectJob: Job? = null

    /** PC 端锁屏状态（锁屏时连接保持、画面暂停，等待解锁恢复）。 */
    private val _pcLocked = MutableStateFlow(false)
    val pcLocked: StateFlow<Boolean> = _pcLocked.asStateFlow()

    /** 远程解锁失败原因（null 表示无错误）。 */
    private val _pcUnlockError = MutableStateFlow<String?>(null)
    val pcUnlockError: StateFlow<String?> = _pcUnlockError.asStateFlow()

    /** 被控端要求访问验证码（弹输入框；验证通过或断开后清除）。 */
    private val _authRequired = MutableStateFlow(false)
    val authRequired: StateFlow<Boolean> = _authRequired.asStateFlow()

    /** 验证码错误提示（null 表示无错误）。 */
    private val _authError = MutableStateFlow<String?>(null)
    val authError: StateFlow<String?> = _authError.asStateFlow()

    /** 画面外空白区触摸板开关（设置页可改，默认启用）。 */
    val blankTouchpad: StateFlow<Boolean> = settingsStore.appSettings
        .map { it.blankTouchpad }
        .stateIn(viewModelScope, SharingStarted.Eagerly, true)

    /** 触摸板完整配置（速度/双击拖动/三指/四指手势开关）。 */
    val touchpadConfig: StateFlow<AppSettings> = settingsStore.appSettings
        .stateIn(viewModelScope, SharingStarted.Eagerly, AppSettings())

    /** 会话状态变更监听器。 */
    private val stateListener = object : RemoteSessionManager.Listener {
        override fun onStateChanged(state: RemoteSessionManager.SessionState) {
            _state.value = state
            _errorMessage.value = sessionManager.errorMessage
            _connectionMode.value = sessionManager.connectionMode
            if (state == RemoteSessionManager.SessionState.DISCONNECTED ||
                state == RemoteSessionManager.SessionState.IDLE) {
                _tunnel.value = null
                _authRequired.value = false
                _authError.value = null
            } else {
                _tunnel.value = sessionManager.tunnel
            }

            when (state) {
                RemoteSessionManager.SessionState.FAILED -> onSessionFailed()
                RemoteSessionManager.SessionState.CONNECTED -> onSessionConnected()
                else -> Unit
            }
        }

        override fun onVideoFrame(w: Int, h: Int) {
            _videoWidth.value = w
            _videoHeight.value = h
            // 分辨率控制帧到达 = 被控端已开始推流（验证通过），兜底关闭验证框
            // （覆盖被控端为 v1.1.54、不发 auth_ok 的场景）
            _authRequired.value = false
        }

        override fun onPcLockStatus(locked: Boolean) {
            _pcLocked.value = locked
            if (locked) _pcUnlockError.value = null
        }

        override fun onPcUnlockFailed() {
            _pcUnlockError.value = "解锁失败：PC 端需要以管理员身份运行"
        }

        override fun onAuthRequired() {
            _authRequired.value = true
            _authError.value = null
        }

        override fun onAuthFailed() {
            _authError.value = "验证码错误，请重新输入"
        }

        override fun onAuthOk() {
            _authRequired.value = false
            _authError.value = null
        }
    }

    init {
        sessionManager.listener = stateListener
        // 对端剪贴板 → 本机：接收线程组装完成后回调，这里写入系统剪贴板
        sessionManager.onClipboardReceived = { text -> writeLocalClipboard(text) }
    }

    /** 读取本机剪贴板文本（Android 10+ 仅前台可读，返回 null 表示无文本或不可读）。 */
    private fun readLocalClipboardText(): String? = try {
        val app = getApplication<Application>()
        val cm = app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clip = cm.primaryClip
        if (clip == null || clip.itemCount == 0) null
        else clip.getItemAt(0).coerceToText(app)?.toString()
    } catch (_: Exception) {
        null
    }

    /** 把对端推来的文本写入本机剪贴板。 */
    private fun writeLocalClipboard(text: String) {
        try {
            val app = getApplication<Application>()
            val cm = app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("QuickRemote", text))
        } catch (_: Exception) {
            // 剪贴板服务不可用（极少数定制 ROM）时静默忽略，不影响会话
        }
    }

    /**
     * 把本机剪贴板推送到对端。由 UI 在"回到前台 / 会话内面板操作后"调用 ——
     * Android 10+ 不允许后台读剪贴板，所以只能靠这些前台时机触发。
     */
    fun syncClipboard() {
        sessionManager.syncLocalClipboard(readLocalClipboardText())
    }

    /** 发送远程解锁请求（PC 锁屏时输入 Windows 登录密码解锁）。 */
    fun sendUnlock(password: String) {
        sessionManager.sendUnlockRequest(password)
    }

    /** 提交访问验证码（被控端要求验证时）。 */
    fun sendAuthCode(code: String) {
        _authError.value = null
        sessionManager.sendAuthCode(code)
    }

    /** 开始一个截屏远程会话（不需要凭据）。幂等：已在连接中/已连接时直接返回，避免重复建连。 */
    fun startSession(device: Device) {
        val cur = sessionManager.state
        if (cur == RemoteSessionManager.SessionState.CONNECTING ||
            cur == RemoteSessionManager.SessionState.CONNECTED) {
            return
        }
        cancelReconnect()
        _device.value = device
        _state.value = RemoteSessionManager.SessionState.CONNECTING
        _errorMessage.value = ""
        _pcLocked.value = false
        _authRequired.value = false
        _authError.value = null
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                val config = settingsStore.serverConfig.first()
                val appSettings = settingsStore.appSettings.first()
                _qualityPercent.value = appSettings.qualityPercent.coerceIn(20, 100)
                sessionManager.start(
                    device.device_id, device.hostname, device.lan_ip,
                    appSettings.qualityPercent, config
                )
                _state.value = sessionManager.state
                _errorMessage.value = sessionManager.errorMessage
            }
        }
    }

    /** 设置渲染 Surface（由 UI 层在 SurfaceView 创建时调用）。 */
    fun setSurface(surface: Surface?) {
        sessionManager.setSurface(surface)
    }

    /** 发送输入事件（阶段 5 接入触摸捕获后调用）。 */
    fun sendInput(type: Byte, data: ByteArray) {
        sessionManager.sendInput(type, data)
    }

    /** 会话内切换画质档位：立即下发控制帧，并写回设置作为下次默认值。 */
    fun setQuality(percent: Int) {
        val clamped = percent.coerceIn(20, 100)
        _qualityPercent.value = clamped
        sessionManager.applyQualityPercent(clamped)
        viewModelScope.launch {
            val cur = settingsStore.appSettings.first()
            settingsStore.saveAppSettings(cur.copy(qualityPercent = clamped))
        }
    }

    /**
     * 会话失败后的处理：仅对"重试有意义"的原因自动重连。
     *
     * 验证码流程中失败（authRequired / authError 处于激活态）一律不自动重连 ——
     * 否则会变成无限重试并反复弹验证框。
     */
    private fun onSessionFailed() {
        val authPending = _authRequired.value || _authError.value != null
        val reason = sessionManager.lastDisconnectReason
        if (authPending || !ReconnectPolicy.shouldAutoReconnect(reason)) {
            _reconnecting.value = null
            reconnectAttempt = 0
            return
        }

        val delayMs = ReconnectPolicy.delayFor(reconnectAttempt)
        if (delayMs == null) {
            _reconnecting.value = null
            reconnectAttempt = 0
            _errorMessage.value =
                "重连失败（已尝试 ${ReconnectPolicy.MAX_ATTEMPTS} 次），请手动重新连接"
            return
        }

        val attempt = reconnectAttempt
        reconnectAttempt = attempt + 1
        _reconnecting.value = ReconnectState(attempt, System.currentTimeMillis() + delayMs)

        reconnectJob?.cancel()
        reconnectJob = viewModelScope.launch {
            delay(delayMs)
            withContext(Dispatchers.IO) { sessionManager.reconnect() }
            // 结果由 onStateChanged 驱动：成功 → onSessionConnected，失败 → onSessionFailed
        }
    }

    /** 连接成功：清理重连状态并恢复会话内副作用（画质档位）。 */
    private fun onSessionConnected() {
        if (reconnectAttempt == 0 && _reconnecting.value == null) return
        reconnectAttempt = 0
        reconnectJob?.cancel()
        reconnectJob = null
        _reconnecting.value = null
        // 重连后 PC 端按自身默认参数重建编码器，需重新下发画质档位
        sessionManager.applyQualityPercent(_qualityPercent.value)
    }

    /** 重连浮层「立即重试」：跳过等待，立刻发起重连。 */
    fun retryNow() {
        val cur = _reconnecting.value ?: return
        reconnectJob?.cancel()
        _reconnecting.value = ReconnectState(cur.attempt, System.currentTimeMillis())
        reconnectJob = viewModelScope.launch {
            withContext(Dispatchers.IO) { sessionManager.reconnect() }
        }
    }

    /** 重连浮层「取消」：停止自动重连并清除状态。 */
    fun cancelReconnect() {
        reconnectJob?.cancel()
        reconnectJob = null
        reconnectAttempt = 0
        _reconnecting.value = null
    }

    /** 断开当前会话。 */
    fun disconnect() {
        cancelReconnect()
        viewModelScope.launch {
            withContext(Dispatchers.IO) { sessionManager.disconnect() }
            _state.value = sessionManager.state
            _tunnel.value = null
        }
    }

    fun toggleFullscreen() {
        _isFullscreen.value = !_isFullscreen.value
        _isAutoFullscreen.value = false
    }

    /**
     * 直接设置全屏状态（横屏自动全屏用，避免 toggle 在状态竞争时不精确）。
     * @param auto true 表示由方向变化自动触发（竖屏时会自动退出），false 为用户手动
     */
    fun setFullscreen(value: Boolean, auto: Boolean = false) {
        _isFullscreen.value = value
        _isAutoFullscreen.value = value && auto
    }

    /** 横屏自动进入的全屏标记：竖屏时自动退出；用户手动全屏不受影响。 */
    val isAutoFullscreen: StateFlow<Boolean> get() = _isAutoFullscreen

    fun toggleKeyboard() {
        _isKeyboardVisible.value = !_isKeyboardVisible.value
    }

    /** 同步真实 IME 可见状态（用户按系统返回键收起键盘时修正状态，避免按钮高亮失真）。 */
    fun setKeyboardVisible(visible: Boolean) {
        _isKeyboardVisible.value = visible
    }

    override fun onCleared() {
        sessionManager.reset()
        super.onCleared()
    }
}
