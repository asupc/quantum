package com.quantum.app.feature.chat.upload

import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import okio.BufferedSink
import okio.IOException
import okio.Source
import okio.source
import okhttp3.MediaType
import okhttp3.RequestBody

/**
 * 流式上传请求体（2.4）：ContentResolver 直读 Uri 分块写网络，替代 readBytes() 整文件进内存
 * （大文件上传不再按文件体积翻倍堆占用：ByteArray + OkHttp 内部缓冲）。
 * - contentLength 由调用方经 [querySize] 预取（顺带做发送前大小上限校验）；
 *   查不到（云盘流/异常 provider）为 -1，OkHttp 走 Transfer-Encoding: chunked 分块上传兜底；
 * - writeTo 每次调用新开输入流（长度已知时可重放重试）；长度未知时 isOneShot=true，
 *   防止 OkHttp 在分块流写坏后尝试重放半截请求体。
 */
class InputStreamRequestBody(
    private val contentResolver: ContentResolver,
    private val uri: Uri,
    private val mediaType: MediaType?,
    private val contentLength: Long
) : RequestBody() {

    override fun contentType(): MediaType? = mediaType

    override fun contentLength(): Long = contentLength

    /** 长度未知（-1）时分块流不可重放：标记 one-shot，失败直接报错交上层提示。 */
    override fun isOneShot(): Boolean = contentLength < 0

    override fun writeTo(sink: BufferedSink) {
        val input = contentResolver.openInputStream(uri)
            ?: throw IOException("无法读取所选文件")
        input.use { stream ->
            val source: Source = stream.source()
            sink.writeAll(source)
        }
    }

    companion object {
        /** 经 ContentResolver 查文件大小（OpenableColumns.SIZE）；查不到/异常一律 -1 兜底分块。 */
        fun querySize(contentResolver: ContentResolver, uri: Uri): Long = runCatching {
            contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val index = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (index >= 0 && !cursor.isNull(index)) cursor.getLong(index) else -1L
                } else {
                    -1L
                }
            } ?: -1L
        }.getOrDefault(-1L)
    }
}
