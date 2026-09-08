using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 在当前交互会话内以 SYSTEM 权限启动进程（远程解锁/锁屏输入代理用）。
///
/// 为什么不用计划任务：schtasks /RU SYSTEM 创建的任务固定运行在 session 0
/// （服务隔离会话），/IT 对 SYSTEM 账户无效。Windows 窗口站按会话隔离，
/// session 0 里的 WinSta0\Winlogon 并非用户锁屏的那个桌面——agent 在其中
/// SendInput 全部返回"成功"却注入到无人使用的影子桌面（2026-09-05 实测：
/// 日志 session=0 + 按键 ok=6/6 但锁屏毫无反应）。
///
/// 正确做法：复制本会话 winlogon.exe（SYSTEM 且在用户会话）的主令牌，
/// CreateProcessWithTokenW 创建子进程——子进程即为用户会话内的 SYSTEM 进程，
/// 可真正打开用户会话的 Winlogon 安全桌面并注入输入。
/// 要求当前进程已提升（管理员：SeDebugPrivilege + SeImpersonatePrivilege）。
/// </summary>
internal static class SystemSessionLauncher
{
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    // winnt.h TOKEN_ALL_ACCESS_P = 0xF01FF（STANDARD_RIGHTS_REQUIRED | 全部 TOKEN_* 位）。
    // v1.1.38 修复：旧定义 0xF000B 缺 QUERY_SOURCE/ADJUST_* 等高位，CreateProcessWithTokenW
    // 报 error 5（实测 2x2 矩阵：0xF000B 恒败、0xF01FF 恒成；含 IMPERSONATE 的 0xF 也败，
    // 说明微软文档"QUERY|DUPLICATE|ASSIGN_PRIMARY 即可"与 Win11 26200 实现不符）。
    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    public static string? LastError { get; private set; }

    /// <summary>在用户会话内以 SYSTEM 启动进程。失败返回 null 并记录 LastError。</summary>
    public static Process? Launch(string exePath, string arguments)
    {
        LastError = null;
        try
        {
            // 1. 提升当前进程令牌的 SeDebugPrivilege（打开 winlogon 进程/令牌）
            //    与 SeImpersonatePrivilege（CreateProcessWithTokenW 必需）
            if (!EnablePrivilege("SeDebugPrivilege"))
            {
                LastError = $"SeDebugPrivilege enable failed: {LastError} (run as Administrator?)";
                return null;
            }
            if (!EnablePrivilege("SeImpersonatePrivilege"))
            {
                LastError = $"SeImpersonatePrivilege enable failed: {LastError} (run as Administrator?)";
                return null;
            }

            // 2. 找到当前会话的 winlogon.exe（SYSTEM 进程，与用户同会话）
            var sessionId = Process.GetCurrentProcess().SessionId;
            var winlogon = Process.GetProcessesByName("winlogon").FirstOrDefault(p => p.SessionId == sessionId);
            if (winlogon == null)
            {
                LastError = $"winlogon.exe not found in session {sessionId}";
                return null;
            }

            // 3. 打开并复制其主令牌
            var hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, (uint)winlogon.Id);
            if (hProcess == IntPtr.Zero)
            {
                LastError = $"OpenProcess(winlogon) failed: {Marshal.GetLastWin32Error()}";
                return null;
            }
            IntPtr hToken = IntPtr.Zero, hPrimary = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(hProcess, TOKEN_QUERY | TOKEN_DUPLICATE, out hToken))
                {
                    LastError = $"OpenProcessToken failed: {Marshal.GetLastWin32Error()}";
                    return null;
                }
                if (!DuplicateTokenEx(hToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                        2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out hPrimary))
                {
                    LastError = $"DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}";
                    return null;
                }
            }
            finally
            {
                CloseHandle(hProcess);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }

            // 4. 以该令牌创建进程——继承 winlogon 的会话（用户会话）与 SYSTEM 身份
            try
            {
                var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
                var cmd = $"\"{exePath}\" {arguments}";
                if (!CreateProcessWithTokenW(hPrimary, 0, null, cmd, CREATE_NO_WINDOW,
                        IntPtr.Zero, null, ref si, out var pi))
                {
                    LastError = $"CreateProcessWithTokenW failed: {Marshal.GetLastWin32Error()} (SeImpersonatePrivilege needed)";
                    return null;
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return Process.GetProcessById((int)pi.dwProcessId);
            }
            finally
            {
                if (hPrimary != IntPtr.Zero) CloseHandle(hPrimary);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>启用当前进程令牌上的指定特权。</summary>
    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var hToken))
        {
            LastError = $"OpenProcessToken(self) failed: {Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
            {
                LastError = $"LookupPrivilegeValue({name}) failed: {Marshal.GetLastWin32Error()}";
                return false;
            }
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES
                {
                    Luid = new LUID { LowPart = (uint)(luid & 0xFFFFFFFF), HighPart = (int)(luid >> 32) },
                    Attributes = 0x00000002 // SE_PRIVILEGE_ENABLED
                }
            };
            if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                LastError = $"AdjustTokenPrivileges({name}) failed: {Marshal.GetLastWin32Error()}";
                return false;
            }
            // AdjustTokenPrivileges 返回 TRUE 但未分配权限时 lastError=ERROR_NOT_ALL_ASSIGNED(1300)
            var err = Marshal.GetLastWin32Error();
            if (err == 1300)
            {
                LastError = $"{name} not held by token (process not elevated?)";
                return false;
            }
            return true;
        }
        finally { CloseHandle(hToken); }
    }

    // ============ Win32 ============

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags, string? lpApplicationName,
        string lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    // 注意：不能用 { uint Count; long Luid; uint Attr; } 直接平铺——long 会被 .NET
    // 对齐到 8 字节边界，而 Win32 TOKEN_PRIVILEGES 的 LUID 紧跟 Count（偏移 4），
    // 错位导致 AdjustTokenPrivileges 拿到乱码 LUID 而失败（v1.1.31 实测教训）。
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
