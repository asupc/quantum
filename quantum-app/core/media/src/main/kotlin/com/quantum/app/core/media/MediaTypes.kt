package com.quantum.app.core.media

/**
 * 端内可播类型判定。音频白名单与服务端 AppMedia 对齐（mp3/flac/m4a/aac/wav，
 * ape ExoPlayer 不支持，服务端也不会产出）；视频覆盖 ExoPlayer 原生容器。
 *
 * 判定基于 URL 扩展名：直链（含查询串/锚点）取路径最后一段的后缀；
 * 无扩展名（如部分直链短链）视为不可播，由调用方走兜底路径。
 */
object MediaTypes {
    private val AUDIO = setOf("mp3", "flac", "m4a", "aac", "wav")
    private val VIDEO = setOf("mp4", "mkv", "webm", "m4v")

    /** 取 URL 的文件扩展名（去查询串/锚点，小写；无扩展名返回空串）。 */
    fun extOf(url: String): String {
        val path = url.substringBefore('#').substringBefore('?').substringAfterLast('/')
        val ext = path.substringAfterLast('.', "")
        return ext.lowercase()
    }

    fun isAudio(url: String): Boolean = extOf(url) in AUDIO

    fun isVideo(url: String): Boolean = extOf(url) in VIDEO

    fun isPlayable(url: String): Boolean = isAudio(url) || isVideo(url)
}
