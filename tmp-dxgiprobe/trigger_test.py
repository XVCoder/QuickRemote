"""QuickRemote relay tunnel trigger test.

Simulates the Android app: /api/auth -> /api/tunnel/request.
Then watches the PC-side log to see if ScreenCaptureService.Start() still fails.
Finally connects as app (0x02) and closes, so the PC session tears down cleanly.

Secrets are read from the PC config and never printed.
"""
import hashlib
import json
import socket
import sys
import time
import urllib.request

BASE = "http://ocirl2.solutionx.top:8443"
# 设备 ID 与 PSK 从本机 PC 客户端配置读取（appsettings.json 不在仓库内，密钥不入库）
_CFG = r"E:\001_DevSoft\QuickRemote-PCClient\appsettings.json"
_cfg = json.load(open(_CFG, encoding="utf-8"))
DEVICE_ID = _cfg["MachineId"]
PSK = _cfg["Server"]["PreSharedKey"]
TUNNEL_HOST = BASE.split("//")[1].split(":")[0]
TUNNEL_PORT = 8445

import ssl
CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE


def post(path, payload, token=None):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, context=CTX, timeout=15) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def main():
    # 1. auth: body uses sha256(secret) hex
    h = hashlib.sha256(PSK.encode()).hexdigest()
    status, body = post("/api/auth", {"pre_shared_key": h})
    print("auth:", status)
    if status != 200:
        print("auth failed:", body[:120])
        sys.exit(1)
    token = json.loads(body).get("token", "")
    print("token acquired, len =", len(token))

    # 2. request tunnel
    status, body = post("/api/tunnel/request", {"device_id": DEVICE_ID}, token)
    print("tunnel request:", status)
    if status != 200:
        print("tunnel request failed:", body[:200])
        sys.exit(1)
    info = json.loads(body)
    session_id = info["session_id"]
    print("tunnel session:", session_id, "port:", info.get("tunnel_port"))
    print(">>> PC should now be starting a session. Watching for 8s...")

    # 3. wait and observe (caller tails PC log in parallel)
    time.sleep(8)

    # 4. connect as fake app and immediately close, to tear down PC session
    s = socket.create_connection((TUNNEL_HOST, TUNNEL_PORT), timeout=10)
    s.sendall(b"\x02" + session_id.encode("ascii").ljust(37, b"\x00"))
    time.sleep(1)
    s.close()
    print("fake app connection closed -> PC session should end")
    print("done")


if __name__ == "__main__":
    main()
