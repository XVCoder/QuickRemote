package com.quickremote.app

import android.app.Application
import com.quickremote.app.services.Logger

/**
 * 应用入口。初始化全局日志目录。
 */
class QuickRemoteApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Logger.init(this)
    }
}
