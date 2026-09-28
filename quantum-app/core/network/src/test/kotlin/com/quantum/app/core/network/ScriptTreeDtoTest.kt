package com.quantum.app.core.network

import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.ScriptTreeDto
import com.quantum.app.core.network.dto.flattenCsFiles
import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals

/** 服务端 ScriptsFile 使用小写字段名，目录 children 与叶节点 path 必须原样解码。 */
class ScriptTreeDtoTest {
    private val json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
        coerceInputValues = true
    }

    @Test
    fun scriptListDecodesLowercaseTreeAndCollectsCsLeaves() {
        val envelope = json.decodeFromString<EnvelopeDto<List<ScriptTreeDto>>>(
            """
            {
              "Code": 200,
              "Message": "Success",
              "Data": [
                {
                  "title": "demo",
                  "contextmenu": false,
                  "children": [
                    {"title": "子目录", "children": [
                      {"title": "task.cs", "contextmenu": true, "children": null, "path": "demo/sub/task.cs"},
                      {"title": "readme.txt", "contextmenu": true, "children": null, "path": "demo/sub/readme.txt"}
                    ], "path": null}
                  ],
                  "path": null
                },
                {"title": "root.CS", "contextmenu": true, "children": null, "path": "root.CS"}
              ]
            }
            """.trimIndent()
        )
        val tree = envelope.unwrap()
        assertEquals("demo", tree.first().title)
        assertEquals("子目录", tree.first().children.single().title)
        assertEquals(listOf("demo/sub/task.cs", "root.CS"), tree.flattenCsFiles())
    }
}
