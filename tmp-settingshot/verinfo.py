"""读取 PE 文件的 ProductVersion / FileFileVersion，用于发版自校验。"""
import ctypes
import sys

def file_version(path: str) -> tuple[str, str]:
    size = ctypes.windll.version.GetFileVersionInfoSizeW(path, None)
    if not size:
        raise SystemExit(f"no version info: {path}")
    buf = ctypes.create_string_buffer(size)
    if not ctypes.windll.version.GetFileVersionInfoW(path, 0, size, buf):
        raise SystemExit("GetFileVersionInfoW failed")
    p = ctypes.c_void_p()
    l = ctypes.c_uint(0)
    ctypes.windll.version.VerQueryValueW(
        buf, "\\VarFileInfo\\Translation", ctypes.byref(p), ctypes.byref(l))
    lang = ctypes.cast(p, ctypes.POINTER(ctypes.c_uint16 * 2)).contents
    sub = "%04x%04x" % (lang[0], lang[1])
    out = []
    for key in ("ProductVersion", "FileVersion"):
        ctypes.windll.version.VerQueryValueW(
            buf, "\\StringFileInfo\\%s\\%s" % (sub, key), ctypes.byref(p), ctypes.byref(l))
        out.append(ctypes.wstring_at(int(p), int(l.value)))
    return out[0], out[1]

if __name__ == "__main__":
    for path in sys.argv[1:]:
        pv, fv = file_version(path)
        print(f"{path}\n  ProductVersion = {pv}\n  FileVersion    = {fv}")
