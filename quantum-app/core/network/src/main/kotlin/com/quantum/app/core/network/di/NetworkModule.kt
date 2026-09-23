package com.quantum.app.core.network.di

import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.session.AuthInterceptor
import com.quantum.app.core.network.session.BaseUrlInterceptor
import com.quantum.app.core.network.session.EnvelopeInterceptor
import dagger.Binds
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import java.time.Duration
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object NetworkModule {

    private const val PLACEHOLDER_BASE = "https://quantum.placeholder/"

    @Provides
    @Singleton
    fun provideJson(): Json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
        coerceInputValues = true
    }

    @Provides
    @Singleton
    fun provideOkHttpClient(
        baseUrlInterceptor: BaseUrlInterceptor,
        authInterceptor: AuthInterceptor,
        envelopeInterceptor: EnvelopeInterceptor
    ): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(Duration.ofSeconds(15))
        .readTimeout(Duration.ofMinutes(10))   // APK 大文件下载
        .writeTimeout(Duration.ofMinutes(5))   // 200MB APK 上传
        .addInterceptor(baseUrlInterceptor)
        .addInterceptor(authInterceptor)
        .addInterceptor(envelopeInterceptor)
        .build()

    @Provides
    @Singleton
    fun provideRetrofit(client: OkHttpClient, json: Json): Retrofit = Retrofit.Builder()
        .baseUrl(PLACEHOLDER_BASE)
        .client(client)
        .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
        .build()

    @Provides
    @Singleton
    fun provideAppApi(retrofit: Retrofit): AppApi = retrofit.create(AppApi::class.java)

    @Provides
    @Singleton
    fun provideAdminApi(retrofit: Retrofit): AdminApi = retrofit.create(AdminApi::class.java)
}

@Module
@InstallIn(SingletonComponent::class)
abstract class NetworkBindings {
    @Binds
    abstract fun bindSessionGateway(impl: com.quantum.app.core.network.session.SessionManager): com.quantum.app.core.network.session.SessionGateway

    @Binds
    abstract fun bindWsGateway(impl: com.quantum.app.core.network.ws.AppWsClient): com.quantum.app.core.network.ws.WsGateway
}
