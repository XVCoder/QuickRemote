package com.quickremote.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.produceState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.material3.CircularProgressIndicator
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.ui.navigation.NavGraph
import com.quickremote.app.ui.theme.QuickRemoteTheme
import kotlinx.coroutines.flow.first

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            QuickRemoteTheme {
                AppRoot()
            }
        }
    }
}

@Composable
private fun AppRoot() {
    val context = LocalContext.current
    val settingsStore = remember { SettingsStore(context) }

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
