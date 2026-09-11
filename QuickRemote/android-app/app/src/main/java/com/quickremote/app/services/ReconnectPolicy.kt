package com.quickremote.app.services

/**
 * 断线自动重连策略：白名单判定 + 指数退避。
 *
 * 只对"重试有意义"的断开原因自动重连 —— 用户主动断开、验证失败、
 * 服务器明确拒绝这三类重试必然失败，自动重连只会白耗电并让用户困惑。
 */
object ReconnectPolicy {

    /** 断线原因。 */
    enum class Reason {
        /** 网络超时 / IO 异常 / 隧道断开。 */
        Network,

        /** 看门狗触发（30 秒无数据或无视频帧）。 */
        Watchdog,

        /** 用户主动点断开。 */
        UserInitiated,

        /** 访问验证码错误或验证失败。 */
        AuthFailed,

        /** 服务器明确拒绝、设备不存在。 */
        ServerRejected
    }

    /** 退避序列：1s → 2s → 4s → 8s → 15s × 4，合计 75 秒。 */
    private val DELAYS_MS = longArrayOf(1000, 2000, 4000, 8000, 15000, 15000, 15000, 15000)

    /** 最多自动重连次数。 */
    val MAX_ATTEMPTS: Int get() = DELAYS_MS.size

    /** 第 attempt 次重连（从 0 起）应等待的毫秒数；超出上限返回 null。 */
    fun delayFor(attempt: Int): Long? =
        if (attempt < 0 || attempt >= DELAYS_MS.size) null else DELAYS_MS[attempt]

    /** 该断线原因是否应触发自动重连。 */
    fun shouldAutoReconnect(reason: Reason): Boolean = when (reason) {
        Reason.Network, Reason.Watchdog -> true
        Reason.UserInitiated, Reason.AuthFailed, Reason.ServerRejected -> false
    }
}
