/*
 * FreeRDP Android JNI 桥接层。
 *
 * 重要：native 库 libfreerdp-android.so 通过类名
 * `com/freerdp/freerdpcore/services/LibFreeRDP` 反向查找本类，并：
 *   1. 在 JNI_OnLoad 时用 NewObject 创建本类实例（因此必须有公有无参构造器）；
 *   2. 通过 GetStaticMethodID + CallStaticVoidMethod / CallStaticBooleanMethod /
 *      CallStaticIntMethod 调用回调（因此所有回调方法必须是 static）。
 *
 * 故本类必须保留包名与类名，回调与 native 方法声明必须与 aFreeRDP 的
 * LibFreeRDP.java 完全一致。方法签名来源：aFreeRDP_3.5.1 的 LibFreeRDP.java。
 */
package com.freerdp.freerdpcore.services;

import android.content.Context;
import android.graphics.Bitmap;
import android.util.Log;

import java.util.concurrent.ConcurrentHashMap;

public class LibFreeRDP {

    private static final String TAG = "LibFreeRDP";

    /** .so 库是否已加载成功。 */
    private static boolean mIsLoaded = false;

    /** 是否有 H264 支持。 */
    private static boolean mHasH264 = false;

    /** 库加载失败时的真实异常信息（便于定位 dlopen 失败原因）。 */
    private static String sLoadError = null;

    /** 已连接实例集合，用于生命周期同步。 */
    private static final ConcurrentHashMap<Long, Boolean> mConnectedInstances =
            new ConcurrentHashMap<>();

    /** 连接生命周期事件监听器。 */
    public static EventListener listener;

    /** UI 事件监听器（图形更新、认证、剪切板等）。 */
    public static UIEventListener uiListener;

    // 与 android_freerdp.c 保持一致的证书校验标志
    public static final long VERIFY_CERT_FLAG_NONE = 0x00;
    public static final long VERIFY_CERT_FLAG_LEGACY = 0x02;
    public static final long VERIFY_CERT_FLAG_REDIRECT = 0x10;
    public static final long VERIFY_CERT_FLAG_GATEWAY = 0x20;
    public static final long VERIFY_CERT_FLAG_CHANGED = 0x40;
    public static final long VERIFY_CERT_FLAG_MISMATCH = 0x80;
    public static final long VERIFY_CERT_FLAG_MATCH_LEGACY_SHA1 = 0x100;
    public static final long VERIFY_CERT_FLAG_FP_IS_PEM = 0x200;

    // 实验性功能标记（与 android_freerdp.c 保持一致）
    public static final int EXPERIMENTAL_REMOTEAPP = 0;
    public static final int EXPERIMENTAL_CAMERA = 1;

    // 公有无参构造器：native 的 jni_load_class 会通过 NewObject 创建本类实例。
    public LibFreeRDP() {
    }

    static {
        try {
            System.loadLibrary("freerdp-android");
            mIsLoaded = true;

            // 加载依赖库，触发 JNI_OnLoad
            String version = freerdp_get_jni_version();
            String[] versions = version.split("[\\.-]");
            if (versions.length > 0) {
                System.loadLibrary("freerdp-client" + versions[0]);
                System.loadLibrary("freerdp" + versions[0]);
                System.loadLibrary("winpr" + versions[0]);
            }

            mHasH264 = freerdp_has_h264();
            Log.i(TAG, "Successfully loaded native library. H264=" + mHasH264);
        } catch (UnsatisfiedLinkError e) {
            mIsLoaded = false;
            sLoadError = e.getMessage();
            Log.w(TAG, "libfreerdp-android.so load failed", e);
        } catch (Exception e) {
            mIsLoaded = false;
            sLoadError = e.getMessage();
            Log.e(TAG, "Failed to load libfreerdp-android.so", e);
        }
    }

    // ==================== native 方法（与 LibFreeRDP.java 一致） ====================

    private static native boolean freerdp_has_h264();
    private static native String freerdp_get_jni_version();
    private static native String freerdp_get_version();
    private static native String freerdp_get_build_revision();
    private static native String freerdp_get_build_config();
    private static native long freerdp_new(Context context);
    private static native void freerdp_free(long inst);
    private static native boolean freerdp_parse_arguments(long inst, String[] args);
    private static native boolean freerdp_connect(long inst);
    private static native boolean freerdp_disconnect(long inst);
    private static native boolean freerdp_update_graphics(long inst, Bitmap bitmap, int x, int y,
                                                          int width, int height);
    private static native boolean freerdp_send_cursor_event(long inst, int x, int y, int flags);
    private static native boolean freerdp_send_key_event(long inst, int keycode, boolean down);
    private static native boolean freerdp_send_unicodekey_event(long inst, int keycode,
                                                                boolean down);
    private static native boolean freerdp_send_clipboard_data(long inst, String data);
    private static native String freerdp_get_last_error_string(long inst);

    // ==================== 公共封装 ====================

    public static boolean isLoaded() {
        return mIsLoaded;
    }

    /** 返回库加载失败时的真实异常信息；加载成功或未尝试过时返回 null。 */
    public static String getLoadError() {
        return sLoadError;
    }

    public static boolean hasH264() {
        return mHasH264;
    }

    public static long newInstance(Context context) {
        return freerdp_new(context);
    }

    public static void freeInstance(long inst) {
        if (mConnectedInstances.containsKey(inst)) {
            freerdp_disconnect(inst);
        }
        // 等待断开完成（最多 2 秒），避免在连接尚未结束时释放实例
        long deadline = System.currentTimeMillis() + 2000;
        while (mConnectedInstances.containsKey(inst) && System.currentTimeMillis() < deadline) {
            try {
                Thread.sleep(50);
            } catch (InterruptedException e) {
                break;
            }
        }
        freerdp_free(inst);
        mConnectedInstances.remove(inst);
    }

    public static boolean connect(long inst) {
        if (mConnectedInstances.containsKey(inst)) {
            throw new RuntimeException("instance already connected");
        }
        return freerdp_connect(inst);
    }

    public static boolean disconnect(long inst) {
        if (mConnectedInstances.containsKey(inst)) {
            return freerdp_disconnect(inst);
        }
        return true;
    }

    public static boolean cancelConnection(long inst) {
        return freerdp_disconnect(inst);
    }

    public static boolean parseArguments(long inst, String[] args) {
        return freerdp_parse_arguments(inst, args);
    }

    public static boolean updateGraphics(long inst, Bitmap bitmap, int x, int y, int width,
                                         int height) {
        return freerdp_update_graphics(inst, bitmap, x, y, width, height);
    }

    public static boolean sendCursorEvent(long inst, int x, int y, int flags) {
        return freerdp_send_cursor_event(inst, x, y, flags);
    }

    public static boolean sendKeyEvent(long inst, int keycode, boolean down) {
        return freerdp_send_key_event(inst, keycode, down);
    }

    public static boolean sendUnicodeKeyEvent(long inst, int keycode, boolean down) {
        return freerdp_send_unicodekey_event(inst, keycode, down);
    }

    public static boolean sendClipboardData(long inst, String data) {
        return freerdp_send_clipboard_data(inst, data);
    }

    public static String getLastErrorString(long inst) {
        return freerdp_get_last_error_string(inst);
    }

    public static String getVersion() {
        return freerdp_get_version();
    }

    // ==================== JNI 回调（方法名与签名必须与 native 一致） ====================

    static void OnConnectionSuccess(long inst) {
        mConnectedInstances.put(inst, true);
        if (listener != null) {
            listener.OnConnectionSuccess(inst);
        }
    }

    static void OnConnectionFailure(long inst) {
        mConnectedInstances.remove(inst);
        if (listener != null) {
            listener.OnConnectionFailure(inst);
        }
    }

    static void OnPreConnect(long inst) {
        if (listener != null) {
            listener.OnPreConnect(inst);
        }
    }

    static void OnDisconnecting(long inst) {
        mConnectedInstances.remove(inst);
        if (listener != null) {
            listener.OnDisconnecting(inst);
        }
    }

    static void OnDisconnected(long inst) {
        mConnectedInstances.remove(inst);
        if (listener != null) {
            listener.OnDisconnected(inst);
        }
    }

    static void OnSettingsChanged(long inst, int width, int height, int bpp) {
        if (uiListener != null) {
            uiListener.OnSettingsChanged(inst, width, height, bpp);
        }
    }

    static boolean OnAuthenticate(long inst, StringBuilder username, StringBuilder domain,
                                  StringBuilder password) {
        if (uiListener != null) {
            return uiListener.OnAuthenticate(inst, username, domain, password);
        }
        return false;
    }

    static boolean OnGatewayAuthenticate(long inst, StringBuilder username, StringBuilder domain,
                                         StringBuilder password) {
        if (uiListener != null) {
            return uiListener.OnGatewayAuthenticate(inst, username, domain, password);
        }
        return false;
    }

    static int OnVerifyCertificateEx(long inst, String host, long port, String commonName,
                                     String subject, String issuer, String fingerprint,
                                     long flags) {
        if (uiListener != null) {
            return uiListener.OnVerifyCertificateEx(inst, host, port, commonName, subject, issuer,
                    fingerprint, flags);
        }
        return 0;
    }

    static int OnVerifyChangedCertificateEx(long inst, String host, long port, String commonName,
                                            String subject, String issuer, String fingerprint,
                                            String oldSubject, String oldIssuer,
                                            String oldFingerprint, long flags) {
        if (uiListener != null) {
            return uiListener.OnVerifyChangedCertificateEx(inst, host, port, commonName, subject,
                    issuer, fingerprint, oldSubject, oldIssuer, oldFingerprint, flags);
        }
        return 0;
    }

    static boolean OnExperimentalFeature(long inst, int feature) {
        if (uiListener != null) {
            return uiListener.OnExperimentalFeature(inst, feature);
        }
        return true;
    }

    static void OnGraphicsUpdate(long inst, int x, int y, int width, int height) {
        if (uiListener != null) {
            uiListener.OnGraphicsUpdate(inst, x, y, width, height);
        }
    }

    static void OnGraphicsResize(long inst, int width, int height, int bpp) {
        if (uiListener != null) {
            uiListener.OnGraphicsResize(inst, width, height, bpp);
        }
    }

    static void OnRemoteClipboardChanged(long inst, String data) {
        if (uiListener != null) {
            uiListener.OnRemoteClipboardChanged(inst, data);
        }
    }

    static void OnRemoteClipboardImageChanged(long inst, byte[] data) {
        if (uiListener != null) {
            uiListener.OnRemoteClipboardImageChanged(inst, data);
        }
    }

    static void OnPointerSet(long inst, int[] pixels, int width, int height, int hotX, int hotY) {
        if (uiListener != null) {
            uiListener.OnPointerSet(inst, pixels, width, height, hotX, hotY);
        }
    }

    static void OnPointerSetNull(long inst) {
        if (uiListener != null) {
            uiListener.OnPointerSetNull(inst);
        }
    }

    static void OnPointerSetDefault(long inst) {
        if (uiListener != null) {
            uiListener.OnPointerSetDefault(inst);
        }
    }

    static void OnRailWindowUpdate(long inst, long windowId, int width, int height, int[] pixels) {
        if (uiListener != null) {
            uiListener.OnRailWindowUpdate(inst, windowId, width, height, pixels);
        }
    }

    static void OnRailWindowMove(long inst, long windowId, int x, int y, int w, int h) {
        if (uiListener != null) {
            uiListener.OnRailWindowMove(inst, windowId, x, y, w, h);
        }
    }

    static void OnRailWindowHide(long inst, long windowId) {
        if (uiListener != null) {
            uiListener.OnRailWindowHide(inst, windowId);
        }
    }

    static void OnRailWindowDestroy(long inst, long windowId) {
        if (uiListener != null) {
            uiListener.OnRailWindowDestroy(inst, windowId);
        }
    }

    static void OnRailSessionEnd(long inst) {
        if (uiListener != null) {
            uiListener.OnRailSessionEnd(inst);
        }
    }

    static void OnRailMonitoredDesktop(long inst, long[] windowIds, long activeWindowId) {
        if (uiListener != null) {
            uiListener.OnRailMonitoredDesktop(inst, windowIds, activeWindowId);
        }
    }

    // ==================== 接口定义 ====================

    public interface EventListener {
        void OnPreConnect(long instance);
        void OnConnectionSuccess(long instance);
        void OnConnectionFailure(long instance);
        void OnDisconnecting(long instance);
        void OnDisconnected(long instance);
    }

    public interface UIEventListener {
        void OnSettingsChanged(long instance, int width, int height, int bpp);
        boolean OnAuthenticate(long instance, StringBuilder username, StringBuilder domain,
                               StringBuilder password);
        boolean OnGatewayAuthenticate(long instance, StringBuilder username, StringBuilder domain,
                                      StringBuilder password);
        int OnVerifyCertificateEx(long instance, String host, long port, String commonName,
                                  String subject, String issuer, String fingerprint, long flags);
        int OnVerifyChangedCertificateEx(long instance, String host, long port, String commonName,
                                         String subject, String issuer, String fingerprint,
                                         String oldSubject, String oldIssuer, String oldFingerprint,
                                         long flags);
        boolean OnExperimentalFeature(long instance, int feature);
        void OnGraphicsUpdate(long instance, int x, int y, int width, int height);
        void OnGraphicsResize(long instance, int width, int height, int bpp);
        void OnRemoteClipboardChanged(long instance, String data);
        void OnRemoteClipboardImageChanged(long instance, byte[] data);
        void OnPointerSet(long instance, int[] pixels, int width, int height, int hotX, int hotY);
        void OnPointerSetNull(long instance);
        void OnPointerSetDefault(long instance);
        void OnRailWindowUpdate(long instance, long windowId, int width, int height, int[] pixels);
        void OnRailWindowMove(long instance, long windowId, int x, int y, int w, int h);
        void OnRailWindowHide(long instance, long windowId);
        void OnRailWindowDestroy(long instance, long windowId);
        void OnRailSessionEnd(long instance);
        void OnRailMonitoredDesktop(long instance, long[] windowIds, long activeWindowId);
    }
}