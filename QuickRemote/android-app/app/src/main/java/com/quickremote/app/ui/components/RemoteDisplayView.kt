package com.quickremote.app.ui.components

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewConfiguration
import com.quickremote.app.services.Logger
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

/**
 * 远程桌面渲染视图（截屏方案）。
 *
 * 尺寸由 Compose 层控制（RemoteSessionScreen 用 requiredSize 按「高度拉满」模式
 * 计算等比 cover 尺寸），本 View 实际布局尺寸与远程画面同宽高比，
 * 视频（H.264/JPEG）直接铺满 Surface 即无变形。
 *
 * pan/scale 只通过 View 变换（translation/scale，pivot=0）实现，不与画布绘制叠加，
 * 避免双重变换导致拖动 2 倍速、边缘拖出黑边、点击坐标错位。
 *
 * 手势：
 * - 单指轻点（位移小于触摸阈值）= 左键点击
 * - 单指拖动 = 平移画面（查看被裁掉的部分）
 * - 长按 = 右键
 * - 双指捏合 = 画面缩放（锚点式相似变换 + EMA 平滑：以捏合判定时刻为锚，
 *   每帧从锚点直接计算终态，span/中心做 α=0.5 指数平滑压制触摸噪声——
 *   无逐帧累积误差、无 focus 放大抖动，触点精确跟手；
 *   fit 整幅可见 ~ 5x；fit 状态竖屏下宽度撑满、上下留空，可拖动摆放位置；
 *   任意缩放下垂直拖到极端均至少各露 1/3 屏高空白，供悬浮球避让与空白区手势）
 * - 双指同向滑动（笔记本触摸板式）= 滚轮：主轴判定后垂直滑动=上下滚、水平滑动=左右滚；
 *   与捏合按「指间距变化 vs 中心位移谁主导」自动区分（滑动中指距微漂不再误判为缩放）
 * - 双指轻点（两指快速落下抬起，无移动无缩放）= 右键
 * - 双指手势全程（第二指落下 → 最后一指抬起）不触发任何单指操作：
 *   长按在落指时取消、轻点在抬指时抑制、剩余手指不再接管平移——
 *   防止捏合/滚轮结束抬指时后抬手指微动被当成拖动导致画面跳动
 *
 * 触摸坐标（View 本地坐标，Android 分发时自动做逆变换）按 View 尺寸线性映射为远程坐标。
 */
class RemoteDisplayView(
    context: Context,
    private val logger: Logger = Logger()
) : SurfaceView(context), SurfaceHolder.Callback {

    /** 远程桌面分辨率（收到控制帧后由上层设置）。 */
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

    /** 滚轮（远程坐标 + 垂直滚动量 + 水平滚动量）。 */
    var onWheel: ((Int, Int, Int, Int) -> Unit)? = null

    /**
     * 显示变换变化回调（scale, panX, panY）——画面拖动/缩放时触发，
     * 供上层叠加层（虚拟鼠标光标）换算远程坐标 → 屏幕坐标。
     */
    var onTransformChanged: ((Float, Float, Float) -> Unit)? = null

    // ============ 视口状态 ============
    /** 可视区域（父容器）尺寸，由 Compose 层在布局变化时传入，用于平移 clamp。 */
    private var parentW = 0
    private var parentH = 0

    // ============ 显示变换 ============
    private var displayScale = 1f
    private var panX = 0f
    private var panY = 0f

    // ============ 触摸状态 ============
    private val handler = Handler(Looper.getMainLooper())
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
    private var downRawX = 0f
    private var downRawY = 0f
    private var downLocalX = 0f
    private var downLocalY = 0f
    private var lastRawX = 0f
    private var lastRawY = 0f
    private var moved = false
    private var longPressFired = false

    // 本次触摸出现过双指（ACTION_UP 时抑制单指轻点左键；双指手势后剩余手指
    // 也不再接管单指平移，防误触/防抬指微动跳画面）
    private var everTwoFingers = false

    /** 双指轻点资格：第二指落下时首指未拖动且长按未触发（否则抬指不判右键）。 */
    private var twoFingerTapEligible = false

    // 双指手势（中心点用屏幕绝对坐标：View 本身随 pan 移动，
    // 若用本地坐标计算增量会与 View 位移叠加形成反馈抖动）
    private var lastPinchCenterRawX = 0f
    private var lastPinchCenterRawY = 0f
    private var pinchActive = false

    // 双指手势判定（笔记本触摸板式：滑动=滚轮 / 捏合=缩放 / 双指轻点=右键）
    private var twoFingerStartSpan = 0f
    /** 上一次 MOVE 事件的指间距（缩放比 = 当前 span / lastPinchSpan，逐事件精确跟手）。 */
    private var lastPinchSpan = 0f
    /** 双指手势起始中心（屏幕绝对坐标，用于区分"同向滑动=滚轮"与"捏合=缩放"）。 */
    private var twoFingerStartCenterX = 0f
    private var twoFingerStartCenterY = 0f
    private var twoFingerDownTime = 0L
    /** 指间距偏离起始超过阈值 → 捏合缩放手势，禁用滚轮。 */
    private var twoFingerZoomed = false
    /** 双指滚轮累计：0=未锁定主轴 1=垂直 2=水平（锁定后另一轴忽略）。 */
    private var wheelAxis = 0
    private var wheelTotalX = 0f
    private var wheelTotalY = 0f
    /** 本次手势已发过滚轮（双指轻点判定排除项）。 */
    private var wheelFired = false

    // 双指滚轮（像素换算 + 惯性）：手机像素 / 显示比例 = 远程像素，
    // 1cm 手指位移 = 1cm 画面内容位移；满 40 滚轮单位发送一次（余量保留）
    private val wheelAccumV = WheelAccumulator { u -> onWheel?.invoke(wheelRx, wheelRy, u, 0) }
    private val wheelAccumH = WheelAccumulator { u -> onWheel?.invoke(wheelRx, wheelRy, 0, u) }
    private var wheelRx = 0
    private var wheelRy = 0
    // 速度追踪（松手惯性初速）与累计滚动距离（远程像素）
    private val scrollTracker = ScrollVelocityTracker()
    private var scrollCum = 0f
    // 惯性：松手后按指数衰减继续滚动（有阻尼感，只多滑一小段），新触摸即打断
    private var flingVel = 0f
    private var flingAxis = 0
    private val flingRunnable = object : Runnable {
        override fun run() {
            val dt = 16f
            (if (flingAxis == 1) wheelAccumV else wheelAccumH).scrollRemoteBy(flingVel * dt)
            flingVel *= exp(-dt / PixelWheel.FLING_TAU_MS)
            if (abs(flingVel) > PixelWheel.FLING_STOP) handler.postDelayed(this, 16)
        }
    }

    // 捏合缩放锚点（相似变换模型）：以"判定为捏合"那一刻的状态为锚，
    // 每帧从锚点直接计算终态（scale' = scale锚 * span/span锚，
    // pan' = 中心现值 - 中心锚值 + pan锚 + (scale锚 - scale') * 中心本地锚值）。
    // 相比逐帧增量（factor = span/lastSpan）无累积误差，且 pan 由中心
    // 平滑值直接决定、不经过 focus 幅度放大——单帧触摸噪声不再震屏。
    // 数学上等价于把内容做与手指相同的相似变换：触点精确跟手不变。
    private var pinchAnchorScale = 1f
    private var pinchAnchorPanX = 0f
    private var pinchAnchorPanY = 0f
    private var pinchAnchorSpan = 1f
    private var pinchAnchorCxRaw = 0f
    private var pinchAnchorCyRaw = 0f
    private var pinchAnchorCxLocal = 0f
    private var pinchAnchorCyLocal = 0f

    // 缩放路径 EMA 平滑：两指坐标量化噪声（±1-2px）与部分触摸屏
    // 交替报告两指位置，会让 span/中心高频跳变 → 画面高频抖动；
    // α=0.5 一阶 IIR 衰减噪声、延迟约 1 帧肉眼无感
    private var smoothSpan = 0f
    private var smoothCxRaw = 0f
    private var smoothCyRaw = 0f

    private val longPressRunnable = Runnable {
        longPressFired = true
        val (rx, ry) = mapToRemote(downLocalX, downLocalY)
        onRightClick?.invoke(rx, ry)
    }

    // 双指捏合缩放不再用 ScaleGestureDetector：其 scaleFactor 是相邻事件增量、
    // 且默认带平滑，缩放比与指间距真实比例脱节（不跟手）。改为在 ACTION_MOVE
    // 里手动计算 span/lastPinchSpan 精确比例，配合中心位移平移实现完全跟手
    // （画面上双指触点始终跟随手指位置）。见 MOVE 分支 twoFingerZoomed 处理。

    init {
        holder.addCallback(this)
        holder.setFormat(android.graphics.PixelFormat.OPAQUE)
        isClickable = true
        isFocusable = true
    }

    // ==================== 远程尺寸与视口 ====================

    /** 设置远程分辨率（View 已由 Compose 按等比尺寸布局，仅需重置平移）。 */
    fun setRemoteSize(w: Int, h: Int) {
        if (w <= 0 || h <= 0 || (w == remoteWidth && h == remoteHeight)) return
        remoteWidth = w
        remoteHeight = h
        logger.info("RemoteDisplayView: remote size $w x $h")
        post {
            // 分辨率变化：重置平移（居中），保留用户缩放倍数
            panX = 0f
            panY = 0f
            applyTransform()
        }
    }

    /** 设置可视区域（父容器）尺寸，平移 clamp 依赖它。 */
    fun setViewport(w: Int, h: Int) {
        if (w <= 0 || h <= 0 || (w == parentW && h == parentH)) return
        parentW = w
        parentH = h
        logger.info("RemoteDisplayView: viewport ${w}x${h}")
        post {
            // 视口变化（如旋转）后 fit 比例改变，重新收敛缩放范围
            displayScale = displayScale.coerceIn(minScale(), MAX_SCALE)
            clampPan()
            applyTransform()
        }
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
                downLocalX = event.x
                downLocalY = event.y
                lastRawX = event.rawX
                lastRawY = event.rawY
                moved = false
                longPressFired = false
                pinchActive = false
                everTwoFingers = false
                resetTwoFingerState()
                handler.postDelayed(longPressRunnable, LONG_PRESS_MS)
                return true
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                if (event.pointerCount == 2) {
                    handler.removeCallbacks(flingRunnable)
                    handler.removeCallbacks(longPressRunnable)
                    pinchActive = true
                    everTwoFingers = true
                    // 双指轻点资格：首指落下后未拖动、长按未触发。拖动中无意落下
                    // 第二指快速抬起、或长按右键已发出后再落指，抬指时都不再误判右键
                    twoFingerTapEligible = !moved && !longPressFired
                    moved = true // 双指手势绝不触发单指轻点左键
                    lastPinchCenterRawX = centerRawX(event)
                    lastPinchCenterRawY = centerRawY(event)
                    twoFingerStartSpan = fingerSpan(event)
                    lastPinchSpan = twoFingerStartSpan
                    twoFingerStartCenterX = lastPinchCenterRawX
                    twoFingerStartCenterY = lastPinchCenterRawY
                    twoFingerDownTime = event.eventTime
                    resetTwoFingerState()
                }
            }

            MotionEvent.ACTION_MOVE -> {
                // !everTwoFingers：双指手势后剩余手指不再接管单指平移——
                // 捏合/滚轮结束抬指时后抬手指的微动会被当成拖动导致画面跳动
                if (event.pointerCount == 1 && !pinchActive && !everTwoFingers) {
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
                        // 单指拖动 = 平移画面（屏幕像素 1:1）
                        panX += dx
                        panY += dy
                        clampPan()
                        applyTransform()
                    }
                } else if (event.pointerCount >= 2) {
                    handler.removeCallbacks(longPressRunnable)
                    val cx = centerRawX(event)
                    val cy = centerRawY(event)
                    // 增量必须在更新 last 之前计算（否则恒为 0）
                    val dx = cx - lastPinchCenterRawX
                    val dy = cy - lastPinchCenterRawY
                    val span = fingerSpan(event)

                    // 手势分类：指间距变化主导 = 捏合缩放；中心位移主导 = 双指同向滑动（滚轮）。
                    // 仅凭指间距变化判定会把双指滑动中的微小间距漂移误判成缩放（禁滚轮
                    // 且画面被悄悄缩放），必须同时要求间距变化超过中心位移；开始滚动后不再改判
                    if (!twoFingerZoomed && !wheelFired && wheelAxis == 0 && twoFingerStartSpan > 0f) {
                        val spanChange = abs(span - twoFingerStartSpan)
                        val centerDist = hypot(cx - twoFingerStartCenterX, cy - twoFingerStartCenterY)
                        if (spanChange > twoFingerStartSpan * ZOOM_RATIO && spanChange > centerDist) {
                            twoFingerZoomed = true
                            // 判定为捏合：以当前状态建锚（span/锚=1 画面不跳变）
                            pinchAnchorScale = displayScale
                            pinchAnchorPanX = panX
                            pinchAnchorPanY = panY
                            pinchAnchorSpan = max(span, 1f)
                            pinchAnchorCxRaw = cx
                            pinchAnchorCyRaw = cy
                            pinchAnchorCxLocal = centerLocalX(event)
                            pinchAnchorCyLocal = centerLocalY(event)
                            smoothSpan = span
                            smoothCxRaw = cx
                            smoothCyRaw = cy
                        }
                    }

                    if (!twoFingerZoomed) {
                        // 主轴判定：累计位移先超阈值的轴锁定（斜向滑动不两轴混滚）
                        if (wheelAxis == 0) {
                            wheelTotalX += dx
                            wheelTotalY += dy
                            if (abs(wheelTotalY) > touchSlop) wheelAxis = 1
                            else if (abs(wheelTotalX) > touchSlop) wheelAxis = 2
                        }
                        if (wheelAxis == 1) {
                            // 垂直滑动 = 上下滚（自然滚动：内容跟随手指——手指上移
                            // remotePx<0 → deltaV 负 = Win32"向后拨" = 滚动条下拉
                            // 查看下方内容）。像素换算：手机像素/显示比例 = 远程像素，
                            // 1cm 手指位移 = 1cm 画面内容位移
                            val remotePx = dy / displayScale.coerceAtLeast(0.05f)
                            scrollCum += remotePx
                            scrollTracker.add(event.eventTime, scrollCum)
                            val (rx, ry) = mapToRemote(centerLocalX(event), centerLocalY(event))
                            wheelRx = rx
                            wheelRy = ry
                            if (wheelAccumV.scrollRemoteBy(remotePx)) wheelFired = true
                        } else if (wheelAxis == 2) {
                            // 水平滑动 = 左右滚（自然滚动：手指左移 → deltaH 正 =
                            // 滚动条右拉查看右侧内容），换算与垂直同款
                            val remotePx = -dx / displayScale.coerceAtLeast(0.05f)
                            scrollCum += remotePx
                            scrollTracker.add(event.eventTime, scrollCum)
                            val (rx, ry) = mapToRemote(centerLocalX(event), centerLocalY(event))
                            wheelRx = rx
                            wheelRy = ry
                            if (wheelAccumH.scrollRemoteBy(remotePx)) wheelFired = true
                        }
                    } else {
                        // 捏合缩放（锚点式相似变换）：span/中心先 EMA 平滑压制
                        // 触摸噪声，再从锚点直接计算终态——scale' = scale锚 * span/span锚；
                        // pan' = 中心现值 - 中心锚值 + pan锚 + (scale锚 - scale') * 中心本地锚值
                        // （pan 完全由平滑中心决定，旧实现 pan 补偿项 (1-r)*scale*focus 会把
                        // r 的单帧噪声按 focus 幅度放大成每帧 ±20px 抖动）。
                        // 等价于把内容做与手指相同的相似变换：触点精确跟手
                        smoothSpan += PINCH_SMOOTH_ALPHA * (span - smoothSpan)
                        smoothCxRaw += PINCH_SMOOTH_ALPHA * (cx - smoothCxRaw)
                        smoothCyRaw += PINCH_SMOOTH_ALPHA * (cy - smoothCyRaw)
                        val targetScale = (pinchAnchorScale * smoothSpan / pinchAnchorSpan)
                            .coerceIn(0.2f * pinchAnchorScale, 5f * pinchAnchorScale)
                            .coerceIn(minScale(), MAX_SCALE)
                        displayScale = targetScale
                        panX = smoothCxRaw - pinchAnchorCxRaw + pinchAnchorPanX +
                                (pinchAnchorScale - targetScale) * pinchAnchorCxLocal
                        panY = smoothCyRaw - pinchAnchorCyRaw + pinchAnchorPanY +
                                (pinchAnchorScale - targetScale) * pinchAnchorCyLocal
                        clampPan()
                        applyTransform()
                    }

                    lastPinchSpan = span
                    lastPinchCenterRawX = cx
                    lastPinchCenterRawY = cy
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                if (event.pointerCount == 2) {
                    pinchActive = false
                    // 双指轻点 = 右键（笔记本触摸板式）：两指快速落下抬起，
                    // 无缩放、无滚轮、未锁定滚动主轴（= 无明显移动），
                    // 且落指时首指处于静止（未拖动、长按未触发）——
                    // 拖动/长按后落第二指再抬起的场景不误发右键
                    if (twoFingerTapEligible && !twoFingerZoomed && !wheelFired && wheelAxis == 0 &&
                        (event.eventTime - twoFingerDownTime) < TWO_FINGER_TAP_MS
                    ) {
                        val (rx, ry) = mapToRemote(centerLocalX(event), centerLocalY(event))
                        onRightClick?.invoke(rx, ry)
                    }
                    // 双指滚动结束 → 惯性：取最近 100ms 位移差商为初速，
                    // 指数衰减继续滚动（阻尼感，只多滑一小段），新触摸即打断
                    if (!twoFingerZoomed && wheelAxis != 0) {
                        val v = scrollTracker.velocity()
                        if (abs(v) > PixelWheel.FLING_MIN_START) {
                            flingVel = v
                            flingAxis = wheelAxis
                            handler.removeCallbacks(flingRunnable)
                            handler.postDelayed(flingRunnable, 16)
                        }
                    }
                }
            }

            MotionEvent.ACTION_UP -> {
                handler.removeCallbacks(longPressRunnable)
                if (event.pointerCount == 1 && !moved && !longPressFired && !everTwoFingers) {
                    // 轻点（位移小于阈值且未长按）= 左键点击
                    val (rx, ry) = mapToRemote(event.x, event.y)
                    onLeftClick?.invoke(rx, ry)
                }
                resetTouchState()
                return true
            }

            MotionEvent.ACTION_CANCEL -> {
                handler.removeCallbacks(longPressRunnable)
                handler.removeCallbacks(flingRunnable)
                resetTouchState()
            }
        }
        return true
    }

    private fun resetTwoFingerState() {
        twoFingerZoomed = false
        wheelAxis = 0
        wheelTotalX = 0f
        wheelTotalY = 0f
        wheelFired = false
        wheelAccumV.reset()
        wheelAccumH.reset()
        scrollTracker.reset()
        scrollCum = 0f
    }

    private fun resetTouchState() {
        moved = false
        longPressFired = false
        pinchActive = false
        everTwoFingers = false
        twoFingerTapEligible = false
        resetTwoFingerState()
    }

    // ============ 显示变换（缩放/平移，pivot=0 模型） ============

    /**
     * 最小缩放 = 整幅画面完整可见（fit）：
     * 竖屏下宽度撑满屏幕、上下留空（可上下拖动摆放），横屏下高度撑满、左右留空。
     * View 布局为 cover 尺寸（高度撑满），故 fit 比例 = min(视口宽/View宽, 视口高/View高)。
     */
    private fun minScale(): Float {
        if (width <= 0 || height <= 0 || parentW <= 0 || parentH <= 0) return 1f
        return minOf(parentW.toFloat() / width, parentH.toFloat() / height)
            .coerceIn(FIT_SCALE_FLOOR, 1f)
    }

    /**
     * 平移 clamp：
     * - 水平方向：内容宽于屏幕（放大/cover）时边缘贴齐、超出部分可拖入视野；
     *   内容窄于屏幕（fit、左右留空）时完整留在屏幕内，可自由拖动摆放
     * - 垂直方向：无论缩放比例多大，拖到上/下极端时至少各露 1/3 屏高的空白
     *   （高度只取决于屏幕，不随画面缩放变化）——统一极值公式：
     *   上限 = 内容顶边最高拖到 1/3 屏高（顶部空白 = 1/3 屏），
     *   下限 = 内容底边最低拖到距屏幕底 1/3 屏高（底部空白 = 1/3 屏）；
     *   画面较矮（< 2/3 屏高）时该范围自动退化回"完整可见、自由摆放"，
     *   此时单侧最大空白本就超过 1/3 屏，无需放宽
     * View 由 Compose 居中布局：layoutLeft = (parentW - width) / 2。
     */
    private fun clampPan() {
        if (parentW <= 0 || parentH <= 0 || width <= 0 || height <= 0) return
        val layoutLeft = (parentW - width) / 2f
        val layoutTop = (parentH - height) / 2f
        val contentW = width * displayScale
        val contentH = height * displayScale
        panX = if (contentW >= parentW) {
            panX.coerceIn(parentW - layoutLeft - contentW, -layoutLeft)
        } else {
            panX.coerceIn(-layoutLeft, parentW - layoutLeft - contentW)
        }
        // 垂直：min/max 各取"完整可见边界"与"1/3 屏空白边界"中更宽松的一侧，
        // 保证任何缩放下拖到极端至少露出 1/3 屏高空白，且画面矮于 2/3 屏时
        // 仍完整可见可自由摆放（两个约束在该区间自动平滑衔接）
        val minBlank = parentH / 3f
        panY = panY.coerceIn(
            min(-layoutTop, parentH - minBlank - layoutTop - contentH),
            max(parentH - layoutTop - contentH, minBlank - layoutTop)
        )
    }

    private fun applyTransform() {
        scaleX = displayScale
        scaleY = displayScale
        pivotX = 0f
        pivotY = 0f
        translationX = panX
        translationY = panY
        onTransformChanged?.invoke(displayScale, panX, panY)
    }

    // ============ 坐标映射 ============

    /**
     * View 本地坐标 → 远程桌面坐标。
     * Android 触摸分发已做逆变换（event.x/y 为未缩放本地坐标），
     * View 实际尺寸与远程画面同宽高比（Compose 等比布局），直接线性映射。
     */
    private fun mapToRemote(localX: Float, localY: Float): Pair<Int, Int> {
        if (remoteWidth <= 0 || remoteHeight <= 0 || width <= 0 || height <= 0) {
            logger.info("mapToRemote: invalid remote=${remoteWidth}x${remoteHeight} view=${width}x${height}")
            return Pair(0, 0)
        }
        val rx = (localX * remoteWidth / width).toInt().coerceIn(0, remoteWidth - 1)
        val ry = (localY * remoteHeight / height).toInt().coerceIn(0, remoteHeight - 1)
        return Pair(rx, ry)
    }

    // ============ 双指几何 ============

    /** 双指中心（屏幕绝对坐标，不随 View 移动）。 */
    private fun centerRawX(event: MotionEvent): Float = (event.getRawX(0) + event.getRawX(1)) / 2f

    private fun centerRawY(event: MotionEvent): Float = (event.getRawY(0) + event.getRawY(1)) / 2f

    /** 双指中心（View 本地坐标，用于滚轮坐标映射）。 */
    private fun centerLocalX(event: MotionEvent): Float = (event.getX(0) + event.getX(1)) / 2f

    private fun centerLocalY(event: MotionEvent): Float = (event.getY(0) + event.getY(1)) / 2f

    /** 双指指间距（捏合/张开检测）。 */
    private fun fingerSpan(event: MotionEvent): Float {
        val dx = event.getX(0) - event.getX(1)
        val dy = event.getY(0) - event.getY(1)
        return sqrt(dx * dx + dy * dy)
    }

    companion object {
        private const val LONG_PRESS_MS = 550L

        /** 捏合缩放 EMA 平滑系数（越大越跟手、越小越稳；0.5 ≈ 噪声减半、延迟约 1 帧）。 */
        private const val PINCH_SMOOTH_ALPHA = 0.5f

        /** 指间距偏离起始超过此比例 → 判定为捏合缩放（禁用滚轮）。 */
        private const val ZOOM_RATIO = 0.12f

        /** 双指轻点判定的最长持续时间（超时视为按住而非点击）。 */
        private const val TWO_FINGER_TAP_MS = 300L

        /** 缩放范围。最小值动态为 fit（整幅可见），此处仅为下限保护。 */
        private const val FIT_SCALE_FLOOR = 0.05f
        private const val MAX_SCALE = 5f
    }
}
