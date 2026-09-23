#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
QuickRemote 全端图标生成器 —— 单一矢量源头，四端导出。

用法：
    python .workbuddy/tools/gen-icons.py            # 生成全部
    python .workbuddy/tools/gen-icons.py --sheet    # 只出对照预览图（不写仓库文件）

源头（唯一真源，改这里就能四端同步）：
    QuickRemote/android-app/app/src/main/res/drawable/ic_launcher_foreground.xml
    QuickRemote/android-app/app/src/main/res/values/themes.xml   (@color/bg_primary)

为什么要有这个脚本（2026-09-23）：
    此前四端各用各的图 —— 安卓 8+ 走自适应矢量（蓝 #3B82F6 + 绿 #10B981 + 底 #0F1117，
    正好是主题令牌 Accent/Success/BgPrimary），而 PC 的 app.ico/app.png、安卓 API<26 的
    传统 mipmap PNG 却还是更早那套青绿「显示器 + WiFi」位图（角落还带「AI生成」水印）。
    现在统一以自适应矢量为准，其他全部由它导出，避免以后再各改各的。

渲染口径：
    - 自适应图标（anydpi-v26）由启动器自行裁切，前景按 108 网格设计、可见安全区 72。
      启动器会把前景放大 1.5 倍后裁到可见方形，所以独立图标（传统 PNG / PC / 网页）
      里字形的视觉大小 = 44/72 ≈ 61%（见 launcher 呈现）。
    - 独立图标一律「字形按包围盒居中 + 圆角方形底」，字号略放大到 70% 以便小尺寸可辨识。
    - 各尺寸逐个独立渲染（而非从一张大图缩），小尺寸才不糊。

产出：
    android-app/.../mipmap-{mdpi,hdpi,xhdpi,xxhdpi,xxxhdpi}/ic_launcher.png      48/72/96/144/192
                                                              /ic_launcher_round.png  同上（圆形）
    pc-client/app.ico            16/24/32/48/64/128/256（exe 图标：资源管理器/任务栏/Alt+Tab）
    pc-client/app.png            256 圆角方形（MainWindow/RemoteViewerWindow 窗口图标 + 托盘图标）
    QuickRemote-about/public/icon.png            192
    QuickRemote-about/public/apple-touch-icon.png 180
    QuickRemote-about/public/favicon.ico          16/32/48

⚠️ 新增静态资源后必须同步 .workbuddy/tools/pack-about.py 的 BINARY_FILES，否则上线 404。
"""
import io
import math
import os
import re
import struct
import sys

from PIL import Image, ImageDraw

ROOT = r"E:\000_AI\QuickRemote"
ANDROID_RES = os.path.join(ROOT, r"QuickRemote\android-app\app\src\main\res")
PC_DIR = os.path.join(ROOT, r"QuickRemote\pc-client")
ABOUT_PUB = os.path.join(ROOT, r"QuickRemote-about\public")

FOREGROUND_XML = os.path.join(ANDROID_RES, r"drawable\ic_launcher_foreground.xml")
THEMES_XML = os.path.join(ANDROID_RES, r"values\themes.xml")

# ---- 视觉参数（要调风格改这里）------------------------------------------------
BG_COLOR = None          # None = 从 themes.xml 的 @color/bg_primary 读
RADIUS_FRAC = 0.223      # 圆角半径 / 边长，与旧青绿图标保持一致
GLYPH_FRAC = 0.70        # 字形最大边 / 边长（独立图标）
GLYPH_FRAC_SMALL = 0.86  # 16/32px 这种极小尺寸放大一点，否则糊成一团
RADIUS_FRAC_SMALL = 0.17 # 小尺寸圆角收一点，避免边缘发灰
SS = 4                   # 超采样倍数
ARC_STEPS = 96           # 圆弧离散段数


# ---- Android vector path 解析（只需支持本文件用到的 M/h/v/l/a/z）-------------
def _arc(p0, p1, rx, ry, laf, sf, steps=ARC_STEPS):
    x0, y0 = p0
    x1, y1 = p1
    if rx == 0 or ry == 0:
        return [p1]
    dx2, dy2 = (x0 - x1) / 2.0, (y0 - y1) / 2.0
    rx, ry = abs(rx), abs(ry)
    lam = dx2 * dx2 / (rx * rx) + dy2 * dy2 / (ry * ry)
    if lam > 1:
        rx *= math.sqrt(lam)
        ry *= math.sqrt(lam)
    num = rx * rx * ry * ry - rx * rx * dy2 * dy2 - ry * ry * dx2 * dx2
    den = rx * rx * dy2 * dy2 + ry * ry * dx2 * dx2
    co = math.sqrt(max(0.0, num / den)) * (-1 if laf == sf else 1)
    cxp, cyp = co * rx * dy2 / ry, -co * ry * dx2 / rx
    cx, cy = (x0 + x1) / 2.0 + cxp, (y0 + y1) / 2.0 + cyp

    def ang(ux, uy, vx, vy):
        d = (ux * vx + uy * vy) / (math.hypot(ux, uy) * math.hypot(vx, vy))
        a = math.acos(max(-1.0, min(1.0, d)))
        return -a if ux * vy - uy * vx < 0 else a

    th1 = ang(1, 0, (dx2 - cxp) / rx, (dy2 - cyp) / ry)
    dth = ang((dx2 - cxp) / rx, (dy2 - cyp) / ry, (-dx2 - cxp) / rx, (-dy2 - cyp) / ry)
    if not sf and dth > 0:
        dth -= 2 * math.pi
    elif sf and dth < 0:
        dth += 2 * math.pi
    return [(cx + rx * math.cos(th1 + dth * i / steps), cy + ry * math.sin(th1 + dth * i / steps))
            for i in range(1, steps + 1)]


def parse_path(d):
    """返回若干子路径（每段是点列表）。"""
    toks = re.findall(r"[MmLlHhVvAaZzCcSsQqTt]|-?\d*\.?\d+(?:[eE][-+]?\d+)?", d)
    i = 0
    cmd = None
    cur = (0.0, 0.0)
    start = (0.0, 0.0)
    subs = [[cur]]
    while i < len(toks):
        t = toks[i]
        if re.match(r"[A-Za-z]", t):
            cmd = t
            i += 1
            if cmd in "Zz":
                if subs[-1][-1] != start:
                    subs[-1].append(start)
                subs.append([start])
                cur = start
                continue
        rel = cmd.islower()
        c = cmd.upper()

        def num():
            nonlocal i
            v = float(toks[i])
            i += 1
            return v

        if c == "M":
            p = (num(), num())
            p = (cur[0] + p[0], cur[1] + p[1]) if rel else p
            if len(subs[-1]) == 1 and subs[-1][0] == cur:
                subs[-1][0] = p
            else:
                subs.append([p])
            cur = start = p
            cmd = "l" if rel else "L"
        elif c == "L":
            p = (num(), num())
            p = (cur[0] + p[0], cur[1] + p[1]) if rel else p
            subs[-1].append(p)
            cur = p
        elif c == "H":
            x = num()
            p = (cur[0] + x if rel else x, cur[1])
            subs[-1].append(p)
            cur = p
        elif c == "V":
            y = num()
            p = (cur[0], cur[1] + y if rel else y)
            subs[-1].append(p)
            cur = p
        elif c == "A":
            rx, ry, _rot = num(), num(), num()
            laf, sf = num(), num()
            tp = (num(), num())
            p = (cur[0] + tp[0], cur[1] + tp[1]) if rel else tp
            subs[-1].extend(_arc(cur, p, rx, ry, laf == 1, sf == 1))
            cur = p
        else:
            raise SystemExit("[gen-icons] 暂不支持的 path 命令: " + cmd)
    return [s for s in subs if len(s) > 2]


# ---- 读取真源 ---------------------------------------------------------------
def load_source():
    xml = open(FOREGROUND_XML, encoding="utf-8").read()
    all_layers = [(c, d) for c, d in
                  re.findall(r'android:fillColor="(#\w+)"\s+android:pathData="([^"]+)"', xml)]
    if not all_layers:
        raise SystemExit("[gen-icons] 没从该 vector 里解析到 path")
    themes = open(THEMES_XML, encoding="utf-8").read()
    m = re.search(r'<color name="bg_primary">#(?:FF)?([0-9A-Fa-f]{6})</color>', themes)
    bg = BG_COLOR or tuple(int(m.group(1)[k:k + 2], 16) for k in (0, 2, 4))

    # 该 vector 的第一个图层是铺满 108 网格的「背景板」（颜色 = bg_primary）。
    # 独立图标自己会画底，所以这里必须把它剔掉，否则字形包围盒 = 整张画布、无法居中。
    layers = []
    for color, d in all_layers:
        pts = [p for sp in parse_path(d) for p in sp]
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        full_bleed = min(xs) <= 0.01 and min(ys) <= 0.01 and max(xs) >= 107.99 and max(ys) >= 107.99
        if full_bleed and rgb(color)[:3] == bg:
            print("[gen-icons] 跳过背景板图层 %s" % color)
            continue
        layers.append((color, d))
    return layers, bg


def rgb(hexstr):
    h = hexstr.lstrip("#")
    return tuple(int(h[k:k + 2], 16) for k in (0, 2, 4)) + (255,)


def render_foreground(px_per_unit):
    """按 px_per_unit 渲染 108 网格前景，返回带 alpha 的图（画布 = 108*px_per_unit）。"""
    side = max(1, int(round(108 * px_per_unit)))
    big = side * SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    dr = ImageDraw.Draw(img)
    k = big / 108.0
    for color, d in LAYERS:
        fill = rgb(color)
        for sp in parse_path(d):
            dr.polygon([(x * k, y * k) for x, y in sp], fill=fill)
    return img.resize((side, side), Image.LANCZOS)


def glyph_box():
    """字形在 108 网格里的包围盒（用于居中）。"""
    return render_foreground(1.0).getbbox()


def compose(size, glyph_frac=GLYPH_FRAC, circular=False, radius_frac=RADIUS_FRAC, bg=None):
    """圆角方形（或圆形）底 + 字形按包围盒居中。"""
    bg = bg or BG_COLOR_RESOLVED
    big = size * SS
    base = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    dr = ImageDraw.Draw(base)
    if circular:
        dr.ellipse([0, 0, big - 1, big - 1], fill=bg + (255,))
    else:
        dr.rounded_rectangle([0, 0, big - 1, big - 1], int(round(big * radius_frac)), fill=bg + (255,))

    x0, y0, x1, y1 = GLYPH_BOX                     # 单位 = 108 网格
    gw, gh = x1 - x0, y1 - y0
    ppu = (size * SS * glyph_frac) / max(gw, gh)   # 工作画布下「每网格单位」的像素数
    glyph = render_foreground(ppu)
    gbox = glyph.getbbox()
    gcx, gcy = (gbox[0] + gbox[2]) / 2.0, (gbox[1] + gbox[3]) / 2.0
    base.alpha_composite(glyph, (int(round(big / 2.0 - gcx)), int(round(big / 2.0 - gcy))))
    return base.resize((size, size), Image.LANCZOS)


def icon(size, **kw):
    """独立图标默认取用的渲染参数（小尺寸单独加大字形）。"""
    kw.setdefault("glyph_frac", GLYPH_FRAC_SMALL if size <= 32 else GLYPH_FRAC)
    kw.setdefault("radius_frac", RADIUS_FRAC_SMALL if size <= 32 else RADIUS_FRAC)
    return compose(size, **kw)


def _ico_dib(frame):
    """把一帧打包成 ICO 内的 DIB（BITMAPINFOHEADER + BGRA 倒序行 + AND 掩码）。

    ⚠️ 为什么不直接用 Pillow 的 ICO 保存：它会把每一帧都写成 PNG 压缩。
       PNG 帧在 ICO 里只对 256×256 有公认支持，16~128 用 PNG 时
       Windows 资源管理器 / GDI+ 有概率读成空白图标。所以小尺寸一律走 DIB。
    """
    w, h = frame.size
    px = frame.convert("RGBA")
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px.getpixel((x, y))
            xor += bytes((b, g, r, a))
    row_bytes = ((w + 31) // 32) * 4
    andm = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(row_bytes)
        for x in range(w):
            if px.getpixel((x, y))[3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        andm += row
    hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor), 0, 0, 0, 0)
    return bytes(hdr) + bytes(xor) + bytes(andm)


def save_ico(path, sizes):
    """逐尺寸独立渲染后手工组装 ico：<256 用 DIB，256 用 PNG。"""
    payloads = []
    for s in sizes:
        frame = icon(s)
        if s >= 256:
            buf = io.BytesIO()
            frame.save(buf, format="PNG", optimize=True)
            data = buf.getvalue()
        else:
            data = _ico_dib(frame)
        payloads.append((s, data))

    offset = 6 + 16 * len(payloads)
    head = bytearray(struct.pack("<HHH", 0, 1, len(payloads)))
    body = bytearray()
    for s, data in payloads:
        dim = 0 if s >= 256 else s          # 256 在 ICO 目录里记作 0
        head += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        body += data
    with open(path, "wb") as f:
        f.write(bytes(head) + bytes(body))


def main():
    global LAYERS, BG_COLOR_RESOLVED, GLYPH_BOX
    LAYERS, BG_COLOR_RESOLVED = load_source()
    GLYPH_BOX = glyph_box()
    sheet_only = "--sheet" in sys.argv
    print("[gen-icons] 底色 #%02X%02X%02X，%d 个矢量图层，字形包围盒 %s"
          % (BG_COLOR_RESOLVED + (len(LAYERS), GLYPH_BOX)))

    written = []

    if not sheet_only:
        # --- Android 传统图标（API < 26 与部分第三方启动器）---
        for dpi, s in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
            p = os.path.join(ANDROID_RES, "mipmap-" + dpi, "ic_launcher.png")
            compose(s).save(p, optimize=True)
            written.append(p)
            p = os.path.join(ANDROID_RES, "mipmap-" + dpi, "ic_launcher_round.png")
            compose(s, circular=True).save(p, optimize=True)
            written.append(p)

        # --- PC ---
        ico = os.path.join(PC_DIR, "app.ico")
        save_ico(ico, [16, 24, 32, 48, 64, 128, 256])
        written.append(ico)
        png = os.path.join(PC_DIR, "app.png")
        compose(256).save(png, optimize=True)
        written.append(png)

        # --- 关于页 ---
        for name, size in (("icon.png", 192), ("apple-touch-icon.png", 180)):
            p = os.path.join(ABOUT_PUB, name)
            compose(size).save(p, optimize=True)
            written.append(p)
        fav = os.path.join(ABOUT_PUB, "favicon.ico")
        save_ico(fav, [16, 32, 48])
        written.append(fav)

    # --- 对照预览 ---
    sizes = [16, 24, 32, 48, 64, 96, 128, 192, 256]
    pad, gap, top = 16, 14, 26
    w = pad * 2 + sum(sizes) + gap * (len(sizes) - 1)
    h = top + max(sizes) + 14 + 30 + 90
    sheet = Image.new("RGB", (w, h), (0x16, 0x18, 0x1e))
    d = ImageDraw.Draw(sheet)
    x = pad
    for s in sizes:
        im = icon(s)
        sheet.paste(im, (x, top + max(sizes) - s), im)
        d.line([(x, top + max(sizes) + 6), (x + s, top + max(sizes) + 6)], fill=(0x30, 0x36, 0x44))
        d.text((x + s // 2, top + max(sizes) + 12), str(s), fill=(0x9c, 0xa3, 0xaf), anchor="ma")
        x += s + gap
    # 第二行：圆形变体 + 真实小尺寸贴在浅色底上的效果
    y2 = top + max(sizes) + 44
    d.text((pad, y2), "round", fill=(0x9c, 0xa3, 0xaf), anchor="la")
    rnd = compose(48, circular=True)
    sheet.paste(rnd, (pad + 44, y2 - 10), rnd)
    d.text((pad + 108, y2), "浅色底 16/24/32/48", fill=(0x9c, 0xa3, 0xaf), anchor="la")
    lx = pad + 260
    d.rectangle([lx, y2 - 12, lx + 236, y2 + 40], fill=(0xf2, 0xf3, 0xf5))
    for s in (16, 24, 32, 48):
        im = icon(s)
        sheet.paste(im, (lx + 12 + (48 - s) // 2, y2 + 26 - s // 2), im)
        lx += 56
    out = os.path.join(ROOT, "tmp-iconwork", "sheet.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    sheet.save(out)
    print("[gen-icons] 预览:", out)

    for p in written:
        print("  %-72s %7d B" % (p.replace(ROOT + "\\", ""), os.path.getsize(p)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
