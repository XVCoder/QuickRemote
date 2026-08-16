package com.quickremote.app.viewmodels

import android.app.Application
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import com.quickremote.app.data.local.SettingsStore

/**
 * 为 SessionViewModel 注入 Application 与 SettingsStore 的工厂。
 */
class SessionViewModelFactory(
    private val app: Application,
    private val settingsStore: SettingsStore
) : ViewModelProvider.Factory {
    @Suppress("UNCHECKED_CAST")
    override fun <T : ViewModel> create(modelClass: Class<T>): T {
        if (modelClass.isAssignableFrom(SessionViewModel::class.java)) {
            return SessionViewModel(app, settingsStore) as T
        }
        throw IllegalArgumentException("Unknown ViewModel class: ${modelClass.name}")
    }
}