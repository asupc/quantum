package com.quantum.app.core.storage.di

import android.content.Context
import androidx.room.Room
import com.quantum.app.core.storage.db.ChatMessageDao
import com.quantum.app.core.storage.db.ChatSessionDao
import com.quantum.app.core.storage.db.NotificationDao
import com.quantum.app.core.storage.db.OutboxDao
import com.quantum.app.core.storage.db.QuantumDatabase
import com.quantum.app.core.storage.db.SyncCursorDao
import com.quantum.app.core.storage.prefs.CredentialCipher
import com.quantum.app.core.storage.prefs.KeystoreCredentialCipher
import com.quantum.app.core.storage.prefs.PrefsStore
import com.quantum.app.core.storage.prefs.TokenStore
import com.quantum.app.core.storage.prefs.TokenStoreApi
import dagger.Binds
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object StorageModule {

    @Provides
    @Singleton
    fun provideDatabase(@ApplicationContext context: Context): QuantumDatabase =
        Room.databaseBuilder(context, QuantumDatabase::class.java, "quantum.db")
            .fallbackToDestructiveMigration()
            .build()

    @Provides
    fun provideChatMessageDao(db: QuantumDatabase): ChatMessageDao = db.chatMessageDao()

    @Provides
    fun provideChatSessionDao(db: QuantumDatabase): ChatSessionDao = db.chatSessionDao()

    @Provides
    fun provideNotificationDao(db: QuantumDatabase): NotificationDao = db.notificationDao()

    @Provides
    fun provideSyncCursorDao(db: QuantumDatabase): SyncCursorDao = db.syncCursorDao()

    @Provides
    fun provideOutboxDao(db: QuantumDatabase): OutboxDao = db.outboxDao()

    @Provides
    @Singleton
    fun provideTokenStore(@ApplicationContext context: Context): TokenStore = TokenStore(context)

    @Provides
    @Singleton
    fun providePrefsStore(@ApplicationContext context: Context): PrefsStore = PrefsStore(context)

    /** 登录页「记住密码」的 Keystore 加密器（单例：Keystore 密钥惰性生成后复用）。 */
    @Provides
    @Singleton
    fun provideCredentialCipher(): CredentialCipher = KeystoreCredentialCipher()
}

@Module
@InstallIn(SingletonComponent::class)
abstract class StorageBindings {
    @Binds
    abstract fun bindTokenStoreApi(impl: TokenStore): com.quantum.app.core.storage.prefs.TokenStoreApi

    /** 会话已读水位存取（PrefsStore 实现）：ChatRepository 等依赖接口，纯 JVM 单测注入内存实现。 */
    @Binds
    abstract fun bindSessionReadStore(impl: PrefsStore): com.quantum.app.core.storage.prefs.SessionReadStore
}
