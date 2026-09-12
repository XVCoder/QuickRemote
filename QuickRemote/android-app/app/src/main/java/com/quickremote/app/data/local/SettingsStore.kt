package com.quickremote.app.data.local

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.data.models.snapToQualityPreset
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

// 顶层 DataStore 实例
private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "quickremote_settings")

/**
 * 使用 DataStore 存储服务器配置和应用设置。
 * 替代 SharedPreferences，提供响应式 Flow 访问。
 */
class SettingsStore(private val context: Context) {

    private object ServerKeys {
        val ADDRESS = stringPreferencesKey("server_address")
        val PRE_SHARED_KEY = stringPreferencesKey("pre_shared_key")
        val TOKEN = stringPreferencesKey("auth_token")
    }

    private object SettingsKeys {
        val AUTO_UPDATE = booleanPreferencesKey("auto_update")
        val MANIFEST_URL = stringPreferencesKey("manifest_url")
        val QUALITY_PERCENT = intPreferencesKey("quality_percent")
        val BLANK_TOUCHPAD = booleanPreferencesKey("blank_touchpad")
        val TOUCHPAD_SPEED = intPreferencesKey("touchpad_speed")
        val TOUCHPAD_DOUBLE_TAP_DRAG = booleanPreferencesKey("touchpad_double_tap_drag")
        val TOUCHPAD_THREE_FINGER = booleanPreferencesKey("touchpad_three_finger")
        val TOUCHPAD_FOUR_FINGER = booleanPreferencesKey("touchpad_four_finger")
    }

    private object DeviceKeys {
        /** 设备备注表（JSON：device_id → 备注文本）。仅本机可见，不上传服务器。 */
        val REMARKS = stringPreferencesKey("device_remarks")

        /** 软删除（本机隐藏）的设备 ID 集合；设备再次上线时移除。 */
        val HIDDEN = stringSetPreferencesKey("hidden_devices")
    }

    val serverConfig: Flow<ServerConfig> = context.dataStore.data.map { prefs ->
        ServerConfig(
            address = prefs[ServerKeys.ADDRESS] ?: "",
            preSharedKey = prefs[ServerKeys.PRE_SHARED_KEY] ?: ""
        )
    }

    val token: Flow<String> = context.dataStore.data.map { prefs ->
        prefs[ServerKeys.TOKEN] ?: ""
    }

    val appSettings: Flow<AppSettings> = context.dataStore.data.map { prefs ->
        AppSettings(
            autoUpdate = prefs[SettingsKeys.AUTO_UPDATE] ?: true,
            // 旧版滑块可存 20-100 任意值，读取时吸附到最近档位（40/60/80/100）
            qualityPercent = snapToQualityPreset(prefs[SettingsKeys.QUALITY_PERCENT] ?: 80),
            blankTouchpad = prefs[SettingsKeys.BLANK_TOUCHPAD] ?: true,
            touchpadSpeed = (prefs[SettingsKeys.TOUCHPAD_SPEED] ?: 100).coerceIn(50, 300),
            touchpadDoubleTapDrag = prefs[SettingsKeys.TOUCHPAD_DOUBLE_TAP_DRAG] ?: true,
            touchpadThreeFinger = prefs[SettingsKeys.TOUCHPAD_THREE_FINGER] ?: true,
            touchpadFourFinger = prefs[SettingsKeys.TOUCHPAD_FOUR_FINGER] ?: true
        )
    }

    val manifestUrl: Flow<String> = context.dataStore.data.map { prefs ->
        prefs[SettingsKeys.MANIFEST_URL] ?: ""
    }

    /** 设备备注表（仅本机可见）。 */
    val deviceRemarks: Flow<Map<String, String>> = context.dataStore.data.map { prefs ->
        decodeRemarks(prefs[DeviceKeys.REMARKS].orEmpty())
    }

    /** 被本机软删除的设备 ID 集合。 */
    val hiddenDevices: Flow<Set<String>> = context.dataStore.data.map { prefs ->
        prefs[DeviceKeys.HIDDEN].orEmpty()
    }

    suspend fun saveServerConfig(config: ServerConfig) {
        context.dataStore.edit { prefs ->
            prefs[ServerKeys.ADDRESS] = config.address
            prefs[ServerKeys.PRE_SHARED_KEY] = config.preSharedKey
        }
    }

    suspend fun saveToken(token: String) {
        context.dataStore.edit { prefs ->
            prefs[ServerKeys.TOKEN] = token
        }
    }

    suspend fun clearToken() {
        context.dataStore.edit { prefs ->
            prefs.remove(ServerKeys.TOKEN)
        }
    }

    /** 重置预共享密钥：清除已保存密钥与认证令牌。 */
    suspend fun clearPreSharedKey() {
        context.dataStore.edit { prefs ->
            prefs.remove(ServerKeys.PRE_SHARED_KEY)
            prefs.remove(ServerKeys.TOKEN)
        }
    }

    suspend fun saveAppSettings(settings: AppSettings) {
        context.dataStore.edit { prefs ->
            prefs[SettingsKeys.AUTO_UPDATE] = settings.autoUpdate
            prefs[SettingsKeys.QUALITY_PERCENT] = snapToQualityPreset(settings.qualityPercent)
            prefs[SettingsKeys.BLANK_TOUCHPAD] = settings.blankTouchpad
            prefs[SettingsKeys.TOUCHPAD_SPEED] = settings.touchpadSpeed.coerceIn(50, 300)
            prefs[SettingsKeys.TOUCHPAD_DOUBLE_TAP_DRAG] = settings.touchpadDoubleTapDrag
            prefs[SettingsKeys.TOUCHPAD_THREE_FINGER] = settings.touchpadThreeFinger
            prefs[SettingsKeys.TOUCHPAD_FOUR_FINGER] = settings.touchpadFourFinger
        }
    }

    suspend fun saveManifestUrl(url: String) {
        context.dataStore.edit { prefs ->
            prefs[SettingsKeys.MANIFEST_URL] = url
        }
    }

    /** 设置/清除设备备注：规范化后为空则删除该条（「清空即删除备注」语义）。 */
    suspend fun setDeviceRemark(deviceId: String, remark: String) {
        if (deviceId.isBlank()) return
        context.dataStore.edit { prefs ->
            val map = decodeRemarks(prefs[DeviceKeys.REMARKS].orEmpty()).toMutableMap()
            val normalized = normalizeRemark(remark)
            if (normalized.isEmpty()) map.remove(deviceId) else map[deviceId] = normalized
            prefs[DeviceKeys.REMARKS] = encodeRemarks(map)
        }
    }

    /** 软删除：把设备加入本机隐藏集合（不动服务端记录，不影响其它客户端）。 */
    suspend fun hideDevices(deviceIds: Set<String>) {
        val ids = deviceIds.filter { it.isNotBlank() }.toSet()
        if (ids.isEmpty()) return
        context.dataStore.edit { prefs ->
            prefs[DeviceKeys.HIDDEN] = prefs[DeviceKeys.HIDDEN].orEmpty() + ids
        }
    }

    /**
     * 解除隐藏（设备重新上线时调用）。
     *
     * 集合清空时必须用 remove 把整个键删掉：DataStore 的 Preferences 写入空集合语义不明，
     * 留着空集合会让「是否还有隐藏设备」的判断变得不可靠。
     */
    suspend fun restoreDevices(deviceIds: Set<String>) {
        val ids = deviceIds.filter { it.isNotBlank() }.toSet()
        if (ids.isEmpty()) return
        context.dataStore.edit { prefs ->
            val left = prefs[DeviceKeys.HIDDEN].orEmpty() - ids
            if (left.isEmpty()) prefs.remove(DeviceKeys.HIDDEN) else prefs[DeviceKeys.HIDDEN] = left
        }
    }
}
