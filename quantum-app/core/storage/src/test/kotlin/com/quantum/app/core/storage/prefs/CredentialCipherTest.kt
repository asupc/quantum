package com.quantum.app.core.storage.prefs

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.util.Base64

/**
 * 登录页「记住密码」配套纯逻辑：
 * - CredentialCipherCodec：Keystore 密文落盘帧（Base64(IV) + "." + Base64(密文)）往返与畸形自愈；
 * - RememberedCredentialsPolicy：落盘决策（勾选加密存、取消/空密码清）。
 */
class CredentialCipherTest {

    @Test
    fun frameUnframe_RoundTrip() {
        val iv = ByteArray(12) { it.toByte() }
        val cipherText = ByteArray(34) { (it * 7).toByte() }

        val framed = CredentialCipherCodec.frame(iv, cipherText)
        val parts = CredentialCipherCodec.unframe(framed)!!

        assertEquals(iv.toList(), parts.first.toList())
        assertEquals(cipherText.toList(), parts.second.toList())
    }

    @Test
    fun unframe_Malformed_ReturnsNull() {
        assertNull(CredentialCipherCodec.unframe(null))
        assertNull(CredentialCipherCodec.unframe(""))
        assertNull(CredentialCipherCodec.unframe("no-separator"))
        assertNull(CredentialCipherCodec.unframe(".startsWithSeparator"))
        assertNull(CredentialCipherCodec.unframe("endsWithSeparator."))
        assertNull(CredentialCipherCodec.unframe("!!!.not-base64"))
        // 空分量同样拒绝（合法 Base64 但解码后为空字节）
        assertNull(
            CredentialCipherCodec.unframe(
                Base64.getEncoder().encodeToString(ByteArray(0)) + "." +
                    Base64.getEncoder().encodeToString(ByteArray(8))
            )
        )
    }

    @Test
    fun policy_CheckedEncrypts_UncheckedOrEmptyClears() {
        val encrypt: (String) -> String? = { "enc($it)" }

        assertEquals("enc(pw)", RememberedCredentialsPolicy.cipherToStore(true, "pw", encrypt))
        assertNull(RememberedCredentialsPolicy.cipherToStore(false, "pw", encrypt)) // 取消勾选 → 清密文
        assertNull(RememberedCredentialsPolicy.cipherToStore(true, null, encrypt))  // 无密码 → 清
        assertNull(RememberedCredentialsPolicy.cipherToStore(true, "", encrypt))    // 空密码 → 清
    }
}
