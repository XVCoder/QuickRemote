using System;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

class Probe
{
    [DllImport("wtsapi32.dll")]
    static extern bool WTSQuerySessionInformation(IntPtr h, int sid, int info, out IntPtr buf, out int bytes);
    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr p);

    static void Main()
    {
        // 0. 会话状态：WTS_SESSIONSTATE (21) —— 需 Win8+ 且注册表正确，仅作参考
        try
        {
            if (WTSQuerySessionInformation(IntPtr.Zero, -1, 21, out var p, out var n))
            {
                int state = Marshal.ReadInt32(p);
                WTSFreeMemory(p);
                Console.WriteLine($"[lock] WTS_SESSIONSTATE raw={state} (0=Active,1=Locked per docs; value may be inverted on some builds)");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[lock] query failed: {ex.Message}"); }

        // 1. D3D11CreateDevice
        ID3D11Device device;
        ID3D11DeviceContext context;
        try
        {
            var flags = DeviceCreationFlags.BgraSupport;
            var res = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, null, out device, out context);
            Console.WriteLine($"[device] D3D11CreateDevice hr=0x{res.Code:X8} ok={res.Success}");
            if (res.Failure) return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[device] EXCEPTION: {ex.Message}");
            return;
        }

        using (device)
        using (context)
        {
            // 2. 枚举 output
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            Console.WriteLine($"[adapter] {adapter.Description.Description}");
            IDXGIOutput output = null;
            try { adapter.EnumOutputs(0, out output); } catch { }
            if (output == null) { Console.WriteLine("[output] no outputs"); return; }
            Console.WriteLine($"[output] {output.Description.DeviceName} attached={output.Description.AttachedToDesktop}");
            using (output)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();

                // 3. DuplicateOutput
                try
                {
                    var dup = output1.DuplicateOutput(device);
                    Console.WriteLine("[duplicate] OK");
                    try
                    {
                        // 4. AcquireNextFrame 一次
                        var hr = dup.AcquireNextFrame(500, out var info, out var resource);
                        if (hr.Success)
                        {
                            Console.WriteLine("[acquire] OK (frame available)");
                            dup.ReleaseFrame();
                        }
                        else
                        {
                            Console.WriteLine($"[acquire] hr=0x{hr.Code:X8} ({hr.Code})");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[acquire] EXCEPTION: {ex.Message}"); }
                    finally { dup.Dispose(); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[duplicate] EXCEPTION: {ex.Message}");
                }
            }
        }
        Console.WriteLine("[done] duplication released cleanly");
    }
}
