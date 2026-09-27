package com.quantum.app.core.media

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class MediaTypesTest {

    @Test
    fun `extOf 去除查询串与锚点`() {
        assertEquals("mp3", MediaTypes.extOf("https://a.b/file/song.mp3?sign=abc&x=1"))
        assertEquals("flac", MediaTypes.extOf("https://a.b/song.flac#t=30"))
        assertEquals("mp4", MediaTypes.extOf("https://a.b/dir/video.mp4"))
        assertEquals("mp3", MediaTypes.extOf("https://a.b/file/song.MP3"))
    }

    @Test
    fun `无扩展名或空路径返回空串`() {
        assertEquals("", MediaTypes.extOf("https://a.b/file/abc123"))
        assertEquals("", MediaTypes.extOf("https://a.b/file/abc123?x=1"))
        assertEquals("", MediaTypes.extOf(""))
    }

    @Test
    fun `扩展名在查询串里时回退取查询值`() {
        // 服务端 AppMedia 流媒体地址：路径尾段是端点名，真实文件在 path 参数（URL 编码）
        assertEquals(
            "mp3",
            MediaTypes.extOf("http://192.168.x.x:5088/api/AppMedia/file?path=music%2F%E6%AD%8C%E6%9B%B3.mp3")
        )
        assertEquals("flac", MediaTypes.extOf("https://a.b/api/AppMedia/file?path=a/b.flac"))
        // 查询值内嵌完整直链
        assertEquals("m4a", MediaTypes.extOf("https://a.b/proxy?url=https%3A%2F%2Fc.d%2Fx.m4a"))
        // 路径优先，不被查询串里的无关后缀带偏
        assertEquals("mp3", MediaTypes.extOf("https://a.b/x.mp3?sign=abc.exe"))
        // 签名/JWT 式点号串不得误判为扩展名
        assertFalse(MediaTypes.isAudio("https://a.b/file/abc?t=eyJhbGci.eyJzdWIi.sRqf_abc"))
        assertFalse(MediaTypes.isPlayable("https://a.b/file/abc?t=1.2.3.4"))
    }

    @Test
    fun `音频白名单判定`() {
        assertTrue(MediaTypes.isAudio("https://a.b/x.mp3"))
        // AppMedia 音频气泡（悬浮播放控制依赖此判定出现）
        assertTrue(MediaTypes.isAudio("https://a.b/api/AppMedia/file?path=music%2Fx.mp3"))
        assertTrue(MediaTypes.isAudio("https://a.b/x.flac"))
        assertTrue(MediaTypes.isAudio("https://a.b/x.m4a"))
        assertTrue(MediaTypes.isAudio("https://a.b/x.aac"))
        assertTrue(MediaTypes.isAudio("https://a.b/x.wav"))
        assertFalse(MediaTypes.isAudio("https://a.b/x.mp4"))
        // ape 不在白名单：ExoPlayer 不支持
        assertFalse(MediaTypes.isAudio("https://a.b/x.ape"))
    }

    @Test
    fun `视频白名单判定`() {
        assertTrue(MediaTypes.isVideo("https://a.b/x.mp4"))
        assertTrue(MediaTypes.isVideo("https://a.b/x.mkv"))
        assertTrue(MediaTypes.isVideo("https://a.b/x.webm"))
        assertFalse(MediaTypes.isVideo("https://a.b/x.mp3"))
        assertFalse(MediaTypes.isVideo("https://a.b/file/noext"))
    }

    @Test
    fun `未知类型不可播`() {
        assertFalse(MediaTypes.isPlayable("https://a.b/x.exe"))
        assertFalse(MediaTypes.isPlayable("https://a.b/x.pdf"))
        assertFalse(MediaTypes.isPlayable("https://a.b/file/abc123"))
    }
}
