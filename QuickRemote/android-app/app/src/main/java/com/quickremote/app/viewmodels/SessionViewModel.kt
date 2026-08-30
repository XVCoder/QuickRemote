package com.quickremote.app.viewmodels

import android.app.Application
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.Logger
import com.quickremote.app.services.RemoteSessionManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
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

    private val _isKeyboardVisible = MutableStateFlow(false)
    val isKeyboardVisible: StateFlow<Boolean> = _isKeyboardVisible.asStateFlow()

    /** 远程画面分辨率（收到控制帧后更新）。 */
    private val _videoWidth = MutableStateFlow(0)
    val videoWidth: StateFlow<Int> = _videoWidth.asStateFlow()

    private val _videoHeight = MutableStateFlow(0)
    val videoHeight: StateFlow<Int> = _videoHeight.asStateFlow()

    /** 会话状态变更监听器。 */
    private val stateListener = object : RemoteSessionManager.Listener {
        override fun onStateChanged(state: RemoteSessionManager.SessionState) {
            _state.value = state
            _errorMessage.value = sessionManager.errorMessage
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
    }

    init {
        sessionManager.listener = stateListener
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
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                val config = settingsStore.serverConfig.first()
                sessionManager.start(device.device_id, device.hostname, config)
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
    }

    fun toggleKeyboard() {
        _isKeyboardVisible.value = !_isKeyboardVisible.value
    }

    override fun onCleared() {
        sessionManager.reset()
        super.onCleared()
    }
}
