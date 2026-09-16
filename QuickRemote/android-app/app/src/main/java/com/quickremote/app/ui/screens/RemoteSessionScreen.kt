package com.quickremote.app.ui.screens

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.imeAnimationTarget
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardReturn
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardCommandKey
import androidx.compose.material.icons.filled.KeyboardControlKey
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.ScreenRotation
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.input.pointer.PointerId
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.quickremote.app.data.models.Device
import com.quickremote.app.data.models.ImeToggle
import com.quickremote.app.data.models.ModKeyState
import com.quickremote.app.data.models.QUALITY_PRESETS
import com.quickremote.app.data.models.consumeOneShot
import com.quickremote.app.services.KeyMapper
import com.quickremote.app.services.ReconnectPolicy
import com.quickremote.app.services.RemoteFrameProtocol
import com.quickremote.app.services.RemoteSessionManager
import com.quickremote.app.ui.components.CompactIconButton
import com.quickremote.app.ui.components.LogViewerDialog
import com.quickremote.app.ui.components.MouseFloatingBall
import com.quickremote.app.ui.components.PixelWheel
import com.quickremote.app.ui.components.RemoteDisplayView
import com.quickremote.app.ui.components.RemoteEditText
import com.quickremote.app.ui.components.ScrollVelocityTracker
import com.quickremote.app.ui.components.StatusColor
import com.quickremote.app.ui.components.StatusIndicator
import com.quickremote.app.ui.components.WheelAccumulator
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.BgHover
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.ui.theme.Warning
import com.quickremote.app.viewmodels.SessionViewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.roundToInt
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch

/**
 * 布局避让用的 IME insets —— 取**目标值**（imeAnimationTarget）而非动画值。
 *
 * 为什么不能直接写 `WindowInsets.ime`：键盘动画期间动画值每帧都在变，每变一次就要
 * 重新布局一次；而本页的布局尺寸直接决定 RemoteDisplayView 的 requiredSize
 * （即 SurfaceView 尺寸重分配），于是弹收键盘时每帧都要重排大半个屏幕，
 * 表现就是「切键盘很卡」。目标值只在起止各变一次，重排从 ~60 次降到 1 次。
 *
 * API < 30 没有 IME 动画信息，imeAnimationTarget 与 ime 等价，行为不变。
 */
@Composable
@OptIn(ExperimentalLayoutApi::class)
private fun imeLayoutInsets(): WindowInsets = WindowInsets.imeAnimationTarget

/**
 * 远程桌面会话页（截屏方案）。
 *
 * - 顶部工具栏: 返回、设备名、断开连接
 * - 中间: RemoteDisplayView（MediaCodec 渲染目标）
 * - 底部工具栏: 键盘切换、全屏切换
 *
 * 触摸事件转换为输入帧发送（阶段 5 完整实现）。
 */
@Composable
fun RemoteSessionScreen(
    device: Device,
    viewModel: SessionViewModel,
    onBack: () -> Unit
) {
    val state by viewModel.state.collectAsState()
    val tunnel by viewModel.tunnel.collectAsState()
    val errorMessage by viewModel.errorMessage.collectAsState()
    val isFullscreen by viewModel.isFullscreen.collectAsState()
    val isKeyboardVisible by viewModel.isKeyboardVisible.collectAsState()
    val videoWidth by viewModel.videoWidth.collectAsState()
    val videoHeight by viewModel.videoHeight.collectAsState()
    val connectionMode by viewModel.connectionMode.collectAsState()
    val pcLocked by viewModel.pcLocked.collectAsState()
    val pcUnlockError by viewModel.pcUnlockError.collectAsState()
    val authRequired by viewModel.authRequired.collectAsState()
    val authError by viewModel.authError.collectAsState()
    val blankTouchpad by viewModel.blankTouchpad.collectAsState()
    val touchpadCfg by viewModel.touchpadConfig.collectAsState()
    val reconnectState by viewModel.reconnecting.collectAsState()

    // 进入页面自动开始截屏远程会话（无需凭据）
    LaunchedEffect(device.device_id) {
        viewModel.startSession(device)
    }

    // 剪贴板同步触发时机：App 回到前台。
    // Android 10+ 禁止后台读剪贴板，无法做变化监听，只能在回到前台时主动上报一次
    // （内容未变化时管理器内部会按哈希跳过，不会重复推送）。
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                // 回到前台：恢复看门狗判定、必要时自动补一次重连，并上报本机剪贴板
                Lifecycle.Event.ON_RESUME -> {
                    viewModel.onAppForeground()
                    viewModel.syncClipboard()
                }
                // 切到后台/锁屏：暂停看门狗超时判定 —— 否则"切出去那几十秒没收到数据"
                // 会被当成链路已死，回来必然看到断开（这正是"一进后台就断线"的成因之一）
                Lifecycle.Event.ON_STOP -> viewModel.onAppBackground()
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // 会话页沉浸模式：非全屏仅隐藏底部导航栏（保留顶部状态栏：时间/电量/网络可见），
    // 全屏完全沉浸（状态栏+导航栏都隐藏，滑动临时浮现）。退出页面时恢复。
    val activity = LocalContext.current.findActivity()

    // Android 13+ 通知权限：会话保活用的前台服务需要通知才直观（未授权不影响保活本身，
    // 只是用户在通知栏看不到"会话正在跑"）。在进入会话页这个时机申请最自然 ——
    // 用户此刻正要开始远程控制，能理解"为什么常驻一条通知"。
    // 系统对同一权限最多弹两次，之后自动静默返回拒绝，不会反复打扰。
    LaunchedEffect(activity) {
        val act = activity ?: return@LaunchedEffect
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return@LaunchedEffect
        val granted = ContextCompat.checkSelfPermission(
            act, android.Manifest.permission.POST_NOTIFICATIONS
        ) == PackageManager.PERMISSION_GRANTED
        if (!granted) {
            ActivityCompat.requestPermissions(
                act,
                arrayOf(android.Manifest.permission.POST_NOTIFICATIONS),
                NOTIFICATION_PERMISSION_REQUEST
            )
        }
    }

    // 当前屏幕方向（manifest 已配置 orientation 不重建 Activity，旋转时此值实时更新）
    val configuration = androidx.compose.ui.platform.LocalConfiguration.current
    val isLandscape = configuration.orientation ==
        android.content.res.Configuration.ORIENTATION_LANDSCAPE

    // 全屏联动沉浸模式：横屏自动全屏时状态栏一并隐藏（向日葵式完全沉浸）
    LaunchedEffect(isFullscreen) {
        activity?.window?.let { win ->
            androidx.core.view.WindowInsetsControllerCompat(
                win, win.decorView
            ).apply {
                if (isFullscreen) {
                    hide(androidx.core.view.WindowInsetsCompat.Type.systemBars())
                } else {
                    show(androidx.core.view.WindowInsetsCompat.Type.statusBars())
                    hide(androidx.core.view.WindowInsetsCompat.Type.navigationBars())
                }
                systemBarsBehavior =
                    androidx.core.view.WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        }
    }
    DisposableEffect(Unit) {
        onDispose {
            activity?.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            activity?.window?.let { win ->
                androidx.core.view.WindowInsetsControllerCompat(
                    win, win.decorView
                ).show(androidx.core.view.WindowInsetsCompat.Type.systemBars())
            }
        }
    }

    // 横屏自动全屏：转横屏时自动进入全屏（记录为"自动"），转回竖屏时仅退出
    // 自动进入的全屏——用户在竖屏手动开的全屏不受影响。
    LaunchedEffect(isLandscape) {
        if (isLandscape) {
            if (!isFullscreen) viewModel.setFullscreen(true, auto = true)
        } else if (viewModel.isAutoFullscreen.value) {
            viewModel.setFullscreen(false)
        }
    }

    /** 切换横屏/竖屏（基于当前实际方向取反，物理旋转后按钮语义依然正确）。 */
    fun toggleOrientation() {
        activity?.requestedOrientation = if (isLandscape) {
            android.content.pm.ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        } else {
            android.content.pm.ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        }
    }

    // 隐藏键盘输入框引用（软键盘文本/物理键盘按键捕获）
    var keyInput by remember { mutableStateOf<RemoteEditText?>(null) }

    // 触摸板虚拟光标位置（远程坐标；< 0 = 尚未使用触摸板，不显示）。
    // 状态提升到画面层：RemoteDisplayView 上叠加绘制虚拟光标（PC 画面本身不含指针）
    var padCursorX by remember { mutableStateOf(-1f) }
    var padCursorY by remember { mutableStateOf(-1f) }
    // 悬浮球展开状态（提升到本层：收缩态在画面外空白区启用鼠标手势层）
    var padExpanded by remember { mutableStateOf(false) }
    LaunchedEffect(videoWidth, videoHeight) {
        // 分辨率变化（连接建立/重连）时光标重新定位（悬浮球内会视为屏幕中心）
        padCursorX = -1f
        padCursorY = -1f
    }

    // 快捷键面板显示状态 + 粘滞修饰键（点击激活后保持，随普通按键组合发送，再次点击取消）
    var showHotkeyPanel by remember { mutableStateOf(false) }
    var stickyMods by remember { mutableStateOf(setOf<Int>()) }

    // 底部 Shift/Ctrl（键盘弹起时替换画质/旋转按钮）的三态：
    // NONE → 单击 → ONESHOT（「单击效果」：修饰下一个按键，组合发出后自动失效）
    // ONESHOT → 300ms 内再点 → LOCKED（「长按效果」：持续修饰后续输入，再点一次解除）
    var bottomModStates by remember {
        mutableStateOf(mapOf(VK_SHIFT to ModKeyState.NONE, VK_CONTROL to ModKeyState.NONE))
    }
    var bottomModLastTapAt by remember { mutableStateOf(mapOf<Int, Long>()) }

    // 「中英」按钮的瞬时高亮：PC 端的输入法中/英状态读不到，所以不做常亮
    // （那会是假状态），只在点击后点亮 200ms 表示「切换序列已发出」。
    // 它不参与 bottomModStates 三态机 —— 切输入法与 Shift 修饰是两件事。
    var imeFlash by remember { mutableStateOf(false) }
    LaunchedEffect(imeFlash) {
        if (imeFlash) {
            delay(200)
            imeFlash = false
        }
    }

    /** 底部修饰键点击：NONE→单击激活；单击后 300ms 内再点→长按锁定；长按中再点→解除。 */
    fun onBottomModTap(vk: Int) {
        val now = System.currentTimeMillis()
        val next = when (bottomModStates[vk] ?: ModKeyState.NONE) {
            ModKeyState.NONE -> ModKeyState.ONESHOT
            ModKeyState.ONESHOT ->
                if (now - (bottomModLastTapAt[vk] ?: 0L) < 300) ModKeyState.LOCKED
                else ModKeyState.ONESHOT // 超时后再点：重新计时，维持单击态
            ModKeyState.LOCKED -> ModKeyState.NONE
        }
        bottomModStates = bottomModStates + (vk to next)
        bottomModLastTapAt = bottomModLastTapAt + (vk to now)
    }

    // 键盘收起时复位底部修饰键（按钮已还原为画质/旋转，残留的修饰态不应影响后续输入）
    LaunchedEffect(isKeyboardVisible) {
        if (!isKeyboardVisible) {
            bottomModStates = mapOf(VK_SHIFT to ModKeyState.NONE, VK_CONTROL to ModKeyState.NONE)
        }
    }

    // 画质快捷面板显示状态（会话内直接切换档位，无需断开重连）
    var showQualityPanel by remember { mutableStateOf(false) }
    val qualityPercent by viewModel.qualityPercent.collectAsState()

    // 同步真实 IME 可见状态（用户按系统返回键收起键盘时修正，避免按钮高亮失真）。
    //
    // ⚠️ 性能关键：**绝不能**在组合里直接读 WindowInsets.ime。键盘动画期间 insets
    // 每帧都在变，组合读取会让整个会话屏（Scaffold / 底部栏 / SurfaceView / 悬浮球 /
    // 手势层）每帧重组一次 —— 这是键盘弹起/收起卡顿的根因之一。
    // 这里改成 snapshotFlow 订阅，并把结果去重成布尔值：重组次数从 ~60 次降到 0 次。
    val imeInsets = WindowInsets.ime
    val imeDensity = LocalDensity.current
    LaunchedEffect(imeInsets, imeDensity) {
        snapshotFlow { imeInsets.getBottom(imeDensity) > 0 }
            .distinctUntilChanged()
            .collect { visible ->
                // 读 StateFlow 的当前值而非组合捕获值，避免 effect 未重启时的旧值比较
                if (viewModel.isKeyboardVisible.value != visible) {
                    viewModel.setKeyboardVisible(visible)
                }
            }
    }

    /** 发送键盘事件帧：[vkCode 2B][down 1B]。 */
    fun sendKeyRaw(vk: Int, down: Boolean) {
        val data = ByteBuffer.allocate(3).order(ByteOrder.LITTLE_ENDIAN)
            .putShort(vk.toShort())
            .put(if (down) 1 else 0)
            .array()
        viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_KEY, data)
    }

    /**
     * 发送一次按键（包裹粘滞修饰键与 Shift）。
     * 修饰键顺序：先按下所有激活的修饰键 → 目标键按下/释放 → 释放修饰键。
     */
    fun sendKeyCombo(vk: Int, needShift: Boolean = false) {
        // 底部 Shift/Ctrl 的「单击」与「长按」态都参与修饰（v1.0.74 曾漏并 LOCKED，
        // 症状：双击高亮但方向键不选字）；与快捷键面板粘滞修饰键按集合并去重
        val bottomMods = bottomModStates.filterValues { it != ModKeyState.NONE }.keys
        val mods = (stickyMods + bottomMods).toList()
        val shiftExtra = needShift && 0x10 !in mods
        if (shiftExtra) sendKeyRaw(0x10, true)
        mods.forEach { sendKeyRaw(it, true) }
        sendKeyRaw(vk, true)
        sendKeyRaw(vk, false)
        mods.forEach { sendKeyRaw(it, false) }
        if (shiftExtra) sendKeyRaw(0x10, false)
        // 「单击效果」：ONESHOT 修饰键作用于本次组合后自动失效（LOCKED 长按不受影响）
        bottomModStates = bottomModStates.consumeOneShot()
    }

    /**
     * 发送上屏文本：可映射 VK 的 ASCII 字符走按键帧（保留游戏等按键语义），
     * 其余（中文/emoji 等）合并为 UTF-8 文本帧，PC 端用 KEYEVENTF_UNICODE 注入。
     */
    fun sendTextToRemote(text: String) {
        val unicodeBuf = StringBuilder()
        fun flushUnicode() {
            if (unicodeBuf.isNotEmpty()) {
                viewModel.sendInput(
                    RemoteFrameProtocol.TYPE_INPUT_TEXT,
                    unicodeBuf.toString().toByteArray(Charsets.UTF_8)
                )
                unicodeBuf.clear()
            }
        }
        text.forEach { ch ->
            val mapped = KeyMapper.charToVk(ch)
            if (mapped != null) {
                flushUnicode()
                sendKeyCombo(mapped.first, mapped.second)
            } else {
                unicodeBuf.append(ch)
            }
        }
        flushUnicode()
        // 中文/emoji 等 Unicode 上屏不携带按键状态，但「单击」修饰键视为已消耗，
        // 避免滞留到之后毫不相关的英文输入上（「长按」LOCKED 态保持）
        bottomModStates = bottomModStates.consumeOneShot()
    }

    /**
     * 「中英」切换：向 PC 注入一次裸按 Shift（按下 + 抬起，中间不夹任何键），
     * PC 端输入法据此判定为「单击 Shift」并切换中/英文。
     *
     * 与 Shift 修饰键彻底解耦：不改 bottomModStates 的 Shift 三态，也不把 Shift 包在别的键外面。
     * 但它是一次**输入动作**，按既有规则消耗单击态（ONESHOT），长按锁定态（LOCKED）保留 ——
     * 否则残留的单击态会让下一次毫不相关的输入突然带上 Shift。
     *
     * 已知边界（暂不处理）：底部三态只是**逻辑**按下，组合之间并不会让 PC 端物理按住 Shift，
     * 所以本方法无需先释放任何东西。唯一的例外是外接物理键盘 —— 若用户正按着实体 Shift
     * （`handleAndroidKeyDown` 会真的把 Shift 按住不放）同时点本按钮，多出的这一对 Shift
     * 按下/抬起会打乱其实体 Shift 的按住状态。场景罕见（有实体键盘时直接用实体 Shift 更顺手），
     * 故不为此增加状态跟踪；真遇到时在发送序列后补一次 Shift 按下即可还原。
     */
    fun onImeToggleTap() {
        ImeToggle.sequence().forEach { sendKeyRaw(it.vk, it.down) }
        bottomModStates = bottomModStates.consumeOneShot()
        imeFlash = true
    }

    /** 唤起/收起软键盘（工具栏按钮与全屏悬浮按钮共用）。 */
    fun toggleIme() {
        val willShow = !isKeyboardVisible
        viewModel.toggleKeyboard()
        val ime = activity?.getSystemService(Context.INPUT_METHOD_SERVICE)
                as? android.view.inputmethod.InputMethodManager
        if (willShow) {
            keyInput?.let { et ->
                et.requestFocus()
                // post 到下一帧，确保焦点生效后再弹出（部分设备直接 show 不生效）
                et.post { ime?.showSoftInput(et, 0) }
            }
        } else {
            ime?.hideSoftInputFromWindow(keyInput?.windowToken, 0)
            keyInput?.clearFocus()
        }
    }

    /** 物理键盘按键按下：映射 VK 并发送（含修饰键状态）。 */
    fun handleAndroidKeyDown(keyCode: Int, event: android.view.KeyEvent): Boolean {
        // 修饰键单独处理（维持按下状态）
        KeyMapper.androidModifierToVk(keyCode)?.let { vk ->
            sendKeyRaw(vk, true)
            return true
        }
        val vk = KeyMapper.androidKeyToVk(keyCode) ?: return false
        sendKeyCombo(vk, event.isShiftPressed)
        return true
    }

    /** 物理键盘按键释放。 */
    fun handleAndroidKeyUp(keyCode: Int, event: android.view.KeyEvent): Boolean {
        KeyMapper.androidModifierToVk(keyCode)?.let { vk ->
            sendKeyRaw(vk, false)
            return true
        }
        val vk = KeyMapper.androidKeyToVk(keyCode) ?: return false
        sendKeyRaw(vk, false)
        return true
    }

    /** IME sendKeyEvent（部分输入法的回车/删除走这里，与 deleteSurroundingText 互斥不重复）。 */
    fun handleImeKeyEvent(event: android.view.KeyEvent): Boolean {
        if (event.action != android.view.KeyEvent.ACTION_DOWN) return false
        return when (event.keyCode) {
            android.view.KeyEvent.KEYCODE_ENTER, android.view.KeyEvent.KEYCODE_NUMPAD_ENTER -> {
                sendKeyCombo(0x0D)
                true
            }
            android.view.KeyEvent.KEYCODE_DEL -> {
                sendKeyCombo(0x08)
                true
            }
            else -> false
        }
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            if (!isFullscreen) {
                // 紧凑顶部栏：垂直留白压到 2dp、图标按钮 38dp（Material3 IconButton 的
                // 48dp 最小触控尺寸会把这一条顶到 64dp，屏幕高度浪费在纯留白上）
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface)
                        .windowInsetsPadding(WindowInsets.statusBars)
                        .padding(horizontal = 4.dp, vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    CompactIconButton(
                        icon = Icons.AutoMirrored.Filled.ArrowBack,
                        contentDescription = "返回",
                        tint = TextPrimary,
                        onClick = onBack
                    )
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            device.hostname.ifBlank { device.device_id },
                            style = MaterialTheme.typography.titleSmall,
                            color = TextPrimary,
                            fontWeight = FontWeight.Medium,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        Text(
                            if (videoWidth > 0) {
                                "$videoWidth x $videoHeight · " +
                                    if (connectionMode == RemoteSessionManager.ConnectionMode.LAN) "局域网直连" else "公网中继"
                            } else "远程桌面",
                            style = MaterialTheme.typography.labelSmall,
                            color = if (connectionMode == RemoteSessionManager.ConnectionMode.LAN) Success else TextMuted,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    CompactIconButton(
                        icon = Icons.Filled.LinkOff,
                        contentDescription = "断开连接",
                        tint = Warning,
                        onClick = { viewModel.disconnect(); onBack() }
                    )
                }
            }
        },
        bottomBar = {
            if (!isFullscreen) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(MaterialTheme.colorScheme.surface)
                        // union 取最大值：无键盘时避让导航栏，键盘弹出时避让 IME（工具栏浮在键盘上方）。
                        // 用 imeLayoutInsets()（目标值）而非 WindowInsets.ime：动画值会让本行
                        // 每帧改一次高度，连带 Scaffold 内容区每帧重排（详见 imeLayoutInsets 注释）
                        .windowInsetsPadding(WindowInsets.navigationBars.union(imeLayoutInsets()))
                        .padding(horizontal = 6.dp, vertical = 4.dp),
                    // 每一格都用 weight(1f) 均分：既保证同排按钮**宽度完全一致**
                    // （此前 SpaceEvenly + 文字定宽，「快捷键」3 字比其它宽一截），
                    // 也自动适配窄屏与横屏，不需要再缩字号。
                    // 列数随键盘状态在 5 / 6 之间切换（键盘弹起时多出「中英」），
                    // 6 列时单格宽度在 360dp 屏上仍高于 48dp 最小触控面积
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    ToolBarButton(
                        icon = Icons.Filled.Keyboard,
                        label = "键盘",
                        active = isKeyboardVisible,
                        modifier = Modifier.weight(1f),
                        onClick = { toggleIme() }
                    )
                    ToolBarButton(
                        icon = Icons.Filled.KeyboardCommandKey,
                        label = "快捷键",
                        active = showHotkeyPanel,
                        modifier = Modifier.weight(1f),
                        onClick = { showHotkeyPanel = !showHotkeyPanel }
                    )
                    // 键盘弹起时「旋转/画质」让位给「Ctrl/Shift」：正在输入时修饰键比
                    // 切屏/调画质常用得多。单击 = 修饰下一个输入（自动失效）；
                    // 300ms 内快速再点 = 长按锁定（持续修饰），再次单击解除（高亮表示激活）
                    if (isKeyboardVisible) {
                        ToolBarButton(
                            icon = Icons.Filled.KeyboardControlKey,
                            label = "Ctrl",
                            active = bottomModStates[VK_CONTROL] != ModKeyState.NONE,
                            modifier = Modifier.weight(1f),
                            onClick = { onBottomModTap(VK_CONTROL) }
                        )
                        ToolBarButton(
                            icon = Icons.Filled.ArrowUpward,
                            label = "Shift",
                            active = bottomModStates[VK_SHIFT] != ModKeyState.NONE,
                            modifier = Modifier.weight(1f),
                            onClick = { onBottomModTap(VK_SHIFT) }
                        )
                        // 中/英切换：与上面的 Shift 修饰键**职责分离** —— 它发的是裸按 Shift
                        // （按下/抬起相邻，不夹其他键），这才是输入法认的「单击 Shift」手势；
                        // Shift 按钮则保持原有的三态修饰语义不变（见 onImeToggleTap 注释）
                        ToolBarButton(
                            icon = Icons.Filled.Language,
                            label = "中英",
                            active = imeFlash,
                            modifier = Modifier.weight(1f),
                            onClick = { onImeToggleTap() }
                        )
                    } else {
                        ToolBarButton(
                            icon = Icons.Filled.ScreenRotation,
                            label = "旋转",
                            active = isLandscape,
                            modifier = Modifier.weight(1f),
                            onClick = { toggleOrientation() }
                        )
                        ToolBarButton(
                            icon = Icons.Filled.Tune,
                            label = "画质",
                            active = showQualityPanel,
                            modifier = Modifier.weight(1f),
                            onClick = { showQualityPanel = !showQualityPanel }
                        )
                    }
                    // 键盘弹起时右端按钮让位给「回车」：此刻用户正在输入，一个紧贴键盘、
                    // 触手可及的 Enter 比「切换全屏」有用得多（收起键盘后全屏按钮自动回来）
                    if (isKeyboardVisible) {
                        ToolBarButton(
                            icon = Icons.AutoMirrored.Filled.KeyboardReturn,
                            label = "回车",
                            active = false,
                            modifier = Modifier.weight(1f),
                            onClick = { sendKeyCombo(0x0D) }
                        )
                    } else {
                        ToolBarButton(
                            icon = if (isFullscreen) Icons.Filled.FullscreenExit else Icons.Filled.Fullscreen,
                            label = "全屏",
                            active = false,
                            modifier = Modifier.weight(1f),
                            onClick = { viewModel.toggleFullscreen() }
                        )
                    }
                }
            }
        }
    ) { padding ->
        BoxWithConstraints(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
                .padding(padding)
                // 全屏无底部工具栏，键盘弹出时自行避让（画面等比缩小，远程底部输入框可见）；
                // 非全屏时 bottomBar 已含 IME 避让（Scaffold content padding 已挤压本区域），无需重复。
                // 用目标值 insets：动画值会让画面尺寸每帧变化一次（SurfaceView 反复重分配）
                .then(if (isFullscreen) Modifier.windowInsetsPadding(imeLayoutInsets()) else Modifier)
        ) {
            val density = LocalDensity.current
            val parentWpx = constraints.maxWidth
            val parentHpx = constraints.maxHeight

            // 高度拉满模式（类似相册缩放打开的照片）：缩放比 = 可用高/远程高。
            // 上下各留 VReserve 边距（即便键盘未弹出）：画面默认不铺满，
            // 可上下拖动避让悬浮球等底部元素遮挡；横屏远程宽度超出屏幕可左右拖动。
            // 用 requiredSize 强制子项尺寸（Compose 会忽略子 View 内部设置的 layoutParams）。
            val vReservePx = with(density) { VReserve.toPx() }.toInt()
            val (coverWpx, coverHpx) = remember(videoWidth, videoHeight, parentWpx, parentHpx, vReservePx) {
                if (videoWidth > 0 && videoHeight > 0 && parentWpx > 0 && parentHpx > 0) {
                    val availH = (parentHpx - 2 * vReservePx).coerceAtLeast(parentHpx / 3)
                    val baseScale = availH.toFloat() / videoHeight
                    ((videoWidth * baseScale).toInt().coerceAtLeast(1)) to
                        ((videoHeight * baseScale).toInt().coerceAtLeast(1))
                } else parentWpx to parentHpx  // 视频未就绪：占满父容器（保证 Surface 正常创建）
            }
            val (coverWdp, coverHdp) = remember(coverWpx, coverHpx, density) {
                with(density) { coverWpx.toDp() to coverHpx.toDp() }
            }

            // 画面显示变换（RemoteDisplayView 的 pan/scale），拖动/缩放时回调更新，
            // 供虚拟光标层将远程坐标换算为屏幕坐标
            var viewScale by remember { mutableStateOf(1f) }
            var viewPanX by remember { mutableStateOf(0f) }
            var viewPanY by remember { mutableStateOf(0f) }

            // 画面外空白区手势层（位于画面 View 之下，仅画面 View 布局边界外的触摸
            // 落入本层）：悬浮球收缩时启用——单指轻点=左键、双指轻点=右键、
            // 双指同向滑动=滚轮（上下/左右），坐标 = 当前虚拟光标位置。
            // 空白区触摸板开启时单指滑动 = 光标相对移动（与悬浮球长按同款增益），
            // 底部叠加半透明水印提示用途（画面拖过来会被自然遮挡）
            if (state == RemoteSessionManager.SessionState.CONNECTED && !padExpanded) {
                BlankAreaGestureLayer(
                    cursorX = padCursorX,
                    cursorY = padCursorY,
                    remoteWidth = videoWidth,
                    remoteHeight = videoHeight,
                    viewScale = viewScale,
                    touchpadEnabled = blankTouchpad,
                    cursorSpeed = touchpadCfg.touchpadSpeed / 100f,
                    doubleTapDrag = touchpadCfg.touchpadDoubleTapDrag,
                    threeFinger = touchpadCfg.touchpadThreeFinger,
                    fourFinger = touchpadCfg.touchpadFourFinger,
                    parentHpx = parentHpx,
                    onCursorMove = { x, y ->
                        sendMouseAction(viewModel, 0, x, y)  // action 0 = 光标移动
                    },
                    onCursorChange = { x, y ->
                        padCursorX = x
                        padCursorY = y
                    },
                    onLeftDown = { x, y ->
                        sendMouseAction(viewModel, 1, x, y)  // 左按下
                    },
                    onLeftUp = { x, y ->
                        sendMouseAction(viewModel, 2, x, y)  // 左释放
                    },
                    onLeftClick = { x, y ->
                        sendMouseAction(viewModel, 1, x, y)  // 左按下
                        sendMouseAction(viewModel, 2, x, y)  // 左释放
                    },
                    onRightClick = { x, y ->
                        sendMouseAction(viewModel, 3, x, y)  // 右按下
                        sendMouseAction(viewModel, 4, x, y)  // 右释放
                    },
                    onWheel = { x, y, deltaV, deltaH ->
                        sendWheel(viewModel, x, y, deltaV, deltaH)
                    },
                    onGestureKey = { gesture ->
                        sendGestureKeys(viewModel, gesture)
                    }
                )
            }

            // 渲染视图（MediaCodec 解码渲染目标）
            AndroidView(
                modifier = Modifier
                    .align(Alignment.Center)
                    .requiredSize(coverWdp, coverHdp),
                factory = { ctx ->
                    RemoteDisplayView(context = ctx).apply {
                        onSurfaceChanged = { surface, _, _ ->
                            viewModel.setSurface(surface)
                        }
                        onLeftClick = { x, y ->
                            // 直接触控也会移动 PC 指针，同步虚拟光标位置防错位
                            padCursorX = x.toFloat()
                            padCursorY = y.toFloat()
                            sendMouseAction(viewModel, 1, x, y)  // 左按下
                            sendMouseAction(viewModel, 2, x, y)  // 左释放
                        }
                        onRightClick = { x, y ->
                            padCursorX = x.toFloat()
                            padCursorY = y.toFloat()
                            sendMouseAction(viewModel, 3, x, y)  // 右按下
                            sendMouseAction(viewModel, 4, x, y)  // 右释放
                        }
                        onWheel = { x, y, deltaV, deltaH ->
                            padCursorX = x.toFloat()
                            padCursorY = y.toFloat()
                            sendWheel(viewModel, x, y, deltaV, deltaH)
                        }
                        onTransformChanged = { s, px, py ->
                            viewScale = s
                            viewPanX = px
                            viewPanY = py
                        }
                        // 双击拖动（快速双击后第二下按住滑动 = 按住左键拖动远程窗口）
                        onLeftDown = { x, y ->
                            padCursorX = x.toFloat()
                            padCursorY = y.toFloat()
                            sendMouseAction(viewModel, 1, x, y)  // 左按下
                        }
                        onDragMove = { x, y ->
                            padCursorX = x.toFloat()
                            padCursorY = y.toFloat()
                            sendMouseAction(viewModel, 0, x, y)  // 拖动中光标跟手
                        }
                        onLeftUp = { x, y ->
                            sendMouseAction(viewModel, 2, x, y)  // 左释放
                        }
                    }
                },
                update = { view ->
                    // 可视区域（clamp 依赖）与远程分辨率变化时同步
                    view.setViewport(parentWpx, parentHpx)
                    if (videoWidth > 0 && videoHeight > 0) {
                        view.setRemoteSize(videoWidth, videoHeight)
                    }
                    // 双击拖动与空白区触摸板共用同一开关
                    view.doubleTapDragEnabled = touchpadCfg.touchpadDoubleTapDrag
                }
            )

            // 虚拟鼠标光标（触摸板模式）：DXGI 捕获的 PC 画面不含鼠标指针，
            // 触摸板相对移动必须在客户端画出光标才知道位置（向日葵同款方案）
            if (padCursorX >= 0 && videoWidth > 0 && videoHeight > 0 && coverWpx > 0) {
                VirtualCursorOverlay(
                    remoteX = padCursorX,
                    remoteY = padCursorY,
                    remoteW = videoWidth.toFloat(),
                    remoteH = videoHeight.toFloat(),
                    viewW = coverWpx.toFloat(),
                    viewH = coverHpx.toFloat(),
                    scale = viewScale,
                    panX = viewPanX,
                    panY = viewPanY
                )
            }

            // 全屏模式：右侧悬浮按钮组（输入法/快捷键/旋转/退出全屏——
            // 全屏时无工具栏，必须有退出入口；旋转按钮供横屏全屏直接切回竖屏）
            if (isFullscreen) {
                Column(
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .padding(top = 36.dp, end = 10.dp),
                    horizontalAlignment = Alignment.End
                ) {
                    FloatingToolButton(
                        icon = Icons.Filled.Keyboard,
                        contentDescription = "输入法",
                        active = isKeyboardVisible,
                        onClick = { toggleIme() }
                    )
                    Spacer(modifier = Modifier.height(10.dp))
                    FloatingToolButton(
                        icon = Icons.Filled.KeyboardCommandKey,
                        contentDescription = "快捷键",
                        active = showHotkeyPanel,
                        onClick = { showHotkeyPanel = !showHotkeyPanel }
                    )
                    Spacer(modifier = Modifier.height(10.dp))
                    FloatingToolButton(
                        icon = Icons.Filled.ScreenRotation,
                        contentDescription = if (isLandscape) "切换竖屏" else "切换横屏",
                        active = isLandscape,
                        onClick = { toggleOrientation() }
                    )
                    Spacer(modifier = Modifier.height(10.dp))
                    FloatingToolButton(
                        icon = Icons.Filled.Tune,
                        contentDescription = "画质",
                        active = showQualityPanel,
                        onClick = { showQualityPanel = !showQualityPanel }
                    )
                    Spacer(modifier = Modifier.height(10.dp))
                    FloatingToolButton(
                        icon = Icons.Filled.FullscreenExit,
                        contentDescription = "退出全屏",
                        active = false,
                        onClick = { viewModel.toggleFullscreen() }
                    )
                }
            }

            // 快捷键面板：叠加在画面底部（非全屏时位于底部工具栏上方；键盘弹出时位于键盘上方）
            if (showHotkeyPanel) {
                HotkeyPanel(
                    stickyMods = stickyMods,
                    onToggleMod = { vk ->
                        stickyMods = if (vk in stickyMods) stickyMods - vk else stickyMods + vk
                    },
                    onKey = { vk -> sendKeyCombo(vk) },
                    modifier = Modifier
                        .align(Alignment.BottomCenter)
                        .fillMaxWidth()
                )
            }

            // 画质快捷面板：会话内切换档位，复用已有 quality 控制帧（PC 端零改动）
            if (showQualityPanel) {
                Row(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .padding(top = 12.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(BgCard.copy(alpha = 0.94f))
                        .padding(horizontal = 8.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    QUALITY_PRESETS.forEach { preset ->
                        val selected = qualityPercent == preset.percent
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(6.dp))
                                .background(if (selected) Accent else Color.Transparent)
                                .clickable {
                                    viewModel.setQuality(preset.percent)
                                    // 面板操作 = 明确的前台交互时机，顺带同步一次剪贴板
                                    viewModel.syncClipboard()
                                }
                                .padding(horizontal = 14.dp, vertical = 7.dp)
                        ) {
                            Text(
                                preset.label,
                                style = MaterialTheme.typography.labelMedium,
                                color = if (selected) Color.White else TextPrimary
                            )
                        }
                    }
                }
            }

            // 鼠标悬浮球（向日葵式精准操控）：拖动移位（松手贴边）、长按拖动移动
            // 远程光标（画面上叠加虚拟光标显示位置）、点击展开左/中/右三键环
            // （中键支持按住上下滑动模拟滚轮）。
            // 根节点铺满容器但仅球与按键区域响应触摸，不遮挡画面操作
            if (state == RemoteSessionManager.SessionState.CONNECTED) {
                MouseFloatingBall(
                    remoteWidth = videoWidth,
                    remoteHeight = videoHeight,
                    cursorX = padCursorX,
                    cursorY = padCursorY,
                    padExpanded = padExpanded,
                    onPadExpandedChange = { padExpanded = it },
                    onCursorMove = { x, y ->
                        sendMouseAction(viewModel, 0, x, y)  // action 0 = 光标移动
                    },
                    onCursorChange = { x, y ->
                        padCursorX = x
                        padCursorY = y
                    },
                    onButtonClick = { button, x, y ->
                        sendMouseAction(viewModel, button.actionDown, x, y)
                        sendMouseAction(viewModel, button.actionUp, x, y)
                    },
                    onWheel = { x, y, deltaV, deltaH ->
                        sendWheel(viewModel, x, y, deltaV, deltaH)
                    },
                    modifier = Modifier.fillMaxSize()
                )
            }

            // 全屏 + 键盘弹起：右下角浮一个「回车」。
            // 全屏没有底部工具栏，且键盘占了屏幕下半，回车键必须紧贴键盘才好按 ——
            // 外层 BoxWithConstraints 已经按 IME insets 收缩过，所以 BottomEnd 恰好就是
            // 键盘上沿，这里不需要再叠一次 inset padding。放在悬浮球之后（后绘者优先接收触摸）。
            if (isFullscreen && isKeyboardVisible) {
                Box(modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(end = 14.dp, bottom = 14.dp)
                ) {
                    FloatingToolButton(
                        icon = Icons.AutoMirrored.Filled.KeyboardReturn,
                        contentDescription = "回车",
                        active = false,
                        onClick = { sendKeyCombo(0x0D) }
                    )
                }
            }

            // 状态覆盖层（连接中/失败时显示）。
            // 自动重连期间不显示：改为下方的半透明重连浮层，保留最后一帧画面（不黑屏）。
            if (state != RemoteSessionManager.SessionState.CONNECTED && reconnectState == null) {
                var showLogViewer by remember { mutableStateOf(false) }
                SessionOverlay(
                    state = state,
                    tunnel = tunnel,
                    errorMessage = errorMessage,
                    onReconnect = { viewModel.startSession(device) },
                    onViewLogs = { showLogViewer = true }
                )
                if (showLogViewer) {
                    LogViewerDialog(onDismiss = { showLogViewer = false })
                }
            }

            // 断线自动重连浮层：半透明遮罩 + 倒计时 + 立即重试/取消。
            // 底下的 RemoteDisplayView 保留最后一帧，用户能看到断线前的画面而不是黑屏。
            reconnectState?.let { rc ->
                ReconnectOverlay(
                    state = rc,
                    onRetryNow = { viewModel.retryNow() },
                    onCancel = { viewModel.cancelReconnect() }
                )
            }

            // PC 锁屏提示条：锁屏时输入由 PC 端 SYSTEM 代理注入 Winlogon 桌面，
            // 可直接点击画面操作锁屏页（如向日葵）；提示条提供一键密码解锁快捷入口。
            if (pcLocked && state == RemoteSessionManager.SessionState.CONNECTED) {
                var showUnlockDialog by remember { mutableStateOf(false) }

                Column(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .padding(top = 24.dp)
                        .clip(RoundedCornerShape(20.dp))
                        .background(BgCard)
                        .clickable { showUnlockDialog = true }
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        StatusIndicator(color = StatusColor.YELLOW, size = 8.dp)
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(
                            "PC 已锁屏 · 画面可直接点击，或点此输 PIN/密码解锁",
                            style = MaterialTheme.typography.bodySmall,
                            color = TextPrimary
                        )
                    }
                    pcUnlockError?.let { err ->
                        Spacer(modifier = Modifier.height(4.dp))
                        Text(
                            err,
                            style = MaterialTheme.typography.labelSmall,
                            color = Warning
                        )
                    }
                }
                if (showUnlockDialog) {
                    UnlockDialog(
                        onConfirm = { password ->
                            showUnlockDialog = false
                            viewModel.sendUnlock(password)
                        },
                        onDismiss = { showUnlockDialog = false }
                    )
                }
            }

            // 访问验证码：被控端开启验证保护时，先输入验证码验证通过才开始推流。
            // 取消 = 主动断开；错误可重试（被控端累计 3 次失败/超时会断开连接）。
            if (authRequired && state == RemoteSessionManager.SessionState.CONNECTED) {
                AuthCodeDialog(
                    error = authError,
                    onConfirm = { code -> viewModel.sendAuthCode(code) },
                    onCancel = { viewModel.disconnect() }
                )
            }

            // 隐藏键盘输入框：捕获软键盘文本（InputConnection 方案，正确处理中文输入法）与物理键盘按键
            AndroidView(
                modifier = Modifier.size(1.dp),
                factory = { ctx ->
                    RemoteEditText(ctx).apply {
                        onCommitText = { text -> sendTextToRemote(text) }
                        onBackspace = { sendKeyCombo(0x08) }
                        onImeKeyEvent = { event -> handleImeKeyEvent(event) }
                        // 物理键盘：直接转发按键事件
                        setOnKeyListener { _, keyCode, event ->
                            when (event.action) {
                                android.view.KeyEvent.ACTION_DOWN ->
                                    handleAndroidKeyDown(keyCode, event)
                                android.view.KeyEvent.ACTION_UP ->
                                    handleAndroidKeyUp(keyCode, event)
                            }
                            true
                        }
                        keyInput = this
                    }
                }
            )
        }
    }
}

/** 从 Context 向上查找宿主 Activity。 */
private fun Context.findActivity(): Activity? {
    var ctx = this
    while (ctx is ContextWrapper) {
        if (ctx is Activity) return ctx
        ctx = ctx.baseContext
    }
    return null
}

/** 远程画面上下预留边距（画面默认上下各留此宽度，可拖动避让悬浮球等遮挡）。 */
private val VReserve = 48.dp

/** 空白区双指轻点判定的最长持续时间（超时视为按住而非点击）。 */
private const val BLANK_TWO_FINGER_TAP_MS = 300L

/** 空白区指间距偏离起始超过此比例判定为捏合（空白区无画面，捏合不触发任何操作）。 */
private const val BLANK_ZOOM_RATIO = 0.12f

/**
 * 画面外空白区手势层（悬浮球收缩时由 RemoteSessionScreen 挂载）：
 * 铺满会话区但位于画面 View 之下，仅画面 View 布局边界之外的触摸会落入本层。
 * 手势映射对齐 Win11 精确式触摸板：
 *
 * - 单指轻点 = 左键点击；单指滑动 = 光标相对移动（速度设置页可调）
 * - 单指快速双击后按住拖动（双击拖动）= 左键按住拖动（快速第二次按下即发
 *   左键按下，滑动拖动、抬指释放；未拖动直接抬起则等价双击）
 * - 双指轻点 = 右键；双指同向滑动 = 滚轮（自然滚动：内容跟随手指——
 *   上滑滚动条下拉 / 下滑上拉 / 左滑右拉 / 右滑左拉）；
 *   像素级换算（手机像素/画面显示比例 = 远程像素，1cm 手指 = 1cm 画面内容
 *   位移），松手后带惯性（速度衰减阻尼，只多滑一小段）；
 *   指间距变化主导 = 捏合（空白区无画面，忽略不触发）
 * - 三指上滑 = 多任务视图（Win+Tab）、三指下滑 = 显示桌面（Win+D）、
 *   三指左右滑 = 切换应用（Alt+Tab 方向，连续滑动连续切换）、
 *   三指点按 = 搜索（Win+S）
 * - 四指左右滑 = 切换虚拟桌面（Ctrl+Win+方向，连续滑动连续切换）、
 *   四指点按 = 通知中心（Win+N）
 *
 * 「空白区触摸板」总开关关闭时仅保留基础手势（单指轻点左键/双指轻点右键/
 * 双指滚轮）；双击拖动、三指、四指手势另有独立开关（设置页，默认全开）。
 * 点击/滚轮坐标 = 当前虚拟光标位置（未使用过则取远程屏幕中心）。
 */
@Composable
private fun BlankAreaGestureLayer(
    cursorX: Float,
    cursorY: Float,
    remoteWidth: Int,
    remoteHeight: Int,
    viewScale: Float,
    touchpadEnabled: Boolean,
    cursorSpeed: Float,
    doubleTapDrag: Boolean,
    threeFinger: Boolean,
    fourFinger: Boolean,
    parentHpx: Int,
    onCursorMove: (x: Int, y: Int) -> Unit,
    onCursorChange: (x: Float, y: Float) -> Unit,
    onLeftDown: (x: Int, y: Int) -> Unit,
    onLeftUp: (x: Int, y: Int) -> Unit,
    onLeftClick: (x: Int, y: Int) -> Unit,
    onRightClick: (x: Int, y: Int) -> Unit,
    onWheel: (x: Int, y: Int, deltaV: Int, deltaH: Int) -> Unit,
    onGestureKey: (TouchpadGesture) -> Unit,
    modifier: Modifier = Modifier
) {
    // pointerInput 的 lambda 不随外部状态变化重启，经 rememberUpdatedState 读最新值
    val effX = if (cursorX >= 0) cursorX else if (remoteWidth > 0) remoteWidth / 2f else 0f
    val effY = if (cursorY >= 0) cursorY else if (remoteHeight > 0) remoteHeight / 2f else 0f
    val latestX by rememberUpdatedState(effX)
    val latestY by rememberUpdatedState(effY)
    val latestRemoteW by rememberUpdatedState(remoteWidth)
    val latestRemoteH by rememberUpdatedState(remoteHeight)
    val latestScale by rememberUpdatedState(viewScale.coerceAtLeast(0.05f))
    val latestTouchpad by rememberUpdatedState(touchpadEnabled)
    val latestSpeed by rememberUpdatedState(cursorSpeed)
    val latestDoubleTapDrag by rememberUpdatedState(doubleTapDrag)
    val latestThreeFinger by rememberUpdatedState(threeFinger)
    val latestFourFinger by rememberUpdatedState(fourFinger)
    val latestOnCursorMove by rememberUpdatedState(onCursorMove)
    val latestOnCursorChange by rememberUpdatedState(onCursorChange)
    val latestLeftDown by rememberUpdatedState(onLeftDown)
    val latestLeftUp by rememberUpdatedState(onLeftUp)
    val latestLeft by rememberUpdatedState(onLeftClick)
    val latestRight by rememberUpdatedState(onRightClick)
    val latestWheel by rememberUpdatedState(onWheel)
    val latestGestureKey by rememberUpdatedState(onGestureKey)
    val scope = rememberCoroutineScope()

    Box(
        modifier = modifier
            .fillMaxSize()
            .pointerInput(Unit) {
                val slop = 8.dp.toPx()
                val dblTapRange = 40.dp.toPx()
                val threeStepH = 48.dp.toPx()
                val fourStep = 64.dp.toPx()
                // 双指滚轮（像素换算 + 惯性，与画面内手势同款）：手机像素/画面
                // 显示比例 = 远程像素，1cm 手指 = 1cm 画面内容位移；
                // 累积器跨手势保留余量，惯性 Job 由新手势打断
                val wheelAccumV = WheelAccumulator { u ->
                    latestWheel(latestX.roundToInt(), latestY.roundToInt(), u, 0)
                }
                val wheelAccumH = WheelAccumulator { u ->
                    latestWheel(latestX.roundToInt(), latestY.roundToInt(), 0, u)
                }
                var flingJob: Job? = null
                // 跨手势状态：上次单指轻点的时间/位置（双击拖动判定锚点）
                var lastTapUpTime = 0L
                var lastTapPos = Offset.Zero
                awaitEachGesture {
                    val down = awaitFirstDown()
                    // 新手势打断惯性，重置滚轮余量
                    flingJob?.cancel()
                    flingJob = null
                    wheelAccumV.reset()
                    wheelAccumH.reset()
                    // 当前按下的指针（id → 最新位置）：Release 事件的 changes 只含
                    // 抬起的那根，其余仍按着的指针沿用之前的记录
                    val active = HashMap<PointerId, Offset>()
                    active[down.id] = down.position
                    var singleMoved = false
                    var everTwo = false
                    var tapEligible = false
                    var twoDownTime = 0L
                    var startSpan = 0f
                    var startCx = 0f
                    var startCy = 0f
                    var lastCx = 0f
                    var lastCy = 0f
                    var zoomed = false
                    var axis = 0
                    var totalX = 0f
                    var totalY = 0f
                    // 滚轮累计（远程像素）与速度采样（惯性初速）
                    var scrollCum = 0f
                    val scrollTracker = ScrollVelocityTracker()
                    var wheelFired = false
                    // 本手势已启动惯性（双指同时抬起的兜底判定用）
                    var flingStarted = false
                    var maxCount = 1
                    // 双击拖动：本手势死亡（拖动被多指打断后不再触发任何手势）
                    var gestureDead = false
                    // 三/四指手势状态
                    var threeInit = false
                    var threeSuppressed = false
                    var threeMode = 0          // 3=三指 4=四指（以第三指落下时计）
                    var threeDownTime = 0L
                    var threeLastCx = 0f
                    var threeLastCy = 0f
                    var threeAxis = 0
                    var threeTotalX = 0f
                    var threeTotalY = 0f
                    var threeAccumX = 0f
                    var threeVFired = false
                    var threeAnyFired = false

                    // 双击拖动判定：上次轻点后 300ms 内、位置接近 → 第二次按下
                    // 即发左键按下（拖动中光标跟手移动，抬指释放；
                    // 未移动直接抬起 = 双击的第二击，行为自然兼容）
                    var dragging = false
                    if (latestTouchpad && latestDoubleTapDrag &&
                        down.uptimeMillis - lastTapUpTime < BLANK_TWO_FINGER_TAP_MS &&
                        (down.position - lastTapPos).getDistance() < dblTapRange
                    ) {
                        dragging = true
                        latestLeftDown(latestX.roundToInt(), latestY.roundToInt())
                    }
                    // 本手势是否以双击拖动开场（决定全抬时不再补发轻点/锚点）
                    val wasDragging = dragging

                    // 触摸板光标相对移动：手势开始时光标取当前虚拟位置，
                    // 之后本地累积（不依赖 recompose 时序），基准增益与悬浮球
                    // 长按一致（屏幕一划 ≈ 1.8 屏宽）× 设置的光标速度
                    var padCurX = latestX
                    var padCurY = latestY
                    var lastSingle = down.position
                    fun moveCursorBy(dx: Float, dy: Float) {
                        if (latestRemoteW <= 0 || latestRemoteH <= 0) return
                        val gain = 1.8f * latestRemoteW / size.width.coerceAtLeast(1) *
                                latestSpeed
                        padCurX = (padCurX + dx * gain).coerceIn(0f, (latestRemoteW - 1).toFloat())
                        padCurY = (padCurY + dy * gain).coerceIn(0f, (latestRemoteH - 1).toFloat())
                        latestOnCursorChange(padCurX, padCurY)
                        latestOnCursorMove(padCurX.roundToInt(), padCurY.roundToInt())
                    }

                    var upTime = 0L
                    while (true) {
                        val event = awaitPointerEvent()
                        val wasCount = active.size
                        for (c in event.changes) {
                            if (c.pressed) active[c.id] = c.position
                            else active.remove(c.id)
                        }
                        val count = active.size
                        if (count == 0) {
                            upTime = event.changes.first().uptimeMillis
                            break
                        }
                        if (count > maxCount) maxCount = count

                        when {
                            // 三/四指落下（首次达到 3 指或 3 指升级 4 指未触发时）
                            count >= 3 && wasCount < 3 || (threeInit && threeMode == 3 &&
                                count >= 4 && !threeAnyFired) -> {
                                // 双击拖动被多指打断：立即释放左键，本手势死亡
                                if (dragging) {
                                    latestLeftUp(latestX.roundToInt(), latestY.roundToInt())
                                    dragging = false
                                    gestureDead = true
                                }
                                if (!threeInit) {
                                    threeInit = true
                                    // 此前已有双指滚动/捏合/单指拖动 → 抑制三/四指
                                    threeSuppressed = wheelFired || zoomed || singleMoved
                                    threeMode = if (count >= 4) 4 else 3
                                    threeDownTime = event.changes.first().uptimeMillis
                                    val pts = active.values.toList()
                                    threeLastCx = pts.map { it.x }.sum() / pts.size
                                    threeLastCy = pts.map { it.y }.sum() / pts.size
                                } else if (threeMode == 3 && count >= 4 && !threeAnyFired) {
                                    threeMode = 4
                                }
                            }
                            // 三/四指移动：轴锁定后触发对应手势
                            count >= 3 && threeInit && !threeSuppressed && !gestureDead -> {
                                if ((threeMode == 3 && !latestThreeFinger) ||
                                    (threeMode >= 4 && !latestFourFinger)
                                ) {
                                    threeSuppressed = true
                                } else {
                                    val pts = active.values.toList()
                                    val cx = pts.map { it.x }.sum() / pts.size
                                    val cy = pts.map { it.y }.sum() / pts.size
                                    val dx = cx - threeLastCx
                                    val dy = cy - threeLastCy
                                    threeLastCx = cx
                                    threeLastCy = cy
                                    if (threeAxis == 0) {
                                        threeTotalX += dx
                                        threeTotalY += dy
                                        if (abs(threeTotalY) > slop) threeAxis = 1
                                        else if (abs(threeTotalX) > slop) threeAxis = 2
                                    }
                                    if (threeAxis == 1 && !threeVFired) {
                                        // 垂直一次性手势：y 向下正，上滑 totalY<0
                                        threeVFired = true
                                        threeAnyFired = true
                                        latestGestureKey(
                                            when {
                                                threeMode == 3 && threeTotalY < 0 ->
                                                    TouchpadGesture.TASK_VIEW
                                                threeMode == 3 -> TouchpadGesture.SHOW_DESKTOP
                                                threeTotalY < 0 -> TouchpadGesture.DESKTOP_PREV
                                                else -> TouchpadGesture.DESKTOP_NEXT
                                            }
                                        )
                                    } else if (threeAxis == 2) {
                                        // 水平连续手势：每滑一步触发一次
                                        val step = if (threeMode == 3) threeStepH else fourStep
                                        threeAccumX += dx
                                        while (abs(threeAccumX) >= step) {
                                            threeAnyFired = true
                                            latestGestureKey(
                                                if (threeAccumX > 0)
                                                    (if (threeMode == 3) TouchpadGesture.APP_NEXT
                                                    else TouchpadGesture.DESKTOP_NEXT)
                                                else (if (threeMode == 3) TouchpadGesture.APP_PREV
                                                else TouchpadGesture.DESKTOP_PREV)
                                            )
                                            threeAccumX -= if (threeAccumX > 0) step else -step
                                        }
                                    }
                                }
                            }
                            // 落入双指：初始化双指手势状态（首指未拖动才有轻点资格）
                            count == 2 && wasCount == 1 && maxCount < 3 -> {
                                everTwo = true
                                tapEligible = !singleMoved
                                twoDownTime = event.changes.first().uptimeMillis
                                val pts = active.values.toList()
                                startSpan = (pts[0] - pts[1]).getDistance()
                                startCx = (pts[0].x + pts[1].x) / 2f
                                startCy = (pts[0].y + pts[1].y) / 2f
                                lastCx = startCx
                                lastCy = startCy
                                zoomed = false
                                axis = 0
                                totalX = 0f
                                totalY = 0f
                                scrollCum = 0f
                                scrollTracker.reset()
                                wheelFired = false
                            }
                            // 双指抬起到单指：双指轻点 = 右键（无捏合、未滚动、
                            // 未锁定主轴且持续足够短）
                            count == 1 && wasCount == 2 && everTwo && maxCount < 3 &&
                                !gestureDead -> {
                                if (tapEligible && !zoomed && !wheelFired && axis == 0 &&
                                    event.changes.first().uptimeMillis - twoDownTime <
                                        BLANK_TWO_FINGER_TAP_MS
                                ) {
                                    latestRight(latestX.roundToInt(), latestY.roundToInt())
                                }
                                // 双指滚动结束 → 惯性：取最近 100ms 位移差商为初速，
                                // 指数衰减继续滚动（阻尼感，只多滑一小段），
                                // 再次触摸即由新手势打断
                                if (!zoomed && axis != 0) {
                                    val v = scrollTracker.velocity()
                                    if (abs(v) > PixelWheel.FLING_MIN_START) {
                                        flingStarted = true
                                        val flingAxisF = axis
                                        flingJob?.cancel()
                                        flingJob = scope.launch {
                                            var vel = v
                                            val acc =
                                                if (flingAxisF == 1) wheelAccumV else wheelAccumH
                                            while (abs(vel) > PixelWheel.FLING_STOP) {
                                                delay(16)
                                                val dt = 16f
                                                acc.scrollRemoteBy(vel * dt)
                                                vel *= exp(-dt / PixelWheel.FLING_TAU_MS)
                                            }
                                        }
                                    }
                                }
                            }
                            // 双指移动：中心位移 = 滚轮（捏合判定与画面内手势同款）
                            count == 2 && everTwo && maxCount < 3 && !gestureDead -> {
                                val pts = active.values.toList()
                                val cx = (pts[0].x + pts[1].x) / 2f
                                val cy = (pts[0].y + pts[1].y) / 2f
                                val span = (pts[0] - pts[1]).getDistance()
                                val dx = cx - lastCx
                                val dy = cy - lastCy
                                if (!zoomed && !wheelFired && axis == 0 && startSpan > 0f) {
                                    val spanChange = abs(span - startSpan)
                                    val centerDist =
                                        Offset(cx - startCx, cy - startCy).getDistance()
                                    if (spanChange > startSpan * BLANK_ZOOM_RATIO &&
                                        spanChange > centerDist
                                    ) {
                                        zoomed = true
                                    }
                                }
                                if (!zoomed) {
                                    if (axis == 0) {
                                        totalX += dx
                                        totalY += dy
                                        if (abs(totalY) > slop) axis = 1
                                        else if (abs(totalX) > slop) axis = 2
                                    }
                                    if (axis == 1) {
                                        // 像素换算：手机像素/画面显示比例 = 远程像素，
                                        // 1cm 手指 = 1cm 画面内容位移；自然滚动方向不变
                                        // （手指上移 → deltaV 负 = 滚动条下拉看下方）
                                        val remotePx = dy / latestScale
                                        scrollCum += remotePx
                                        scrollTracker.add(
                                            event.changes.first().uptimeMillis, scrollCum
                                        )
                                        if (wheelAccumV.scrollRemoteBy(remotePx)) {
                                            wheelFired = true
                                        }
                                    } else if (axis == 2) {
                                        // 自然滚动：手指左移 → deltaH 正 = 滚动条右拉
                                        val remotePx = -dx / latestScale
                                        scrollCum += remotePx
                                        scrollTracker.add(
                                            event.changes.first().uptimeMillis, scrollCum
                                        )
                                        if (wheelAccumH.scrollRemoteBy(remotePx)) {
                                            wheelFired = true
                                        }
                                    }
                                }
                                lastCx = cx
                                lastCy = cy
                            }
                            // 单指移动：双击拖动中 = 拖动；触摸板开 = 光标相对移动
                            // （滑动后不算轻点，也失去双指轻点资格）
                            count == 1 && !everTwo -> {
                                val p = active.values.first()
                                if (!singleMoved &&
                                    (p - down.position).getDistance() > slop
                                ) {
                                    singleMoved = true
                                }
                                if (dragging || latestTouchpad) {
                                    moveCursorBy(p.x - lastSingle.x, p.y - lastSingle.y)
                                }
                                lastSingle = p
                            }
                        }
                    }
                    // 全部抬起：先释放可能挂着的左键（双击拖动）
                    if (dragging) {
                        latestLeftUp(latestX.roundToInt(), latestY.roundToInt())
                        dragging = false
                    }
                    // 兜底：两指同一事件批次抬起（未经历 2→1）时的惯性
                    if (!flingStarted && everTwo && maxCount == 2 && axis != 0 &&
                        !zoomed && !gestureDead
                    ) {
                        val v = scrollTracker.velocity()
                        if (abs(v) > PixelWheel.FLING_MIN_START) {
                            val flingAxisF = axis
                            flingJob?.cancel()
                            flingJob = scope.launch {
                                var vel = v
                                val acc = if (flingAxisF == 1) wheelAccumV else wheelAccumH
                                while (abs(vel) > PixelWheel.FLING_STOP) {
                                    delay(16)
                                    val dt = 16f
                                    acc.scrollRemoteBy(vel * dt)
                                    vel *= exp(-dt / PixelWheel.FLING_TAU_MS)
                                }
                            }
                        }
                    }
                    when {
                        // 三/四指点按：未触发滑动、无抑制、位移小、持续短
                        threeInit && !threeSuppressed && !threeAnyFired && !gestureDead &&
                            threeTotalX * threeTotalX + threeTotalY * threeTotalY < slop * slop &&
                            upTime - threeDownTime < BLANK_TWO_FINGER_TAP_MS -> {
                            if (threeMode == 3 && latestThreeFinger) {
                                latestGestureKey(TouchpadGesture.SEARCH)
                            } else if (threeMode >= 4 && latestFourFinger) {
                                latestGestureKey(TouchpadGesture.NOTIFICATIONS)
                            }
                        }
                        // 单指轻点 = 左键（未拖动、无双指介入），记录双击拖动锚点
                        // （双击拖动开场的手势不补发：其 down/up 已等价第二击）
                        maxCount == 1 && !singleMoved && !wasDragging -> {
                            latestLeft(latestX.roundToInt(), latestY.roundToInt())
                            lastTapUpTime = upTime
                            lastTapPos = down.position
                        }
                    }
                }
            }
    ) {
        // 底部触摸板水印提示（不改变区域样式，画面拖过来会被自然遮挡）：
        // 位于底部 1/6 屏高处 ≈ 底部空白（至少 1/3 屏高）的中央
        if (touchpadEnabled) {
            val density = LocalDensity.current
            val bottomPad = with(density) { (parentHpx / 6f).toDp() }
            Column(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(bottom = bottomPad),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    "触摸板 · 单指滑动移光标 / 轻点左键 / 双击后拖动 = 按住拖动",
                    style = MaterialTheme.typography.labelSmall,
                    color = TextMuted.copy(alpha = 0.55f),
                    textAlign = TextAlign.Center
                )
                Text(
                    "双指轻点右键 / 滑动滚动 · 三指切换应用与桌面 · 四指切虚拟桌面",
                    style = MaterialTheme.typography.labelSmall,
                    color = TextMuted.copy(alpha = 0.45f),
                    textAlign = TextAlign.Center
                )
            }
        }
    }
}

/**
 * 虚拟鼠标光标叠加层（触摸板模式）：经典箭头（白填充黑描边），热点在尖端。
 *
 * PC 端 DXGI 桌面复制捕获的画面纹理不包含鼠标指针，触摸板相对移动必须在
 * 客户端自行绘制光标才知道当前位置（向日葵同款方案）。注入用坐标即本光标
 * 位置，显示与注入天然自洽。
 *
 * 坐标换算：远程坐标 → View 本地坐标（View 与远程画面等比布局）→ 屏幕坐标。
 * View 由 Compose 居中布局 layout=(parent-view)/2，View 变换链为
 * translation(pan) + scale(pivot=0)，故 屏幕坐标 = layout + pan + scale*本地坐标。
 */
@Composable
private fun VirtualCursorOverlay(
    remoteX: Float,
    remoteY: Float,
    remoteW: Float,
    remoteH: Float,
    viewW: Float,
    viewH: Float,
    scale: Float,
    panX: Float,
    panY: Float,
    modifier: Modifier = Modifier
) {
    Canvas(modifier = modifier.fillMaxSize()) {
        if (remoteW <= 0f || remoteH <= 0f || viewW <= 0f || viewH <= 0f) return@Canvas
        val layoutLeft = (size.width - viewW) / 2f
        val layoutTop = (size.height - viewH) / 2f
        val sx = layoutLeft + panX + scale * (remoteX / remoteW * viewW)
        val sy = layoutTop + panY + scale * (remoteY / remoteH * viewH)

        // 光标尺寸固定（不随画面缩放，始终清晰可见）；基准形状高 17px，放大 1.8 倍 ≈ 30px
        val arrow = Path().apply {
            moveTo(0f, 0f)
            lineTo(0f, 14.44f)
            lineTo(3.36f, 11.5f)
            lineTo(5.89f, 17f)
            lineTo(8.5f, 15.87f)
            lineTo(5.97f, 10.5f)
            lineTo(10.42f, 10.5f)
            close()
        }
        translate(left = sx, top = sy) {
            scale(scale = 1.8f, pivot = Offset.Zero) {
                drawPath(arrow, Color.White)
                drawPath(arrow, Color.Black, style = Stroke(width = 0.9f))
            }
        }
    }
}

/** 发送鼠标事件帧：[action 1B][x 2B][y 2B]（远程坐标）。 */
private fun sendMouseAction(
    viewModel: SessionViewModel,
    action: Int,
    x: Int,
    y: Int
) {
    val data = ByteBuffer.allocate(5).order(ByteOrder.LITTLE_ENDIAN)
        .put(action.toByte())
        .putShort(x.toShort())
        .putShort(y.toShort())
        .array()
    viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_MOUSE, data)
}

/** 发送滚轮事件帧：[deltaV 2B(有符号)][x 2B][y 2B][deltaH 2B(有符号)]（远程坐标；deltaH 为水平滚动，旧 PC 端忽略）。 */
private fun sendWheel(
    viewModel: SessionViewModel,
    x: Int,
    y: Int,
    deltaV: Int,
    deltaH: Int
) {
    val data = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN)
        .putShort(deltaV.toShort())
        .putShort(x.toShort())
        .putShort(y.toShort())
        .putShort(deltaH.toShort())
        .array()
    viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_WHEEL, data)
}

/** 发送按键事件帧：[vkCode 2B(有符号)][down 1B]（Windows 虚拟键码）。 */
private fun sendKey(
    viewModel: SessionViewModel,
    vk: Int,
    down: Boolean
) {
    val data = ByteBuffer.allocate(3).order(ByteOrder.LITTLE_ENDIAN)
        .putShort(vk.toShort())
        .put(if (down) 1.toByte() else 0.toByte())
        .array()
    viewModel.sendInput(RemoteFrameProtocol.TYPE_INPUT_KEY, data)
}

// Android 13+ 通知权限申请码（会话保活前台服务的通知需要）
private const val NOTIFICATION_PERMISSION_REQUEST = 8801

// Win11 触摸板手势 → Windows 组合键（VK 码）
private const val VK_TAB = 0x09
private const val VK_SHIFT = 0x10
private const val VK_CONTROL = 0x11
private const val VK_MENU = 0x12          // Alt
private const val VK_LWIN = 0x5B
private const val VK_LEFT = 0x25
private const val VK_RIGHT = 0x27
private const val VK_D = 0x44
private const val VK_N = 0x4E
private const val VK_S = 0x53

/** 触摸板三/四指手势语义（Win11 精确式触摸板默认映射）。 */
enum class TouchpadGesture {
    /** 三指上滑：多任务视图（Win+Tab）。 */
    TASK_VIEW,
    /** 三指下滑：显示桌面（Win+D）。 */
    SHOW_DESKTOP,
    /** 三指右滑：切换到下一个应用（Alt+Tab）。 */
    APP_NEXT,
    /** 三指左滑：切换到上一个应用（Shift+Alt+Tab）。 */
    APP_PREV,
    /** 三指点按：搜索（Win+S）。 */
    SEARCH,
    /** 四指右滑/下滑：下一个虚拟桌面（Ctrl+Win+Right）。 */
    DESKTOP_NEXT,
    /** 四指左滑/上滑：上一个虚拟桌面（Ctrl+Win+Left）。 */
    DESKTOP_PREV,
    /** 四指点按：通知中心（Win+N）。 */
    NOTIFICATIONS
}

/** 发送完整组合键序列（按下→抬起，修饰键包裹）。 */
private fun sendGestureKeys(viewModel: SessionViewModel, gesture: TouchpadGesture) {
    when (gesture) {
        TouchpadGesture.TASK_VIEW -> {
            sendKey(viewModel, VK_LWIN, true); sendKey(viewModel, VK_TAB, true)
            sendKey(viewModel, VK_TAB, false); sendKey(viewModel, VK_LWIN, false)
        }
        TouchpadGesture.SHOW_DESKTOP -> {
            sendKey(viewModel, VK_LWIN, true); sendKey(viewModel, VK_D, true)
            sendKey(viewModel, VK_D, false); sendKey(viewModel, VK_LWIN, false)
        }
        TouchpadGesture.APP_NEXT -> {
            sendKey(viewModel, VK_MENU, true); sendKey(viewModel, VK_TAB, true)
            sendKey(viewModel, VK_TAB, false); sendKey(viewModel, VK_MENU, false)
        }
        TouchpadGesture.APP_PREV -> {
            sendKey(viewModel, VK_MENU, true); sendKey(viewModel, VK_SHIFT, true)
            sendKey(viewModel, VK_TAB, true); sendKey(viewModel, VK_TAB, false)
            sendKey(viewModel, VK_SHIFT, false); sendKey(viewModel, VK_MENU, false)
        }
        TouchpadGesture.SEARCH -> {
            sendKey(viewModel, VK_LWIN, true); sendKey(viewModel, VK_S, true)
            sendKey(viewModel, VK_S, false); sendKey(viewModel, VK_LWIN, false)
        }
        TouchpadGesture.DESKTOP_NEXT -> {
            sendKey(viewModel, VK_CONTROL, true); sendKey(viewModel, VK_LWIN, true)
            sendKey(viewModel, VK_RIGHT, true); sendKey(viewModel, VK_RIGHT, false)
            sendKey(viewModel, VK_LWIN, false); sendKey(viewModel, VK_CONTROL, false)
        }
        TouchpadGesture.DESKTOP_PREV -> {
            sendKey(viewModel, VK_CONTROL, true); sendKey(viewModel, VK_LWIN, true)
            sendKey(viewModel, VK_LEFT, true); sendKey(viewModel, VK_LEFT, false)
            sendKey(viewModel, VK_LWIN, false); sendKey(viewModel, VK_CONTROL, false)
        }
        TouchpadGesture.NOTIFICATIONS -> {
            sendKey(viewModel, VK_LWIN, true); sendKey(viewModel, VK_N, true)
            sendKey(viewModel, VK_N, false); sendKey(viewModel, VK_LWIN, false)
        }
    }
}

/** 底部修饰键三态与「消耗单击态」规则已抽到 `data/models/ModifierState.kt`（纯逻辑，有单测）。 */

/** 画质档位定义与会话内/设置页共用的 4 档预设见 `data/models/Models.kt`（QUALITY_PRESETS）。 */

/**
 * 会话底部工具栏按钮。
 *
 * ⚠️ 尺寸一致性：整列宽度由调用方用 `Modifier.weight(1f)` 均分，
 * 内部**不能**再让内容决定宽度 —— 否则「快捷键」（3 字）会比「键盘」「旋转」（2 字）
 * 明显更宽，一排按钮看起来就是「大小不一致」。
 * 同理不再用 Material3 的 IconButton：它带 48dp 最小触控尺寸，
 * 会把这一行顶得过高（实测 92dp），而这里的图标本身已是可点区域。
 */
@Composable
private fun ToolBarButton(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    active: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
        modifier = modifier
            .clip(RoundedCornerShape(8.dp))
            .background(if (active) BgCard else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(vertical = 5.dp)
    ) {
        Icon(
            icon,
            contentDescription = label,
            tint = if (active) Success else TextPrimary,
            modifier = Modifier.size(22.dp)
        )
        Spacer(modifier = Modifier.height(2.dp))
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            color = if (active) Success else TextMuted,
            maxLines = 1,
            softWrap = false
        )
    }
}

/** 全屏模式悬浮工具按钮（圆形）。 */
@Composable
private fun FloatingToolButton(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    contentDescription: String,
    active: Boolean,
    onClick: () -> Unit
) {
    Box(
        modifier = Modifier
            .size(44.dp)
            .clip(RoundedCornerShape(22.dp))
            .background(if (active) Accent else BgCard.copy(alpha = 0.85f))
            .clickable { onClick() },
        contentAlignment = Alignment.Center
    ) {
        Icon(
            icon,
            contentDescription = contentDescription,
            tint = if (active) Color.White else TextPrimary,
            modifier = Modifier.size(20.dp)
        )
    }
}

/** 快捷键面板：粘滞修饰键 + F1-F12 + 常用编辑/方向键。 */
@Composable
private fun HotkeyPanel(
    stickyMods: Set<Int>,
    onToggleMod: (Int) -> Unit,
    onKey: (Int) -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .background(BgCard.copy(alpha = 0.95f))
            .padding(horizontal = 6.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        // 粘滞修饰键：点击激活（高亮保持），随普通按键组合发送，再次点击取消
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf(
                0x11 to "Ctrl",
                0x10 to "Shift",
                0x12 to "Alt",
                0x5B to "Win"
            ).forEach { (vk, label) ->
                HotkeyButton(
                    label = label,
                    active = vk in stickyMods,
                    modifier = Modifier.weight(1f),
                    onClick = { onToggleMod(vk) }
                )
            }
        }
        // 功能键 F1-F12（两行）
        (0x70..0x7B).mapIndexed { i, vk -> vk to "F${i + 1}" }
            .chunked(6)
            .forEach { row ->
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    row.forEach { (vk, label) ->
                        HotkeyButton(
                            label = label,
                            active = false,
                            modifier = Modifier.weight(1f),
                            onClick = { onKey(vk) }
                        )
                    }
                }
            }
        // 常用编辑键
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf(
                0x1B to "Esc", 0x09 to "Tab", 0x14 to "CapsLk", 0x2D to "Ins",
                0x2E to "Del", 0x08 to "Bksp", 0x0D to "Enter"
            ).forEach { (vk, label) ->
                HotkeyButton(
                    label = label,
                    active = false,
                    modifier = Modifier.weight(1f),
                    onClick = { onKey(vk) }
                )
            }
        }
        // 方向与导航键
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf(
                0x24 to "Home", 0x23 to "End", 0x21 to "PgUp", 0x22 to "PgDn",
                0x25 to "←", 0x26 to "↑", 0x27 to "→", 0x28 to "↓"
            ).forEach { (vk, label) ->
                HotkeyButton(
                    label = label,
                    active = false,
                    modifier = Modifier.weight(1f),
                    onClick = { onKey(vk) }
                )
            }
        }
    }
}

/** 快捷键面板单个按键（修饰键激活时高亮）。 */
@Composable
private fun HotkeyButton(
    label: String,
    active: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(6.dp))
            .background(if (active) Accent else BgHover)
            .clickable { onClick() }
            .padding(vertical = 10.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = if (active) Color.White else TextPrimary,
            maxLines = 1
        )
    }
}

/** 远程解锁对话框：输入 Windows 登录 PIN 或密码，发送到 PC 端锁屏桌面注入解锁。 */
@Composable
private fun UnlockDialog(
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit
) {
    var password by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text("远程解锁", style = MaterialTheme.typography.titleMedium, color = TextPrimary)
        },
        text = {
            Column {
                Text(
                    "输入 PC 锁屏界面要求的登录凭证（PIN 或密码——即平时在 PC 上输入的那个），将在锁屏界面自动输入并解锁。",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextSecondary
                )
                Spacer(modifier = Modifier.height(12.dp))
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    visualTransformation = PasswordVisualTransformation(),
                    singleLine = true,
                    placeholder = { Text("登录 PIN 或密码", color = TextSecondary) },
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { if (password.isNotEmpty()) onConfirm(password) },
                enabled = password.isNotEmpty()
            ) {
                Text("解锁", color = if (password.isNotEmpty()) Accent else TextSecondary)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("取消", color = TextSecondary)
            }
        },
        containerColor = BgCard
    )
}

/** 访问验证码对话框：被控端开启验证保护时输入 6 位数字验证码，验证通过才建立远程会话。 */
@Composable
private fun AuthCodeDialog(
    error: String?,
    onConfirm: (String) -> Unit,
    onCancel: () -> Unit
) {
    var code by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onCancel,
        title = {
            Text("访问验证", style = MaterialTheme.typography.titleMedium, color = TextPrimary)
        },
        text = {
            Column {
                Text(
                    "对方开启了访问验证码保护，输入 6 位数字验证码后开始远程。" +
                        "连续 3 次错误或长时间未验证将断开连接。",
                    style = MaterialTheme.typography.bodySmall,
                    color = TextSecondary
                )
                if (error != null) {
                    Spacer(modifier = Modifier.height(6.dp))
                    Text(error, style = MaterialTheme.typography.labelSmall, color = Warning)
                }
                Spacer(modifier = Modifier.height(12.dp))
                OutlinedTextField(
                    value = code,
                    onValueChange = { code = it.filter(Char::isDigit).take(6) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    placeholder = { Text("6 位数字验证码", color = TextSecondary) },
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { if (code.length == 6) onConfirm(code) },
                enabled = code.length == 6
            ) {
                Text("验证", color = if (code.length == 6) Accent else TextSecondary)
            }
        },
        dismissButton = {
            TextButton(onClick = onCancel) {
                Text("取消", color = TextSecondary)
            }
        },
        containerColor = BgCard
    )
}

@Composable
private fun SessionOverlay(
    state: RemoteSessionManager.SessionState,
    tunnel: com.quickremote.app.data.models.TunnelResponse?,
    errorMessage: String,
    onReconnect: () -> Unit = {},
    onViewLogs: () -> Unit = {}
) {
    val (color, text) = when (state) {
        RemoteSessionManager.SessionState.CONNECTING -> StatusColor.YELLOW to "连接中…"
        RemoteSessionManager.SessionState.CONNECTED -> StatusColor.GREEN to "隧道已建立"
        RemoteSessionManager.SessionState.FAILED -> StatusColor.RED to "连接失败"
        RemoteSessionManager.SessionState.DISCONNECTED -> StatusColor.RED to "已断开"
        RemoteSessionManager.SessionState.IDLE -> StatusColor.YELLOW to "等待中…"
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        if (state == RemoteSessionManager.SessionState.CONNECTING) {
            CircularProgressIndicator(modifier = Modifier.size(36.dp), strokeWidth = 3.dp, color = TextPrimary)
            Spacer(modifier = Modifier.height(16.dp))
        } else {
            StatusIndicator(color = color, size = 12.dp)
            Spacer(modifier = Modifier.height(12.dp))
        }
        Text(text, style = MaterialTheme.typography.titleSmall, color = TextPrimary, fontWeight = FontWeight.Medium)

        if (state == RemoteSessionManager.SessionState.FAILED && errorMessage.isNotBlank()) {
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                errorMessage,
                style = MaterialTheme.typography.bodySmall,
                color = Warning
            )
        }

        // 断连/失败时提供重连 + 查看日志入口
        if (state == RemoteSessionManager.SessionState.FAILED ||
            state == RemoteSessionManager.SessionState.DISCONNECTED
        ) {
            Spacer(modifier = Modifier.height(20.dp))
            Button(onClick = onReconnect) {
                Icon(Icons.Filled.Refresh, contentDescription = null, tint = TextPrimary)
                Spacer(modifier = Modifier.width(6.dp))
                Text("重新连接", color = TextPrimary)
            }
            Spacer(modifier = Modifier.height(10.dp))
            Text(
                "查看日志",
                style = MaterialTheme.typography.labelMedium,
                color = Accent,
                fontWeight = FontWeight.Medium,
                modifier = Modifier
                    .clickable(onClick = onViewLogs)
                    .padding(vertical = 4.dp)
            )
        }

        tunnel?.let { t ->
            Spacer(modifier = Modifier.height(20.dp))
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(BgCard)
                    .padding(16.dp)
            ) {
                Text("隧道信息", style = MaterialTheme.typography.labelMedium, color = TextMuted)
                Spacer(modifier = Modifier.height(8.dp))
                InfoRow("会话 ID", t.session_id)
                InfoRow("隧道主机", t.tunnel_host.ifBlank { "-" })
                InfoRow("隧道端口", if (t.tunnel_port > 0) t.tunnel_port.toString() else "待分配")
            }
        }
    }
}

/**
 * 断线自动重连浮层。
 *
 * 设计要点：
 * - 半透明遮罩，不全屏遮挡 —— 底下的 RemoteDisplayView 保留最后一帧画面，
 *   用户看到断线前的画面而不是黑屏（fail() 不清空 surface，这是刻意保留的行为）
 * - 倒计时按绝对时间戳计算，nextRetryAtMs 变化（如点了"立即重试"）时自动重启计时
 * - 次数耗尽后 ViewModel 会清除重连状态，本浮层消失并回落 SessionOverlay 的错误详情
 */
@Composable
private fun ReconnectOverlay(
    state: SessionViewModel.ReconnectState,
    onRetryNow: () -> Unit,
    onCancel: () -> Unit
) {
    // 倒计时：200ms 粒度刷新，到点即停（到点后由 ViewModel 驱动状态流转）
    var nowMs by remember(state.nextRetryAtMs) { mutableStateOf(System.currentTimeMillis()) }
    LaunchedEffect(state.nextRetryAtMs) {
        while (System.currentTimeMillis() < state.nextRetryAtMs) {
            nowMs = System.currentTimeMillis()
            delay(200)
        }
        nowMs = state.nextRetryAtMs
    }
    val remainSec = ((state.nextRetryAtMs - nowMs).coerceAtLeast(0L) + 999L) / 1000L

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.55f)),
        contentAlignment = Alignment.Center
    ) {
        Column(
            modifier = Modifier
                .padding(horizontal = 32.dp)
                .clip(RoundedCornerShape(14.dp))
                .background(BgCard)
                .padding(horizontal = 24.dp, vertical = 20.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(
                    modifier = Modifier.size(18.dp),
                    strokeWidth = 2.dp,
                    color = Accent
                )
                Spacer(modifier = Modifier.width(10.dp))
                Text(
                    "连接中断，正在重连…",
                    style = MaterialTheme.typography.titleSmall,
                    color = TextPrimary,
                    fontWeight = FontWeight.Medium
                )
            }

            Spacer(modifier = Modifier.height(10.dp))
            Text(
                if (remainSec > 0) {
                    "第 ${state.displayAttempt}/${ReconnectPolicy.MAX_ATTEMPTS} 次重试 · ${remainSec} 秒后自动重连"
                } else {
                    "第 ${state.displayAttempt}/${ReconnectPolicy.MAX_ATTEMPTS} 次重试 · 正在尝试连接…"
                },
                style = MaterialTheme.typography.bodySmall,
                color = TextSecondary,
                textAlign = TextAlign.Center
            )

            Spacer(modifier = Modifier.height(16.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Button(onClick = onRetryNow) {
                    Icon(Icons.Filled.Refresh, contentDescription = null, tint = TextPrimary)
                    Spacer(modifier = Modifier.width(6.dp))
                    Text("立即重试", color = TextPrimary)
                }
                Spacer(modifier = Modifier.width(12.dp))
                Text(
                    "取消",
                    style = MaterialTheme.typography.labelLarge,
                    color = TextMuted,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable(onClick = onCancel)
                        .padding(horizontal = 12.dp, vertical = 8.dp)
                )
            }
        }
    }
}

@Composable
private fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 3.dp),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, style = MaterialTheme.typography.bodySmall, color = TextMuted)
        Spacer(modifier = Modifier.weight(1f))
        Text(
            value,
            style = MaterialTheme.typography.bodySmall,
            color = TextPrimary,
            fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace
        )
    }
}
