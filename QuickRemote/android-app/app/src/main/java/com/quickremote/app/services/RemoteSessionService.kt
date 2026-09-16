package com.quickremote.app.services

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.quickremote.app.MainActivity
import com.quickremote.app.R

/**
 * 会话前台服务 —— 「切到其他应用不断开」的**唯一可靠手段**。
 *
 * ## 为什么非它不可（2026-09-16 查证，AOSP 官方文档）
 *
 * 自 Android 11（API 30）起系统有「缓存应用冻结器」（cached apps freezer）：
 * 应用进入 cached 状态后会被迁进冻结 cgroup，**所有线程挂起**。
 * 而 Android 14（API 34）起这条规则变得致命：
 *
 * 1. 应用进入 cached 状态 **10 秒后**就被冻结（Android 13 及以前是 10 分钟）；
 * 2. **若某应用的所有进程都被冻结，系统会终止该应用持有的全部 TCP socket**
 *    （原因是防止服务端 TCP keepalive 把设备 modem 唤醒）；
 * 3. 冻结豁免清单里**没有 wakelock**——只有「持有阻塞他人的文件锁」和
 *    `BIND_WAIVE_PRIORITY` 绑定两种，官方明确说明豁免是「实现细节」。
 *
 * 也就是说：只靠 [SessionKeepAlive] 的 PARTIAL_WAKE_LOCK 时，用户一按 Home / 切到微信，
 * 本进程 10 秒后即被冻结、隧道 socket 被系统掐断，接收线程随后抛读超时 →
 * 判定链路已死 → 自动重连。用户感知就是「切出去一下就断线」。
 *
 * 前台服务的意义在于把进程的 adj 从 cached（≥900，会被冻结）提到
 * PERCEPTIBLE_APP_ADJ（200，可感知进程）——**根本进不了 cached 状态**，
 * 于是既不会被冻结、也不会被 LMK 优先回收，socket 与接收线程得以持续存活。
 *
 * ## 设计取舍
 *
 * - **类型选 `connectedDevice`**（不是 `dataSync`）：本服务维持的是与外部设备（PC）的
 *   网络长连接，语义相符；且 Android 15 给 `dataSync` 加了「每 24 小时最多 6 小时」的
 *   运行时上限，长会话会被系统掐掉，故不可用。
 * - **通知保持静默克制**：IMPORTANCE_LOW、无声音无振动无角标、不显示时间戳，
 *   仅一行「正在远程控制 <设备名>」，点击回到 App —— 前台服务必须有通知，这是硬性代价，
 *   但可以做到几乎不打扰。
 * - **只在会话活跃期间存在**（连接中 / 已连接 / 断线重连退避中），
 *   由 [com.quickremote.app.viewmodels.SessionViewModel] 统一启停，退出会话立即 stopSelf。
 * - `stopWithTask = true`（见 Manifest）：用户从最近任务划掉 App = 明确要关闭，会话随之结束。
 * - 锁屏时**也会**继续保持连接 —— 用户要求只保证"切应用不断"，
 *   但前台服务天然覆盖锁屏场景，多保住一种场景是净收益（耗电差异主要在屏幕已熄灭、
 *   解码器已随 Surface 停止的前提下，仅维持 socket 与心跳）。
 */
class RemoteSessionService : Service() {

    private val logger = Logger()

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val deviceName = intent?.getStringExtra(EXTRA_DEVICE_NAME).orEmpty()
        return try {
            ServiceCompat.startForeground(
                this,
                NOTIFICATION_ID,
                buildNotification(deviceName),
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
                } else {
                    0
                }
            )
            logger.info("Session foreground service started (device=${deviceName.ifBlank { "unknown" }})")
            START_NOT_STICKY
        } catch (e: Exception) {
            // 极端情况：后台启动前台服务被系统禁止（Android 12+ 的
            // ForegroundServiceStartNotAllowedException）或厂商 ROM 限制。
            // 保活降级为「只有 wakelock」，会话本身不受影响，故不向上抛。
            logger.warn("startForeground failed: ${e.javaClass.name}: ${e.message}")
            stopSelf()
            START_NOT_STICKY
        }
    }

    /** 纯启动式服务，不提供绑定。 */
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        logger.info("Session foreground service stopped")
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    /** 通知渠道：LOW 重要性 = 无声音、无横幅，只在通知栏安静驻留。 */
    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notify_channel_session_name),
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = getString(R.string.notify_channel_session_desc)
            setShowBadge(false)
            enableVibration(false)
            setSound(null, null)
            // 锁屏通知栏不展示 —— 保活是后台行为，不必在锁屏上占位
            lockscreenVisibility = Notification.VISIBILITY_SECRET
        }
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(deviceName: String): Notification {
        // 点击通知回到 App（singleTop，已存在的任务会被拉到前台并触发 ON_RESUME → 会话自愈）
        val contentIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val title = if (deviceName.isBlank()) {
            getString(R.string.notify_session_title_unknown)
        } else {
            "${getString(R.string.notify_session_title)} $deviceName"
        }

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_session_notify)
            .setContentTitle(title)
            .setContentText(getString(R.string.notify_session_text))
            .setContentIntent(contentIntent)
            .setOngoing(true)
            .setShowWhen(false)
            .setSilent(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            // Android 12+ 默认会把前台服务通知延迟 10 秒展示，这里要求立即展示
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    companion object {
        private const val CHANNEL_ID = "qr_remote_session"
        private const val NOTIFICATION_ID = 1001
        private const val EXTRA_DEVICE_NAME = "device_name"

        /**
         * 启动保活（幂等：已在运行时重复调用只是刷新通知文本）。
         *
         * 必须**在 App 处于前台时**调用（会话建立/重连发起都在会话页可见时发生），
         * 否则 Android 12+ 会抛 ForegroundServiceStartNotAllowedException。
         */
        fun start(context: Context, deviceName: String) {
            try {
                val intent = Intent(context, RemoteSessionService::class.java)
                    .putExtra(EXTRA_DEVICE_NAME, deviceName)
                ContextCompat.startForegroundService(context, intent)
            } catch (e: Exception) {
                Logger().warn("Start session service failed: ${e.javaClass.name}: ${e.message}")
            }
        }

        /** 停止保活（会话结束 / 用户主动断开 / ViewModel 销毁）。 */
        fun stop(context: Context) {
            try {
                context.stopService(Intent(context, RemoteSessionService::class.java))
            } catch (e: Exception) {
                Logger().warn("Stop session service failed: ${e.javaClass.name}: ${e.message}")
            }
        }
    }
}
