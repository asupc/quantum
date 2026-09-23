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
    fun `音频白名单判定`() {
        assertTrue(MediaTypes.isAudio("https://a.b/x.mp3"))
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
