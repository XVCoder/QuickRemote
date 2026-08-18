using System.IO;
using System.Runtime.InteropServices;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// H.264 硬件编码器（Media Foundation IMFTransform 直接调用）。
/// 输入 BGRA 帧，输出 H.264 NAL unit 流（elementary stream）。
/// 使用系统 H.264 编码器 MFT（含硬件加速，如 NVENC/AMF/QSV）。
/// 系统无 H.264 编码器时 CoCreateInstance 会失败（E_NOINTERFACE），
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

    /// <summary>输入帧宽度。</summary>
    public int Width { get; private set; }

    /// <summary>输入帧高度。</summary>
    public int Height { get; private set; }

    /// <summary>编码器是否可用（创建/初始化成功）。</summary>
    public bool IsAvailable => _initialized;

    /// <summary>初始化编码器。</summary>
    public void Initialize(int width, int height, int fps = 30, int bitrateKbps = 5000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(H264Encoder));
        if (_initialized) throw new InvalidOperationException("Encoder already initialized");

        Width = width;
        Height = height;
        _sampleDuration = 10_000_000L / fps;

        int hr = MFInterop.MFStartup(MFInterop.MF_VERSION, 0);
        if (MFHr.Succeeded(hr) == false)
            throw new COMException($"MFStartup failed: 0x{hr:X8}");

        try
        {
            // 1. 创建 H.264 编码器 MFT
            var clsid = MFInterop.CLSID_CMSH264EncoderMFT;
            var iid = MFInterop.IID_IMFTransform;
            hr = MFInterop.CoCreateInstance(ref clsid, IntPtr.Zero, MFInterop.CLSCTX_INPROC_SERVER,
                ref iid, out var encoderPtr);
            if (MFHr.Succeeded(hr) == false)
                throw new COMException($"Create H.264 encoder failed: 0x{hr:X8}");
            _encoder = MFInterop.GetObject<IMFTransform>(encoderPtr);

            // 2. 设置输入媒体类型（BGRA 视频）
            _inputType = CreateMediaType();
            SetGuid(_inputType, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
            SetGuid(_inputType, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_BGRA32);
            SetUInt(_inputType, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(width, height));
            SetUInt(_inputType, MFInterop.MF_MT_FRAME_RATE, MakeFrameRate(fps));
            SetUInt(_inputType, MFInterop.MF_MT_PIXEL_ASPECT_RATIO, MakeAspectRatio(1, 1));
            SetUInt(_inputType, MFInterop.MF_MT_INTERLACE_MODE, 2); // Progressive
            SetUInt(_inputType, MFInterop.MF_MT_DEFAULT_STRIDE, (uint)(width * 4)); // BGRA stride

            hr = _encoder.SetInputType(0, _inputType, 0);
            if (MFHr.Succeeded(hr) == false)
                throw new COMException($"SetInputType failed: 0x{hr:X8}");

            // 3. 设置输出媒体类型（H.264）
            var outputType = CreateMediaType();
            try
            {
                SetGuid(outputType, MFInterop.MF_MT_MAJOR_TYPE, MFInterop.MFMediaType_Video);
                SetGuid(outputType, MFInterop.MF_MT_SUBTYPE, MFInterop.MFVideoFormat_H264);
                SetUInt(outputType, MFInterop.MF_MT_AVG_BITRATE, (uint)(bitrateKbps * 1000));
                SetUInt(outputType, MFInterop.MF_MT_FRAME_SIZE, MakeFrameSize(width, height));
                SetUInt(outputType, MFInterop.MF_MT_FRAME_RATE, MakeFrameRate(fps));

                hr = _encoder.SetOutputType(0, outputType, 0);
                if (MFHr.Succeeded(hr) == false)
                    throw new COMException($"SetOutputType failed: 0x{hr:X8}");
            }
            finally
            {
                Marshal.ReleaseComObject(outputType);
            }

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

        // 1. 创建输入 buffer 并写入数据
        int hr = MFInterop.MFCreateMemoryBuffer(bgraData.Length, out var bufferPtr);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateMemoryBuffer failed: 0x{hr:X8}");
        var buffer = MFInterop.GetObject<IMFMediaBuffer>(bufferPtr);

        try
        {
            hr = buffer.Lock(out var dataPtr, out _, out _);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"Buffer Lock failed: 0x{hr:X8}");
            Marshal.Copy(bgraData, 0, dataPtr, bgraData.Length);
            buffer.Unlock();
            buffer.SetCurrentLength(bgraData.Length);

            // 2. 创建 sample 并添加 buffer
            hr = MFInterop.MFCreateSample(out var samplePtr);
            if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateSample failed: 0x{hr:X8}");
            var sample = MFInterop.GetObject<IMFSample>(samplePtr);
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(_sampleTime);
                _sampleTime += _sampleDuration;

                // 3. 输入编码器
                hr = _encoder.ProcessInput(0, sample, 0);
                if (MFHr.Succeeded(hr) == false)
                {
                    if (hr == MFHr.MF_E_NOTACCEPTING)
                        return Array.Empty<byte>(); // 编码器忙，下帧重试
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
                hr = MFInterop.MFCreateMemoryBuffer(4 * 1024 * 1024, out var bufferPtr);
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

    private static IMFMediaType CreateMediaType()
    {
        int hr = MFInterop.MFCreateMediaType(out var ptr);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"CreateMediaType failed: 0x{hr:X8}");
        return MFInterop.GetObject<IMFMediaType>(ptr);
    }

    private static void SetGuid(IMFAttributes attrs, Guid key, Guid value)
    {
        int hr = attrs.SetGUID(ref key, ref value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetGUID failed: 0x{hr:X8}");
    }

    private static void SetUInt(IMFAttributes attrs, Guid key, uint value)
    {
        int hr = attrs.SetUINT32(ref key, value);
        if (MFHr.Succeeded(hr) == false) throw new COMException($"SetUINT32 failed: 0x{hr:X8}");
    }

    private static uint MakeFrameSize(int width, int height) => (uint)(width | height << 16);
    private static uint MakeFrameRate(int fps) => (uint)(fps << 16 | 1);
    private static uint MakeAspectRatio(int x, int y) => (uint)(x | y << 16);

    private void CleanupMf()
    {
        if (_inputType != null)
        {
            try { Marshal.ReleaseComObject(_inputType); } catch { }
            _inputType = null;
        }
        if (_encoder != null)
        {
            try { Marshal.ReleaseComObject(_encoder); } catch { }
            _encoder = null;
        }
        try { MFInterop.MFShutdown(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupMf();
    }
}
