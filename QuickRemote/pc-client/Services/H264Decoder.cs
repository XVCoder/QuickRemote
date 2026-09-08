using System.Runtime.InteropServices;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// H.264 解码器（Media Foundation IMFTransform 直接调用）。
/// 输入 H.264 NAL 流（Annex-B，与 H264Encoder 输出一致），输出 BGRA 帧。
/// 使用系统软件 H.264 解码器 MFT（CLSID_CMSH264DecoderMFT，Windows 8+ 内置）。
/// 分辨率在首个 SPS 解析后通过 MF_E_TRANSFORM_STREAM_CHANGE 通知，
/// 调用方通过 DecodedFrame 的宽高感知变化（重建渲染目标）。
/// 注意：所有 COM 操作（创建/解码/销毁）必须在同一线程调用
/// （与 H264Encoder 相同的 apartment 约束），本类不做内部同步。
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private IMFTransform? _decoder;
    private bool _disposed;
    private long _sampleTime; // 100ns 单位，人工推进（解码器要求单调递增）

    /// <summary>解码输出缓冲大小（来自 GetOutputStreamInfo，不足时 ProcessOutput 报错）。</summary>
    private int _outputBufferSize;

    /// <summary>当前视频宽度（流变化协商后更新）。</summary>
    public int Width { get; private set; }

    /// <summary>当前视频高度（流变化协商后更新）。</summary>
    public int Height { get; private set; }

    /// <summary>解码器是否可用（创建成功）。</summary>
    public bool IsAvailable => _decoder != null;

    /// <summary>解码输出帧（BGRA，top-down，stride = Width*4）。</summary>
    public sealed class DecodedFrame
    {
        public required byte[] Bgra { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    /// <summary>
    /// 创建解码器 MFT 并设置 H.264 输入类型。
    /// widthHint/heightHint 来自握手控制帧（可为 0：解码器解析 SPS 后自行协商）。
    /// </summary>
    public void Initialize(int widthHint, int heightHint)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(H264Decoder));
        if (_decoder != null) throw new InvalidOperationException("Decoder already initialized");

        MfPlatform.EnsureStarted();

        _hintWidth = widthHint;
        _hintHeight = heightHint;

        _decoder = CreateDecoder();
        if (_decoder == null)
            throw new COMException("Create H.264 decoder failed: 0x80040154 (no decodable H.264 MFT)");

        try
        {
            // 设置输入类型（H.264）：只需主类型/子类型，分辨率由 SPS 提供
            //（设置 hint 无害，解码器以 SPS 为准）
            var inputType = CreateMediaType();
            try
            {
                SetGuid(inputType, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
                SetGuid(inputType, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_H264);
                if (widthHint > 0 && heightHint > 0)
                    SetU64(inputType, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(widthHint, heightHint));

                int hr = _decoder.SetInputType(0, inputType, 0);
                if (MFHr.Succeeded(hr) == false)
                    throw new COMException($"SetInputType failed: 0x{hr:X8}");
            }
            finally
            {
                Marshal.ReleaseComObject(inputType);
            }

            // 输出类型：MS 解码器 MFT 要求 ProcessOutput 前必须 SetOutputType，
            // 否则恒返 0xC00D6D60 MF_E_TRANSFORM_TYPE_NOT_SET（不走 STREAM_CHANGE
            // 路径，输出永远拉不出来 → 输入积压 NOTACCEPTING）。先设初始 NV12，
            // 实际分辨率在首个 SPS 解析后的 STREAM_CHANGE 中重新协商。
            SetInitialOutputType();

            // 低延迟模式（必须在 BEGIN_STREAMING 前设置）：MS 解码器默认重排缓冲，
            // 持续输入也不吐帧（实测 27 帧全持有，仅 DRAIN 冲出）——远程桌面流
            // 必须"解码即输出"。设置失败仅降级（老系统解码器可能不支持）。
            try
            {
                int attrHr = _decoder.GetAttributes(out var decAttrs);
                if (MFHr.Succeeded(attrHr) && decAttrs != null)
                {
                    var llKey = MFInterop.CODECAPI_AVLowLatencyMode;
                    decAttrs.SetUINT32(ref llKey, 1);
                    Marshal.ReleaseComObject(decAttrs);
                }
            }
            catch { /* 低延迟不支持时保持默认行为 */ }

            _decoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);

            // 输出缓冲初值：按握手 hint 估算 NV12 大小（协商分辨率后按实际修正）；
            // 无 hint 时按 1080p。STREAM_CHANGE 协商前的输出帧也够用。
            _outputBufferSize = (widthHint > 0 && heightHint > 0)
                ? widthHint * heightHint * 3 / 2
                : 1920 * 1080 * 3 / 2;
        }
        catch
        {
            CleanupMf();
            throw;
        }
    }

    /// <summary>
    /// 设置初始输出类型：取解码器可用的第 0 个输出类型并确认是 NV12
    ///（ConvertNv12ToBgra 只认 NV12）。此时 SPS 未解析，类型不含实际分辨率——
    /// 无碍，SetOutputType 可通过，分辨率由后续 STREAM_CHANGE 协商修正。
    /// </summary>
    private void SetInitialOutputType()
    {
        int hr = _decoder!.GetOutputAvailableType(0, 0, out var type);
        if (MFHr.Succeeded(hr) == false || type == null)
            throw new COMException($"GetOutputAvailableType failed: 0x{hr:X8}");
        try
        {
            var subKey = MFInterop.MF_MT_SUBTYPE;
            if (MFHr.Succeeded(type.GetGUID(ref subKey, out var sub)) == false ||
                sub != MFInterop.MFVideoFormat_NV12)
                throw new COMException($"Unexpected decoder output type: {sub}");

            int setHr = _decoder.SetOutputType(0, type, 0);
            if (MFHr.Succeeded(setHr) == false)
                throw new COMException($"SetOutputType failed: 0x{setHr:X8}");
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    /// <summary>
    /// 送入一段 H.264 数据，返回最新解码帧。
    /// 返回 null = 正常（解码器需要更多输入：B 帧重排/流变化未完成）。
    /// 一次输入可能产出多帧（重排 flush），只保留最新一帧（低延迟优先）。
    /// </summary>
    public DecodedFrame? Decode(byte[] h264Data)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(H264Decoder));
        var decoder = _decoder ?? throw new InvalidOperationException("Decoder not initialized");

        // 1. 包装输入 sample
        int hr = MFInterop.MFCreateMemoryBuffer(h264Data.Length, out var bufferPtr);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateMemoryBuffer failed: 0x{hr:X8}");
        var buffer = MFInterop.GetObject<IMFMediaBuffer>(bufferPtr);
        try
        {
            hr = buffer.Lock(out var dataPtr, out _, out _);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"Buffer Lock failed: 0x{hr:X8}");
            Marshal.Copy(h264Data, 0, dataPtr, h264Data.Length);
            buffer.Unlock();
            buffer.SetCurrentLength(h264Data.Length);

            hr = MFInterop.MFCreateSample(out var samplePtr);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateSample failed: 0x{hr:X8}");
            var sample = MFInterop.GetObject<IMFSample>(samplePtr);
            try
            {
                sample.AddBuffer(buffer);
                _sampleTime += 333_333; // 30fps 假定时长，单调递增即可
                sample.SetSampleTime(_sampleTime);

                hr = decoder.ProcessInput(0, sample, 0);
                if (MFHr.Succeeded(hr) == false)
                    throw new COMException($"ProcessInput failed: 0x{hr:X8}");
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }

        // 2. 拉取输出（可能多帧，保留最新）
        DecodedFrame? last = null;
        for (int i = 0; i < 8; i++) // 上限防护：单次输入的输出帧数有限
        {
            var frame = ProcessOneOutput(decoder);
            if (frame == null) break;
            last = frame;
        }
        return last;
    }

    /// <summary>拉取一帧输出；null = 无更多输出（或流变化已处理但需继续输入）。</summary>
    private DecodedFrame? ProcessOneOutput(IMFTransform decoder)
    {
        while (true)
        {
            int hr = MFInterop.MFCreateSample(out var samplePtr);
            if (MFHr.Succeeded(hr) == false) return null;
            var outputSample = MFInterop.GetObject<IMFSample>(samplePtr);
            try
            {
                hr = MFInterop.MFCreateMemoryBuffer(_outputBufferSize, out var bufferPtr);
                if (MFHr.Succeeded(hr) == false) return null;
                var outputBuffer = MFInterop.GetObject<IMFMediaBuffer>(bufferPtr);
                try
                {
                    outputSample.AddBuffer(outputBuffer);

                    var outputData = new MFT_OUTPUT_DATA_BUFFER { pSample = samplePtr };
                    hr = decoder.ProcessOutput(0, 1, ref outputData, out _);
                    if (hr == MFHr.MF_E_TRANSFORM_STREAM_CHANGE)
                    {
                        // 首个 SPS 解析完成 / 分辨率变化：协商输出类型后重试
                        if (!NegotiateOutputType(decoder)) return null;
                        continue;
                    }
                    if (MFHr.Succeeded(hr) == false)
                        return null; // MF_E_TRANSFORM_NEED_MORE_INPUT 等

                    outputSample.GetBufferByIndex(0, out var decodedBuf);
                    try
                    {
                        decodedBuf.Lock(out var ptr, out _, out var curLen);
                        try
                        {
                            // 低延迟 + 输入类型带尺寸时，解码器可能跳过 STREAM_CHANGE
                            // 直接出帧（Win11 26200 实测）——此时用握手 hint 初始化分辨率，
                            // stride 由实际数据长度反推（NV12 = stride*(h + h/2)）。
                            if (Width <= 0 || Height <= 0)
                            {
                                if (_hintWidth <= 0 || _hintHeight <= 0 || curLen <= 0)
                                    return null; // 无 hint 且无协商，只能丢弃
                                Width = _hintWidth;
                                Height = _hintHeight;
                                int implied = curLen / (Height + (Height + 1) / 2);
                                _yStride = implied > 0 ? implied : Width;
                            }
                            var nv12 = new byte[curLen];
                            Marshal.Copy(ptr, nv12, 0, curLen);
                            return ConvertNv12ToBgra(nv12);
                        }
                        finally
                        {
                            decodedBuf.Unlock();
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(decodedBuf);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(outputBuffer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(outputSample);
            }
        }
    }

    /// <summary>
    /// 流变化协商：遍历解码器给出的输出类型，找第一个带 FRAME_SIZE 的 NV12 类型
    ///（index 0 可能是无尺寸模板——Win11 26200 实测低延迟模式下 STREAM_CHANGE 后
    /// 出帧但 index 0 缺尺寸，导致协商"成功"却拿不到分辨率 → 后续帧转换抛异常）。
    /// 找不到带尺寸的类型时用握手 hint 兜底。返回 false = 完全协商失败。
    /// </summary>
    private bool NegotiateOutputType(IMFTransform decoder)
    {
        for (int index = 0; index < 8; index++)
        {
            int hr = decoder.GetOutputAvailableType(0, index, out var type);
            if (!MFHr.Succeeded(hr) || type == null)
                break; // 可用类型枚举完毕

            try
            {
                // 只认 NV12（ConvertNv12ToBgra 只处理 NV12）
                var subKey = MFInterop.MF_MT_SUBTYPE;
                if (MFHr.Succeeded(type.GetGUID(ref subKey, out var sub)) == false ||
                    sub != MFInterop.MFVideoFormat_NV12)
                    continue;

                var sizeKey = MFInterop.MF_MT_FRAME_SIZE;
                if (MFHr.Succeeded(type.GetUINT64(ref sizeKey, out var size)))
                {
                    Width = (int)(size >> 32);
                    Height = (int)(size & 0xFFFFFFFF);
                }
                if (Width <= 0 || Height <= 0)
                    continue; // 该 index 无尺寸，试下一个

                // stride（可能对齐到 32/64，绝不能假设 == width）
                var strideKey = MFInterop.MF_MT_DEFAULT_STRIDE;
                var stride = Width;
                if (MFHr.Succeeded(type.GetUINT32(ref strideKey, out var strideVal)) && strideVal > 0)
                    stride = (int)strideVal;
                _yStride = stride;

                hr = decoder.SetOutputType(0, type, 0);
                if (!MFHr.Succeeded(hr))
                    return false;

                // NV12 缓冲：Y 平面 stride*h + UV 平面 stride*ceil(h/2)
                _outputBufferSize = Math.Max(_outputBufferSize,
                    stride * Height + stride * ((Height + 1) / 2));
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }
        }

        // 兜底：无带尺寸的类型时，用初始 hint（来自握手帧 = 实际分辨率）+ index 0 类型
        int hr0 = decoder.GetOutputAvailableType(0, 0, out var fallback);
        if (!MFHr.Succeeded(hr0) || fallback == null || _hintWidth <= 0)
            return false;
        try
        {
            int setHr = decoder.SetOutputType(0, fallback, 0);
            if (!MFHr.Succeeded(setHr))
                return false;
            Width = _hintWidth;
            Height = _hintHeight;
            _yStride = _hintWidth; // 软件 MFT 的 NV12 stride 通常 == width
            _outputBufferSize = Math.Max(_outputBufferSize,
                _yStride * Height + _yStride * ((Height + 1) / 2));
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(fallback);
        }
    }

    /// <summary>握手 hint（分辨率兜底：STREAM_CHANGE 协商拿不到尺寸时使用）。</summary>
    private int _hintWidth, _hintHeight;

    /// <summary>Y 平面 stride（UV 平面 stride 与之相同，流变化协商时更新）。</summary>
    private int _yStride;

    /// <summary>NV12 → BGRA（BT.601 full-range，与 H264Encoder.ConvertBgraToNv12 互逆）。</summary>
    private unsafe DecodedFrame ConvertNv12ToBgra(byte[] nv12)
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("解码分辨率未知（流变化未协商）");

        int yStride = _yStride > 0 ? _yStride : w;
        // UV 平面紧跟 Y 平面：偏移 = yStride * h（NV12 布局，h 恒为偶数——H.264 宏块对齐）
        int uvOffset = yStride * h;
        int minLen = uvOffset + yStride * ((h + 1) / 2);
        if (nv12.Length < minLen)
            throw new ArgumentException($"NV12 buffer too small: {nv12.Length} < {minLen}");

        var bgra = new byte[w * h * 4];
        fixed (byte* srcBase = nv12, dstBase = bgra)
        {
            for (int row = 0; row < h; row++)
            {
                byte* yRow = srcBase + row * yStride;
                byte* uvRow = srcBase + uvOffset + (row >> 1) * yStride;
                byte* dRow = dstBase + row * w * 4;

                for (int col = 0; col < w; col++)
                {
                    int y = yRow[col];
                    int u = uvRow[col & ~1] - 128;     // U 在偶数下标
                    int v = uvRow[(col & ~1) + 1] - 128; // V 在奇数下标

                    // JFIF 整数近似（与编码端正向变换互逆）
                    int r = y + ((91881 * v) >> 16);
                    int g = y - ((22554 * u + 46802 * v) >> 16);
                    int b = y + ((116130 * u) >> 16);

                    int o = col * 4;
                    dRow[o] = (byte)Math.Clamp(b, 0, 255);
                    dRow[o + 1] = (byte)Math.Clamp(g, 0, 255);
                    dRow[o + 2] = (byte)Math.Clamp(r, 0, 255);
                    dRow[o + 3] = 255;
                }
            }
        }

        return new DecodedFrame { Bgra = bgra, Width = w, Height = h };
    }

    // ============ 辅助 ============

    /// <summary>
    /// 枚举本机可用的 H.264 视频解码器 MFT，逐个尝试创建，返回第一个可用的；
    /// 全部失败返回 null（本机无可用 H.264 解码器，如精简版/无 Media Feature Pack）。
    /// 不直接 CoCreateInstance(CLSID_CMSH264DecoderMFT)——该 CLSID 在部分 Windows 安装
    /// 上未注册为普通 COM 类（REGDB_E_CLASSNOTREG 0x80040154），但 MFTEnumEx 仍能枚举到
    /// 实际可创建的解码器。
    /// </summary>
    private static IMFTransform? CreateDecoder()
    {
        var inputType = new MFT_REGISTER_TYPE_INFO
        {
            guidMajorType = MFInterop.MFMediaType_Video,
            guidSubtype = MFInterop.MFVideoFormat_H264,
        };

        // 排除硬件（DXVA）MFT：需要 D3D 设备初始化，直接解码不可用，只枚举同步软件解码器
        uint flags = MFInterop.MFT_ENUM_FLAG_SYNCMFT;
        int hr;
        IntPtr pActivates;
        int count;
        unsafe
        {
            // inputType 为栈上局部变量，天然固定，直接取地址
            hr = MFInterop.MFTEnumEx(MFInterop.MFT_CATEGORY_VIDEO_DECODER, flags,
                (IntPtr)(&inputType), IntPtr.Zero, out pActivates, out count);
        }
        if (MFHr.Succeeded(hr) == false || count <= 0)
            return null;

        try
        {
            var iid = MFInterop.IID_IMFTransform;
            for (int i = 0; i < count; i++)
            {
                nint pActivate;
                try
                {
                    pActivate = Marshal.ReadIntPtr(pActivates, i * IntPtr.Size);
                }
                catch
                {
                    continue;
                }
                if (pActivate == IntPtr.Zero) continue;

                var activate = MFInterop.GetObject<IMFActivate>(pActivate);
                try
                {
                    // 标准激活路径：IMFActivate::ActivateObject 直接创建 MFT。
                    // 不走"读 MFT_TRANSFORM_CLSID_Attribute 再 CoCreateInstance"：
                    // 该属性 GUID 曾记错（0c1afd2c-...，实际 6821c42b-...），且
                    // activate 激活不依赖任何属性读取，实测（Win11 26200）稳定成功。
                    int activateHr = activate.ActivateObject(ref iid, out var decoderPtr);
                    if (MFHr.Succeeded(activateHr) == false || decoderPtr == IntPtr.Zero)
                        continue; // 该解码器不可激活，试下一个

                    return MFInterop.GetObject<IMFTransform>(decoderPtr);
                }
                finally
                {
                    try { Marshal.ReleaseComObject(activate); } catch { }
                }
            }
        }
        finally
        {
            if (pActivates != IntPtr.Zero)
            {
                // 引用所有权：GetObject<T> 已把每个 activate 元素的引用转移给 RCW
                //（循环内 try/finally 的 ReleaseComObject 配对释放），此处若再逐元素
                // Marshal.Release 会双重释放 → COM 引用计数腐坏 → 0xC0000409 fastfail
                //（v1.1.50 闭环实测崩点）。只释放数组内存本身。
                MFInterop.CoTaskMemFree(pActivates);
            }
        }
        return null;
    }

    /// <summary>
    /// 判断本机是否可创建 H.264 解码器（供主控端握手前探测；避免每帧重试失败）。
    /// 创建成功的解码器立即释放（探测用，不进入解码流程）。
    /// </summary>
    public static bool IsH264DecoderAvailable()
    {
        try
        {
            var d = CreateDecoder();
            if (d == null) return false;
            try { d.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            Marshal.ReleaseComObject(d);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IMFMediaType CreateMediaType()
    {
        int hr = MFInterop.MFCreateMediaType(out var ptr);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateMediaType failed: 0x{hr:X8}");
        return MFInterop.GetObject<IMFMediaType>(ptr);
    }

    private static void SetGuid(IMFMediaType attrs, Guid key, Guid value)
    {
        int hr = attrs.SetGUID(ref key, ref value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetGUID failed: 0x{hr:X8}");
    }

    private static void SetU64(IMFMediaType attrs, Guid key, ulong value)
    {
        int hr = attrs.SetUINT64(ref key, value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetUINT64 failed: 0x{hr:X8}");
    }

    private static ulong MakeFrameSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;

    private void CleanupMf()
    {
        if (_decoder != null)
        {
            try { _decoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_decoder); } catch { }
            _decoder = null;
        }
        // 不调 MFShutdown（MfPlatform 全局策略，见类注释）
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupMf();
    }
}
