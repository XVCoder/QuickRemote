#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
qd 平台 MCP 直连工具（含 session 握手）。

## 为什么需要它

QuickRemote 的发版上传/部署走 qd.solutionx.top 的 QuickDeploy MCP。
正常路径是 WorkBuddy 里的 `mcp__qdrl__*` 工具；但当该 MCP 未注册/未加载
（表现为 ToolSearch 搜不到、`~/.workbuddy/mcp.json` 里没有 qdrl 条目）时，
**平台本身仍然健康** —— 其 JSON-RPC 端点 https://qd.solutionx.top/mcp 可以直接调用。
本脚本用同一 Bearer 调 MCP 工具，等价于 `mcp__qdrl__*`。
2026-09-16 发 Android v1.0.80 全程即用此路径完成上传、manifest 覆盖、about 升级、清理。

## 用法（在仓库根执行）

    python .workbuddy/tools/qd-mcp.py tools/list
    python .workbuddy/tools/qd-mcp.py call <tool_name> '<params-json>'

示例：

    # 列目录 / 申请上传令牌（allow_overwrite=true 才能保住固定分享链接）
    python .workbuddy/tools/qd-mcp.py call list_files '{"dir_id":"5b681a68-ff94-4a14-bc89-8aab6a200a53"}'
    python .workbuddy/tools/qd-mcp.py call create_upload_token '{"target_dir_id":"ef060395-855b-4b32-ac20-720854f0e9f9","permanent_share":true,"allow_overwrite":true,"allowed_extensions":"apk","max_uploads":5}'

    # 升级 about 应用（volumes 必带，否则下载统计清零）
    python .workbuddy/tools/qd-mcp.py call upgrade_app '{"app_id":"quickremote-about","version":"1.0.124","package_content":"file://<file_id>","auto_start":true,"volumes":["data"]}'

## 两个坑（都踩过）

1. **必须带 session**：流程是 initialize（从响应头取 `Mcp-Session-Id`）→
   `notifications/initialized` → 再发 tools/call。跳过握手直接 tools/list 会返回
   `HTTP 404 + "Invalid session ID"` —— 看着像"平台没有这些工具"，其实是没握手。
   本脚本每次调用都重新握手一次（开销可忽略）。
2. **curl 上传仍走 `curl.exe --noproxy '*'`**：本机常驻 ShadowsocksR PAC，
   走代理会 502 或中断；上传用重试循环 + 判断响应里是否含 `file_id`（不能只看 HTTP 200）。
3. **密钥不入库**：本脚本从 `~/.workbuddy/mcp.json` 里任一 solutionx.top 服务的
   `Authorization` 头取 Bearer（也可用环境变量 `QD_MCP_KEY` 覆盖）。
   这是平台级密钥（可上传/删除/部署），**绝不要硬编码进仓库**（本仓库已推 GitHub）。
"""
import json
import os
import sys
import urllib.error
import urllib.request

URL = "https://qd.solutionx.top/mcp"
MCP_JSON = os.path.join(os.path.expanduser("~"), ".workbuddy", "mcp.json")


def load_key():
    """按优先级取平台 Bearer：环境变量 → mcp.json 里任一 solutionx.top 服务。"""
    env = os.environ.get("QD_MCP_KEY")
    if env:
        return env.strip()
    try:
        with open(MCP_JSON, encoding="utf-8") as f:
            cfg = json.load(f)
        for name, srv in (cfg.get("mcpServers") or {}).items():
            url = (srv.get("url") or "")
            auth = ((srv.get("headers") or {}).get("Authorization") or "")
            if "solutionx.top" in url and auth.startswith("Bearer "):
                return auth[7:].strip()
    except Exception as e:  # noqa: BLE001
        raise SystemExit("无法从 %s 读取密钥（%s）；请设置环境变量 QD_MCP_KEY" % (MCP_JSON, e))
    raise SystemExit(
        "mcp.json 里没有可用密钥，也没有 QD_MCP_KEY 环境变量 —— "
        "请填入 WorkBuddy 的 qd MCP 配置，或临时 export QD_MCP_KEY=<Bearer>"
    )


BASE_HEADERS = {
    "Content-Type": "application/json",
    "Accept": "application/json, text/event-stream",
}


def _post(payload, session=None):
    headers = dict(BASE_HEADERS)
    headers["Authorization"] = "Bearer " + load_key()
    if session:
        headers["Mcp-Session-Id"] = session
    req = urllib.request.Request(URL, data=json.dumps(payload).encode("utf-8"), headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            return resp.read().decode("utf-8", "replace"), dict(resp.headers)
    except urllib.error.HTTPError as e:
        return e.read().decode("utf-8", "replace"), dict(e.headers or {})


def _parse(raw):
    """兼容纯 JSON 与 SSE（data: {...}）两种返回形态。"""
    txt = (raw or "").strip()
    if not txt:
        return {}
    if txt.startswith("{"):
        try:
            return json.loads(txt)
        except json.JSONDecodeError:
            pass
    for line in txt.splitlines():
        line = line.strip()
        if line.startswith("data:"):
            payload = line[5:].strip()
            if payload.startswith("{"):
                try:
                    return json.loads(payload)
                except json.JSONDecodeError:
                    continue
    return {"_unparsed": raw}


def open_session():
    raw, headers = _post(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "quickremote-release", "version": "1.0"},
            },
        }
    )
    sid = None
    for k, v in headers.items():
        if k.lower() == "mcp-session-id":
            sid = v
    if not sid:
        raise RuntimeError("initialize 未返回 Mcp-Session-Id：" + raw[:200])
    _post({"jsonrpc": "2.0", "method": "notifications/initialized"}, session=sid)
    return sid


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    cmd = sys.argv[1]
    sid = open_session()
    if cmd == "tools/list":
        raw, _ = _post({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, session=sid)
        out = _parse(raw)
    elif cmd == "call":
        tool = sys.argv[2]
        params = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
        raw, _ = _post(
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {"name": tool, "arguments": params},
            },
            session=sid,
        )
        out = _parse(raw)
    else:
        print("unknown command: " + cmd)
        return 1
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
