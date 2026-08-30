package com.quickremote.app.viewmodels

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.Device
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.services.Logger
import com.quickremote.app.services.RelayConnection
import com.quickremote.app.services.UpdateChecker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

enum class ConnectionState { DISCONNECTED, CONNECTING, CONNECTED, ERROR }

/** 检测到的新版本信息（用于弹窗展示更新内容并确认下载）。 */
data class UpdateInfo(
    val latestVersion: String,
    val changelog: String,
    val downloadUrl: String
)

/**
 * 全局状态管理：服务器配置、设备列表、连接状态、设置。
 */
class MainViewModel(
    private val settingsStore: SettingsStore,
    private val relay: RelayConnection = RelayConnection(),
    private val logger: Logger = Logger(),
    private val updateChecker: UpdateChecker = UpdateChecker(logger = logger)
) : ViewModel() {

    private val _serverConfig = MutableStateFlow(ServerConfig())
    val serverConfig: StateFlow<ServerConfig> = _serverConfig.asStateFlow()

    private val _devices = MutableStateFlow<List<Device>>(emptyList())
    val devices: StateFlow<List<Device>> = _devices.asStateFlow()

    private val _connectionState = MutableStateFlow(ConnectionState.DISCONNECTED)
    val connectionState: StateFlow<ConnectionState> = _connectionState.asStateFlow()

    private val _isRefreshing = MutableStateFlow(false)
    val isRefreshing: StateFlow<Boolean> = _isRefreshing.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String??> = _toast.asStateFlow()

    /** 最近一次错误详情（用于 UI 显示具体原因，区别于 toast 的简短提示）。 */
    private val _lastError = MutableStateFlow<String>("")
    val lastError: StateFlow<String> = _lastError.asStateFlow()

    private val _appSettings = MutableStateFlow(AppSettings())
    val appSettings: StateFlow<AppSettings> = _appSettings.asStateFlow()

    private val _isConfigured = MutableStateFlow(false)
    val isConfigured: StateFlow<Boolean> = _isConfigured.asStateFlow()

    val updateCheckerRef: UpdateChecker get() = updateChecker

    /** 检测到的新版本信息；非空时设置页应弹出「发现新版本」对话框。 */
    private val _updateInfo = MutableStateFlow<UpdateInfo?>(null)
    val updateInfo: StateFlow<UpdateInfo?> = _updateInfo.asStateFlow()

    init {
        // 加载已保存的服务器配置
        viewModelScope.launch {
            settingsStore.serverConfig.collect { config ->
                _serverConfig.value = config
                _isConfigured.value = config.address.isNotBlank() && config.preSharedKey.isNotBlank()
            }
        }
        viewModelScope.launch {
            settingsStore.appSettings.collect { _appSettings.value = it }
        }
    }

    fun consumeToast() { _toast.value = null }

    /** 测试连接（不修改已缓存状态）。 */
    fun testConnection(config: ServerConfig) {
        viewModelScope.launch {
            _connectionState.value = ConnectionState.CONNECTING
            _lastError.value = ""
            val ok = withContext(Dispatchers.IO) { relay.testConnection(config) }
            _connectionState.value = if (ok) ConnectionState.CONNECTED else ConnectionState.ERROR
            if (ok) {
                _toast.value = "连接成功"
            } else {
                _lastError.value = relay.lastError
                _toast.value = "连接失败"
            }
        }
    }

    /** 保存服务器配置并继续：保存 + 认证 + 拉取设备。 */
    fun saveAndContinue(config: ServerConfig) {
        viewModelScope.launch {
            settingsStore.saveServerConfig(config)
            _serverConfig.value = config
            _isConfigured.value = true
            authenticateAndLoadDevices(config)
        }
    }

    /** 刷新设备列表。 */
    fun refreshDevices() {
        viewModelScope.launch {
            if (_serverConfig.value.address.isBlank()) return@launch
            loadDevices(_serverConfig.value)
        }
    }

    private fun authenticateAndLoadDevices(config: ServerConfig) {
        viewModelScope.launch {
            _connectionState.value = ConnectionState.CONNECTING
            loadDevices(config)
        }
    }

    private suspend fun loadDevices(config: ServerConfig) {
        _isRefreshing.value = true
        try {
            val ok = withContext(Dispatchers.IO) { relay.authenticate(config) }
            if (!ok) {
                _connectionState.value = ConnectionState.ERROR
                _devices.value = emptyList()
                _lastError.value = relay.lastError
                _toast.value = "认证失败"
                return
            }
            _connectionState.value = ConnectionState.CONNECTED
            _lastError.value = ""
            val list = withContext(Dispatchers.IO) { relay.getDevices() }
            _devices.value = list
            if (list.isEmpty()) _toast.value = "暂无在线设备"
        } catch (e: Exception) {
            _connectionState.value = ConnectionState.ERROR
            _devices.value = emptyList()
            _lastError.value = e.message ?: "未知错误"
            _toast.value = "加载失败"
            logger.warn("loadDevices failed: ${e.message}")
        } finally {
            _isRefreshing.value = false
        }
    }

    /** 上传日志。 */
    fun uploadLogs(deviceId: String) {
        viewModelScope.launch {
            // 未认证时先自动认证（使用已保存的服务器配置）
            if (relay.token.isEmpty()) {
                val ok = withContext(Dispatchers.IO) { relay.authenticate(_serverConfig.value) }
                if (!ok) {
                    _lastError.value = relay.lastError
                    _toast.value = "未认证，无法上传日志"
                    return@launch
                }
            }
            val ok = withContext(Dispatchers.IO) {
                relay.uploadLogs(deviceId, logger.toBase64(), "info")
            }
            if (ok) {
                _toast.value = "日志已上传"
            } else {
                _lastError.value = relay.lastError
                _toast.value = "日志上传失败"
            }
        }
    }

    /** 读取本地日志内容（用于「查看日志」弹窗）。 */
    fun readLogContent(): String = logger.readAll()

    /** 清空本地日志文件。 */
    fun clearLogs() {
        logger.clearAll()
        _toast.value = "日志已清空"
    }

    /**
     * 重置预共享密钥：清除已保存的密钥与认证令牌，断开设备列表。
     * 用户需在设置页输入新密钥并保存后重新连接。
     */
    fun resetPreSharedKey() {
        viewModelScope.launch {
            settingsStore.clearPreSharedKey()
            _serverConfig.value = _serverConfig.value.copy(preSharedKey = "")
            _devices.value = emptyList()
            _connectionState.value = ConnectionState.DISCONNECTED
            _lastError.value = ""
            _toast.value = "预共享密钥已重置"
        }
    }

    fun updateSettings(settings: AppSettings) {
        viewModelScope.launch {
            settingsStore.saveAppSettings(settings)
            _appSettings.value = settings
            _toast.value = "设置已保存"
        }
    }

    /** 检查更新（manifest URL 硬编码在代码中，无需用户配置）。 */
    fun checkUpdate() {
        viewModelScope.launch {
            updateChecker.check(MANIFEST_URL)
            if (updateChecker.isUpdateAvailable) {
                // 弹窗展示更新内容，由用户点击「立即更新」确认下载
                _updateInfo.value = UpdateInfo(
                    latestVersion = updateChecker.latestVersion,
                    changelog = updateChecker.changelog,
                    downloadUrl = updateChecker.downloadUrl
                )
            } else if (updateChecker.latestVersion.isEmpty()) {
                _toast.value = "检查更新失败"
                _lastError.value = "无法获取版本信息，请检查网络或 manifest 地址是否可访问"
            } else {
                _toast.value = "已是最新版本（v${updateChecker.latestVersion}）"
            }
        }
    }

    /** 关闭「发现新版本」弹窗（暂时不更新）。 */
    fun dismissUpdate() {
        _updateInfo.value = null
    }

    /** 获取最新版本的更新记录（changelog 文本）。 */
    fun checkChangelog() {
        viewModelScope.launch {
            updateChecker.check(MANIFEST_URL)
            val changelog = updateChecker.changelog
            if (changelog.isBlank()) {
                _toast.value = "获取更新记录失败"
                _lastError.value = "无法获取更新记录，请检查网络或 manifest 地址是否可访问"
            } else {
                _toast.value = "更新记录：$changelog"
            }
        }
    }

    companion object {
        /** qdrl manifest 地址（硬编码，用户无需也不应修改）。 */
        const val MANIFEST_URL = "https://qd.solutionx.top/d/p/609d4fcb-7415-4d70-96d1-18f2201631b6"
    }
}
