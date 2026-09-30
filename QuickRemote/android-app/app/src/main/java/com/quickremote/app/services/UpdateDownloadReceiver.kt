package com.quickremote.app.services

import android.app.DownloadManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.quickremote.app.R

/**
 * 更新包下载广播（v1.0.86）。
 *
 * 两个来源：
 *  1. 系统 `DownloadManager.ACTION_DOWNLOAD_COMPLETE` —— 后台下载结束（成功/失败）。
 *     成功即校验 APK 并发「已下载，点击安装」通知；失败仅记日志（下次打开设置页会看到状态）。
 *  2. 本通知的「点击安装」动作 [ACTION_INSTALL] —— 校验后唤起系统安装器。
 *
 * ⚠️ 只能**动态注册**：Android 8（API 26）起隐式广播禁止静态注册，而
 * `ACTION_DOWNLOAD_COMPLETE` 不在豁免清单里，写在 Manifest 里收不到。
 * 故由 `QuickRemoteApp.onCreate` 在进程生命周期内注册。
 */
class UpdateDownloadReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            DownloadManager.ACTION_DOWNLOAD_COMPLETE -> {
                val id = intent.getLongExtra(DownloadManager.EXTRA_DOWNLOAD_ID, -1L)
                if (id <= 0) return
                val saved = UpdateDownloader.savedDownload(context)
                if (saved == null || saved.first != id) return // 不是本次更新任务
                val version = saved.second
                val progress = UpdateDownloader.query(context, id)
                if (progress?.status != DownloadManager.STATUS_SUCCESSFUL) {
                    Logger().warn("Update download not successful: status=${progress?.status} reason=${progress?.reason}")
                    return
                }
                val file = try {
                    UpdateDownloader.validate(context, version)
                } catch (e: Exception) {
                    Logger().warn("Update apk validation failed: ${e.message}")
                    UpdateDownloader.clear(context)
                    return
                }
                Logger().info("Update apk ready: ${file.absolutePath} (${file.length()}B)")
                notifyReady(context, version)
            }
            ACTION_INSTALL -> {
                val version = intent.getStringExtra(EXTRA_VERSION)
                    ?: UpdateDownloader.savedDownload(context)?.second
                    ?: return
                val file = try {
                    UpdateDownloader.validate(context, version)
                } catch (e: Exception) {
                    Logger().warn("Install aborted, apk invalid: ${e.message}")
                    return
                }
                cancel(context)
                UpdateDownloader.install(context, file)
            }
        }
    }

    /** 「已下载完成，点击安装」通知：后台下载完成后用户回设备的入口。 */
    private fun notifyReady(context: Context, version: String) {
        createChannel(context)
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            NotificationManagerCompat.from(context).areNotificationsEnabled()
        ) {
            val pi = PendingIntent.getBroadcast(
                context,
                REQ_INSTALL,
                Intent(context, UpdateDownloadReceiver::class.java)
                    .setAction(ACTION_INSTALL)
                    .putExtra(EXTRA_VERSION, version)
                    .setPackage(context.packageName),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val notification = NotificationCompat.Builder(context, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_session_notify)
                .setContentTitle(context.getString(R.string.notify_update_ready_title))
                .setContentText(context.getString(R.string.notify_update_ready_text, version))
                .setContentIntent(pi)
                .setAutoCancel(true)
                .setSilent(true)
                .setPriority(NotificationCompat.PRIORITY_DEFAULT)
                .build()
            NotificationManagerCompat.from(context).notify(NOTIFICATION_ID, notification)
        }
    }

    private fun cancel(context: Context) {
        NotificationManagerCompat.from(context).cancel(NOTIFICATION_ID)
    }

    private fun createChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        val channel = NotificationChannel(
            CHANNEL_ID,
            context.getString(R.string.notify_channel_update_name),
            NotificationManager.IMPORTANCE_DEFAULT
        ).apply {
            description = context.getString(R.string.notify_channel_update_desc)
        }
        manager.createNotificationChannel(channel)
    }

    companion object {
        const val ACTION_INSTALL = "com.quickremote.app.action.INSTALL_UPDATE"
        const val EXTRA_VERSION = "version"
        private const val CHANNEL_ID = "qr_update"
        private const val NOTIFICATION_ID = 1002
        private const val REQ_INSTALL = 2001
    }
}
