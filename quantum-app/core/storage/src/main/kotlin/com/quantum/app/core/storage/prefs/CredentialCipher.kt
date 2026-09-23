package com.quantum.app.core.storage.prefs

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 凭据加密抽象（登录页「记住密码」用；JVM 单测可注入假实现）。
 * encrypt/decrypt 返回 null = 失败（换机迁移/Keystore 密钥被删/密文损坏），
 * 调用方按「未保存」自愈处理，绝不抛出阻断登录流程。
 */
interface CredentialCipher {
    fun encrypt(plain: String): String?
    fun decrypt(stored: String): String?
}

/**
 * 落盘帧格式编解码（纯逻辑，JVM 可测）：Base64(IV) + "." + Base64(密文)。
 * GCM 的 IV 每次加密随机生成，必须与密文一起存。
 */
object CredentialCipherCodec {

    private const val SEPARATOR = '.'

    fun frame(iv: ByteArray, cipherText: ByteArray): String =
        Base64.getEncoder().encodeToString(iv) + SEPARATOR + Base64.getEncoder().encodeToString(cipherText)

    /** 解帧；格式非法返回 null（调用方自愈清除存值）。 */
    fun unframe(raw: String?): Pair<ByteArray, ByteArray>? {
        if (raw.isNullOrEmpty()) {
            return null
        }
        val idx = raw.lastIndexOf(SEPARATOR)
        if (idx <= 0 || idx == raw.length - 1) {
            return null
        }
        return runCatching {
            val iv = Base64.getDecoder().decode(raw.substring(0, idx))
            val cipherText = Base64.getDecoder().decode(raw.substring(idx + 1))
            if (iv.isEmpty() || cipherText.isEmpty()) null else iv to cipherText
        }.getOrNull()
    }
}

/**
 * Android Keystore AES-256-GCM 实现：密钥不出硬件安全边界（StrongBox 可用则随硬件），
 * 拿到落盘数据也解不出密码——保护强度高于明文存 DataStore 的 refresh token。
 */
class KeystoreCredentialCipher : CredentialCipher {

    override fun encrypt(plain: String): String? = runCatching {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val cipherText = cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
        CredentialCipherCodec.frame(cipher.iv, cipherText)
    }.getOrNull()

    override fun decrypt(stored: String): String? {
        val parts = CredentialCipherCodec.unframe(stored) ?: return null
        return runCatching {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(TAG_LENGTH_BITS, parts.first))
            String(cipher.doFinal(parts.second), Charsets.UTF_8)
        }.getOrNull()
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }

    companion object {
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val KEY_ALIAS = "quantum_saved_pwd"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val TAG_LENGTH_BITS = 128
    }
}

/**
 * 「记住密码」落盘决策（纯逻辑，JVM 可测）：
 * 勾选且密码非空 → 加密存密文；未勾选/密码空 → null（调用方清除旧密文）；账号恒记。
 */
object RememberedCredentialsPolicy {

    fun cipherToStore(remember: Boolean, password: String?, encrypt: (String) -> String?): String? =
        if (remember && !password.isNullOrEmpty()) encrypt(password) else null
}
