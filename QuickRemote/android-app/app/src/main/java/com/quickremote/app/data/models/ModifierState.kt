package com.quickremote.app.data.models

/**
 * 底部修饰键（Shift / Ctrl）的三态。
 *
 * - [NONE]：未激活。
 * - [ONESHOT]：单击 —— 修饰「下一次输入动作」，动作发生后自动失效。
 * - [LOCKED]：长按锁定 —— 持续修饰后续所有输入，再次点击该键才解除。
 *
 * ⚠️ 这里的「按下」是**逻辑上**的：LOCKED 并不代表 PC 端物理 Shift 一直按住不放。
 * 实际注入时每次都在一个组合内部完成「修饰键按下 → 目标键按下/抬起 → 修饰键抬起」，
 * 组合之间修饰键是松开的（见 `sendKeyCombo`）。
 */
enum class ModKeyState { NONE, ONESHOT, LOCKED }

/** 一次输入动作发生后，本修饰键应迁移到的状态：单击态被消耗，长按态保持。 */
fun ModKeyState.afterInputAction(): ModKeyState =
    if (this == ModKeyState.ONESHOT) ModKeyState.NONE else this

/**
 * 消耗一组修饰键中的「单击态」，返回新的映射。
 *
 * 抽出来的原因：这条规则原先在 `RemoteSessionScreen` 里被内联抄了两份
 * （`sendKeyCombo` 与 `sendTextToRemote`），新增「中英」按键后是第三处调用。
 *
 * 注意语义边界：**只要发生了一次输入动作就消耗单击态**，哪怕这次动作没有真的用到
 * 该修饰键（例如发的是 Unicode 文本帧，或发的是裸按 Shift 切输入法）——
 * 否则残留的单击态会以「下一次毫不相关的输入被莫名加上 Shift」的形式暴露给用户。
 *
 * 无变化时返回原实例，避免 Compose 状态被赋等值新实例而触发无谓重组。
 */
fun Map<Int, ModKeyState>.consumeOneShot(): Map<Int, ModKeyState> {
    if (values.none { it == ModKeyState.ONESHOT }) return this
    return mapValues { (_, v) -> v.afterInputAction() }
}

/** 一次按键注入动作（vk 为 Windows 虚拟键码，down 为按下/抬起）。 */
data class KeyAction(val vk: Int, val down: Boolean)

/**
 * 远程 PC 输入法的中/英文切换。
 *
 * 原理：Windows 上「微软拼音」「搜狗拼音」「QQ 拼音」等默认都把**单击 Shift**绑定为
 * 中/英切换，而输入法判定「单击」的条件是 Shift 的按下与抬起**干净相邻**——
 * 中间只要夹了任何其他按键，输入法就会认定用户想按 Shift+某键，取消这次切换判定。
 *
 * 这正是老实现做不到该功能的原因：底部 Shift 按钮走的是「修饰键」语义，
 * 只会把 Shift 包在下一个按键外面送出（Shift↓ 键↓ 键↑ Shift↑），
 * 永远产生不了输入法要的那个孤立按下/抬起。
 *
 * 因此这里单列一个动作：**裸按 Shift**，与修饰键语义彻底解耦。
 * [sequence] 返回的序列必须逐帧按序发送，中间不得插入任何其他按键。
 */
object ImeToggle {
    /** 通用 VK_SHIFT。与 [KeyMapper] / 快捷键面板使用的键码保持一致。 */
    const val VK_SHIFT = 0x10

    /** 中/英切换的注入序列：只用 Shift 按下 + 抬起两个动作。 */
    fun sequence(): List<KeyAction> = listOf(
        KeyAction(VK_SHIFT, true),
        KeyAction(VK_SHIFT, false)
    )
}
