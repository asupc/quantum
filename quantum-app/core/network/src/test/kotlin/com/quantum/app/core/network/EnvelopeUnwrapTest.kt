package com.quantum.app.core.network

import com.quantum.app.core.common.ApiException
import com.quantum.app.core.common.AuthException
import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.api.unwrapOrNull
import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

/** 信封解包（HTTP 恒 200，成败只看 Code：200/500/401）。 */
class EnvelopeUnwrapTest {

    @Test
    fun code200ReturnsData() {
        val env = EnvelopeDto(code = 200, message = "Success", data = "ok")
        assertEquals("ok", env.unwrap())
    }

    @Test
    fun code500ThrowsBusinessException() {
        val env: EnvelopeDto<String> = EnvelopeDto(code = 500, message = "请求频繁，请稍后重试！")
        val e = assertFailsWith<ApiException> { env.unwrap() }
        assertEquals("请求频繁，请稍后重试！", e.message)
    }

    @Test
    fun code401ThrowsAuthException() {
        val env: EnvelopeDto<String> = EnvelopeDto(code = 401, message = "Token验证失败")
        assertFailsWith<AuthException> { env.unwrap() }
    }

    @Test
    fun parsesPascalCaseEnvelope() {
        val json = Json { ignoreUnknownKeys = true }
        val env = json.decodeFromString<EnvelopeDto<Long>>(
            """{"Code":200,"Message":"Success","Data":3}"""
        )
        assertEquals(3L, env.unwrap(), "Data 应为 3")
    }

    @Test
    fun code200WithNullDataThrowsOnUnwrap() {
        // §4-1：泛型擦除曾让 `data as T` 把 null 漏成非空类型 → 上游 NPE；现显式抛 ApiException
        val env: EnvelopeDto<String> = EnvelopeDto(code = 200, message = "Success", data = null)
        assertFailsWith<ApiException> { env.unwrap() }
    }

    @Test
    fun unwrapOrNullReturnsNullOnEmptyDataAndStillGuardsAuth() {
        val env: EnvelopeDto<String> = EnvelopeDto(code = 200, message = "Success", data = null)
        assertNull(env.unwrapOrNull(), "合法空响应应折叠为 null")
        assertFailsWith<AuthException> {
            EnvelopeDto<String>(code = 401, message = "Token验证失败").unwrapOrNull()
        }
    }
}
