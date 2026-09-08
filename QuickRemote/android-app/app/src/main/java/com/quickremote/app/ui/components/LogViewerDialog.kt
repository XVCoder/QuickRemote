package com.quickremote.app.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quickremote.app.services.Logger
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.ui.theme.Warning
import kotlinx.coroutines.delay

/**
 * 日志查看对话框：展示最近保留的日志内容（等宽、可滚动）。
 * 连接失败等异常场景的排障入口，点击时才读取日志文件。
 * 支持：刷新（重读文件）、复制（全文复制到剪贴板）、清空（删除全部日志文件）。
 *
 * @param onDismiss 关闭回调
 */
@Composable
fun LogViewerDialog(onDismiss: () -> Unit) {
    val logger = remember { Logger() }
    var logs by remember { mutableStateOf(logger.readAll()) }
    var copied by remember { mutableStateOf(false) }
    val clipboard = LocalClipboardManager.current

    // 复制反馈："已复制"显示 1.5 秒后还原按钮文案
    LaunchedEffect(copied) {
        if (copied) {
            delay(1500)
            copied = false
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("运行日志", color = TextPrimary) },
        text = {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(380.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .background(BgCard)
                    .verticalScroll(rememberScrollState())
                    .padding(10.dp)
            ) {
                Text(
                    text = logs.ifBlank { "暂无日志" },
                    color = TextSecondary,
                    fontFamily = FontFamily.Monospace,
                    fontSize = 11.sp,
                    lineHeight = 15.sp
                )
            }
        },
        confirmButton = {
            Row {
                TextButton(onClick = { logs = logger.readAll() }) {
                    Text("刷新", color = TextSecondary)
                }
                TextButton(onClick = {
                    clipboard.setText(AnnotatedString(logs))
                    copied = true
                }) {
                    Text(if (copied) "已复制" else "复制", color = TextSecondary)
                }
                TextButton(onClick = {
                    logger.clearAll()
                    logs = ""
                }) {
                    Text("清空", color = Warning)
                }
                TextButton(onClick = onDismiss) {
                    Text("关闭", color = TextPrimary)
                }
            }
        },
        containerColor = MaterialTheme.colorScheme.background
    )
}
