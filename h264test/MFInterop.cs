using System.Runtime.InteropServices;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// Media Foundation COM 互操作层（最小接口集）。
/// 仅定义 H.264 编码所需的接口与函数，未使用的方法以占位声明保持 vtable 顺序。
/// vtable 布局严格遵循 Microsoft 文档。
/// </summary>
internal static class MFInterop
{
    // ============ GUID 常量 ============

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_BGRA32 = new("32374152-0000-0010-8000-00aa00389b71"); // 'ARGB' (BGRA 布局)
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");   // 'NV12'（编码器实测支持的输入格式）
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");   // 'H264'

    public static readonly Guid CLSID_CMSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    // 正确的 IMFTransform IID（mftransform.h）。此前误用 6ff27a4d-... 导致
    // CoCreateInstance 返回 E_NOINTERFACE，H.264 从未激活成功（已实测验证此 IID 可用）。
    public static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");

    // CODECAPI 属性（可通过 MFT 的 IMFAttributes store 设置，codecapi.h）
    /// <summary>GOP 大小（两个关键帧之间的帧数）。默认无限——丢了解码起点后画面永久黑屏。</summary>
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc7e0c8");
    /// <summary>强制下一帧为关键帧（运行时动态设置，触发立即 IDR 刷新）。</summary>
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("159c60be-33d5-46f0-8d1d-0972d1c142a9");
    /// <summary>低延迟模式（codecapi.h CODECAPI_AVLowLatencyMode，pc-client v1.1.56 启用）。</summary>
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    public const int MF_VERSION = 0x20070; // MF_API_VERSION (2.7)

    // ============ MF 全局函数 ============

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int Version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    public const uint CLSCTX_INPROC_SERVER = 0x1;

    /// <summary>把 COM 指针包装成指定接口。</summary>
    public static T GetObject<T>(IntPtr ptr) where T : class
    {
        var obj = Marshal.GetObjectForIUnknown(ptr);
        return (T)obj;
    }
}

/// <summary>
/// IMFAttributes（vtable 33 槽：3 IUnknown 隐式 + 30 自身方法）。
/// 注意：ComImport + InterfaceIsIUnknown 下 .NET 隐式提供 IUnknown 三槽，
/// 绝不能手动声明 QueryInterface/AddRef/Release，否则全部方法后移 3 槽
/// （SetGUID 会落到 SetUnknown 槽位导致 AccessViolation）。
/// </summary>
[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    // IMFAttributes（按 vtable 顺序，未用方法占位）
    [PreserveSig] int GetItem_();
    [PreserveSig] int GetItemType_();
    [PreserveSig] int CompareItem_();
    [PreserveSig] int Compare_();
    [PreserveSig] int GetUINT32_(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64_();
    [PreserveSig] int GetDouble_();
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength_();
    [PreserveSig] int GetString_();
    [PreserveSig] int GetAllocatedString_();
    [PreserveSig] int GetBlobSize_();
    [PreserveSig] int GetBlob_();
    [PreserveSig] int GetAllocatedBlob_();
    [PreserveSig] int GetUnknown_();
    [PreserveSig] int SetItem_();
    [PreserveSig] int DeleteItem_();
    [PreserveSig] int DeleteAllItems_();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble_();
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString_();
    [PreserveSig] int SetBlob_();
    [PreserveSig] int SetUnknown_();
    [PreserveSig] int LockStore_();
    [PreserveSig] int UnlockStore_();
    [PreserveSig] int GetCount_();
    [PreserveSig] int GetItemByIndex_();
    [PreserveSig] int CopyAllItems_();
}

/// <summary>
/// IMFMediaType（拍平声明：3 IUnknown 隐式 + 30 IMFAttributes + 5 自身 = 38 槽）。
/// 不用接口继承：CLR 对派生 ComImport 接口的自有方法槽位分配与真实 COM vtable 不符
///（实测 IMFSample 派生方法被排到槽 3 起，SetSampleTime 实际命中 GetItem），
/// 拍平后槽位与 mfobjects.h 完全一致。
/// </summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // ---- IMFAttributes（槽 3-32）----
    [PreserveSig] int GetItem_();
    [PreserveSig] int GetItemType_();
    [PreserveSig] int CompareItem_();
    [PreserveSig] int Compare_();
    [PreserveSig] int GetUINT32_(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64_();
    [PreserveSig] int GetDouble_();
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength_();
    [PreserveSig] int GetString_();
    [PreserveSig] int GetAllocatedString_();
    [PreserveSig] int GetBlobSize_();
    [PreserveSig] int GetBlob_();
    [PreserveSig] int GetAllocatedBlob_();
    [PreserveSig] int GetUnknown_();
    [PreserveSig] int SetItem_();
    [PreserveSig] int DeleteItem_();
    [PreserveSig] int DeleteAllItems_();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble_();
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString_();
    [PreserveSig] int SetBlob_();
    [PreserveSig] int SetUnknown_();
    [PreserveSig] int LockStore_();
    [PreserveSig] int UnlockStore_();
    [PreserveSig] int GetCount_();
    [PreserveSig] int GetItemByIndex_();
    [PreserveSig] int CopyAllItems_();
    // ---- IMFMediaType（槽 33-37）----
    [PreserveSig] int GetMajorType_();
    [PreserveSig] int IsCompressedFormat_();
    [PreserveSig] int IsEqual_();
    [PreserveSig] int GetRepresentation_();
    [PreserveSig] int FreeRepresentation_();
}

/// <summary>IMFMediaBuffer（隐式 IUnknown + 5 个方法，vtable 8 槽）。</summary>
[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    // IMFMediaBuffer
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(int cbCurrentLength);
    [PreserveSig] int GetMaxLength(out int pcbMaxLength);
}

/// <summary>
/// IMFSample（拍平声明：3 IUnknown 隐式 + 30 IMFAttributes + 14 自身 = 47 槽）。
/// 同 IMFMediaType：不用接口继承，避免 CLR 派生接口槽位错乱。
/// 注意：IMFSample 自身方法的声明顺序必须与 mfobjects.h 完全一致
///（GetSampleFlags/SetSampleFlags 在 GetSampleTime/SetSampleTime 之前）。
/// v1.1.33 黑屏根因：Flags 方法曾被排在 Time/Duration 之后，整体错位 2 槽——
/// SetSampleDuration 实际打到 SetSampleTime，duration 从未设置，
/// 编码器每帧 ProcessInput 报 MF_E_NO_SAMPLE_DURATION(0xC00D36C9)，编码线程立即退出，
/// Android 端收到 0 帧黑屏。已用 C++ 原生对照程序验证。
/// </summary>
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // ---- IMFAttributes（槽 3-32）----
    [PreserveSig] int GetItem_();
    [PreserveSig] int GetItemType_();
    [PreserveSig] int CompareItem_();
    [PreserveSig] int Compare_();
    [PreserveSig] int GetUINT32_(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64_();
    [PreserveSig] int GetDouble_();
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength_();
    [PreserveSig] int GetString_();
    [PreserveSig] int GetAllocatedString_();
    [PreserveSig] int GetBlobSize_();
    [PreserveSig] int GetBlob_();
    [PreserveSig] int GetAllocatedBlob_();
    [PreserveSig] int GetUnknown_();
    [PreserveSig] int SetItem_();
    [PreserveSig] int DeleteItem_();
    [PreserveSig] int DeleteAllItems_();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble_();
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString_();
    [PreserveSig] int SetBlob_();
    [PreserveSig] int SetUnknown_();
    [PreserveSig] int LockStore_();
    [PreserveSig] int UnlockStore_();
    [PreserveSig] int GetCount_();
    [PreserveSig] int GetItemByIndex_();
    [PreserveSig] int CopyAllItems_();
    // ---- IMFSample（槽 33-46，严格按 mfobjects.h 声明顺序：Flags 在 Time/Duration 之前！）----
    [PreserveSig] int GetSampleFlags_(out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags_(uint dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out int pcBufferCount);
    [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer_();
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex_();
    [PreserveSig] int RemoveAllBuffers_();
    [PreserveSig] int GetTotalLength_();
    [PreserveSig] int CopyToBuffer_();
}

/// <summary>IMFTransform（隐式 IUnknown + 23 个方法，vtable 26 槽）。</summary>
[ComImport]
[Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    // IMFTransform（按 vtable 顺序）
    [PreserveSig] int GetStreamLimits_();
    [PreserveSig] int GetStreamCount_();
    [PreserveSig] int GetStreamIDs_();
    [PreserveSig] int GetInputStreamInfo(int dwInputStreamID, out MFT_INPUT_STREAM_INFO pInfo);
    [PreserveSig] int GetOutputStreamInfo(int dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pInfo);
    [PreserveSig] int GetAttributes(out IMFAttributes? ppAttributes);
    [PreserveSig] int GetInputStreamAttributes_();
    [PreserveSig] int GetOutputStreamAttributes_();
    [PreserveSig] int DeleteInputStream_();
    [PreserveSig] int AddInputStreams_();
    [PreserveSig] int GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, out IMFMediaType? ppType);
    [PreserveSig] int GetOutputAvailableType_();
    [PreserveSig] int SetInputType(int dwInputStreamID, IMFMediaType pType, int dwFlags);
    [PreserveSig] int SetOutputType(int dwOutputStreamID, IMFMediaType pType, int dwFlags);
    [PreserveSig] int GetInputCurrentType_();
    [PreserveSig] int GetOutputCurrentType_();
    [PreserveSig] int GetInputStatus_();
    [PreserveSig] int GetOutputStatus_();
    [PreserveSig] int SetOutputBounds_();
    [PreserveSig] int ProcessEvent_();
    [PreserveSig] int ProcessMessage(int eMessageType, IntPtr ulParam);
    [PreserveSig] int ProcessInput(int dwInputStreamID, IMFSample pSample, int dwFlags);
    [PreserveSig] int ProcessOutput(int dwFlags, int cOutputBufferCount,
        ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out int pdwStatus);
}

/// <summary>MFT_OUTPUT_DATA_BUFFER 结构。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public int dwStreamID;
    public IntPtr pSample;
    public int dwStatus;
    public IntPtr pEvents;
}

/// <summary>MFT_INPUT_STREAM_INFO 结构（mftransform.h，占位签名用）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_INPUT_STREAM_INFO
{
    public long hnsMaxLatency;
    public int dwFlags;
    public int cbSize;
    public int cbMaxLookahead;
    public int cbAlignment;
}

/// <summary>MFT_OUTPUT_STREAM_INFO 结构（mftransform.h，占位签名用）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO
{
    public int dwFlags;
    public int cbSize;
    public int cbAlignment;
}

/// <summary>Media Foundation HRESULT 辅助。</summary>
internal static class MFHr
{
    // 常见 MF 错误码
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D76);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5); // 注意：B2 是 MF_E_INVALIDREQUEST，勿混淆
    public const int MF_E_NO_SAMPLE_TIMESTAMP = unchecked((int)0xC00D36C8);
    public const int MF_E_NO_SAMPLE_DURATION = unchecked((int)0xC00D36C9);

    // MFT_MESSAGE_*（ProcessMessage 用）
    public const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000001;
    public const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000002;
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    public static bool Succeeded(int hr) => hr >= 0;
}
