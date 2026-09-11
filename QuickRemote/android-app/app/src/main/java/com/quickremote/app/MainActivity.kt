package com.quickremote.app

import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.lifecycleScope
import com.quickremote.app.data.PairingPayload
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.ui.navigation.NavGraph
import com.quickremote.app.ui.theme.QuickRemoteTheme
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    companion object {
        /**
         * 配对结果提示文案。放在 companion 是因为配对成功后要 recreate()
         * 让起始路由重新评估，普通 remember 状态活不过重建。
         */
        var pendingPairMessage: String? = null
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        handlePairIntent(intent)
        setContent {
            QuickRemoteTheme {
                AppRoot()
            }
        }
    }

    /** singleTop 下 App 已在后台时扫码不会重建 Activity，走这里。 */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handlePairIntent(intent)
    }

    /**
     * 处理 quickremote://pair 深链：解析载荷 → 写入服务器地址与密钥 → 重建界面。
     * 解析失败同样给出明确提示，不静默失败。
     */
    private fun handlePairIntent(intent: Intent?) {
        val data = intent?.data?.toString() ?: return
        if (!data.startsWith("${PairingPayload.SCHEME}://${PairingPayload.HOST}")) return

        val store = SettingsStore(applicationContext)
        lifecycleScope.launch {
            PairingPayload.parse(data)
                .onSuccess { info ->
                    store.saveServerConfig(
                        ServerConfig(address = info.addr, preSharedKey = info.psk)
                    )
                    // 地址/密钥变了，旧认证令牌作废
                    store.clearToken()
                    pendingPairMessage =
                        if (info.name.isBlank()) "已导入配置" else "已导入配置：${info.name}"
                    recreate()
                }
                .onFailure { e ->
                    pendingPairMessage = "配对失败：${e.message ?: "载荷无效"}"
                    recreate()
                }
        }
    }
}

@Composable
private fun AppRoot() {
    val context = LocalContext.current
    val settingsStore = remember { SettingsStore(context) }

    // 配对提示在重建后展示一次即清除
    val pairMessage = MainActivity.pendingPairMessage
    LaunchedEffect(pairMessage) {
        if (pairMessage != null) {
            MainActivity.pendingPairMessage = null
            Toast.makeText(context, pairMessage, Toast.LENGTH_LONG).show()
        }
    }

    // 等待 DataStore 的首次发射，确定起始页面是否已配置。
    // 避免用空 initial 误判"未配置"。
    val initialConfig by produceState<ServerConfig?>(initialValue = null, settingsStore) {
        value = settingsStore.serverConfig.first()
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background),
        contentAlignment = Alignment.Center
    ) {
        val config = initialConfig
        if (config == null) {
            // 首次加载中
            CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
        } else {
            val startConfigured = config.address.isNotBlank() && config.preSharedKey.isNotBlank()
            NavGraph(settingsStore = settingsStore, startConfigured = startConfigured)
        }
    }
}
