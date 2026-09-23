package com.quantum.app.di

import android.content.Context
import coil.ImageLoader
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import okhttp3.OkHttpClient
import javax.inject.Singleton

/**
 * 图片加载（image 消息走 GET api/AppUpload/{fileId}，必须带 JWT 的客户端加载——
 * 服务端按行鉴权归属，401 时 EnvelopeInterceptor 单飞刷新后重放）。
 */
@Module
@InstallIn(SingletonComponent::class)
object AppModule {

    @Provides
    @Singleton
    fun provideAuthenticatedImageLoader(@ApplicationContext context: Context, okHttpClient: OkHttpClient): ImageLoader =
        ImageLoader.Builder(context)
            .okHttpClient(okHttpClient)
            .crossfade(true)
            .build()
}
