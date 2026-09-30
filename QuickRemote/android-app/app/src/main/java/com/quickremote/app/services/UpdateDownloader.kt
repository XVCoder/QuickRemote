package com.quickremote.app.services

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * 更新包下载器（v1.0.86）—— 后台下载 + 断点续传。
 *
 * ## 为什么改（v1.0.86 前的问题）
 *
 * 旧实现在设置页对话框里用 HttpURLConnection 同步下载：
 *   ① 关闭对话框 / 切到其他应用 / 锁屏 → 下载随之中断（无后台能力）；
 *   ② 单次 GET 请求，断网或超时即整体失败重来（无断点续传），APK 13.8MB 弱网体验很差。
 *
 * ## 现在的实现
 *
 * 1. **主路径：系统 DownloadManager**（`enqueue`）
 *    - 下载由系统进程执行：App 退到后台、被划掉甚至重启后仍在继续（文档原文：
 *      "conduct the download in the background … retrying downloads after failures or
 *      across connectivity changes and system reboots"）。
 *    - **断点续传由系统负责**：中断后继续时会带 `Range: bytes=<已下载>-`
 *      （服务端需支持 Range —— 实测 qd 平台返回 `Accept-Ranges: bytes` + `206`）。
 *    - 全程有系统通知栏进度（`VISIBILITY_VISIBLE_NOTIFY_COMPLETED`）。
 * 2. **兜底路径：App 内带 Range 续传的下载**（`downloadResumable`）
 *    - DownloadManager 被部分 ROM 禁用/异常时启用，自己发 `Range: bytes=N-` 续传，
 *      失败重试最多 5 次，保留 `.part` 文件避免重复下载。
 *
 * 两条路径下载完成后都走同一套 [validate] 三重校验（长度 / ZIP 头 / APK 内嵌 versionName），
 * 这是 v1.0.44 起就有的防线——避免"装成旧版本"这种最坏结果。
 */
object UpdateDownloader {

    private const val PREFS = "qr_update_download"
    private const val KEY_ID = "download_id"
    private const val KEY_VERSION = "version"
    private const val APK_NAME = "QuickRemote-update.apk"
    private const val MIN_APK_BYTES = 1_000_000L
    private const val MAX_ATTEMPTS = 5

    /** 下载状态（供 UI 显示进度）。 */
    data class Progress(
        val status: Int,
        val bytes: Long,
        val total: Long,
        val reason: Int
    ) {
        val isFinished: Boolean get() = status == DownloadManager.STATUS_SUCCESSFUL || status == DownloadManager.STATUS_FAILED
    }

    // ==================== 路径与状态持久化 ====================

    /** 最终 APK 路径（下载目的文件，与 DownloadManager 落盘位置一致）。 */
    fun targetFile(context: Context): File =
        File(context.getExternalFilesDir(null) ?: context.filesDir, APK_NAME)

    /** 断点续传的临时文件（仅兜底路径使用）。 */
    private fun partFile(context: Context, version: String): File =
        File(targetFile(context).parentFile, "$APK_NAME.$version.part")

    /** 已持久化的下载任务（downloadId + 目标版本），无则 null。 */
    fun savedDownload(context: Context): Pair<Long, String>? {
        val sp = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val id = sp.getLong(KEY_ID, -1L)
        val version = sp.getString(KEY_VERSION, "").orEmpty()
        return if (id > 0 && version.isNotBlank()) id to version else null
    }

    private fun save(context: Context, id: Long, version: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putLong(KEY_ID, id).putString(KEY_VERSION, version).apply()
    }

    fun clear(context: Context) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().apply()
    }

    // ==================== 主路径：DownloadManager ====================

    /**
     * 通过系统 DownloadManager 后台下载。
     * @return downloadId；null 表示 DownloadManager 不可用（ROM 禁用等），调用方应走 [downloadResumable]。
     */
    fun enqueue(context: Context, url: String, version: String): Long? {
        return try {
            val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as? DownloadManager
                ?: return null
            // DownloadManager 不覆盖已存在文件（会直接报 ERROR_FILE_ALREADY_EXISTS）
            val dest = targetFile(context)
            if (dest.exists() && !dest.delete()) {
                Logger().warn("Update: cannot remove stale apk ${dest.absolutePath}")
            }
            // 时间戳参数防中间层缓存返回旧包；只在入队时生成一次，后续重试沿用同一 URL
            val busted = if (url.contains("?")) "$url&_t=${System.currentTimeMillis()}" else "$url?_t=${System.currentTimeMillis()}"
            val request = DownloadManager.Request(Uri.parse(busted))
                .setTitle("QuickRemote v$version")
                .setDescription("正在后台下载更新包")
                .setDestinationInExternalFilesDir(context, null, APK_NAME)
                .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                .setMimeType("application/vnd.android.package-archive")
                .addRequestHeader("Cache-Control", "no-cache")
            val id = dm.enqueue(request)
            save(context, id, version)
            Logger().info("Update: enqueued downloadId=$id version=$version")
            id
        } catch (e: Exception) {
            Logger().warn("Update: DownloadManager unavailable (${e.javaClass.simpleName}: ${e.message})")
            null
        }
    }

    /** 查询下载进度；任务不存在时返回 null。 */
    fun query(context: Context, id: Long): Progress? {
        return try {
            val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as? DownloadManager ?: return null
            dm.query(DownloadManager.Query().setFilterById(id))?.use { c ->
                if (!c.moveToFirst()) return null
                fun col(name: String) = c.getColumnIndexOrThrow(name)
                Progress(
                    status = c.getInt(col(DownloadManager.COLUMN_STATUS)),
                    bytes = c.getLong(col(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR)),
                    total = c.getLong(col(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)),
                    reason = c.getInt(col(DownloadManager.COLUMN_REASON))
                )
            }
        } catch (e: Exception) {
            null
        }
    }

    // ==================== 兜底路径：App 内 Range 续传 ====================

    /**
     * App 内下载（DownloadManager 不可用时的兜底）。
     * 支持断点续传：已下载的 `.part` 保留，重试用 `Range: bytes=<已下载>-` 续传；
     * 服务端不支持 Range（返回 200 而非 206）时自动从头重下，不会写出半截拼接文件。
     */
    fun downloadResumable(
        context: Context,
        url: String,
        version: String,
        onProgress: (Float) -> Unit
    ): File {
        val dest = targetFile(context)
        val part = partFile(context, version)
        if (dest.exists() && !dest.delete()) throw IOException("无法清除旧安装包，请重试")

        val busted = if (url.contains("?")) "$url&_t=${System.currentTimeMillis()}" else "$url?_t=${System.currentTimeMillis()}"
        var lastError: Exception? = null

        for (attempt in 1..MAX_ATTEMPTS) {
            val existing = if (part.exists()) part.length() else 0L
            var conn: HttpURLConnection? = null
            try {
                conn = (URL(busted).openConnection() as HttpURLConnection).apply {
                    connectTimeout = 15_000
                    readTimeout = 60_000
                    instanceFollowRedirects = true
                    setRequestProperty("Cache-Control", "no-cache")
                    if (existing > 0) setRequestProperty("Range", "bytes=$existing-")
                }
                val code = conn.responseCode
                // 206 = 续传成功；200 = 服务端忽略 Range，必须从头写（截断 part 文件）
                if (code != HttpURLConnection.HTTP_OK && code != HttpURLConnection.HTTP_PARTIAL) {
                    throw IOException("服务器返回 HTTP $code")
                }
                val resume = code == HttpURLConnection.HTTP_PARTIAL && existing > 0
                val totalHeader = conn.getHeaderField("Content-Length")?.toLongOrNull() ?: -1L
                val total = if (resume && totalHeader > 0) existing + totalHeader else if (totalHeader > 0) totalHeader else -1L
                conn.inputStream.use { input ->
                    FileOutputStream(part, resume).use { out ->
                        val buf = ByteArray(64 * 1024)
                        var done = existing
                        while (true) {
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            done += n
                            if (total > 0) onProgress((done.toFloat() / total.toFloat()).coerceIn(0f, 1f))
                        }
                        if (total > 0 && done < total) throw IOException("下载中断（$done/$total 字节）")
                    }
                }
                if (!part.renameTo(dest)) {
                    part.copyTo(dest, overwrite = true)
                    part.delete()
                }
                Logger().info("Update: in-app download done (attempt=$attempt, resumed=$resume, ${dest.length()}B)")
                return dest
            } catch (e: Exception) {
                lastError = e
                // 保留 part 文件：下次 attempt 从当前长度续传
                Logger().warn("Update: in-app download attempt $attempt failed: ${e.message}")
                if (attempt < MAX_ATTEMPTS) Thread.sleep(800L * attempt)
            } finally {
                conn?.disconnect()
            }
        }
        throw lastError ?: IOException("下载失败")
    }

    // ==================== 校验与安装 ====================

    /**
     * 三重校验（v1.0.44 起沿用）：长度 → ZIP(PK) 头 → APK 内嵌 versionName == 期望版本。
     * 任一项不符即删除文件并抛异常，杜绝"下载到旧包 / 半包却显示安装成功"。
     */
    @Throws(IOException::class)
    fun validate(context: Context, expectedVersion: String): File {
        val file = targetFile(context)
        if (!file.exists()) throw IOException("安装包不存在，请重新下载")
        if (file.length() < MIN_APK_BYTES) throw IOException("安装包不完整（${file.length()} 字节）")
        FileInputStream(file).use {
            if (!(it.read() == 0x50 && it.read() == 0x4B)) {
                file.delete()
                throw IOException("下载内容不是有效安装包")
            }
        }
        val pkgInfo = try {
            context.packageManager.getPackageArchiveInfo(file.absolutePath, 0)
        } catch (e: Exception) {
            null
        }
        if (pkgInfo == null) {
            file.delete()
            throw IOException("安装包解析失败，请重新下载")
        }
        val actual = pkgInfo.versionName.orEmpty().trim().trimStart('v', 'V')
        val expected = expectedVersion.trim().trimStart('v', 'V')
        if (actual != expected) {
            file.delete()
            throw IOException("下载到旧版本安装包（实际 v$actual，应为 v$expected），已拦截。请稍后重试")
        }
        return file
    }

    /** 经 FileProvider 唤起系统安装器；失败回退浏览器打开下载页。 */
    fun install(context: Context, apk: File) {
        try {
            val uri = androidx.core.content.FileProvider.getUriForFile(
                context, "${context.packageName}.fileprovider", apk
            )
            val intent = android.content.Intent(android.content.Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        } catch (e: Exception) {
            Logger().warn("Installer launch failed: ${e.message}, fallback to browser")
            val fallback = android.content.Intent(
                android.content.Intent.ACTION_VIEW,
                Uri.parse("https://qd.solutionx.top/app/23dafeae-1f70-4d6c-8023-dc585b0f4366/about")
            ).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
            try {
                context.startActivity(fallback)
            } catch (e2: Exception) {
                Logger().warn("Browser fallback failed: ${e2.message}")
            }
        }
    }
}
