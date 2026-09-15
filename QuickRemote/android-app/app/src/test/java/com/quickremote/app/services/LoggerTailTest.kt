package com.quickremote.app.services

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import java.io.File

/**
 * [Logger.readTail] 的边界测试 —— 意见反馈「附带最近 1000 行日志」依赖它。
 *
 * 生产环境 [Logger.init] 一定会在 Application.onCreate 里调用（日志落在 filesDir/logs）；
 * 未调用时回退到 `tmpdir/quickremote_logs`，因此本测试可以安全占用该回退目录。
 */
class LoggerTailTest {

    private val dir = File(System.getProperty("java.io.tmpdir") ?: ".", "quickremote_logs")

    @Before
    fun setUp() {
        dir.deleteRecursively()
        dir.mkdirs()
    }

    @After
    fun tearDown() {
        dir.deleteRecursively()
    }

    private fun write(name: String, lines: List<String>) {
        File(dir, name).writeText(lines.joinToString("\n") + "\n")
    }

    @Test
    fun `日志不足上限时返回全部行`() {
        write("quickremote-2026-09-01.log", listOf("a1", "a2", "a3"))

        assertEquals(listOf("a1", "a2", "a3"), Logger().readTail(1000).lines())
    }

    @Test
    fun `日志超上限时只保留最新 maxLines 行`() {
        write("quickremote-2026-09-01.log", (1..1500).map { "line-$it" })

        val tail = Logger().readTail(1000).lines()

        assertEquals(1000, tail.size)
        assertEquals("line-501", tail.first())
        assertEquals("line-1500", tail.last())
    }

    @Test
    fun `跨天文件按日期升序拼接后截尾`() {
        write("quickremote-2026-09-01.log", (1..50).map { "d1-$it" })
        write("quickremote-2026-09-02.log", (1..50).map { "d2-$it" })

        val tail = Logger().readTail(60).lines()

        assertEquals(60, tail.size)
        // 09-01 只保留尾部 10 行，其余全部来自更晚的 09-02
        assertEquals("d1-41", tail.first())
        assertEquals("d2-50", tail.last())
    }

    @Test
    fun `目录为空时返回空串`() {
        assertEquals("", Logger().readTail(1000))
    }

    @Test
    fun `maxLines 非正时返回空串`() {
        write("quickremote-2026-09-01.log", listOf("x"))

        assertEquals("", Logger().readTail(0))
        assertEquals("", Logger().readTail(-5))
    }
}
