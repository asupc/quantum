package com.quantum.app.core.network

import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.ScanLoginResult
import kotlinx.serialization.json.Json
import org.junit.Test
import kotlin.test.assertTrue

class ScanLoginContractTest {
    @Test
    fun scanAuthorizationDecodesObjectEnvelope() {
        val response = Json.decodeFromString<EnvelopeDto<ScanLoginResult>>(
            """{"Data":{"Authorized":true},"Code":200,"Message":"Success"}"""
        )
        assertTrue(response.unwrap().authorized)
    }
}
