package com.quickremote.app.services

import android.content.Context
import android.os.PowerManager

/**
 * 远程会话期间的 CPU 保活锁（PARTIAL_WAKE_LOCK）。
 *
 * ## 为什么需要
 *
 * 远程会话是一个长连接的纯后台 I/O 场景。屏幕熄灭后 CPU 会进入省电状态，
 * socket 收发被显著延迟，容易连续漏掉 PC 每 5 秒一次的心跳而被判超时。
 * 持有 PARTIAL_WAKE_LOCK 让 CPU 在屏幕熄灭时仍可运行，链路吞吐与心跳判定保持稳定。
 *
 * ## ⚠️ 澄清一个曾经的错误结论（2026-09-16 查证修正）
 *
 * 本类早期注释写着「持有 PARTIAL_WAKE_LOCK 会让进程脱离 cached 判定，从而不被冻结」——
 * **这是错的**。Android 11 起系统有「缓存应用冻结器」：应用进入 cached 状态即被迁入
 * 冻结 cgroup、所有线程挂起；Android 14 起进入 cached **10 秒后**就冻结，且
 * **系统会终止被冻结应用的全部 TCP socket**。官方给出的冻结豁免只有「阻塞他人的文件锁」
 * 与 `BIND_WAIVE_PRIORITY` 绑定两种，**wakelock 不在其中**。
 *
 * 因此：防冻结、防 socket 被掐断靠的是 [RemoteSessionService] 前台服务
 * （把进程 adj 提到 PERCEPTIBLE_APP_ADJ 200，根本进不了 cached 状态）；
 * 本类只负责「屏幕熄灭后 CPU 不被挂起」这一件事。两者互补，缺一不可。
 *
 * 仍然**只在会话活跃期间持有**，会话结束/ViewModel 销毁立即释放 —— 常驻持有会明显耗电。
 */
class SessionKeepAlive(context: Context, private val logger: Logger = Logger()) {

    private val appContext = context.applicationContext

    private var wakeLock: PowerManager.WakeLock? = null

    /** 幂等获取（会话进入 CONNECTED 时调用）。 */
    @Synchronized
    fun acquire() {
        if (wakeLock?.isHeld == true) return
        try {
            val pm = appContext.getSystemService(Context.POWER_SERVICE) as? PowerManager
            if (pm == null) {
                logger.warn("WakeLock unavailable (PowerManager null)")
                return
            }
            val lock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKE_LOCK_TAG)
            // 非引用计数：acquire/release 成对调用，避免计数错乱导致锁一直挂着
            lock.setReferenceCounted(false)
            lock.acquire()
            wakeLock = lock
            logger.info("Session keep-alive wakelock acquired")
        } catch (e: Exception) {
            // 极端情况（厂商 ROM 限制）获取失败：只影响后台存活，不影响会话本身
            logger.warn("Acquire wakelock failed: ${e.javaClass.name}: ${e.message}")
        }
    }

    /** 幂等释放（断线 / 会话结束 / ViewModel 销毁时调用）。 */
    @Synchronized
    fun release() {
        val lock = wakeLock ?: return
        wakeLock = null
        try {
            if (lock.isHeld) lock.release()
            logger.info("Session keep-alive wakelock released")
        } catch (e: Exception) {
            logger.warn("Release wakelock failed: ${e.javaClass.name}: ${e.message}")
        }
    }

    companion object {
        /** 带包名前缀，便于在 `dumpsys power` 里定位是谁拿着锁。 */
        private const val WAKE_LOCK_TAG = "QuickRemote:remote-session"
    }
}
