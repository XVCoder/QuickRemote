#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
打包 PC 客户端为**扁平 ZIP**（无顶层目录），供 qd 平台上传。

用法（在仓库根执行）：
    python .workbuddy/tools/pack-pc.py 1.1.70

输入：QuickRemote/pc-client/publish/（由 dotnet publish 产出，且已放入 update.exe）
产出：QuickRemote/QuickRemote-PCClient-v{ver}.zip

要点（都踩过）：
  1. **必须扁平**：ZIP 内不能有顶层目录，用户解压即用；update.exe 必须在内，否则无法自动升级。
  2. 排除 `.pdb`（发布符号，无用于分发）→ 正常 14 个条目。
  3. 期望目标机为**框架依赖**模式（需预装 .NET 8 桌面运行时 x64）——
     页面/文案**不要**写「自包含」。
  4. 打包前会打印每个条目的名称/大小，便于与上一版比对；同时校验
     `QuickRemote.PCClient.dll` 的 AssemblyInformationalVersion 是否带本次提交号
     （形如 `1.1.70+0653816f...`），这是「这个包 = 哪个 commit」的唯一凭据。
"""
import os
import re
import sys
import zipfile

ROOT = r"E:\000_AI\QuickRemote"
PUB = os.path.join(ROOT, r"QuickRemote\pc-client\publish")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    ver = sys.argv[1].lstrip("v")
    out = os.path.join(ROOT, "QuickRemote-PCClient-v%s.zip" % ver)
    if not os.path.isdir(PUB):
        raise SystemExit("[pack-pc] 找不到发布目录: " + PUB)

    names = sorted(os.listdir(PUB))
    if "update.exe" not in names:
        raise SystemExit("[pack-pc] publish 里没有 update.exe —— 用户将无法自动升级")
    if not any(n.endswith(".dll") and "PCClient" in n for n in names):
        raise SystemExit("[pack-pc] publish 里没有主程序 dll，publish 可能未完成")

    info = os.path.join(ROOT, r"QuickRemote\pc-client\obj\Release\net8.0-windows\win-x64\QuickRemote.PCClient.AssemblyInfo.cs")
    commit = ""
    if os.path.isfile(info):
        m = re.search(r'AssemblyInformationalVersionAttribute\("([^"]+)"\)',
                      open(info, encoding="utf-8", errors="replace").read())
        if m:
            commit = m.group(1)
            print("[pack-pc] 主程序内嵌版本 = %s" % commit)
            if not commit.startswith(ver):
                raise SystemExit("[pack-pc] 内嵌版本 %s 与目标版本 %s 不符，拒绝打包" % (commit, ver))

    n = 0
    total = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name in names:
            if name.lower().endswith(".pdb"):
                continue
            p = os.path.join(PUB, name)
            if not os.path.isfile(p):
                continue
            z.write(p, name)          # arcname = 纯文件名 ⇒ 扁平结构
            n += 1
            total += os.path.getsize(p)
    if commit and "update.exe" in names:
        print("[pack-pc] 提示：请确认上面打印的包名与内嵌提交号是本次 HEAD 对应的提交")
    print("[pack-pc] %d 个条目，未压缩合计 %.2f MB" % (n, total / 1048576.0))
    print("[pack-pc] 产出 %s  %.2f MB" % (out, os.path.getsize(out) / 1048576.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
