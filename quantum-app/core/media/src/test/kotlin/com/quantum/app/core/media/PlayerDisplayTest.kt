package com.quantum.app.core.media

import kotlin.test.Test
import kotlin.test.assertEquals

/** 悬浮播放控制的纯逻辑：标题回退链与时长格式化。 */
class PlayerDisplayTest {

    @Test
    fun `displayTitleOf 优先显式标题`() {
        assertEquals("歌手 - 歌名", PlayerManager.displayTitleOf("歌手 - 歌名", "https://a.b/x.mp3"))
        assertEquals("歌手 - 歌名", PlayerManager.displayTitleOf("歌手 - 歌名", null))
    }

    @Test
    fun `displayTitleOf 无标题时回退 URL 尾段文件名`() {
        assertEquals("song.mp3", PlayerManager.displayTitleOf(null, "https://a.b/dir/song.mp3"))
        assertEquals("song.mp3", PlayerManager.displayTitleOf("  ", "https://a.b/dir/song.mp3?sign=abc"))
        assertEquals("video.mp4", PlayerManager.displayTitleOf(null, "https://a.b/dir/video.mp4#t=30"))
    }

    @Test
    fun `displayTitleOf 文件名做 URL 解码`() {
        assertEquals("李佳薇 - 甲乙丙丁.mp3", PlayerManager.displayTitleOf(null, "https://a.b/%E6%9D%8E%E4%BD%B3%E8%96%87%20-%20%E7%94%B2%E4%B9%99%E4%B8%99%E4%B8%81.mp3"))
    }

    @Test
    fun `displayTitleOf 无文件名回退`() {
        // 尾段非空即展示尾段（裸域名 URL 的尾段是 host，展示上可接受）
        assertEquals("a.b", PlayerManager.displayTitleOf(null, "https://a.b"))
        // 纯路径尾段为空串时展示整个 URL；全空返回空串
        assertEquals("https://a.b/", PlayerManager.displayTitleOf(null, "https://a.b/"))
        assertEquals("", PlayerManager.displayTitleOf(null, null))
    }

    @Test
    fun `duration 时分秒格式化`() {
        assertEquals("--:--", MediaFormat.duration(0L))
        assertEquals("--:--", MediaFormat.duration(-5L))
        assertEquals("0:05", MediaFormat.duration(5_000L))
        assertEquals("3:25", MediaFormat.duration(205_000L))
        assertEquals("1:02:03", MediaFormat.duration(3_723_000L))
    }
}
