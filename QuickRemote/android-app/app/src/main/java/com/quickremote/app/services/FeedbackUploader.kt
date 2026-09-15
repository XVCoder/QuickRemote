package com.quickremote.app.services

import android.os.Build
import com.quickremote.app.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.TimeUnit

/** 反馈提交结果。 */
data class FeedbackResult(
    val success: Boolean,
    val fileName: String,
    val message: String
)

/**
 * 意见反馈上报。
 *
 * 把用户反馈（可选附带最近 [LOG_TAIL_LINES] 行运行日志）打包成一个文本文件，
 * 以 `设备id_反馈时间.log` 命名，上传到 QuickDeploy 的 quickremote/feedback 目录。
 *
 * 直传 QuickDeploy 而不经中继服务器：用户反馈的常见场景恰恰是「连不上」，
 * 因此不能依赖中继可用。令牌仅对该目录、仅对 .log 扩展名生效且不允许覆盖。
 */
class FeedbackUploader(private val logger: Logger = Logger()) {

    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .writeTimeout(30, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    /**
     * 提交反馈。网络异常不抛出，返回带失败原因的结果由 UI 提示。
     *
     * @param content 用户填写的反馈内容
     * @param includeLogs 是否附带最近 [LOG_TAIL_LINES] 行运行日志
     * @param deviceId 设备 ID（由 SettingsStore 持久化，首次生成后稳定）
     */
    suspend fun submit(content: String, includeLogs: Boolean, deviceId: String): FeedbackResult =
        withContext(Dispatchers.IO) {
            val now = Date()
            val safeId = sanitize(deviceId)
            val fileName = "${safeId}_${timestamp(now)}.log"
            val payload = buildPayload(content, includeLogs, safeId, now)

            try {
                logger.info(
                    "Submitting feedback: $fileName " +
                        "(${payload.toByteArray(Charsets.UTF_8).size} bytes, logs=$includeLogs)"
                )

                val body = MultipartBody.Builder()
                    .setType(MultipartBody.FORM)
                    .addFormDataPart(
                        "file",
                        fileName,
                        payload.toByteArray(Charsets.UTF_8).toRequestBody(TEXT_MEDIA)
                    )
                    .build()

                val request = Request.Builder().url(UPLOAD_URL).post(body).build()
                client.newCall(request).execute().use { resp ->
                    val respBody = resp.body?.string().orEmpty()
                    when {
                        !resp.isSuccessful -> {
                            logger.warn("Feedback upload failed: HTTP ${resp.code} $respBody")
                            FeedbackResult(false, fileName, "提交失败（HTTP ${resp.code}）")
                        }
                        // 平台成功响应形如 {"file_id":"...","name":"...","size":N}
                        !respBody.contains("file_id") -> {
                            logger.warn("Feedback upload unexpected response: $respBody")
                            FeedbackResult(false, fileName, "提交失败：服务器响应异常")
                        }
                        else -> {
                            logger.info("Feedback submitted OK: $fileName")
                            FeedbackResult(true, fileName, "反馈已提交，感谢你的反馈！")
                        }
                    }
                }
            } catch (e: Exception) {
                logger.warn("Feedback upload failed: ${e.message}")
                FeedbackResult(false, fileName, "提交失败：${e.message}")
            }
        }

    /** 拼装提交内容：反馈头信息 + 用户正文 +（可选）日志。 */
    private fun buildPayload(
        content: String,
        includeLogs: Boolean,
        deviceId: String,
        now: Date
    ): String = buildString {
        appendLine("========== QuickRemote 意见反馈 ==========")
        appendLine("客户端   : Android v${BuildConfig.VERSION_NAME}")
        appendLine("设备 ID  : $deviceId")
        appendLine("设备型号 : ${Build.MANUFACTURER} ${Build.MODEL}")
        appendLine("系统     : Android ${Build.VERSION.RELEASE} (SDK ${Build.VERSION.SDK_INT})")
        appendLine("提交时间 : ${headerTime(now)}")
        appendLine("附带日志 : ${if (includeLogs) "是（最近 $LOG_TAIL_LINES 行）" else "否"}")
        appendLine("==========================================")
        appendLine()
        appendLine("【反馈内容】")
        appendLine(content.trim())
        appendLine()

        if (includeLogs) {
            val logs = logger.readTail(LOG_TAIL_LINES)
            appendLine("---------- 运行日志（最近 $LOG_TAIL_LINES 行） ----------")
            appendLine(logs.ifBlank { "（暂无日志）" })
        }
    }

    /** 把设备 ID 清洗成安全的文件名片段（仅保留字母数字、-、_）。 */
    private fun sanitize(value: String): String {
        val cleaned = value.filter { it.isLetterOrDigit() || it == '-' || it == '_' }
        return cleaned.ifBlank { "unknown" }
    }

    private fun timestamp(date: Date): String =
        SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(date)

    private fun headerTime(date: Date): String =
        SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(date)

    companion object {
        /** QuickDeploy 上传令牌（目标目录 quickremote/feedback）。 */
        private const val UPLOAD_URL =
            "https://qd.solutionx.top/api/upload/lJ7mTnJu0FHkNcx4gOET5NS6IDFDkgE3Ch7FDsN4TNk"

        /** 勾选「附带日志」时截取的日志行数。 */
        const val LOG_TAIL_LINES = 1000

        private val TEXT_MEDIA = "text/plain; charset=utf-8".toMediaType()
    }
}
