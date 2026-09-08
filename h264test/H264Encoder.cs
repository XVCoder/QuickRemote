using System.IO;
using System.Runtime.InteropServices;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// H.264 编码器（Media Foundation IMFTransform 直接调用）。
/// 输入 BGRA 帧（内部转 NV12 —— MS 编码器仅接受 IYUV/YV12/NV12/YUY2），
/// 输出 H.264 NAL unit 流（elementary stream，Annex-B 起始码）。
/// 使用系统软件 H.264 编码器 MFT（mfh264enc.dll，CLSID_CMSH264EncoderMFT）。
/// 系统无 H.264 编码器时 CoCreateInstance 会失败，
/// 由调用方回退到 JpegFrameEncoder。
/// </summary>
public sealed class H264Encoder : IFrameEncoder
{
    /// <inheritdoc/>
    public string CodecName => "h264";

    private IMFTransform? _encoder;
    private IMFMediaType? _inputType;
    private bool _initialized;
    private bool _disposed;
    private long _sampleTime = 0;
    private long _sampleDuration = 333333; // 30fps 的 100ns 单位

    /// <summary>NV12 中转缓冲（BGRA 转 NV12 后送编码器，避免每帧分配）。</summary>
    private byte[] _nv12 = Array.Empty<byte>();

    /// <summary>NV12 数据有效长度（Y 平面 + UV 平面）。</summary>
    private int _nv12Length;

    /// <summary>编码器输出缓冲大小（来自 GetOutputStreamInfo.cbSize，输出 buffer 小于它 ProcessOutput 报 MF_E_BUFFERTOOSMALL）。</summary>
    private int _outputBufferSize;

    /// <summary>输入帧宽度。</summary>
    public int Width { get; private set; }

    /// <summary>输入帧高度。</summary>
    public int Height { get; private set; }

    /// <summary>编码器是否可用（创建/初始化成功）。</summary>
    public bool IsAvailable => _initialized;

    /// <summary>
    /// MF 平台全局启动标志（跨实例静态）。
    /// v1.1.34 崩溃根因：每次 Dispose 调 MFShutdown，引用计数 0↔1 震荡——
    /// ReconfigureEncoder（quality 调整）销毁重建编码器期间平台被拆，
    /// 此时 EncodeLoop 并发 ProcessInput 访问已拆平台的 MFT → AccessViolation 进程闪退。
    /// MFStartup 仅首次调用真正启动（后续只递增计数），进程退出由 OS 回收，不再 Shutdown。
    /// </summary>
    private static int _mfStarted;

    /// <summary>初始化编码器。</summary>
    public void Initialize(int width, int height, int fps = 30, int bitrateKbps = 5000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(H264Encoder));
        if (_initialized) throw new InvalidOperationException("Encoder already initialized");

        Width = width;
        Height = height;
        _sampleDuration = 10_000_000L / fps;
        // NV12：Y 平面 W*H + UV 交错平面 ((W+1)/2)*((H+1)/2)*2
        _nv12Length = width * height + (((width + 1) / 2) * ((height + 1) / 2)) * 2;
        _nv12 = new byte[_nv12Length];

        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
        {
            int hr = MFInterop.MFStartup(MFInterop.MF_VERSION, 0);
            if (MFHr.Succeeded(hr) == false)
            {
                _mfStarted = 0;
                throw new COMException($"MFStartup failed: 0x{hr:X8}");
            }
        }

        try
        {
            // 1. 创建 H.264 编码器 MFT
            var clsid = MFInterop.CLSID_CMSH264EncoderMFT;
            var iid = MFInterop.IID_IMFTransform;
            int hr = MFInterop.CoCreateInstance(ref clsid, IntPtr.Zero, MFInterop.CLSCTX_INPROC_SERVER,
                ref iid, out var encoderPtr);
            if (MFHr.Succeeded(hr) == false)
                throw new COMException($"Create H.264 encoder failed: 0x{hr:X8}");
            _encoder = MFInterop.GetObject<IMFTransform>(encoderPtr);

            // 1.5 设置周期性 GOP（必须在 SetOutputType 前通过 MFT attribute store 设置）。
            // 不设置时 MS 编码器默认 GOP 无限——只有首帧是 IDR：解码端错过首帧
            //（如 surface 晚就绪、重连）即永久黑屏。3 秒（fps×3）一个关键帧自愈。
            // CODECAPI 属性写入失败仅降级（保持无限 GOP），不阻断编码器创建。
            try
            {
                int attrHr = _encoder.GetAttributes(out var encAttrs);
                if (MFHr.Succeeded(attrHr) && encAttrs != null)
                {
                    var gopKey = MFInterop.CODECAPI_AVEncMPVGOPSize;
                    encAttrs.SetUINT32(ref gopKey, (uint)Math.Max(1, fps * 3));
                }
            }
            catch { /* GOP 设置失败不影响编码，保持默认 */ }

            // 2. 先设置输出媒体类型（H.264）—— MS 编码器 MFT 要求输出类型必须先于输入类型
            var outputType = CreateMediaType();
            try
            {
                SetGuid(outputType, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
                SetGuid(outputType, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_H264);
                SetUInt(outputType, MFInterop.MF_MT_AVG_BITRATE, (uint)(bitrateKbps * 1000));
                SetU64(outputType, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(width, height));
                SetU64(outputType, MFInterop.MF_MT_FRAME_RATE, MakeFrameRate(fps));
                SetU64(outputType, MFInterop.MF_MT_PIXEL_ASPECT_RATIO, MakeAspectRatio(1, 1));
                SetUInt(outputType, MFInterop.MF_MT_INTERLACE_MODE, 2); // Progressive

                hr = _encoder.SetOutputType(0, outputType, 0);
                if (MFHr.Succeeded(hr) == false)
                    throw new COMException($"SetOutputType failed: 0x{hr:X8}");
            }
            finally
            {
                Marshal.ReleaseComObject(outputType);
            }

            // 3. 设置输入媒体类型（NV12 —— 编码器仅接受 IYUV/YV12/NV12/YUY2，不接受 RGB，
            //    BGRA 由本类先转换为 NV12）
            // 注意：MF_MT_FRAME_SIZE / FRAME_RATE / PIXEL_ASPECT_RATIO 是 UINT64 属性
            //（高 32 位与低 32 位各存一个分量），必须用 SetUINT64 设置。
            _inputType = CreateMediaType();
            SetGuid(_inputType, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
            SetGuid(_inputType, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_NV12);
            SetU64(_inputType, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(width, height));
            SetU64(_inputType, MFInterop.MF_MT_FRAME_RATE, MakeFrameRate(fps));
            SetU64(_inputType, MFInterop.MF_MT_PIXEL_ASPECT_RATIO, MakeAspectRatio(1, 1));
            SetUInt(_inputType, MFInterop.MF_MT_INTERLACE_MODE, 2); // Progressive
            SetUInt(_inputType, MFInterop.MF_MT_DEFAULT_STRIDE, (uint)width); // NV12 Y 平面 stride

            hr = _encoder.SetInputType(0, _inputType, 0);
            if (MFHr.Succeeded(hr) == false)
                throw new COMException($"SetInputType failed: 0x{hr:X8}");

            // 4. 通知开始流式处理（NOTIFY_START_OF_STREAM 仅限异步 MFT，同步编码器不发）
            _encoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);

            // 5. 查询编码器要求的输出缓冲大小（固定 4MB 时 2560x1440 实测报
            //    MF_E_BUFFERTOOSMALL(0xC00D36B1)，每帧输出被丢弃 → 解码端黑屏）
            _outputBufferSize = 4 * 1024 * 1024;
            try
            {
                if (MFHr.Succeeded(_encoder.GetOutputStreamInfo(0, out var outInfo)) && outInfo.cbSize > 0)
                    _outputBufferSize = Math.Max(_outputBufferSize, outInfo.cbSize);
            }
            catch { /* 查询失败退回默认 4MB */ }

            _initialized = true;
        }
        catch
        {
            CleanupMf();
            throw;
        }
    }

    /// <summary>编码一帧 BGRA 数据，返回 H.264 数据（可能为空数组）。</summary>
    public byte[] EncodeFrame(byte[] bgraData)
    {
        if (!_initialized) throw new InvalidOperationException("Encoder not initialized");
        if (bgraData.Length != Width * Height * 4)
            throw new ArgumentException($"BGRA data size mismatch: {bgraData.Length} != {Width * Height * 4}");

        // 0. BGRA → NV12（编码器不支持 RGB 输入）
        ConvertBgraToNv12(bgraData);

        // 1. 创建输入 buffer 并写入数据
        int hr = MFInterop.MFCreateMemoryBuffer(_nv12Length, out var bufferPtr);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateMemoryBuffer failed: 0x{hr:X8}");
        var buffer = MFInterop.GetObject<IMFMediaBuffer>(bufferPtr);

        try
        {
            hr = buffer.Lock(out var dataPtr, out _, out _);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"Buffer Lock failed: 0x{hr:X8}");
            Marshal.Copy(_nv12, 0, dataPtr, _nv12Length);
            buffer.Unlock();
            buffer.SetCurrentLength(_nv12Length);

            // 2. 创建 sample 并添加 buffer
            hr = MFInterop.MFCreateSample(out var samplePtr);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateSample failed: 0x{hr:X8}");
            var sample = MFInterop.GetObject<IMFSample>(samplePtr);
            try
            {
                sample.AddBuffer(buffer);
                // 时间戳不能为 0：部分 MFT 把 0 当"未设置"（MF_E_NO_SAMPLE_TIMESTAMP）
                _sampleTime += _sampleDuration;
                sample.SetSampleTime(_sampleTime);
                sample.SetSampleDuration(_sampleDuration);

                // 3. 输入编码器
                hr = _encoder.ProcessInput(0, sample, 0);
                if (MFHr.Succeeded(hr) == false)
                {
                    if (hr == MFHr.MF_E_NOTACCEPTING)
                    {
                        // 编码器还有输出没取走：先拉取输出（本帧丢弃，下一帧重试）
                        return DrainOutput();
                    }
                    throw new COMException($"ProcessInput failed: 0x{hr:X8}");
                }
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

        // 4. 拉取所有输出
        return DrainOutput();
    }

    /// <summary>冲刷编码器缓冲，返回剩余编码数据（会话结束前调用）。</summary>
    public byte[] Flush()
    {
        if (!_initialized) return Array.Empty<byte>();
        return DrainOutput();
    }

    /// <summary>
    /// 强制下一帧输出为 IDR 关键帧（丢帧/解码端加入后快速刷新画面）。
    /// 通过 MFT attribute store 动态设置 CODECAPI_AVEncVideoForceKeyFrame。
    /// 返回 false 表示编码器不支持动态强制（调用方可重建编码器兜底）。
    /// </summary>
    public bool ForceKeyFrame()
    {
        if (!_initialized || _encoder == null) return false;
        try
        {
            int attrHr = _encoder.GetAttributes(out var encAttrs);
            if (!MFHr.Succeeded(attrHr) || encAttrs == null) return false;
            var key = MFInterop.CODECAPI_AVEncVideoForceKeyFrame;
            return MFHr.Succeeded(encAttrs.SetUINT32(ref key, 1));
        }
        catch
        {
            return false;
        }
    }

    private byte[] DrainOutput()
    {
        using var result = new MemoryStream();

        while (true)
        {
            // 创建输出 sample + buffer
            int hr = MFInterop.MFCreateSample(out var samplePtr);
            if (MFHr.Succeeded(hr) == false) break;
            var outputSample = MFInterop.GetObject<IMFSample>(samplePtr);

            try
            {
                hr = MFInterop.MFCreateMemoryBuffer(_outputBufferSize, out var bufferPtr);
                if (MFHr.Succeeded(hr) == false) break;
                var outputBuffer = MFInterop.GetObject<IMFMediaBuffer>(bufferPtr);
                try
                {
                    outputSample.AddBuffer(outputBuffer);

                    var outputData = new MFT_OUTPUT_DATA_BUFFER { pSample = samplePtr };
                    hr = _encoder.ProcessOutput(0, 1, ref outputData, out _);
                    if (MFHr.Succeeded(hr) == false)
                    {
                        // 无更多输出（需要更多输入或流变化）
                        if (hr == MFHr.MF_E_TRANSFORM_NEED_MORE_INPUT ||
                            hr == MFHr.MF_E_TRANSFORM_STREAM_CHANGE)
                            break;
                        break;
                    }

                    // 提取编码后的数据
                    outputSample.GetBufferByIndex(0, out var encodedBuf);
                    try
                    {
                        encodedBuf.Lock(out var ptr, out _, out var curLen);
                        if (curLen > 0)
                        {
                            var data = new byte[curLen];
                            Marshal.Copy(ptr, data, 0, curLen);
                            result.Write(data, 0, data.Length);
                        }
                        encodedBuf.Unlock();
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(encodedBuf);
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

        return result.ToArray();
    }

    // ============ 辅助 ============

    /// <summary>
    /// BGRA → NV12 转换（BT.601，与 MF 编码器默认 YUV 矩阵一致）。
    /// Y 平面 W*H，UV 交错平面每 2x2 块取平均后计算一次色度。奇数宽/高时末行末列复制采样。
    /// </summary>
    private unsafe void ConvertBgraToNv12(byte[] bgra)
    {
        int w = Width, h = Height;
        fixed (byte* srcBase = bgra, dstBase = _nv12)
        {
            byte* yPlane = dstBase;
            byte* uvPlane = dstBase + w * h;
            int uvStride = w; // NV12 UV 平面 stride 与宽度相同（U,V 交错）

            for (int row = 0; row < h; row += 2)
            {
                bool lastRow = row + 1 >= h;
                byte* s0 = srcBase + row * w * 4;
                byte* s1 = lastRow ? s0 : s0 + w * 4;
                byte* y0 = yPlane + row * w;
                byte* y1 = lastRow ? y0 : y0 + w;
                byte* uv = uvPlane + (row >> 1) * uvStride;

                int col = 0;
                for (; col + 1 < w; col += 2)
                {
                    int o0 = col * 4, o1 = o0 + 4;
                    // BGRA 内存布局：B, G, R, A
                    int b0 = s0[o0], g0 = s0[o0 + 1], r0 = s0[o0 + 2];
                    int b1 = s0[o1], g1 = s0[o1 + 1], r1 = s0[o1 + 2];
                    int b2 = s1[o0], g2 = s1[o0 + 1], r2 = s1[o0 + 2];
                    int b3 = s1[o1], g3 = s1[o1 + 1], r3 = s1[o1 + 2];

                    y0[col] = (byte)((r0 * 77 + g0 * 150 + b0 * 29 + 128) >> 8);
                    y0[col + 1] = (byte)((r1 * 77 + g1 * 150 + b1 * 29 + 128) >> 8);
                    y1[col] = (byte)((r2 * 77 + g2 * 150 + b2 * 29 + 128) >> 8);
                    y1[col + 1] = (byte)((r3 * 77 + g3 * 150 + b3 * 29 + 128) >> 8);

                    // 2x2 块平均后算一次 U/V
                    int rA = (r0 + r1 + r2 + r3) >> 2;
                    int gA = (g0 + g1 + g2 + g3) >> 2;
                    int bA = (b0 + b1 + b2 + b3) >> 2;
                    uv[col] = (byte)(((-43 * rA - 85 * gA + 128 * bA + 128) >> 8) + 128);       // U
                    uv[col + 1] = (byte)(((128 * rA - 107 * gA - 21 * bA + 128) >> 8) + 128);   // V
                }
                // 奇数宽度：最后一列复制前一像素
                if (col < w)
                {
                    int o = col * 4;
                    y0[col] = y0[col - 1];
                    y1[col] = y1[col - 1];
                    int rA = s0[o + 2], gA = s0[o + 1], bA = s0[o];
                    uv[col] = (byte)(((-43 * rA - 85 * gA + 128 * bA + 128) >> 8) + 128);
                    uv[col + 1] = (byte)(((128 * rA - 107 * gA - 21 * bA + 128) >> 8) + 128);
                }
            }
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

    private static void SetUInt(IMFMediaType attrs, Guid key, uint value)
    {
        int hr = attrs.SetUINT32(ref key, value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetUINT32 failed: 0x{hr:X8}");
    }

    private static void SetU64(IMFMediaType attrs, Guid key, ulong value)
    {
        int hr = attrs.SetUINT64(ref key, value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetUINT64 failed: 0x{hr:X8}");
    }

    /// <summary>MF_MT_FRAME_SIZE：高 32 位宽度，低 32 位高度（UINT64）。</summary>
    private static ulong MakeFrameSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;
    /// <summary>MF_MT_FRAME_RATE：高 32 位分子，低 32 位分母（UINT64）。</summary>
    private static ulong MakeFrameRate(int fps) => ((ulong)(uint)fps << 32) | 1;
    /// <summary>MF_MT_PIXEL_ASPECT_RATIO：高 32 位 X，低 32 位 Y（UINT64）。</summary>
    private static ulong MakeAspectRatio(int x, int y) => ((ulong)(uint)x << 32) | (uint)y;

    private void CleanupMf()
    {
        if (_inputType != null)
        {
            try { Marshal.ReleaseComObject(_inputType); } catch { }
            _inputType = null;
        }
        if (_encoder != null)
        {
            // 先通知 MFT 停流再释放（让内部 lookahead 队列安全排空），降低悬挂引用风险
            try { _encoder.ProcessMessage(MFHr.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_encoder); } catch { }
            _encoder = null;
        }
        // 注意：不调 MFShutdown——平台全局只启动一次（见 _mfStarted），
        // 运行中关闭平台会拆除其他活跃 MFT 的依赖（v1.1.34 闪退根因之一）
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupMf();
    }
}
