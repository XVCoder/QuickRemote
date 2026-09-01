package com.quickremote.app.ui.components

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewConfiguration
import android.widget.FrameLayout
import com.quickremote.app.services.Logger
import kotlin.math.abs
import kotlin.math.max

/**
 * 远程桌面渲染视图（截屏方案）。
 *
 * 布局为「高度拉满」模式（类似相册里缩放打开的照片）：
 * 画面按「父高 / 远程高」缩放，高度铺满屏幕，宽度按同比例（横屏视频宽度超出屏幕，
 * 可左右拖动查看未显示部分；宽度不足时左右留白居中）。用户双指缩放倍数在布局后保持。
 *
 * 手势：
 * - 单指轻点（位移小于触摸阈值）= 左键点击
 * - 单指拖动 = 平移画面（查看被裁掉的部分）
 * - 长按 = 右键
 * - 双指缩放 = 画面缩放（1x~5x，围绕捏合中心，带阻尼）
 * - 双指拖动 = 平移画面
 * - 双指垂直滑动 = 滚轮
 *
 * 触摸坐标（View 本地坐标，Android 自动做逆变换）按视频尺寸线性映射为远程坐标。
 */
class RemoteDisplayView(
    context: Context,
    private val logger: Logger = Logger()
) : SurfaceView(context), SurfaceHolder.Callback {

    /** 远程桌面分辨率（收到控制帧后由上层设置，触发 cover 重布局）。 */
    var remoteWidth: Int = 0
        private set
    var remoteHeight: Int = 0
        private set

    /** Surface 变化回调（surface 可能为 null 表示销毁）。 */
    var onSurfaceChanged: ((android.view.Surface?, Int, Int) -> Unit)? = null

    /** 左键点击（远程坐标，上层负责按下+释放）。 */
    var onLeftClick: ((Int, Int) -> Unit)? = null

    /** 右键点击（远程坐标）。 */
    var onRightClick: ((Int, Int) -> Unit)? = null

    /** 滚轮（远程坐标 + 滚动量）。 */
    var onWheel: ((Int, Int, Int) -> Unit)? = null

    // ============ 布局状态（高度拉满） ============
    private var parentW = 0
    private var parentH = 0

    // 基础缩放比（父高 / 远程高），布局时计算；displayScale 叠加在它之上用于用户双指缩放
    private var baseScale = 1f

    // ============ 显示变换 ============
    private var displayScale = 1f
    private var panX = 0f
    private var panY = 0f

    // ============ 触摸状态 ============
    private val handler = Handler(Looper.getMainLooper())
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
    private var downRawX = 0f
    private var downRawY = 0f
    private var lastRawX = 0f
    private var lastRawY = 0f
    private var moved = false
    private var longPressFired = false

    // 双指捏合
    private var lastPinchCenterX = 0f
    private var lastPinchCenterY = 0f
    private var pinchActive = false

    // 双指滚轮累计
    private var wheelAccumY = 0f

    private val longPressRunnable = Runnable {
        longPressFired = true
        val (rx, ry) = mapToRemote(downRawX, downRawY)
        onRightClick?.invoke(rx, ry)
    }

    // ZoomLayout 风格：双指缩放围绕捏合中心，带阻尼平滑
    private val scaleDetector = ScaleGestureDetector(
        context,
        object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                // 阻尼系数 0.7：缩放更平滑，避免手指微小移动引起画面抖动
                val factor = 1f + (detector.scaleFactor - 1f) * SCALE_DAMPING
                applyScale(displayScale * factor, detector.focusX, detector.focusY)
                return true
            }
        }
    )

    init {
        holder.addCallback(this)
        holder.setFormat(android.graphics.PixelFormat.OPAQUE)
        isClickable = true
        isFocusable = true
    }

    // ==================== 远程尺寸与高度拉满布局 ====================

    /** 设置远程分辨率并按「高度拉满」模式重布局。 */
    fun setRemoteSize(w: Int, h: Int) {
        if (w <= 0 || h <= 0 || (w == remoteWidth && h == remoteHeight)) return
        remoteWidth = w
        remoteHeight = h
        logger.info("RemoteDisplayView: remote size $w x $h, relayout height-fit")
        post { relayoutHeightFit() }
    }

    /**
     * 按「高度拉满」重布局（类似相册里缩放打开的照片）：
     * baseScale = 父高 / 远程高，画面高度铺满屏幕，宽度按同比例
     * （横屏视频宽度超出屏幕，可左右拖动查看未显示部分；宽度不足时左右留白居中）。
     * 居中显示；保留用户已设定的双指缩放倍数 [displayScale]，仅重置平移。
     */
    private fun relayoutHeightFit() {
        val parent = parent as? android.view.ViewGroup ?: return
        parentW = parent.width
        parentH = parent.height
        if (parentW <= 0 || parentH <= 0 || remoteWidth <= 0 || remoteHeight <= 0) return

        // 高度拉满：缩放比 = 父高 / 远程高
        baseScale = parentH.toFloat() / remoteHeight
        val coverW = (remoteWidth * baseScale).toInt().coerceAtLeast(1)
        val coverH = (remoteHeight * baseScale).toInt().coerceAtLeast(1)

        layoutParams = FrameLayout.LayoutParams(coverW, coverH, android.view.Gravity.CENTER)
        // 重置平移（居中）；保留用户缩放倍数 displayScale
        panX = 0f
        panY = 0f
        applyTransform()
        logger.info("RemoteDisplayView: height-fit layout ${coverW}x${coverH} in ${parentW}x${parentH}, baseScale=$baseScale, displayScale=$displayScale")
    }

    // ==================== Surface 生命周期 ====================

    override fun surfaceCreated(holder: SurfaceHolder) {
        logger.info("RemoteDisplayView: surface created")
        onSurfaceChanged?.invoke(holder.surface, holder.surfaceFrame.width(), holder.surfaceFrame.height())
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        logger.info("RemoteDisplayView: surface changed ${width}x${height}")
        onSurfaceChanged?.invoke(holder.surface, width, height)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        logger.info("RemoteDisplayView: surface destroyed")
        onSurfaceChanged?.invoke(null, 0, 0)
    }

    // ==================== 触摸事件 ====================

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downRawX = event.rawX
                downRawY = event.rawY
                lastRawX = event.rawX
                lastRawY = event.rawY
                moved = false
                longPressFired = false
                pinchActive = false
                wheelAccumY = 0f
                handler.postDelayed(longPressRunnable, LONG_PRESS_MS)
                logger.info("Touch DOWN: raw=(${event.rawX},${event.rawY}) local=(${event.x},${event.y}) view=${width}x${height}")
                return true
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                if (event.pointerCount == 2) {
                    handler.removeCallbacks(longPressRunnable)
                    pinchActive = true
                    lastPinchCenterX = centerX(event)
                    lastPinchCenterY = centerY(event)
                    wheelAccumY = 0f
                }
            }

            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount == 1 && !pinchActive) {
                    val dx = event.rawX - lastRawX
                    val dy = event.rawY - lastRawY
                    lastRawX = event.rawX
                    lastRawY = event.rawY

                    if (!moved && (abs(event.rawX - downRawX) > touchSlop || abs(event.rawY - downRawY) > touchSlop)) {
                        // 超过触摸阈值：判定为拖动（平移画面），取消长按
                        handler.removeCallbacks(longPressRunnable)
                        moved = true
                    }
                    if (moved) {
                        // 单指拖动 = 平移画面
                        panX += dx
                        panY += dy
                        clampPan()
                        applyTransform()
                    }
                } else if (event.pointerCount >= 2) {
                    handler.removeCallbacks(longPressRunnable)
                    moved = true
                    scaleDetector.onTouchEvent(event)

                    // 双指平移
                    val cx = centerX(event)
                    val cy = centerY(event)
                    if (pinchActive) {
                        // 双指中点位移（本地坐标差 ≈ 屏幕位移/scale，直接按屏幕位移驱动）
                        panX += (cx - lastPinchCenterX) * displayScale
                        panY += (cy - lastPinchCenterY) * displayScale
                        lastPinchCenterX = cx
                        lastPinchCenterY = cy
                        clampPan()
                        applyTransform()
                    }

                    // 双指垂直滑动 = 滚轮
                    val dy = cy - lastPinchCenterY
                    wheelAccumY += dy
                    if (abs(wheelAccumY) >= WHEEL_THRESHOLD) {
                        val (rx, ry) = mapToRemote(cx, cy)
                        val delta = (-wheelAccumY / WHEEL_THRESHOLD).toInt()
                        onWheel?.invoke(rx, ry, delta)
                        wheelAccumY = 0f
                    }
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                if (event.pointerCount == 2) {
                    pinchActive = false
                }
            }

            MotionEvent.ACTION_UP -> {
                handler.removeCallbacks(longPressRunnable)
                if (event.pointerCount == 1 && !moved && !longPressFired) {
                    // 轻点（位移小于阈值且未长按）= 左键点击
                    val (rx, ry) = mapToRemote(event.x, event.y)
                    logger.info("Touch UP → left click: local=(${event.x},${event.y}) remote=($rx,$ry) view=${width}x${height}")
                    onLeftClick?.invoke(rx, ry)
                } else {
                    logger.info("Touch UP: no click (moved=$moved longPress=$longPressFired ptrs=${event.pointerCount})")
                }
                resetTouchState()
                return true
            }

            MotionEvent.ACTION_CANCEL -> {
                handler.removeCallbacks(longPressRunnable)
                resetTouchState()
            }
        }
        return true
    }

    private fun resetTouchState() {
        moved = false
        longPressFired = false
        pinchActive = false
        wheelAccumY = 0f
    }

    // ============ 显示变换（缩放/平移，pivot=0 模型） ============

    /**
     * 围绕本地坐标点 (focusX, focusY) 缩放（ZoomLayout 风格）。
     * 模型：显示位置 = layout位置 + pan + scale * 本地坐标。
     * 围绕 F 缩放 r：pan' = pan + scale * F * (1 - r)。
     */
    private fun applyScale(newScale: Float, focusX: Float, focusY: Float) {
        val clamped = newScale.coerceIn(MIN_SCALE, MAX_SCALE)
        if (abs(clamped - displayScale) < 0.001f) return
        val r = clamped / displayScale
        panX += displayScale * focusX * (1 - r)
        panY += displayScale * focusY * (1 - r)
        displayScale = clamped
        clampPan()
        applyTransform()
    }

    /** 平移 clamp：画面边缘不越入屏幕（cover 超出部分可拖入视野）。 */
    private fun clampPan() {
        if (parentW <= 0 || parentH <= 0) return
        val coverW = layoutParams?.width?.toFloat() ?: return
        val coverH = layoutParams?.height?.toFloat() ?: return
        val layoutLeft = (parentW - coverW) / 2f
        val layoutTop = (parentH - coverH) / 2f
        // 内容显示区间需覆盖屏幕
        val minPanX = parentW - layoutLeft - coverW * displayScale
        val maxPanX = -layoutLeft
        val minPanY = parentH - layoutTop - coverH * displayScale
        val maxPanY = -layoutTop
        panX = panX.coerceIn(minPanX, maxPanX)
        panY = panY.coerceIn(minPanY, maxPanY)
    }

    private fun applyTransform() {
        scaleX = displayScale
        scaleY = displayScale
        pivotX = 0f
        pivotY = 0f
        translationX = panX
        translationY = panY
    }

    // ============ 坐标映射 ============

    /**
     * View 本地坐标 → 远程桌面坐标。
     * Android 触摸分发已做逆变换（event.x/y 为未缩放本地坐标），
     * 本地尺寸比例 = 远程尺寸比例，直接线性映射。
     */
    private fun mapToRemote(localX: Float, localY: Float): Pair<Int, Int> {
        if (remoteWidth <= 0 || remoteHeight <= 0 || width <= 0 || height <= 0) {
            return Pair(0, 0)
        }
        val rx = (localX / width * remoteWidth).toInt().coerceIn(0, remoteWidth - 1)
        val ry = (localY / height * remoteHeight).toInt().coerceIn(0, remoteHeight - 1)
        return Pair(rx, ry)
    }

    // ============ 双指几何 ============

    private fun centerX(event: MotionEvent): Float = (event.getX(0) + event.getX(1)) / 2f

    private fun centerY(event: MotionEvent): Float = (event.getY(0) + event.getY(1)) / 2f

    companion object {
        private const val LONG_PRESS_MS = 550L
        private const val WHEEL_THRESHOLD = 40f

        /** 缩放范围与阻尼系数。 */
        private const val MIN_SCALE = 1f
        private const val MAX_SCALE = 5f

        /** 缩放阻尼：手指移动 1 单位，画面只变 0.7，更平滑不易抖动。 */
        private const val SCALE_DAMPING = 0.7f
    }
}
