package com.quickremote.app.data.local

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.quickremote.app.data.models.AppSettings
import com.quickremote.app.data.models.ResolutionMode
import com.quickremote.app.data.models.ServerConfig
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
        val RESOLUTION_MODE = stringPreferencesKey("resolution_mode")
        val CUSTOM_WIDTH = intPreferencesKey("custom_width")
        val CUSTOM_HEIGHT = intPreferencesKey("custom_height")
        val COLOR_DEPTH = intPreferencesKey("color_depth")
        val AUDIO_REDIRECT = booleanPreferencesKey("audio_redirect")
        val AUTO_UPDATE = booleanPreferencesKey("auto_update")
        val MANIFEST_URL = stringPreferencesKey("manifest_url")
        val QUALITY_PERCENT = intPreferencesKey("quality_percent")
        val BLANK_TOUCHPAD = booleanPreferencesKey("blank_touchpad")
        val TOUCHPAD_SPEED = intPreferencesKey("touchpad_speed")
        val TOUCHPAD_DOUBLE_TAP_DRAG = booleanPreferencesKey("touchpad_double_tap_drag")
        val TOUCHPAD_THREE_FINGER = booleanPreferencesKey("touchpad_three_finger")
        val TOUCHPAD_FOUR_FINGER = booleanPreferencesKey("touchpad_four_finger")
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
            resolutionMode = runCatching {
                ResolutionMode.valueOf(prefs[SettingsKeys.RESOLUTION_MODE] ?: ResolutionMode.AUTO.name)
            }.getOrDefault(ResolutionMode.AUTO),
            customWidth = prefs[SettingsKeys.CUSTOM_WIDTH] ?: 1920,
            customHeight = prefs[SettingsKeys.CUSTOM_HEIGHT] ?: 1080,
            colorDepth = prefs[SettingsKeys.COLOR_DEPTH] ?: 32,
            audioRedirect = prefs[SettingsKeys.AUDIO_REDIRECT] ?: false,
            autoUpdate = prefs[SettingsKeys.AUTO_UPDATE] ?: true,
            qualityPercent = prefs[SettingsKeys.QUALITY_PERCENT] ?: 80,
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
            prefs[SettingsKeys.RESOLUTION_MODE] = settings.resolutionMode.name
            prefs[SettingsKeys.CUSTOM_WIDTH] = settings.customWidth
            prefs[SettingsKeys.CUSTOM_HEIGHT] = settings.customHeight
            prefs[SettingsKeys.COLOR_DEPTH] = settings.colorDepth
            prefs[SettingsKeys.AUDIO_REDIRECT] = settings.audioRedirect
            prefs[SettingsKeys.AUTO_UPDATE] = settings.autoUpdate
            prefs[SettingsKeys.QUALITY_PERCENT] = settings.qualityPercent
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
}
