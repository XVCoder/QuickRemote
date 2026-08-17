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
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");   // 'H264'

    public static readonly Guid CLSID_CMSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    public static readonly Guid IID_IMFTransform = new("6ff27a4d-114f-4e62-b7cd-b59a5a02777c");
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");

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

/// <summary>IMFAttributes（vtable 33 个槽：3 IUnknown + 30 自身）。</summary>
[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int q1();
    [PreserveSig] int q2();
    [PreserveSig] int q3();
    [PreserveSig] int GetItem_();
    [PreserveSig] int GetItemType_();
    [PreserveSig] int CompareItem_();
    [PreserveSig] int Compare_();
    [PreserveSig] int GetUINT32_(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64_();
    [PreserveSig] int GetDouble_();
    [PreserveSig] int GetGUID_();
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
    [PreserveSig] int SetUINT64_();
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

/// <summary>IMFMediaType（继承 IMFAttributes，+5 个方法，共 38 槽）。</summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] new int GetMajorType_();
    [PreserveSig] new int IsCompressedFormat_();
    [PreserveSig] new int IsEqual_();
    [PreserveSig] new int GetRepresentation_();
    [PreserveSig] new int FreeRepresentation_();
}

/// <summary>IMFMediaBuffer（vtable 8 槽）。</summary>
[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int q1();
    [PreserveSig] int q2();
    [PreserveSig] int q3();
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(int cbCurrentLength);
    [PreserveSig] int GetMaxLength(out int pcbMaxLength);
}

/// <summary>IMFSample（继承 IMFMediaBuffer，+14 方法，共 22 槽）。</summary>
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFMediaBuffer
{
    [PreserveSig] new int GetSampleTime_();
    [PreserveSig] new int SetSampleTime(long hnsSampleTime);
    [PreserveSig] new int GetSampleDuration_();
    [PreserveSig] new int SetSampleDuration_();
    [PreserveSig] new int GetSampleFlags_();
    [PreserveSig] new int SetSampleFlags_();
    [PreserveSig] new int GetBufferCount(out int pcBufferCount);
    [PreserveSig] new int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] new int ConvertToContiguousBuffer_();
    [PreserveSig] new int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] new int RemoveBufferByIndex_();
    [PreserveSig] new int RemoveAllBuffers_();
    [PreserveSig] new int GetTotalLength_();
    [PreserveSig] new int CopyToBuffer_();
}

/// <summary>IMFTransform（vtable 26 槽）。</summary>
[ComImport]
[Guid("6ff27a4d-114f-4e62-b7cd-b59a5a02777c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int q1();
    [PreserveSig] int q2();
    [PreserveSig] int q3();
    [PreserveSig] int GetStreamLimits_();
    [PreserveSig] int GetStreamCount_();
    [PreserveSig] int GetStreamIDs_();
    [PreserveSig] int GetInputStreamInfo_();
    [PreserveSig] int GetOutputStreamInfo_();
    [PreserveSig] int GetAttributes_();
    [PreserveSig] int GetInputStreamAttributes_();
    [PreserveSig] int GetOutputStreamAttributes_();
    [PreserveSig] int DeleteInputStream_();
    [PreserveSig] int AddInputStreams_();
    [PreserveSig] int GetInputAvailableType_();
    [PreserveSig] int GetOutputAvailableType_();
    [PreserveSig] int SetInputType(int dwInputStreamID, IMFMediaType pType, int dwFlags);
    [PreserveSig] int SetOutputType(int dwOutputStreamID, IMFMediaType pType, int dwFlags);
    [PreserveSig] int GetInputCurrentType_();
    [PreserveSig] int GetOutputCurrentType_();
    [PreserveSig] int GetInputStatus_();
    [PreserveSig] int GetOutputStatus_();
    [PreserveSig] int SetOutputBounds_();
    [PreserveSig] int ProcessEvent_();
    [PreserveSig] int ProcessMessage_();
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

/// <summary>Media Foundation HRESULT 辅助。</summary>
internal static class MFHr
{
    // 常见 MF 错误码
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D76);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B2);

    public static bool Succeeded(int hr) => hr >= 0;
}
