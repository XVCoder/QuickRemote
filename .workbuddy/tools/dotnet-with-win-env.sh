#!/usr/bin/env bash
# 在 WorkBuddy 的 bash 沙箱里运行 dotnet 的包装脚本。
#
# 背景：该 bash 环境缺失 APPDATA / ProgramData / ProgramFiles(x86) / SystemRoot 等
# Windows 环境变量，导致 NuGet 的 XPlatMachineWideSetting 构造时取不到
# 机器级配置目录，Path.Combine(null, ...) 抛
#   System.ArgumentNullException: Value cannot be null. (Parameter 'path1')
# 报错位置：NuGet.targets 的 GetRestoreSettingsTask（restore / build / test 全部失败，
# 且与项目无关——全新的 hello world 工程同样失败）。
#
# 用法：bash .workbuddy/tools/dotnet-with-win-env.sh test pc-client.Tests/QuickRemote.PCClient.Tests.csproj
set -euo pipefail

exec env \
  "SystemRoot=C:/Windows" \
  "windir=C:/Windows" \
  "SystemDrive=C:" \
  "ProgramData=C:/ProgramData" \
  "ALLUSERSPROFILE=C:/ProgramData" \
  "APPDATA=C:/Users/xiong/AppData/Roaming" \
  "LOCALAPPDATA=C:/Users/xiong/AppData/Local" \
  "USERPROFILE=C:/Users/xiong" \
  "HOMEDRIVE=C:" \
  "HOMEPATH=/Users/xiong" \
  "ProgramFiles=C:/Program Files" \
  "ProgramFiles(x86)=C:/Program Files (x86)" \
  "CommonProgramFiles=C:/Program Files/Common Files" \
  "TEMP=C:/Users/xiong/AppData/Local/Temp" \
  "TMP=C:/Users/xiong/AppData/Local/Temp" \
  dotnet "$@"
