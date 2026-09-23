import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.ksp)
    alias(libs.plugins.hilt)
}

// 单一事实源：keystore.properties（仓库外、gitignored）是否存在——签名回落、WARN、产物名标注都读它
val releaseKeystore = rootProject.file("keystore.properties")

android {
    namespace = "com.quantum.app"
    compileSdk = 36
    defaultConfig {
        applicationId = "com.quantum.app"
        minSdk = 26
        targetSdk = 36
        // 版本由 build-apk.bat 注入（脚本内维护大版本，每次构建补丁号+1，-PappVersionName/-PappVersionCode 传入）；
        // 直接用 gradlew 构建时回退缺省值
        versionCode = (findProperty("appVersionCode") as String?)?.toInt() ?: 1
        versionName = (findProperty("appVersionName") as String?) ?: "1.0.0"
    }

    signingConfigs {
        create("release") {
            // A5.1 首日生成 keystore 并异地备份（自分发+端内自更新依赖同一签名）；
            // CI/本地通过 keystore.properties 注入，仓库不保存任何口令。
            val props = Properties()
            if (releaseKeystore.exists()) {
                releaseKeystore.inputStream().use { props.load(it) }
                storeFile = rootProject.file(props.getProperty("storeFile"))
                storePassword = props.getProperty("storePassword")
                keyAlias = props.getProperty("keyAlias")
                keyPassword = props.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ""
        }
        release {
            // R8 决策项（§4-12）：暂不启用（isMinifyEnabled=false）。keep 规则未经全链路验证即开
            // 会产出「仅发布版崩溃」的包；待补 R8 探针回归后再单列批次开启。
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            if (releaseKeystore.exists()) {
                signingConfig = signingConfigs.getByName("release")
            } else {
                signingConfig = signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
    }
}

// keystore.properties 缺失即回落 debug 签名：CI/裸 gradlew 会静默产出「无法端内自更新」的包（审计 §4-12 根因）。
// 构建期必须吵闹——WARN 覆盖不经 build-apk.bat 的路径；产物名 -debugsigned 标注由 build-apk.bat 分发拷贝处施加
// （AGP 输出保持默认 app-release.apk，避免脚本硬编码源路径失配）。
if (!releaseKeystore.exists()) {
    logger.warn(
        "quantum-app: 未找到 keystore.properties，release 将回落 debug 签名——" +
            "此类 APK 无法用于端内自更新（覆盖安装要求同一签名），仅供本地联调，严禁分发。" +
            "正式构建请在仓库外配置 keystore.properties。"
    )
}

dependencies {
    implementation(project(":core:common"))
    implementation(project(":core:network"))
    implementation(project(":core:storage"))
    implementation(project(":core:push"))
    implementation(project(":core:media"))
    implementation(project(":feature:chat"))
    implementation(project(":feature:notify"))
    implementation(project(":feature:task"))
    implementation(project(":feature:docker"))
    implementation(project(":feature:admin"))
    implementation(project(":feature:ai"))
    implementation(project(":feature:mine"))

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.process)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.hilt.android)
    implementation(libs.hilt.navigation.compose)
    ksp(libs.hilt.compiler)
    implementation(libs.coil.compose)
    implementation(libs.kotlinx.coroutines.android)
    // 悬浮播放控制直接引用 Player 状态常量（PlayerManager 单例经 core:media 传递，类型需本模块可见）
    implementation(libs.media3.exoplayer)
    implementation(libs.androidx.biometric)
    implementation(libs.glance.appwidget)
    implementation(libs.room.runtime)
    implementation(libs.okhttp)

    testImplementation(libs.junit)
}
