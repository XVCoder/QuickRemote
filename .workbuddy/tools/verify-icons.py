#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
图标产物回归校验：把四端图标文件读回来，与 `gen-icons.py` 的生成结果逐像素比对。

用法：
    python .workbuddy/tools/verify-icons.py

为什么需要它（2026-09-23 的教训）：
    1. ICO 格式极易踩坑 —— Pillow 默认把每一帧都写成 PNG 压缩，而 PNG 帧在 ICO 里
       只对 256×256 有公认支持，16~128 用 PNG 会让 Windows 资源管理器读成空白。
       `gen-icons.py` 因此手工组装 DIB 帧，本脚本负责回读验证 DIB 的行序/alpha 都正确。
    2. 图标是"改一次用很久"的资源，手改四个端很容易只改了一部分 —— 这里一次性全查。

判据：所有帧 `OK`（像素零差异）+ 各文件尺寸齐全。
"""
import importlib.util
import os
import sys

from PIL import Image

ROOT = r"E:\000_AI\QuickRemote"
ANDROID_RES = os.path.join(ROOT, r"QuickRemote\android-app\app\src\main\res")


def load_gen():
    spec = importlib.util.spec_from_file_location("gen", os.path.join(ROOT, r".workbuddy\tools\gen-icons.py"))
    gen = importlib.util.module_from_spec(spec)
    sys.argv = ["gen-icons.py", "--sheet"]      # 阻止 main() 写文件
    spec.loader.exec_module(gen)
    gen.LAYERS, gen.BG_COLOR_RESOLVED = gen.load_source()
    gen.GLYPH_BOX = gen.glyph_box()
    return gen


def cmp_frame(gen, im, size, label):
    im.size = (size, size)
    im.load()
    got = im.convert("RGBA")
    want = gen.icon(size)
    diff = sum(1 for a, b in zip(got.getdata(), want.getdata()) if a != b)
    print("   %-22s %3dpx  %s" % (label, size, "OK" if diff == 0 else "差异 %d px" % diff))
    return diff


def main():
    gen = load_gen()
    bad = 0

    # 1) ICO：PC exe 图标 + about favicon
    for path, sizes in ((os.path.join(ROOT, r"QuickRemote\pc-client\app.ico"), [16, 24, 32, 48, 64, 128, 256]),
                        (os.path.join(ROOT, r"QuickRemote-about\public\favicon.ico"), [16, 32, 48])):
        im = Image.open(path)
        have = sorted(im.ico.sizes())
        want = [(s, s) for s in sizes]
        print("%s  %d B" % (os.path.relpath(path, ROOT), os.path.getsize(path)))
        if have != want:
            print("   !! ICO 目录尺寸不符：期望 %s，实际 %s" % (want, have))
            bad += 1
        for s in sizes:
            bad += 1 if cmp_frame(gen, im, s, "") else 0

    # 2) PNG：PC 窗口/托盘图标 + about 图标
    for path, expect in ((os.path.join(ROOT, r"QuickRemote\pc-client\app.png"), (256, 256)),
                         (os.path.join(ROOT, r"QuickRemote-about\public\icon.png"), (192, 192)),
                         (os.path.join(ROOT, r"QuickRemote-about\public\apple-touch-icon.png"), (180, 180))):
        im = Image.open(path).convert("RGBA")
        ok = im.size == expect
        # 四角必须真透明（旧图标四角是 alpha=255 的白角，窗口图标会露白方块）
        corners = [im.getpixel(p)[3] for p in ((0, 0), (im.size[0] - 1, 0),
                                               (0, im.size[1] - 1), (im.size[0] - 1, im.size[1] - 1))]
        if not ok or any(a != 0 for a in corners):
            bad += 1
        print("%-46s %s  %s  四角alpha=%s" % (os.path.relpath(path, ROOT), im.size, "OK" if ok else "尺寸不符",
                                             corners))

    # 3) 安卓传统 mipmap：尺寸 + 逐像素（方形 / 圆形变体）
    print(r"android-app\app\src\main\res\mipmap-*")
    for dpi, size in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
        for name, circular in (("ic_launcher.png", False), ("ic_launcher_round.png", True)):
            p = os.path.join(ANDROID_RES, "mipmap-" + dpi, name)
            im = Image.open(p)
            if im.size != (size, size):
                print("   !! %s 尺寸 %s != %s" % (p, im.size, (size, size)))
                bad += 1
                continue
            got = im.convert("RGBA")
            want = gen.icon(size, circular=circular)
            diff = sum(1 for a, b in zip(got.getdata(), want.getdata()) if a != b)
            if diff:
                bad += 1
            print("   %-24s %3dpx  %s" % (dpi + "/" + name, size, "OK" if diff == 0 else "差异 %d px" % diff))

    print("\n结论:", "全部一致" if not bad else "有 %d 处不一致，需人工复核" % bad)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
