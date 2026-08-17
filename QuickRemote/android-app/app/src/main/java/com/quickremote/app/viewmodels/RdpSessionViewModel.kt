package com.quickremote.app.viewmodels

import android.app.Application
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.Credentials
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.Logger
import com.quickremote.app.services.RdpSessionManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 管理 RDP 会话状态。封装 RdpSessionManager 并暴露响应式状态。
 */
class RdpSessionViewModel(
    app: Application,
    private val settingsStore: SettingsStore,
    private val lanDevice: com.quickremote.app.services.LanDiscovery.LanDevice? = null,
    private val sessionManager: RdpSessionManager = RdpSessionManager(app, logger = Logger())
) : AndroidViewModel(app) {

    val state: StateFlow<RdpSessionManager.SessionState> get() = _state
    private val _state = MutableStateFlow(RdpSessionManager.SessionState.IDLE)

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

    private val _savedCredentials = MutableStateFlow<Credentials?>(null)
    val savedCredentials: StateFlow<Credentials?> = _savedCredentials.asStateFlow()

    /** FreeRDP 是否可用。 */
    val isFreeRdpAvailable: Boolean get() = sessionManager.isFreeRdpAvailable

    /** 获取 FreeRDP 客户端（供 RdpSurfaceView 使用）。 */
    fun getFreeRdpClient(): com.quickremote.app.freerdp.FreeRdpClient = sessionManager.freeRdpClient

    /** 会话状态变更监听器。 */
    private val stateListener = object : RdpSessionManager.Listener {
        override fun onStateChanged(state: RdpSessionManager.SessionState) {
            _state.value = state
            _errorMessage.value = sessionManager.errorMessage
            if (state == RdpSessionManager.SessionState.DISCONNECTED ||
                state == RdpSessionManager.SessionState.IDLE) {
                _tunnel.value = null
            } else {
                _tunnel.value = sessionManager.tunnel
            }
        }

        override fun onGraphicsUpdated(x: Int, y: Int, width: Int, height: Int) {
            // 图形更新由 SurfaceView 自动渲染，无需 UI 层处理
        }
    }

    init {
        sessionManager.listener = stateListener
    }

    /** 开始一个 RDP 会话：内网设备直连局域网 IP，远程设备走隧道。 */
    fun startSession(
        device: Device,
        username: String = "",
        password: String = "",
        domain: String = ""
    ) {
        _device.value = device
        _state.value = RdpSessionManager.SessionState.CONNECTING
        _errorMessage.value = ""
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                val lan = lanDevice
                if (lan != null) {
                    // 内网直连：FreeRDP 直接连接局域网 IP（不经中继隧道）
                    startLanDirect(lan, username, password, domain)
                } else {
                    val config = settingsStore.serverConfig.first()
                    sessionManager.start(
                        device.device_id, device.hostname, config,
                        username, password, domain
                    )
                }
                _state.value = sessionManager.state
                _errorMessage.value = sessionManager.errorMessage
            }
        }
    }

    /** 内网直连：FreeRDP 直接连接局域网主机的 RDP 端口。 */
    private fun startLanDirect(
        lan: com.quickremote.app.services.LanDiscovery.LanDevice,
        username: String,
        password: String,
        domain: String
    ) {
        sessionManager.startLanDirect(
            host = lan.ip,
            port = lan.rdpPort,
            username = username,
            password = password,
            domain = domain
        )
    }

    /** 加载指定设备保存的凭据，供凭据表单预填。 */
    fun loadCredentials(deviceId: String) {
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                _savedCredentials.value = settingsStore.getCredentials(deviceId).first()
            }
        }
    }

    /** 保存或清除指定设备的凭据。
     *
     * @param save true 保存，false 清除已保存凭据。
     */
    fun saveCredentials(
        deviceId: String,
        username: String,
        password: String,
        domain: String,
        save: Boolean
    ) {
        viewModelScope.launch {
            withContext(Dispatchers.IO) {
                settingsStore.saveCredentials(
                    deviceId, Credentials(username, password, domain), save
                )
                _savedCredentials.value = if (save) Credentials(username, password, domain) else null
            }
        }
    }

    /** 设置渲染 Surface（由 UI 层在 SurfaceView 创建时调用）。 */
    fun setSurface(surface: Surface?) {
        sessionManager.setSurface(surface)
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
