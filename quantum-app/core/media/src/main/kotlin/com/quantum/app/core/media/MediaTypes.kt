package com.quantum.app.core.media

/**
 * 端内可播类型判定。音频白名单与服务端 AppMedia 对齐（mp3/flac/m4a/aac/wav，
 * ape ExoPlayer 不支持，服务端也不会产出）；视频覆盖 ExoPlayer 原生容器。
 *
 * 无扩展名（如部分直链短链）视为不可播，由调用方走兜底路径。
 */
object MediaTypes {
    private val AUDIO = setOf("mp3", "flac", "m4a", "aac", "wav")
    private val VIDEO = setOf("mp4", "mkv", "webm", "m4v")

    /**
     * 取 URL 的媒体扩展名（小写；判定不到返回空串）。
     * 先取路径尾段；服务端流媒体通道（`api/AppMedia/file?path=music%2Fa.mp3`）的真实文件在
     * 查询串里、路径尾段只是端点名，故路径无后缀时逐个解码查询值回退取后缀——否则音频气泡
     * 被判成非音频，全局悬浮播放控制（只跟 `PlaybackState.audio` 出现）与退出续播一起失效。
     */
    fun extOf(url: String): String {
        val base = url.substringBefore('#')
        extOfSegment(base.substringBefore('?').substringAfterLast('/'))?.let { return it }
        return base.substringAfter('?', "").split('&').firstNotNullOfOrNull { param ->
            val value = param.substringAfter('=', param)
            val decoded = runCatching { java.net.URLDecoder.decode(value, "UTF-8") }.getOrDefault(value)
            extOfSegment(decoded.substringAfterLast('/'))
        } ?: ""
    }

    /** 单段扩展名：须为 1-5 位字母数字，避免把签名/JWT 里末段点号后的内容误判成扩展名。 */
    private fun extOfSegment(segment: String): String? =
        segment.substringAfterLast('.', "")
            .takeIf { it.length in 1..5 && it.all(Char::isLetterOrDigit) }
            ?.lowercase()

    fun isAudio(url: String): Boolean = extOf(url) in AUDIO

    fun isVideo(url: String): Boolean = extOf(url) in VIDEO

    fun isPlayable(url: String): Boolean = isAudio(url) || isVideo(url)
}
