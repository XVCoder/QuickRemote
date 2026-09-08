package com.quickremote.app.ui.components

import kotlin.math.abs

/**
 * 双指滚动「像素换算 + 惯性」共用参数（画面内手势与空白区触摸板共享）。
 *
 * 换算目标：手机上滑动 1cm，画面内容也位移 1cm（物理距离一致）。
 * 手机像素 → 远程像素按画面当前显示比例换算（远程像素 = 手机像素 / displayScale），
 * 再按「1 滚轮档位（120 单位）≈ 100 远程像素」折算成滚轮增量——
 * Chrome/Edge 默认每档滚 100px，Explorer/Firefox 每档约 3 行（78-102px），
 * 该校准值在主流应用下均接近 1:1 物理位移。分数值增量现代应用按比例平滑滚动、
 * 旧应用每个非零事件滚 1 行，两种语义下位移都 ≈ 手指位移。
 */
internal object PixelWheel {
    /** 1 滚轮档位（120 单位）对应的远程像素数（换算校准常数，手感偏差可调）。 */
    const val PX_PER_NOTCH = 100f

    /** Win32 WHEEL_DELTA：每档 120 单位。 */
    const val UNITS_PER_NOTCH = 120f

    /** 发送粒度：累计满 40 单位（≈1/3 档 ≈ 33 远程像素）发送一次，平滑不刷屏。 */
    const val SEND_UNITS = 40

    /** 速度采样窗口（ms）：取窗口内最旧/最新样本差商为松手速度。 */
    const val VELOCITY_WINDOW_MS = 100L

    /** 触发惯性的最小松手速度（远程像素/ms），低于此视为有意停住。 */
    const val FLING_MIN_START = 0.15f

    /** 惯性衰减时间常数（ms）：速度每 τ 衰减到 1/e，额外滑行距离 ≈ v0 × τ。 */
    const val FLING_TAU_MS = 150f

    /** 惯性停止阈值（远程像素/ms）。 */
    const val FLING_STOP = 0.04f
}

/**
 * 速度采样器：环形缓存（时间, 累计位移）样本，
 * [velocity] 取最近 [PixelWheel.VELOCITY_WINDOW_MS] 窗口内差商。
 */
internal class ScrollVelocityTracker {
    private var times = LongArray(32)
    private var positions = FloatArray(32)
    private var head = 0
    private var size = 0

    fun add(t: Long, position: Float) {
        times[head] = t
        positions[head] = position
        head = (head + 1) % times.size
        if (size < times.size) size++
    }

    /** 最新样本时刻的速度（远程像素/ms）；样本不足或时间跨度太小返回 0。 */
    fun velocity(): Float {
        if (size < 2) return 0f
        val newest = (head - 1 + times.size) % times.size
        val tNew = times[newest]
        var oldest = newest
        for (i in 1 until size) {
            val idx = (newest - i + times.size) % times.size
            if (tNew - times[idx] <= PixelWheel.VELOCITY_WINDOW_MS) oldest = idx else break
        }
        val dt = tNew - times[oldest]
        if (dt < 8L) return 0f
        return (positions[newest] - positions[oldest]) / dt
    }

    fun reset() {
        head = 0
        size = 0
    }
}

/**
 * 滚轮增量累积器：远程像素 → 滚轮增量（120 单位/档），
 * 累计满 [PixelWheel.SEND_UNITS] 单位发送一次并保留余量
 * （手势与惯性共用同一实例，跨阶段位移不丢失）。
 */
internal class WheelAccumulator(private val send: (units: Int) -> Unit) {
    private var units = 0f

    /** 滚动 remotePx 远程像素；本次发送了增量返回 true（用于滚动标记）。 */
    fun scrollRemoteBy(remotePx: Float): Boolean {
        if (remotePx == 0f) return false
        units += remotePx * PixelWheel.UNITS_PER_NOTCH / PixelWheel.PX_PER_NOTCH
        val whole = units.toInt()
        if (abs(whole) >= PixelWheel.SEND_UNITS) {
            send(whole)
            units -= whole
            return true
        }
        return false
    }

    fun reset() {
        units = 0f
    }
}
