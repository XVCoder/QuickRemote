# QuickRemote ProGuard 规则
# 默认 ProGuard 文件已包含基本 Android 规则。

# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keepclassmembers class kotlinx.serialization.json.** {
    *** Companion;
}
-keepclasseswithmembers class kotlinx.serialization.json.** {
    kotlinx.serialization.KSerializer serializer(...);
}
-keep,includedescriptorclasses class com.quickremote.app.**$$serializer { *; }
-keepclassmembers class com.quickremote.app.** {
    *** Companion;
}
-keepclasseswithmembers class com.quickremote.app.** {
    kotlinx.serialization.KSerializer serializer(...);
}
