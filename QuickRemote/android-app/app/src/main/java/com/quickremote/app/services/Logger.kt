package com.quickremote.app.services

import android.content.Context
import android.util.Base64
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.TimeUnit

/**
 * 按天滚动的日志器。
 *
 * - 日志目录: app filesDir/logs/
 * - 文件名: quickremote-YYYY-MM-DD.log
 * - 保留 7 天，启动时清理过期文件
 * - 提供 toBase64() 便于上传到服务器
 */
class Logger {

    private val dir: File = globalDir ?: File(System.getProperty("java.io.tmpdir") ?: ".", "quickremote_logs")

    fun info(message: String) = write("INFO", message)
    fun warn(message: String) = write("WARN", message)
    fun error(message: String) = write("ERROR", message)

    private fun write(level: String, message: String) {
        try {
            if (!dir.exists()) dir.mkdirs()
            val ts = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US).format(Date())
            val line = "$ts [$level] $message\n"
            val file = File(dir, currentLogFileName())
            file.appendText(line)
        } catch (_: Exception) {
            // 日志失败不应影响业务流程
        }
    }

    /** 返回最近 N 天的日志内容（默认全部保留的日志）。 */
    fun readAll(): String {
        return try {
            dir.listFiles { f -> f.name.endsWith(".log") }
                ?.sortedBy { it.name }
                ?.joinToString("\n") { f -> "--- ${f.name} ---\n${f.readText()}" }
                ?: ""
        } catch (_: Exception) {
            ""
        }
    }

    /** 返回 Base64 编码的日志内容，用于上传。 */
    fun toBase64(): String {
        val content = readAll()
        return Base64.encodeToString(content.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
    }

    /**
     * 返回最近 [maxLines] 行日志（用于意见反馈附带日志）。
     *
     * 按文件名（即日期）从旧到新逐行读入，环形缓冲只保留尾部 [maxLines] 行，
     * 不会把 7 天日志全量载入内存；单日文件含换行用 forEachLine 兼容 CRLF。
     */
    fun readTail(maxLines: Int = 1000): String {
        if (maxLines <= 0) return ""
        return try {
            val files = dir.listFiles { f -> f.name.endsWith(".log") }?.sortedBy { it.name }
                ?: return ""
            val tail = ArrayDeque<String>()
            for (file in files) {
                try {
                    file.forEachLine { line ->
                        tail.addLast(line)
                        if (tail.size > maxLines) tail.removeFirst()
                    }
                } catch (_: Exception) {
                    // 单个文件读失败不影响其它文件
                }
            }
            tail.joinToString("\n")
        } catch (_: Exception) {
            ""
        }
    }

    /** 清理 7 天前的日志文件。 */
    fun cleanupOldLogs() {
        try {
            val cutoff = System.currentTimeMillis() - TimeUnit.DAYS.toMillis(RETENTION_DAYS)
            dir.listFiles { f -> f.name.endsWith(".log") }?.forEach { f ->
                if (f.lastModified() < cutoff) f.delete()
            }
        } catch (_: Exception) {
            // ignore
        }
    }

    /** 清空全部日志文件（下次写入时自动重建当前日志文件）。 */
    fun clearAll() {
        try {
            dir.listFiles { f -> f.name.endsWith(".log") }?.forEach { f -> f.delete() }
        } catch (_: Exception) {
            // ignore
        }
    }

    private fun currentLogFileName(): String {
        val dateStr = SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date())
        return "quickremote-$dateStr.log"
    }

    companion object {
        private const val RETENTION_DAYS = 7L

        @Volatile
        private var globalDir: File? = null

        /** 在 Application 中初始化全局日志目录。 */
        fun init(context: Context) {
            globalDir = File(context.filesDir, "logs").apply { mkdirs() }
        }
    }
}
