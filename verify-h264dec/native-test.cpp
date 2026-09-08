// C++ 原生对照程序：验证 MS H.264 解码器 MFT 的行为（零 C# 互操作风险）。
// 读 allframes.h264（C# 编码器 dump 的码流），逐帧 ProcessInput/ProcessOutput。
#include <windows.h>
#include <mfapi.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <stdio.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")

static BYTE* g_frames[64];
static int g_sizes[64];
static int g_frameCount = 0;

// 按 Annex-B 起始码把码流切成"帧"（每个 AUD 开启一个 access unit）
static void LoadFrames(const char* path)
{
    FILE* f = fopen(path, "rb");
    if (!f) { printf("cannot open %s\n", path); exit(1); }
    fseek(f, 0, SEEK_END);
    long total = ftell(f);
    fseek(f, 0, SEEK_SET);
    BYTE* all = (BYTE*)malloc(total);
    fread(all, 1, total, f);
    fclose(f);
    printf("loaded %ld bytes\n", total);

    // 找每个 AUD (nal type 9) 的起始位置作为帧边界
    int start = -1;
    for (long i = 0; i + 4 < total; i++)
    {
        if (all[i] == 0 && all[i+1] == 0 && all[i+2] == 1 && (all[i+3] & 0x1F) == 9)
        {
            if (start >= 0)
            {
                g_frames[g_frameCount] = all + start;
                g_sizes[g_frameCount] = (int)(i - start);
                g_frameCount++;
            }
            start = (int)i;
        }
    }
    if (start >= 0 && g_frameCount < 64)
    {
        g_frames[g_frameCount] = all + start;
        g_sizes[g_frameCount] = (int)(total - start);
        g_frameCount++;
    }
    printf("split into %d access units\n", g_frameCount);
}

int main()
{
    LoadFrames("e:\\000_AI\\QuickRemote\\verify-h264dec\\allframes.h264");

    HRESULT hr = MFStartup(MF_VERSION, 0);
    printf("MFStartup hr=0x%08X\n", (unsigned)hr);

    MFT_REGISTER_TYPE_INFO filter = { MFMediaType_Video, MFVideoFormat_H264 };
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG_SYNCMFT,
        &filter, nullptr, &activates, &count);
    printf("MFTEnumEx hr=0x%08X count=%u\n", (unsigned)hr, count);
    if (FAILED(hr) || count == 0) return 1;

    IMFTransform* dec = nullptr;
    for (UINT32 i = 0; i < count; i++)
    {
        WCHAR name[256] = {};
        UINT32 len = 0;
        activates[i]->GetStringLength(MFT_FRIENDLY_NAME_Attribute, &len);
        if (len > 0 && len < 256) activates[i]->GetString(MFT_FRIENDLY_NAME_Attribute, name, 256, nullptr);
        printf("candidate[%u] name=%ls\n", i, name);
        hr = activates[i]->ActivateObject(IID_PPV_ARGS(&dec));
        printf("ActivateObject hr=0x%08X\n", (unsigned)hr);
        if (SUCCEEDED(hr)) break;
    }
    if (!dec) { printf("no decoder\n"); return 1; }

    // 是否异步 MFT？
    IMFAttributes* attrs = nullptr;
    hr = dec->GetAttributes(&attrs);
    if (SUCCEEDED(hr))
    {
        UINT32 isAsync = 0;
        hr = attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync);
        printf("MF_TRANSFORM_ASYNC hr=0x%08X value=%u\n", (unsigned)hr, isAsync);
        attrs->Release();
    }

    // 输入类型：H.264
    IMFMediaType* inType = nullptr;
    MFCreateMediaType(&inType);
    inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    hr = dec->SetInputType(0, inType, 0);
    printf("SetInputType hr=0x%08X\n", (unsigned)hr);
    inType->Release();

    // 初始输出类型
    IMFMediaType* outType = nullptr;
    hr = dec->GetOutputAvailableType(0, 0, &outType);
    printf("GetOutputAvailableType hr=0x%08X\n", (unsigned)hr);
    if (SUCCEEDED(hr))
    {
        GUID sub = {};
        outType->GetGUID(MF_MT_SUBTYPE, &sub);
        printf("  subtype=%08X-%04X...\n", sub.Data1, sub.Data2);
        hr = dec->SetOutputType(0, outType, 0);
        printf("SetOutputType hr=0x%08X\n", (unsigned)hr);
        outType->Release();
    }

    // 低延迟模式：CODECAPI_AVLowLatencyMode（解码即输出，不重排缓冲）
    IMFAttributes* decAttrs = nullptr;
    hr = dec->GetAttributes(&decAttrs);
    printf("GetAttributes hr=0x%08X\n", (unsigned)hr);
    if (SUCCEEDED(hr) && decAttrs)
    {
        hr = decAttrs->SetUINT32(CODECAPI_AVLowLatencyMode, 1);
        printf("SetLowLatency hr=0x%08X\n", (unsigned)hr);
        decAttrs->Release();
    }

    dec->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    dec->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

    MFT_OUTPUT_STREAM_INFO outInfo = {};
    dec->GetOutputStreamInfo(0, &outInfo);
    printf("outInfo flags=0x%X cbSize=%u\n", outInfo.dwFlags, (unsigned)outInfo.cbSize);

    long sampleTime = 0;
    int outputs = 0;
    for (int f = 0; f < g_frameCount && outputs < 5; f++)
    {
        // 输入
        IMFSample* smp = nullptr;
        MFCreateSample(&smp);
        IMFMediaBuffer* buf = nullptr;
        MFCreateMemoryBuffer(g_sizes[f], &buf);
        BYTE* p = nullptr;
        buf->Lock(&p, nullptr, nullptr);
        memcpy(p, g_frames[f], g_sizes[f]);
        buf->Unlock();
        buf->SetCurrentLength(g_sizes[f]);
        smp->AddBuffer(buf);
        sampleTime += 666667;
        smp->SetSampleTime(sampleTime);
        smp->SetSampleDuration(666667);
        hr = dec->ProcessInput(0, smp, 0);
        if (FAILED(hr)) { printf("f%d ProcessInput hr=0x%08X\n", f, (unsigned)hr); smp->Release(); buf->Release(); break; }
        smp->Release();
        buf->Release();

        // 输出（最多拉 6 次）
        for (int pull = 0; pull < 6; pull++)
        {
            IMFSample* os = nullptr;
            MFCreateSample(&os);
            IMFMediaBuffer* ob = nullptr;
            MFCreateMemoryBuffer(1920 * 1080 * 2, &ob);
            os->AddBuffer(ob);
            MFT_OUTPUT_DATA_BUFFER od = {};
            od.pSample = os;
            DWORD status = 0;
            hr = dec->ProcessOutput(0, 1, &od, &status);
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                printf("  f%d STREAM_CHANGE\n", f);
                IMFMediaType* nt = nullptr;
                HRESULT hr2 = dec->GetOutputAvailableType(0, 0, &nt);
                if (SUCCEEDED(hr2))
                {
                    hr2 = dec->SetOutputType(0, nt, 0);
                    printf("  re-SetOutputType hr=0x%08X\n", (unsigned)hr2);
                    nt->Release();
                }
                os->Release(); ob->Release();
                continue;
            }
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { if (f < 3) printf("  f%d NEED_MORE_INPUT\n", f); os->Release(); ob->Release(); break; }
            if (FAILED(hr)) { printf("  f%d ProcessOutput hr=0x%08X\n", f, (unsigned)hr); os->Release(); ob->Release(); break; }
            DWORD len = 0;
            os->GetTotalLength(&len);
            printf("  f%d OUTPUT #%d: %u bytes\n", f, ++outputs, (unsigned)len);
            os->Release(); ob->Release();
            if (outputs >= 5) break;
        }
    }
    printf("TOTAL outputs: %d\n", outputs);

    if (outputs == 0)
    {
        printf("--- DRAIN test ---\n"); fflush(stdout);
        dec->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
        printf("DRAIN sent\n"); fflush(stdout);
        for (int pull = 0; pull < 40; pull++)
        {
            IMFSample* os = nullptr;
            MFCreateSample(&os);
            IMFMediaBuffer* ob = nullptr;
            MFCreateMemoryBuffer(1920 * 1080 * 2, &ob);
            os->AddBuffer(ob);
            MFT_OUTPUT_DATA_BUFFER od = {};
            od.pSample = os;
            DWORD status = 0;
            hr = dec->ProcessOutput(0, 1, &od, &status);
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { printf("drain: NEED_MORE_INPUT after %d\n", pull); os->Release(); ob->Release(); break; }
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) { printf("drain: STREAM_CHANGE\n"); os->Release(); ob->Release(); continue; }
            if (FAILED(hr)) { printf("drain hr=0x%08X\n", (unsigned)hr); os->Release(); ob->Release(); break; }
            DWORD len = 0;
            os->GetTotalLength(&len);
            printf("drain frame %d: %u bytes\n", pull, (unsigned)len);
            os->Release(); ob->Release();
        }
    }

    dec->Release();
    MFShutdown();
    printf("DONE\n");
    return 0;
}
