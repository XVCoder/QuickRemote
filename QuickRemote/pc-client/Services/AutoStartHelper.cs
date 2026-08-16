using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace QuickRemote.PCClient.Services;

/// <summary>
/// 开机自启管理。通过注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 实现。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "QuickRemote.PCClient";

    /// <summary>应用或移除开机自启。</summary>
    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName
                              ?? Environment.ProcessPath
                              ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // 注册表操作失败忽略
        }
    }

    /// <summary>查询当前是否已设置开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }
}
