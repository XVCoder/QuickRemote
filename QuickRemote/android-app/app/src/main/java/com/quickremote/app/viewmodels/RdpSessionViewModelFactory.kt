package com.quickremote.app.viewmodels

import android.app.Application
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.services.LanDiscovery

/**
 * 为 RdpSessionViewModel 注入 Application、SettingsStore 与可选的内网设备。
 * lanDevice 非空时走内网直连（FreeRDP 直连局域网 IP），为空走远程隧道。
 */
class RdpSessionViewModelFactory(
    private val app: Application,
    private val settingsStore: SettingsStore,
    private val lanDevice: LanDiscovery.LanDevice? = null
) : ViewModelProvider.Factory {
    @Suppress("UNCHECKED_CAST")
    override fun <T : ViewModel> create(modelClass: Class<T>): T {
        if (modelClass.isAssignableFrom(RdpSessionViewModel::class.java)) {
            return RdpSessionViewModel(app, settingsStore, lanDevice) as T
        }
        throw IllegalArgumentException("Unknown ViewModel class: ${modelClass.name}")
    }
}
