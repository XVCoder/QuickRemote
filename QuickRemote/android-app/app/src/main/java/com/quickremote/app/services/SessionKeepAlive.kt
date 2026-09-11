package com.quickremote.app.services

import android.content.Context
import android.os.PowerManager

/**
 * 远程会话期间的 CPU 保活锁（PARTIAL_WAKE_LOCK）。
 *
 * 为什么需要：远程会话是一个长连接的纯后台 I/O 场景，但 App 里**没有**前台服务 ——
 * 一旦用户切到后台或锁屏，进程很快进入 cached 状态被冻结（Android 12+ 直接冻结进程，
 * 更早版本进 Doze）。此时：
 * - 接收线程停摆 → 隧道 socket 不再读到 PC 每 5 秒一次的心跳 → 看门狗判定"无数据"断开；
 * - Wi-Fi 进入省电态，回前台后 socket 可能已成半开连接（对端已走，本端 read 永远阻塞）。
 * 用户感知就是「一进后台就断线，锁屏后经常连不回来」。
 *
 * 持有 PARTIAL_WAKE_LOCK 会让本进程脱离 cached 判定，从而不被冻结、Doze 也不限制其网络，
 * 同时 CPU 保持可运行。**只在 CONNECTED 期间持有**，断线/会话结束/ViewModel 销毁立即释放 ——
 * 常驻持有会明显耗电，绝不可取。
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
