package com.quickremote.app

import android.app.Application
import android.app.DownloadManager
import android.content.Context
import android.content.IntentFilter
import android.os.Build
import com.quickremote.app.services.Logger
import com.quickremote.app.services.UpdateDownloadReceiver

/**
 * 应用入口。初始化全局日志目录，并注册更新下载广播（进程生命周期内有效）。
 */
class QuickRemoteApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Logger.init(this)
        registerUpdateReceiver()
    }

    /**
     * 注册 DownloadManager 完成广播 —— 只能动态注册（Android 8+ 禁止静态注册隐式广播）。
     * 作用：后台下载结束后校验 APK 并弹出「点击安装」通知。
     */
    private fun registerUpdateReceiver() {
        try {
            val filter = IntentFilter(DownloadManager.ACTION_DOWNLOAD_COMPLETE).apply {
                addAction(UpdateDownloadReceiver.ACTION_INSTALL)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                registerReceiver(UpdateDownloadReceiver(), filter, Context.RECEIVER_NOT_EXPORTED)
            } else {
                registerReceiver(UpdateDownloadReceiver(), filter)
            }
        } catch (e: Exception) {
            Logger().warn("Register update receiver failed: ${e.message}")
        }
    }
}
