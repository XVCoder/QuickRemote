#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
打包 QuickRemote-about（**强制 LF 行尾**），供 qd 平台 upgrade_app 使用。

用法：
    python .workbuddy/tools/pack-about.py 1.0.124

产出：E:/000_AI/QuickRemote/QuickRemote-about-v{ver}.tar.gz
包内结构：public/index.html, public/style.css, public/app.js, server.js, package.json, seed.json
（**不含** node_modules 与 data/ —— data 是运行时下载统计目录，打进去会污染，且升级时卷优先、包内内容会被丢弃）

⚠️ 为什么必须转 LF（2026-09-16 踩过）：
    仓库 core.autocrlf=true ⇒ 工作区文件是 CRLF、git 里是 LF。
    上一版 tar.gz 用的是 LF（index.html 23,037B）。若直接 `tar -czf` 打工作区文件，
    包内 HTML 会变成 23,370B（333 行每行多 1 字节），CSS/JS 同理。
    功能上无害，但会让「本地字节数 == 线上字节数」这条校验基线整体漂移，
    也把逐文件 diff 变成"假差异"（看着像线上少/多了内容）。
    所以打包时统一归一为 LF，与历史包形态保持一致。
"""
import io
import os
import sys
import tarfile

SRC = r"E:\000_AI\QuickRemote\QuickRemote-about"

FILES = [
    ("public/index.html", "public/index.html"),
    ("public/style.css", "public/style.css"),
    ("public/app.js", "public/app.js"),
    ("server.js", "server.js"),
    ("package.json", "package.json"),
    ("seed.json", "seed.json"),
]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    ver = sys.argv[1].lstrip("v")
    out = r"E:\000_AI\QuickRemote\QuickRemote-about-v%s.tar.gz" % ver

    staged = {}
    for rel, arc in FILES:
        p = os.path.join(SRC, rel)
        with open(p, "rb") as f:
            data = f.read()
        crlf = data.count(b"\r\n")
        data = data.replace(b"\r\n", b"\n")
        staged[arc] = data
        print("%-22s 磁盘 %6dB (CRLF %3d) -> 包内 %6dB" % (rel, os.path.getsize(p), crlf, len(data)))

    with tarfile.open(out, "w:gz") as tar:
        for arc, data in staged.items():
            info = tarfile.TarInfo(arc)
            info.size = len(data)
            info.mtime = 0
            info.mode = 0o644
            tar.addfile(info, io.BytesIO(data))

    print("\n产出:", out, os.path.getsize(out), "B")
    print("下一步校验（包内 == 工作副本，仅行尾归一）：")
    print("  mkdir -p tmp-pkg/.v && cd tmp-pkg/.v && tar --force-local -xzf \"%s\"" % out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
