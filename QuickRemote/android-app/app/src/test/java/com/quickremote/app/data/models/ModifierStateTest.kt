package com.quickremote.app.data.models

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * 底部修饰键状态机（v1.0.76 抽出为纯逻辑以便测试）。
 *
 * 背景：Shift/Ctrl 三态（NONE 无 / ONESHOT 单击 / LOCKED 长按）原先的状态迁移与
 * 「单击态在输入动作后被消耗」规则内联在 RemoteSessionScreen 里，且被抄了两份
 * （sendKeyCombo 与 sendTextToRemote）。新增「中英」按键后出现第三处调用，
 * 故抽出集中实现，并用测试锁住语义。
 */
class ModifierStateTest {

    @Test
    fun `单击态在下一次输入动作后被消耗为无`() {
        assertEquals(ModKeyState.NONE, ModKeyState.ONESHOT.afterInputAction())
    }

    @Test
    fun `长按锁定态不受输入动作影响`() {
        assertEquals(ModKeyState.LOCKED, ModKeyState.LOCKED.afterInputAction())
    }

    @Test
    fun `无态保持无`() {
        assertEquals(ModKeyState.NONE, ModKeyState.NONE.afterInputAction())
    }

    @Test
    fun `消耗时只清单击态保留长按态`() {
        val before = mapOf(0x11 to ModKeyState.ONESHOT, 0x10 to ModKeyState.LOCKED)
        val after = before.consumeOneShot()
        assertEquals(ModKeyState.NONE, after[0x11])
        assertEquals(ModKeyState.LOCKED, after[0x10])
    }

    @Test
    fun `全部为无态时不产生变化`() {
        val before = mapOf(0x11 to ModKeyState.NONE, 0x10 to ModKeyState.NONE)
        assertEquals(before, before.consumeOneShot())
    }

    @Test
    fun `中英切换序列为裸按 Shift 且中间不夹任何其他键`() {
        // 这是本功能的关键不变量：PC 端输入法只有在 Shift 按下与抬起「干净相邻」时
        // 才判定为「单击 Shift 切中英」；一旦中间夹了别的键，输入法会取消这次判定。
        val seq = ImeToggle.sequence()
        assertEquals("序列长度必须为 2（按下 + 抬起）", 2, seq.size)
        assertEquals(KeyAction(ImeToggle.VK_SHIFT, true), seq[0])
        assertEquals(KeyAction(ImeToggle.VK_SHIFT, false), seq[1])
    }
}
