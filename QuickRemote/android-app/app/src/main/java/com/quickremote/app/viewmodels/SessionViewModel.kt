package com.quickremote.app.viewmodels

import android.app.Application
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.Logger
import com.quickremote.app.services.RemoteSessionManager
import kotlinx.coroutines.Dispatchers
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

    /** PC 端锁屏状态（锁屏时连接保持、画面暂停，等待解锁恢复）。 */
    private val _pcLocked = MutableStateFlow(false)
    val pcLocked: StateFlow<Boolean> = _pcLocked.asStateFlow()

    /** 远程解锁失败原因（null 表示无错误）。 */
    private val _pcUnlockError = MutableStateFlow<String?>(null)
    val pcUnlockError: StateFlow<String?> = _pcUnlockError.asStateFlow()

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
            } else {
                _tunnel.value = sessionManager.tunnel
            }
        }

        override fun onVideoFrame(w: Int, h: Int) {
            _videoWidth.value = w
            _videoHeight.value = h
        }

        override fun onPcLockStatus(locked: Boolean) {
            _pcLocked.value = locked
            if (locked) _pcUnlockError.value = null
        }

        override fun onPcUnlockFailed() {
            _pcUnlockError.value = "解锁失败：PC 端需要以管理员身份运行"
        }
    }

    init {
        sessionManager.listener = stateListener
    }

    /** 发送远程解锁请求（PC 锁屏时输入 Windows 登录密码解锁）。 */
    fun sendUnlock(password: String) {
        sessionManager.sendUnlockRequest(password)
    }

    /** 开始一个截屏远程会话（不需要凭据）。幂等：已在连接中/已连接时直接返回，避免重复建连。 */
    fun startSession(device: Device) {
        val cur = sessionManager.state
        if (cur == RemoteSessionManager.SessionState.CONNECTING ||
            cur == RemoteSessionManager.SessionState.CONNECTED) {
            return
        }
        _device.value = device
        _state.value = RemoteSessionManager.SessionState.CONNECTING
        _errorMessage.value = ""
        _pcLocked.value = false
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                val config = settingsStore.serverConfig.first()
                val appSettings = settingsStore.appSettings.first()
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

    /** 断开当前会话。 */
    fun disconnect() {
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
