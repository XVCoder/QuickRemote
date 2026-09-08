# QuickRemote PC Client 打包脚本
# 用法: powershell -ExecutionPolicy Bypass -File build-pcclient.ps1 [-Version 1.0.7]
#
# 生成干净的扁平 ZIP 包（不含顶层目录），可直接用于自动更新覆盖。

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $ScriptDir "QuickRemote.PCClient.csproj"
$PublishDir = Join-Path $ScriptDir "publish"
$StagingDir = Join-Path $ScriptDir "publish-staging"

# 从 csproj 读取版本号（如果未指定）
if (-not $Version) {
    [xml]$csproj = Get-Content $ProjectFile
    $Version = $csproj.Project.PropertyGroup.Version
    if (-not $Version) { $Version = "1.0.0" }
}

Write-Host "=== QuickRemote PC Client Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host ""

# 1. 清理旧产物
Write-Host "[1/4] Cleaning previous build..." -ForegroundColor Yellow
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }

# 2. 编译发布
Write-Host "[2/4] Publishing..." -ForegroundColor Yellow
dotnet publish $ProjectFile -c Release -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed" -ForegroundColor Red
    exit 1
}

# 3. 准备暂存目录（只包含运行所需文件，排除 pdb）
Write-Host "[3/4] Preparing staging directory..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
Get-ChildItem -Path $PublishDir -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
    Copy-Item $_.FullName -Destination $StagingDir
}

# 3.1 纳入独立更新程序 update.exe（自动更新依赖它，缺失则无法自动升级）
#     单文件发布产物，运行不依赖同目录的其他文件
$UpdaterProject = Join-Path (Split-Path -Parent $ScriptDir) "pc-updater"
$UpdaterExe = Join-Path $UpdaterProject "bin\publish\update.exe"
if (Test-Path $UpdaterExe) {
    Copy-Item $UpdaterExe -Destination (Join-Path $StagingDir "update.exe") -Force
    Write-Host "  + update.exe (updater)" -ForegroundColor DarkGray
} else {
    Write-Host "  [WARN] update.exe not found at $UpdaterExe - run: dotnet build pc-updater -c Release" -ForegroundColor Red
    Write-Host "         The release package will NOT support automatic update!" -ForegroundColor Red
}

# 3.2 纳入 SYSTEM 辅助程序 QuickRemote.Agent（锁屏密码解锁 + 锁屏输入代理，需随包分发）
#     framework-dependent apphost 必须连同 .dll/.deps.json/.runtimeconfig.json 一起分发，
#     缺 .dll 时 exe 启动即崩（"The application to execute does not exist"），
#     曾导致远程解锁/锁屏输入代理在线上完全失效（v1.1.25~1.1.27）
$AgentProject = Join-Path (Split-Path -Parent $ScriptDir) "pc-unlocker"
Write-Host "  Building pc-agent..." -ForegroundColor DarkGray
dotnet build $AgentProject -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: pc-agent build failed" -ForegroundColor Red
    exit 1
}
$AgentBin = Join-Path $AgentProject "bin\Release\net8.0-windows"
$AgentFiles = @(
    "QuickRemote.Agent.exe",
    "QuickRemote.Agent.dll",
    "QuickRemote.Agent.deps.json",
    "QuickRemote.Agent.runtimeconfig.json"
)
$missing = @()
foreach ($f in $AgentFiles) {
    $src = Join-Path $AgentBin $f
    if (Test-Path $src) {
        Copy-Item $src -Destination (Join-Path $StagingDir $f) -Force
        Write-Host "  + $f (unlock + locked-screen input agent)" -ForegroundColor DarkGray
    } else {
        $missing += $f
    }
}
if ($missing.Count -gt 0) {
    Write-Host "ERROR: pc-agent output incomplete, missing: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "       The release package would NOT support remote unlock!" -ForegroundColor Red
    exit 1
}

# 列出暂存目录内容
Write-Host "  Staging contents:" -ForegroundColor DarkGray
Get-ChildItem $StagingDir | ForEach-Object { Write-Host "    $($_.Name)" -ForegroundColor DarkGray }

# 4. 创建 ZIP（扁平结构，无顶层目录）
$ZipName = "QuickRemote-PCClient-v$Version.zip"
$ZipPath = Join-Path (Split-Path -Parent $ScriptDir) $ZipName
Write-Host "[4/4] Creating ZIP: $ZipName" -ForegroundColor Yellow
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$StagingDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal

# 清理暂存目录
Remove-Item $StagingDir -Recurse -Force

# 验证 ZIP 结构
Write-Host ""
Write-Host "=== ZIP Verification ===" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
$zip.Entries | ForEach-Object { Write-Host "  $($_.FullName)  ($($_.Length) bytes)" }
$zip.Dispose()

Write-Host ""
Write-Host "Build complete: $ZipPath" -ForegroundColor Green
Write-Host "File size: $([math]::Round((Get-Item $ZipPath).Length / 1KB, 1)) KB"
