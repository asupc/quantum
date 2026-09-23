package com.quantum.app.feature.chat.upload

import android.content.Context
import android.net.Uri
import androidx.core.content.FileProvider
import java.io.File

/** 发送面板附件工具：相册/拍照（FileProvider）/文件选择，先 AppUpload 再发消息。 */
object Uploads {
    enum class UploadKind { IMAGE, FILE }

    fun newCameraFile(context: Context): File =
        File(context.cacheDir, "camera_${System.currentTimeMillis()}.jpg")

    fun cameraUri(context: Context, file: File): Uri =
        FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)

    /** 按扩展名推断展示名（服务端白名单以此校验）。 */
    fun displayName(context: Context, uri: Uri, kind: UploadKind): String {
        val fromProvider = uri.lastPathSegment?.substringAfterLast('/') ?: ""
        return when {
            fromProvider.contains('.') -> fromProvider
            kind == UploadKind.IMAGE -> "img_${System.currentTimeMillis()}.jpg"
            else -> "file_${System.currentTimeMillis()}.bin"
        }
    }

    fun mimeType(context: Context, uri: Uri): String =
        context.contentResolver.getType(uri) ?: "application/octet-stream"
}
