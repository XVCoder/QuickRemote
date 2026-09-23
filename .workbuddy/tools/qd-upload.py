#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
qd 平台「申请令牌 + 上传 + 校验」一步到位。

用法（在仓库根执行）：
    python .workbuddy/tools/qd-upload.py <本地文件绝对路径> <目标目录ID> [allowed_extensions]

例：
    python .workbuddy/tools/qd-upload.py "E:/000_AI/QuickRemote/QuickRemote-PCClient-v1.1.70.zip" \
        3a0a3b57-a928-4da1-8528-b9e15d2047b3 zip

目录 ID 速查：
    quickremote/（根）        5b681a68-ff94-4a14-bc89-8aab6a200a53   （manifest/CHANGELOG/about 包）
    quickremote/pc-client/    3a0a3b57-a928-4da1-8528-b9e15d2047b3
    quickremote/android-app/  ef060395-855b-4b32-ac20-720854f0e9f9
    quickremote/relay-server/ 9b4af080-07e1-4822-8b84-892ab29c3ebe

为什么要有它（都是踩过的坑）：
  1. **令牌必须 json.load 后再正则**：`qd-mcp.py` 的返回里 `\\n` 是转义字面量而非换行，
     用 shell `grep -o '令牌: \\S+'` 会把紧随其后的中文一起吞进 token，上传只会得到
     `token invalid or expired` —— 看着像令牌过期，实为解析错误。
  2. **失败的上传也消耗 `max_uploads`** → 申请时给足 5 次，上传用重试循环。
  3. **判据必须是响应里的 `file_id`**，不能只看 HTTP 200。
  4. 本机常驻 ShadowsocksR PAC，`curl` 一律 `--noproxy '*'`；Windows 原生 curl 不认
     MSYS 风格 `/e/...` 路径，文件路径要写 `E:/...`。

输出：一行 JSON（含 file_id / share_url / size），供 manifest 与 upgrade_app 使用。
密钥由 `qd-mcp.py` 运行期从 `~/.workbuddy/mcp.json` 读取，不入库。
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MCP = os.path.join(HERE, "qd-mcp.py")


def mcp_call(tool, params):
    out = subprocess.run([sys.executable, MCP, "call", tool, json.dumps(params)],
                         capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        raise SystemExit("[qd-upload] qd-mcp 调用失败: " + (out.stderr or out.stdout)[-500:])
    return json.loads(out.stdout)


def text_of(resp):
    return resp["result"]["content"][0]["text"]


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    path = sys.argv[1]
    dir_id = sys.argv[2]
    ext = sys.argv[3] if len(sys.argv) > 3 else os.path.splitext(path)[1].lstrip(".")
    if not os.path.isfile(path):
        raise SystemExit("[qd-upload] 找不到文件: " + path)
    size = os.path.getsize(path)

    tok_resp = mcp_call("create_upload_token", {
        "target_dir_id": dir_id,
        "permanent_share": True,
        "allow_overwrite": True,
        "allowed_extensions": ext,
        "max_uploads": 5,
    })
    body = text_of(tok_resp)
    token = re.search(r"令牌:\s*([A-Za-z0-9_\-]+)", body)
    url = re.search(r"上传 URL:\s*(\S+)", body)
    if not token or not url:
        raise SystemExit("[qd-upload] 未能从返回中解析令牌：\n" + body[:400])
    token, url = token.group(1), url.group(1)
    print("[qd-upload] 令牌已获取，目标 %s，文件 %.1f KB" % (dir_id, size / 1024.0), file=sys.stderr)

    win_path = path.replace("\\", "/")
    resp = ""
    for attempt in range(1, 6):
        proc = subprocess.run(
            ["curl.exe", "-sS", "--noproxy", "*", "--max-time", "300",
             "-X", "POST", "-F", "file=@" + win_path, url],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        resp = (proc.stdout or "") + (proc.stderr or "")
        if "file_id" in resp:
            break
        print("[qd-upload] 第 %d 次尝试未含 file_id，重试… (%s)" % (attempt, resp.strip()[:200]),
              file=sys.stderr)
    if "file_id" not in resp:
        raise SystemExit("[qd-upload] 上传失败：\n" + resp[:800])

    info = json.loads(resp[resp.index("{"):resp.rindex("}") + 1])
    info["local_bytes"] = size
    print(json.dumps(info, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
