package com.quantum.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import dagger.hilt.android.AndroidEntryPoint

@AndroidEntryPoint
class MainActivity : androidx.fragment.app.FragmentActivity() {

    /** 当前深链（State 驱动 Compose 重组；singleTask 下 onNewIntent 亦更新此值）。 */
    private var deepLink by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        deepLink = extractJump(intent)
        setContent {
            AppRoot(initialJump = deepLink)
        }
    }

    /** singleTask 复用实例时新深链经此转发到与 onCreate 相同的跳转处理。 */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        deepLink = extractJump(intent)
    }

    private fun extractJump(intent: Intent?): String? = intent?.data?.toString()
}
