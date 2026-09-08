// C++ control experiment: identical flow to .NET h264test
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mftransform.h>
#include <mferror.h>
#include <stdio.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")

static const UINT32 W = 2560, H = 1440, FPS = 15;

static const CLSID MY_CLSID_CMSH264EncoderMFT =
    {0x6CA50344, 0x051A, 0x4DED, {0x97, 0x79, 0xA4, 0x33, 0x05, 0x16, 0x5E, 0x35}};
static const GUID MY_CODECAPI_AVEncMPVGOPSize =
    {0x95f31b26, 0x95a4, 0x41aa, {0x93, 0x03, 0x24, 0x6a, 0x7f, 0xc7, 0xe0, 0xc8}};

#define SAFE_RELEASE(x) if (x) { (x)->Release(); (x) = nullptr; }

static void RunVariant(bool setDur)
{
    printf("\n--- C++ variant (setDur=%d) ---\n", setDur);
    HRESULT hr;

    IMFTransform* enc = nullptr;
    hr = CoCreateInstance(MY_CLSID_CMSH264EncoderMFT, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(&enc));
    if (FAILED(hr)) { printf("CoCreateInstance failed 0x%08X\n", hr); return; }

    IMFAttributes* encAttrs = nullptr;
    hr = enc->GetAttributes(&encAttrs);
    if (SUCCEEDED(hr)) {
        hr = encAttrs->SetUINT32(MY_CODECAPI_AVEncMPVGOPSize, FPS * 3);
        printf("SetGOP hr=0x%08X\n", hr);
        SAFE_RELEASE(encAttrs);
    }

    IMFMediaType* outType = nullptr;
    hr = MFCreateMediaType(&outType);
    if (FAILED(hr)) { SAFE_RELEASE(enc); return; }
    outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    outType->SetUINT32(MF_MT_AVG_BITRATE, 5000000);
    outType->SetUINT64(MF_MT_FRAME_SIZE, ((UINT64)W << 32) | H);
    outType->SetUINT64(MF_MT_FRAME_RATE, ((UINT64)FPS << 32) | 1);
    outType->SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, ((UINT64)1 << 32) | 1);
    outType->SetUINT32(MF_MT_INTERLACE_MODE, 2);
    hr = enc->SetOutputType(0, outType, 0);
    printf("SetOutputType hr=0x%08X\n", hr);
    SAFE_RELEASE(outType);
    if (FAILED(hr)) { SAFE_RELEASE(enc); return; }

    IMFMediaType* inType = nullptr;
    hr = MFCreateMediaType(&inType);
    if (FAILED(hr)) { SAFE_RELEASE(enc); return; }
    inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    inType->SetUINT64(MF_MT_FRAME_SIZE, ((UINT64)W << 32) | H);
    inType->SetUINT64(MF_MT_FRAME_RATE, ((UINT64)FPS << 32) | 1);
    inType->SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, ((UINT64)1 << 32) | 1);
    inType->SetUINT32(MF_MT_INTERLACE_MODE, 2);
    inType->SetUINT32(MF_MT_DEFAULT_STRIDE, W);
    hr = enc->SetInputType(0, inType, 0);
    printf("SetInputType hr=0x%08X\n", hr);
    SAFE_RELEASE(inType);
    if (FAILED(hr)) { SAFE_RELEASE(enc); return; }

    hr = enc->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    printf("BEGIN_STREAMING hr=0x%08X\n", hr);

    const int nv12Len = W * H + (W / 2) * (H / 2) * 2;
    IMFMediaBuffer* buf = nullptr;
    hr = MFCreateMemoryBuffer(nv12Len, &buf);
    if (FAILED(hr)) { printf("CreateBuffer failed 0x%08X\n", hr); SAFE_RELEASE(enc); return; }
    BYTE* p = nullptr;
    buf->Lock(&p, nullptr, nullptr);
    for (int i = 0; i < nv12Len; i++) p[i] = (BYTE)(i % 251);
    buf->Unlock();
    buf->SetCurrentLength(nv12Len);

    IMFSample* smp = nullptr;
    hr = MFCreateSample(&smp);
    if (FAILED(hr)) { printf("CreateSample failed 0x%08X\n", hr); SAFE_RELEASE(buf); SAFE_RELEASE(enc); return; }
    smp->AddBuffer(buf);

    LONGLONG time = 666666, dur = 666666;
    hr = smp->SetSampleTime(time);
    printf("SetSampleTime hr=0x%08X\n", hr);
    if (setDur) {
        hr = smp->SetSampleDuration(dur);
        printf("SetSampleDuration hr=0x%08X\n", hr);
    }

    LONGLONG rt = 0, rd = 0;
    smp->GetSampleTime(&rt);
    smp->GetSampleDuration(&rd);
    printf("readback: time=%lld duration=%lld\n", rt, rd);

    hr = enc->ProcessInput(0, smp, 0);
    printf("ProcessInput hr=0x%08X %s\n", hr, SUCCEEDED(hr) ? "SUCCESS" : "FAIL");

    if (SUCCEEDED(hr)) {
        IMFSample* outSmp = nullptr;
        IMFMediaBuffer* outBufMem = nullptr;
        MFCreateSample(&outSmp);
        MFCreateMemoryBuffer(4 * 1024 * 1024, &outBufMem);
        outSmp->AddBuffer(outBufMem);
        MFT_OUTPUT_DATA_BUFFER outBuf = {};
        outBuf.pSample = outSmp;
        DWORD status = 0;
        hr = enc->ProcessOutput(0, 1, &outBuf, &status);
        printf("ProcessOutput hr=0x%08X\n", hr);
        if (SUCCEEDED(hr)) {
            IMFMediaBuffer* encBuf = nullptr;
            outSmp->GetBufferByIndex(0, &encBuf);
            DWORD len = 0;
            encBuf->GetCurrentLength(&len);
            printf("encoded %lu bytes\n", len);
            SAFE_RELEASE(encBuf);
        }
        SAFE_RELEASE(outBufMem);
        SAFE_RELEASE(outSmp);
    }

    SAFE_RELEASE(smp);
    SAFE_RELEASE(buf);
    SAFE_RELEASE(enc);
}

int main()
{
    HRESULT hr = MFStartup(MF_VERSION, 0);
    printf("MFStartup hr=0x%08X\n", hr);

    RunVariant(true);
    RunVariant(false);

    MFShutdown();
    return 0;
}
