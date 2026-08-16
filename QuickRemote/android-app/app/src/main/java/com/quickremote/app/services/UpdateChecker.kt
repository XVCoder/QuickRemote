package com.quickremote.app.services

import com.quickremote.app.BuildConfig
import com.quickremote.app.data.models.ManifestData
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/**
 * 版本更新检查服务。从 quickdeploy manifest URL 下载 manifest.json，
 * 解析 android-app 组件的最新版本号，与当前版本对比。
 */
class UpdateChecker(
    private val currentVersion: String = BuildConfig.VERSION_NAME,
    private val logger: Logger = Logger()
) {
    private val json = Json { ignoreUnknownKeys = true }

    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()

    @Volatile
    var isUpdateAvailable: Boolean = false
        private set

    @Volatile
    var latestVersion: String = ""
        private set

    @Volatile
    var downloadUrl: String = ""
        private set

    @Volatile
    var changelog: String = ""
        private set

    @Volatile
    var manifest: ManifestData? = null
        private set

    /** 检查更新。manifestUrl 为空或请求失败时安全返回。 */
    suspend fun check(manifestUrl: String) = withContext(Dispatchers.IO) {
        if (manifestUrl.isBlank()) {
            isUpdateAvailable = false
            return@withContext
        }
        try {
            logger.info("Checking update from $manifestUrl")
            val request = Request.Builder().url(manifestUrl).get().build()
            client.newCall(request).execute().use { resp ->
                if (!resp.isSuccessful) {
                    logger.warn("Update check HTTP ${resp.code}")
                    isUpdateAvailable = false
                    return@use
                }
                val body = resp.body?.string().orEmpty()
                val m = json.decodeFromString(ManifestData.serializer(), body)
                manifest = m
                val app = m.androidApp
                latestVersion = app?.latest_version?.trimStart('v', 'V').orEmpty()
                changelog = app?.changelog.orEmpty()
                downloadUrl = app?.versions?.get(app.latest_version)?.get("apk").orEmpty()
                isUpdateAvailable = latestVersion.isNotEmpty() && compareVersions(latestVersion, currentVersion) > 0
                logger.info("Update check done: latest=$latestVersion current=$currentVersion available=$isUpdateAvailable")
            }
        } catch (e: Exception) {
            logger.warn("Update check failed: ${e.message}")
            isUpdateAvailable = false
        }
    }

    companion object {
        /** 对比语义化版本号：1 表示 a 更新，-1 表示 b 更新，0 表示相同。 */
        fun compareVersions(a: String, b: String): Int {
            val pa = parseVersion(a)
            val pb = parseVersion(b)
            for (i in 0 until 3) {
                if (pa[i] > pb[i]) return 1
                if (pa[i] < pb[i]) return -1
            }
            return 0
        }

        private fun parseVersion(v: String): IntArray {
            val parts = v.trim().trimStart('v', 'V').split(".")
            val result = IntArray(3)
            for (i in 0 until minOf(3, parts.size)) {
                result[i] = parts[i].toIntOrNull() ?: 0
            }
            return result
        }
    }
}
