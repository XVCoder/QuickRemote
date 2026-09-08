# 本地部署最新构建到 E:\001_DevSoft\QuickRemote-PCClient（需在沙箱外执行）
# 注意：主程序构建输出在 win-x64 子目录（build-pcclient.ps1 用 RID 发布），不要用无 RID 的旧目录
$ErrorActionPreference = "Stop"

$src = "e:\000_AI\QuickRemote\QuickRemote\pc-client\bin\Release\net8.0-windows\win-x64"
$asrc = "e:\000_AI\QuickRemote\QuickRemote\pc-unlocker\bin\Release\net8.0-windows"
$dst = "E:\001_DevSoft\QuickRemote-PCClient"

# 停止运行中的客户端（文件占用会导致复制失败）
Get-Process -Name "QuickRemote.PCClient" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$files = @(
    @{ S = $src; F = "QuickRemote.PCClient.exe" },
    @{ S = $src; F = "QuickRemote.PCClient.dll" },
    @{ S = $src; F = "QuickRemote.PCClient.deps.json" },
    @{ S = $src; F = "QuickRemote.PCClient.runtimeconfig.json" },
    @{ S = $asrc; F = "QuickRemote.Agent.exe" },
    @{ S = $asrc; F = "QuickRemote.Agent.dll" },
    @{ S = $asrc; F = "QuickRemote.Agent.deps.json" },
    @{ S = $asrc; F = "QuickRemote.Agent.runtimeconfig.json" }
)

foreach ($item in $files) {
    $from = Join-Path $item.S $item.F
    $to = Join-Path $dst $item.F
    Copy-Item -Path $from -Destination $to -Force
    Write-Host "OK: $($item.F)"
}

# 部署后重启客户端（管理员清单会弹 UAC）
Start-Process -FilePath (Join-Path $dst "QuickRemote.PCClient.exe") -WorkingDirectory $dst
$dll = Get-Item (Join-Path $src "QuickRemote.PCClient.dll")
$ver = (Get-Item (Join-Path $src "QuickRemote.PCClient.exe")).VersionInfo.ProductVersion
Write-Host ""
Write-Host "Deployed v$ver (built $($dll.LastWriteTime)) and client restarted."
