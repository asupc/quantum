# core:network 反序列化/会话
-keepattributes *Annotation*, InnerClasses
-dontnote okhttp3.**
-dontwarn okhttp3.**

# kotlinx serialization
-keepattributes RuntimeVisibleAnnotations,AnnotationDefault
-keepclassmembers class com.quantum.app.**$$serializer { *; }
-keepclasseswithmembers class com.quantum.app.** {
    *** Companion;
}
