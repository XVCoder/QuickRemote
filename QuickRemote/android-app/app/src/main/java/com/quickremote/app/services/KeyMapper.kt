package com.quickremote.app.services

/**
 * 键盘按键映射工具：把 Android 字符/KeyEvent 映射为 Windows 虚拟键码（VK）。
 *
 * PC 端 RemoteInputHandler 直接使用 VK 码调用 SendInput：
 *   INPUT_KEY (0x03): [vkCode 2B LE][down 1B]
 *
 * 返回的 Pair 第二个元素表示该字符是否需要按住 Shift（如大写字母、符号层）。
 */
object KeyMapper {

    /**
     * ASCII 字符 → (VK 码, 需要 Shift)。返回 null 表示无法映射（如中文、emoji）。
     */
    fun charToVk(c: Char): Pair<Int, Boolean>? = when (c) {
        in 'a'..'z' -> (0x41 + (c - 'a')) to false
        in 'A'..'Z' -> (0x41 + (c - 'A')) to true
        in '0'..'9' -> (0x30 + (c - '0')) to false
        ' ' -> 0x20 to false
        '\n', '\r' -> 0x0D to false            // Enter
        '\t' -> 0x09 to false                   // Tab
        '!' -> 0x31 to true
        '@' -> 0x32 to true
        '#' -> 0x33 to true
        '$' -> 0x34 to true
        '%' -> 0x35 to true
        '^' -> 0x36 to true
        '&' -> 0x37 to true
        '*' -> 0x38 to true
        '(' -> 0x39 to true
        ')' -> 0x30 to true
        '-' -> 0xBD to false
        '_' -> 0xBD to true
        '=' -> 0xBB to false
        '+' -> 0xBB to true
        '[' -> 0xDB to false
        '{' -> 0xDB to true
        ']' -> 0xDD to false
        '}' -> 0xDD to true
        '\\' -> 0xDC to false
        '|' -> 0xDC to true
        ';' -> 0xBA to false
        ':' -> 0xBA to true
        '\'' -> 0xDE to false
        '"' -> 0xDE to true
        ',' -> 0xBC to false
        '<' -> 0xBC to true
        '.' -> 0xBE to false
        '>' -> 0xBE to true
        '/' -> 0xBF to false
        '?' -> 0xBF to true
        '`' -> 0xC0 to false
        '~' -> 0xC0 to true
        else -> null
    }

    /**
     * Android KeyEvent.keyCode → Windows VK 码（物理键盘/外接键盘）。
     * 返回 null 表示无映射。
     */
    fun androidKeyToVk(keyCode: Int): Int? = when (keyCode) {
        android.view.KeyEvent.KEYCODE_A -> 0x41
        android.view.KeyEvent.KEYCODE_B -> 0x42
        android.view.KeyEvent.KEYCODE_C -> 0x43
        android.view.KeyEvent.KEYCODE_D -> 0x44
        android.view.KeyEvent.KEYCODE_E -> 0x45
        android.view.KeyEvent.KEYCODE_F -> 0x46
        android.view.KeyEvent.KEYCODE_G -> 0x47
        android.view.KeyEvent.KEYCODE_H -> 0x48
        android.view.KeyEvent.KEYCODE_I -> 0x49
        android.view.KeyEvent.KEYCODE_J -> 0x4A
        android.view.KeyEvent.KEYCODE_K -> 0x4B
        android.view.KeyEvent.KEYCODE_L -> 0x4C
        android.view.KeyEvent.KEYCODE_M -> 0x4D
        android.view.KeyEvent.KEYCODE_N -> 0x4E
        android.view.KeyEvent.KEYCODE_O -> 0x4F
        android.view.KeyEvent.KEYCODE_P -> 0x50
        android.view.KeyEvent.KEYCODE_Q -> 0x51
        android.view.KeyEvent.KEYCODE_R -> 0x52
        android.view.KeyEvent.KEYCODE_S -> 0x53
        android.view.KeyEvent.KEYCODE_T -> 0x54
        android.view.KeyEvent.KEYCODE_U -> 0x55
        android.view.KeyEvent.KEYCODE_V -> 0x56
        android.view.KeyEvent.KEYCODE_W -> 0x57
        android.view.KeyEvent.KEYCODE_X -> 0x58
        android.view.KeyEvent.KEYCODE_Y -> 0x59
        android.view.KeyEvent.KEYCODE_Z -> 0x5A

        android.view.KeyEvent.KEYCODE_0 -> 0x30
        android.view.KeyEvent.KEYCODE_1 -> 0x31
        android.view.KeyEvent.KEYCODE_2 -> 0x32
        android.view.KeyEvent.KEYCODE_3 -> 0x33
        android.view.KeyEvent.KEYCODE_4 -> 0x34
        android.view.KeyEvent.KEYCODE_5 -> 0x35
        android.view.KeyEvent.KEYCODE_6 -> 0x36
        android.view.KeyEvent.KEYCODE_7 -> 0x37
        android.view.KeyEvent.KEYCODE_8 -> 0x38
        android.view.KeyEvent.KEYCODE_9 -> 0x39

        android.view.KeyEvent.KEYCODE_ENTER, android.view.KeyEvent.KEYCODE_NUMPAD_ENTER -> 0x0D
        android.view.KeyEvent.KEYCODE_SPACE -> 0x20
        android.view.KeyEvent.KEYCODE_TAB -> 0x09
        android.view.KeyEvent.KEYCODE_DEL -> 0x08          // Backspace
        android.view.KeyEvent.KEYCODE_FORWARD_DEL -> 0x2E  // Delete
        android.view.KeyEvent.KEYCODE_ESCAPE -> 0x1B
        android.view.KeyEvent.KEYCODE_MOVE_HOME -> 0x24
        android.view.KeyEvent.KEYCODE_MOVE_END -> 0x23
        android.view.KeyEvent.KEYCODE_PAGE_UP -> 0x21
        android.view.KeyEvent.KEYCODE_PAGE_DOWN -> 0x22
        android.view.KeyEvent.KEYCODE_INSERT -> 0x2D
        android.view.KeyEvent.KEYCODE_CAPS_LOCK -> 0x14
        android.view.KeyEvent.KEYCODE_SCROLL_LOCK -> 0x91
        android.view.KeyEvent.KEYCODE_NUM_LOCK -> 0x90

        android.view.KeyEvent.KEYCODE_DPAD_UP -> 0x26
        android.view.KeyEvent.KEYCODE_DPAD_DOWN -> 0x28
        android.view.KeyEvent.KEYCODE_DPAD_LEFT -> 0x25
        android.view.KeyEvent.KEYCODE_DPAD_RIGHT -> 0x27

        android.view.KeyEvent.KEYCODE_F1 -> 0x70
        android.view.KeyEvent.KEYCODE_F2 -> 0x71
        android.view.KeyEvent.KEYCODE_F3 -> 0x72
        android.view.KeyEvent.KEYCODE_F4 -> 0x73
        android.view.KeyEvent.KEYCODE_F5 -> 0x74
        android.view.KeyEvent.KEYCODE_F6 -> 0x75
        android.view.KeyEvent.KEYCODE_F7 -> 0x76
        android.view.KeyEvent.KEYCODE_F8 -> 0x77
        android.view.KeyEvent.KEYCODE_F9 -> 0x78
        android.view.KeyEvent.KEYCODE_F10 -> 0x79
        android.view.KeyEvent.KEYCODE_F11 -> 0x7A
        android.view.KeyEvent.KEYCODE_F12 -> 0x7B

        android.view.KeyEvent.KEYCODE_MINUS -> 0xBD
        android.view.KeyEvent.KEYCODE_EQUALS -> 0xBB
        android.view.KeyEvent.KEYCODE_LEFT_BRACKET -> 0xDB
        android.view.KeyEvent.KEYCODE_RIGHT_BRACKET -> 0xDD
        android.view.KeyEvent.KEYCODE_BACKSLASH -> 0xDC
        android.view.KeyEvent.KEYCODE_SEMICOLON -> 0xBA
        android.view.KeyEvent.KEYCODE_APOSTROPHE -> 0xDE
        android.view.KeyEvent.KEYCODE_COMMA -> 0xBC
        android.view.KeyEvent.KEYCODE_PERIOD -> 0xBE
        android.view.KeyEvent.KEYCODE_SLASH -> 0xBF
        android.view.KeyEvent.KEYCODE_GRAVE -> 0xC0

        android.view.KeyEvent.KEYCODE_NUMPAD_0 -> 0x60
        android.view.KeyEvent.KEYCODE_NUMPAD_1 -> 0x61
        android.view.KeyEvent.KEYCODE_NUMPAD_2 -> 0x62
        android.view.KeyEvent.KEYCODE_NUMPAD_3 -> 0x63
        android.view.KeyEvent.KEYCODE_NUMPAD_4 -> 0x64
        android.view.KeyEvent.KEYCODE_NUMPAD_5 -> 0x65
        android.view.KeyEvent.KEYCODE_NUMPAD_6 -> 0x66
        android.view.KeyEvent.KEYCODE_NUMPAD_7 -> 0x67
        android.view.KeyEvent.KEYCODE_NUMPAD_8 -> 0x68
        android.view.KeyEvent.KEYCODE_NUMPAD_9 -> 0x69
        android.view.KeyEvent.KEYCODE_NUMPAD_ADD -> 0x6B
        android.view.KeyEvent.KEYCODE_NUMPAD_SUBTRACT -> 0x6D
        android.view.KeyEvent.KEYCODE_NUMPAD_MULTIPLY -> 0x6A
        android.view.KeyEvent.KEYCODE_NUMPAD_DIVIDE -> 0x6F
        android.view.KeyEvent.KEYCODE_NUMPAD_DOT -> 0x6E

        else -> null
    }

    /** 修饰键映射（Android 物理键盘修饰键 → VK）。 */
    fun androidModifierToVk(keyCode: Int): Int? = when (keyCode) {
        android.view.KeyEvent.KEYCODE_SHIFT_LEFT, android.view.KeyEvent.KEYCODE_SHIFT_RIGHT -> 0x10
        android.view.KeyEvent.KEYCODE_CTRL_LEFT, android.view.KeyEvent.KEYCODE_CTRL_RIGHT -> 0x11
        android.view.KeyEvent.KEYCODE_ALT_LEFT, android.view.KeyEvent.KEYCODE_ALT_RIGHT -> 0x12
        android.view.KeyEvent.KEYCODE_META_LEFT, android.view.KeyEvent.KEYCODE_META_RIGHT -> 0x5B // Win
        else -> null
    }
}
