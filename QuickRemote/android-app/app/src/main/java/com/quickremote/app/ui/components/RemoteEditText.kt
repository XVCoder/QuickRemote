package com.quickremote.app.ui.components

import android.content.Context
import android.text.InputType
import android.util.AttributeSet
import android.view.KeyEvent
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputConnectionWrapper

/**
 * 隐藏的远程输入框：拦截 IME 的文本上屏/删除事件，转发给远程桌面。
 *
 * 相比 TextWatcher 方案的优势：能区分输入法 composing（拼音组合中，不应发送）
 * 与 commitText（真正上屏，应发送），中文输入法不会把拼音误发到 PC 端。
 *
 * 事件流：
 * - 拼音组合阶段：setComposingText("nihao") → 忽略（仅记录 composing 状态）
 * - 上屏：commitText("你好") → onCommitText("你好")
 * - 退格：deleteSurroundingText(1, 0)（非 composing）→ onBackspace()
 * - 部分 IME 的回车/删除：sendKeyEvent → onImeKeyEvent
 */
class RemoteEditText @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : android.widget.EditText(context, attrs) {

    /** 上屏文本（IME commitText，中文/emoji/ASCII 均走这里）。 */
    var onCommitText: ((String) -> Unit)? = null

    /** 退格（非 composing 状态下的删除）。 */
    var onBackspace: (() -> Unit)? = null

    /** IME sendKeyEvent（部分输入法的回车/删除/方向键走这里）。返回 true 表示已消费。 */
    var onImeKeyEvent: ((KeyEvent) -> Boolean)? = null

    /** 当前是否有未上屏的组合文本（拼音），用于过滤误退格。 */
    private var hasComposing = false

    init {
        setBackgroundColor(android.graphics.Color.TRANSPARENT)
        isCursorVisible = false
        isFocusable = true
        isFocusableInTouchMode = true
        textSize = 1f
        inputType = InputType.TYPE_CLASS_TEXT
        // 禁止 IME 全屏编辑模式（横屏时避免输入法占满全屏）
        imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN
    }

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? {
        val base = super.onCreateInputConnection(outAttrs) ?: return null
        return object : InputConnectionWrapper(base, true) {

            override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
                hasComposing = text?.isNotEmpty() == true
                return super.setComposingText(text, newCursorPosition)
            }

            override fun finishComposingText(): Boolean {
                hasComposing = false
                return super.finishComposingText()
            }

            override fun commitText(text: CharSequence, newCursorPosition: Int): Boolean {
                hasComposing = false
                if (text.isNotEmpty()) onCommitText?.invoke(text.toString())
                val ok = super.commitText(text, newCursorPosition)
                // 文本已转发远程，清空防止无限增长（无 TextWatcher，程序清空无副作用）
                if (ok) post { editableText.clear() }
                return ok
            }

            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                // composing 阶段的删除（删拼音）不转发，仅上屏后的删除才是真退格
                if (!hasComposing && beforeLength > 0) {
                    repeat(beforeLength) { onBackspace?.invoke() }
                }
                return super.deleteSurroundingText(beforeLength, afterLength)
            }

            override fun sendKeyEvent(event: KeyEvent): Boolean {
                if (onImeKeyEvent?.invoke(event) == true) return true
                return super.sendKeyEvent(event)
            }
        }
    }
}
