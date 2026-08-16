# QuickRemote

安卓远程连接 Windows 桌面的工具，通过自建中转服务器全量转发 RDP 流量。PC 端安装客户端并配置服务器地址后自动注册，安卓 App 配置相同服务器地址即可获取所有在线 PC 并发起远程桌面连接。

## 架构

```
┌──────────────┐         ┌─────────────────┐         ┌──────────────┐
│  Android App │◄───────►│  Relay Server   │◄───────►│  PC Client   │
│  (Kotlin)    │  HTTPS  │  (Go)           │   TCP   │  (C#/.NET)   │
│              │         │                 │         │              │
│ - 配置服务器  │         │ - 设备注册管理    │         │ - WPF界面     │
│ - 获取设备列表│         │ - 设备列表服务    │         │ - 托盘最小化  │
│ - RDP客户端   │         │ - TCP隧道桥接    │         │ - 注册到服务器 │
│   (FreeRDP)  │         │ - 认证鉴权       │         │ - 维持心跳    │
│ - 版本更新    │         │ - 关于页面       │         │ - 按需建隧道  │
│ - 日志上报    │         │ - 日志接收       │         │ - 版本更新    │
└──────────────┘         └─────────────────┘         │ - 日志上报    │
                               │                     └──────────────┘
                          ┌────┴────┐
                          │ SQLite  │
                          │ 设备注册表│
                          └─────────┘
```

### 核心流程

1. **PC 注册**：PC 客户端启动 → TCP 控制连接 → 预共享密钥认证 → 注册设备信息 → 维持心跳
2. **设备发现**：安卓 App 认证 → 请求设备列表 → 服务器返回在线 PC
3. **远程连接**：安卓选择 PC → 请求隧道 → 服务器通知 PC 建数据连接 → 双向桥接 RDP 流量

### 安全机制

- 预共享密钥 SHA-256 哈希传输
- TLS 加密两种模式：
  - **服务器原生 TLS**（推荐）：服务器 8444 端口直接监听 TLS，PC 客户端用 `https://` 连接
  - **nginx 反向代理**：服务器监听明文 HTTP/TCP，TLS 由 nginx 处理（仅适用于 HTTP API，TCP 控制连接无法代理）
- JWT 令牌认证（安卓 App）
- 隧道 session_id 验证

## 项目结构

```
QuickRemote/
├── relay-server/          # Go 中转服务器
├── pc-client/             # C#/.NET 8 WPF Windows 客户端
├── android-app/           # Kotlin 安卓应用
├── docs/                  # 设计文档与实现计划
│   └── superpowers/
│       ├── specs/2026-08-01-quickremote-design.md
│       └── plans/2026-08-01-relay-server.md
└── manifest.json          # quickdeploy 版本清单
```

## 技术栈

| 组件 | 技术 | 说明 |
|------|------|------|
| 中转服务器 | Go 1.22+ + SQLite (modernc.org/sqlite) | 高性能并发，纯 Go SQLite 无 CGO 依赖 |
| PC 客户端 | C# / .NET 8 WPF | Windows 原生，UI + 后台服务一体 |
| 安卓 App | Kotlin + Jetpack Compose + OkHttp | 原生体验，Material3 设计 |
| 部署分发 | quickdeploy | 统一包管理和分发 |

## 部署

### quickdeploy 资源地址

所有产物托管在 quickdeploy，固定分享 URL（通过 overwrite 机制保持不变）：

| 资源 | 下载 URL |
|------|----------|
| install.sh（部署脚本） | `https://quickdeploy.solutionx.top/d/p/1a72422e-9c43-4681-bcb6-dd24f90f6f7d` |
| manifest.json（版本清单） | `https://quickdeploy.solutionx.top/d/p/c356c4e0-cf85-497d-b8dd-f445a4ccf2b3` |
| Relay Server v1.0.1 (amd64) | `https://quickdeploy.solutionx.top/d/p/256f06cf-423e-4a4b-a206-9f415afdc70d` |
| Relay Server v1.0.1 (arm64) | `https://quickdeploy.solutionx.top/d/p/c23185f6-ce83-4b13-a93d-a5740982288c` |
| PC Client v1.0.1 (zip) | `https://quickdeploy.solutionx.top/d/p/cf866e90-3678-41f1-9b9d-6f5e2a077c85` |
| Android APK v1.0.1 | `https://quickdeploy.solutionx.top/d/p/de2bdffe-850a-448d-99e6-97ff01e42d5c` |

> PC 客户端为 zip 压缩包，解压后运行 `QuickRemote.PCClient.exe`，需安装 .NET 8 Desktop Runtime。

### 部署 Relay Server（Linux 服务器）

**一键部署（推荐）**：

```bash
curl -fsSL -o install.sh https://quickdeploy.solutionx.top/d/p/1a72422e-9c43-4681-bcb6-dd24f90f6f7d && sudo bash install.sh
```

脚本为交互式，无需传参：

- **首次安装**：自动检测架构（amd64/arm64）→ 提示输入监听端口、预共享密钥、JWT 密钥（均可回车采用默认值）→ 下载二进制 → 创建配置 → 注册 systemd 服务并启动
- **已安装**：显示交互菜单
  ```
  QuickRemote 已安装 (当前版本: v1.0.0)
  请选择操作:
  1) 升级到最新版本
  2) 修改配置
  3) 卸载
  请输入选项序号 [1-3]:
  ```

**安装路径**：

| 路径 | 用途 |
|------|------|
| `/opt/quickremote/quickremote-relay` | 二进制 |
| `/opt/quickremote/VERSION` | 版本号 |
| `/etc/quickremote/config.yaml` | 配置文件 |
| `/var/lib/quickremote/registry.db` | SQLite 设备注册表 |
| `/etc/systemd/system/quickremote-relay.service` | systemd 服务 |

**服务管理**：

```bash
sudo systemctl start quickremote-relay      # 启动
sudo systemctl stop quickremote-relay       # 停止
sudo systemctl restart quickremote-relay    # 重启
sudo systemctl status quickremote-relay     # 状态
sudo journalctl -u quickremote-relay -f     # 实时日志
```

**配置文件示例**（`/etc/quickremote/config.yaml`）：

```yaml
server:
  listen: ":8443"           # HTTP API 监听端口（明文）
  control_listen: ":8444"   # PC 控制连接监听端口（明文或 TLS）
  tls:
    cert: ""                # 启用服务器原生 TLS 时填写证书路径（留空则监听明文）
    key: ""                 # 启用服务器原生 TLS 时填写私钥路径
auth:
  pre_shared_key: "your-secret-key"
  jwt_secret: "jwt-signing-secret"
storage:
  sqlite_path: "/var/lib/quickremote/registry.db"
quickdeploy:
  base_url: ""
```

**TLS 配置**：支持两种 TLS 模式，按部署场景选择其一：

1. **服务器原生 TLS**（推荐）：在 `config.yaml` 中填写 `server.tls.cert` 和 `server.tls.key`，服务器 8444 端口直接监听 TLS，PC 客户端用 `https://` 前缀连接
2. **nginx 反向代理**：服务器监听明文，TLS 由 nginx 处理。HTTP API 可用 nginx http 模块代理，TCP 控制连接需用 nginx stream 模块代理。nginx 示例：

```nginx
server {
    listen 443 ssl;
    server_name relay.example.com;
    ssl_certificate     /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://127.0.0.1:8443;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
    }
}

# PC 控制连接（TCP 9443 → 8444），需要 nginx stream 模块
stream {
    server {
        listen 9443 ssl;
        ssl_certificate     /path/to/cert.pem;
        ssl_certificate_key /path/to/key.pem;
        proxy_pass 127.0.0.1:8444;
    }
}
```

**关于页面**：部署后访问 `http://<服务器地址>:8443/about`（或通过 nginx 的 `https://relay.example.com/about`）查看项目介绍和更新记录。

### 部署 PC 客户端（Windows）

**前置要求**：已安装 [.NET 8 运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（Desktop Runtime）。

**安装步骤**：

1. 从 manifest.json 中查询 `pc-client.versions.1.0.0.files`，下载所有文件到同一目录：
   - `QuickRemote.PCClient.exe`
   - `QuickRemote.PCClient.dll`
   - `QuickRemote.PCClient.deps.json`
   - `QuickRemote.PCClient.runtimeconfig.json`
   - `appsettings.json`
2. 运行 `QuickRemote.PCClient.exe`
3. 在主界面「设置」区域填入：
   - 服务器地址：根据部署方式选择
     - 服务器原生 TLS（推荐）：`https://relay.example.com`（默认 8444 端口）
     - 明文 TCP：`relay.example.com:8444`
     - nginx 反向代理 TLS：`https://relay.example.com:9443`（stream 模块代理 8444）
   - 预共享密钥：与服务器配置一致
4. 点击「保存设置」

**首次配置建议**：

- 点击「检查 RDP」确认远程桌面服务已启用；若未启用，点击「一键启用 RDP」（需 UAC 提权）
- 勾选「开机自启」让程序随系统启动
- 勾选「启动时检查更新」自动获取新版本通知

**配置文件**（`appsettings.json`）：

```json
{
  "Server": {
    "Address": "https://relay.example.com",
    "PreSharedKey": "your-secret-key"
  },
  "Rdp": {
    "Port": 3389,
    "AutoEnable": false
  },
  "AutoStart": false,
  "CheckUpdateOnStart": true,
  "AutoUploadLogs": false,
  "QuickDeploy": {
    "ManifestUrl": "https://quickdeploy.solutionx.top/d/p/c356c4e0-cf85-497d-b8dd-f445a4ccf2b3",
    "ChangelogUrl": ""
  },
  "MachineId": ""
}
```

### 部署 Android App

**安装步骤**：

1. 手机开启「允许安装未知来源应用」
2. 下载 APK：`https://quickdeploy.solutionx.top/d/p/de2bdffe-850a-448d-99e6-97ff01e42d5c`
3. 点击安装
4. 打开 App，进入「服务器配置」页
5. 输入中转服务器地址（例如 `http://relay.example.com:8443` 或 nginx 代理的 `https://relay.example.com`）和预共享密钥
6. 点击「测试连接」确认可达后保存

## 使用说明

### PC 客户端

- **主界面**：实时显示服务器连接状态、设备 ID、心跳时间、当前远程会话列表、RDP 配置状态
- **系统托盘**：点击窗口关闭按钮(✕)会最小化到托盘而非退出；双击托盘图标恢复窗口；右键菜单可「显示主窗口」或「退出」
- **托盘图标**：显示应用图标，状态变化时通过气泡通知提示（已连接/连接中/重连中/未连接）
- **RDP 配置**：自动检查注册表 `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\fDenyTSConnections` 和防火墙 3389 端口规则；未启用时提供一键启用按钮
- **更新记录**：点击「更新记录」按钮查看版本变更日志
- **日志上报**：点击「上传日志」将本地日志发送到中转服务器

### Android App

- **设备列表页（主页）**：自动获取所有在线 PC，下拉刷新；点击设备卡片发起远程连接；长按可收藏常用设备
- **远程会话页**：连接建立后显示远程桌面画面
  - 单指移动 = 鼠标移动
  - 单击 = 左键
  - 双指点击 = 右键
  - 双指缩放 = 画面缩放
  - 顶部工具栏：断开、键盘切换、全屏切换
- **设置页**：
  - 服务器地址修改
  - 显示分辨率：自适应 / 原分辨率 / 指定分辨率
  - 颜色深度：16bit / 32bit
  - 音频重定向开关
  - 版本检查更新
  - 更新记录
  - 日志上传
  - 关于

## 版本管理与更新

### 版本清单机制

所有版本信息统一记录在 `manifest.json` 中，包含各组件的：
- `latest_version`：最新版本号
- `versions.<版本号>`：各版本下载 URL
- `changelog`：版本更新日志

manifest.json 的分享 URL 固定不变（通过 quickdeploy 的 overwrite 机制），客户端和 install.sh 通过此 URL 获取最新版本信息。

### 升级 Relay Server

```bash
sudo bash /opt/quickremote/install.sh
# 选择选项 1：升级到最新版本
```

或在已安装的服务器上重新执行一键命令：

```bash
curl -fsSL -o install.sh https://quickdeploy.solutionx.top/d/p/1a72422e-9c43-4681-bcb6-dd24f90f6f7d && sudo bash install.sh
```

### 升级 PC 客户端

- 启动时若勾选了「启动时检查更新」，会自动查询 manifest.json 比对版本
- 在主界面点击「检查更新」手动触发
- 检测到新版本后，从 manifest.json 中获取下载 URL，下载替换后重启

### 升级 Android App

- 在「设置」页点击「检查更新」
- 下载新版本 APK 后触发系统安装意图

## 日志与问题上报

### 日志格式

所有组件统一日志格式：

```
[2026-08-01 10:00:00] [INFO] [RelayConnection] Connected to relay server
```

### 日志收集

- 本地日志按天滚动，保留最近 7 天
- PC 客户端日志路径：`%AppData%\QuickRemote\logs\`
- Android App 日志路径：应用内部存储
- 中转服务器日志：通过 `journalctl -u quickremote-relay` 查看

### 日志上传

- PC 客户端/Android App 在设置页点击「上传日志」
- 日志上传到中转服务器 `/api/logs/upload` 端点
- 支持自动上传 error 级别日志（可在设置中开关）

## 通信协议

### PC ↔ 服务器（控制连接）

TCP 长连接（默认 8444 端口），JSON 帧协议（`[4字节长度][JSON负载]`）。支持两种模式：
- **服务器原生 TLS**：PC 客户端用 `https://` 前缀连接，服务器 8444 端口直接监听 TLS
- **明文 TCP**：不带 `https://` 前缀连接，服务器监听明文（可由 nginx stream 模块代理 TLS）

- `register`：PC 注册设备信息
- `register_ack`：服务器返回分配的 device_id
- `heartbeat`：每 30 秒心跳
- `tunnel_request`：服务器通知 PC 建立数据隧道

### Android ↔ 服务器（HTTP API）

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/auth` | POST | 认证获取 JWT token |
| `/api/devices` | GET | 获取在线设备列表 |
| `/api/tunnel/request` | POST | 请求建立隧道 |
| `/api/logs/upload` | POST | 上传客户端日志 |

### 隧道数据协议

隧道建立阶段（TCP 连接）：

```
[1字节类型][session_id(37字节)]
类型: 0x01=PC隧道连接, 0x02=App隧道连接
```

session_id 验证后双方进入裸数据转发模式，服务器仅做 `io.Copy` 双向桥接 RDP 原始协议字节流。

## 开发

### 从源码构建

**Relay Server**：

```bash
cd relay-server
go test ./...                          # 运行测试
go build -o quickremote-relay ./cmd/server   # 本地编译

# 交叉编译双架构
GOOS=linux GOARCH=amd64 go build -ldflags "-s -w" -o quickremote-relay-v1.0.0-amd64 ./cmd/server
GOOS=linux GOARCH=arm64 go build -ldflags "-s -w" -o quickremote-relay-v1.0.0-arm64 ./cmd/server
```

**PC Client**：

```bash
cd pc-client
dotnet build                           # 调试编译
dotnet publish -c Release -r win-x64 --self-contained false -o publish   # 框架依赖发布
```

**Android App**：

```bash
cd android-app
./gradlew assembleDebug                # 调试 APK
./gradlew assembleRelease              # 发布 APK
```

### 发布新版本

1. 更新各组件代码，更新 `CHANGELOG.md`
2. 编译各端产物
3. 更新 `manifest.json` 中的版本号、下载 URL、changelog
4. 通过 quickdeploy MCP 上传新版本文件（`overwrite=true` 保持 share_url 不变）
5. 客户端和 install.sh 通过 manifest.json 自动感知新版本

## 已知限制

- **Android RDP 连接为占位实现**：当前版本未集成 FreeRDP 库，远程会话页面仅展示连接状态和触摸交互框架，实际 RDP 画面渲染需后续集成 FreeRDP NDK 库
- **PC 客户端为框架依赖模式**：目标机器需要预装 .NET 8 Desktop Runtime；如需独立运行，可改用 `--self-contained true` 重新发布
- **证书信任**：服务器支持原生 TLS（8444 端口）或 nginx 反向代理 TLS。若使用自签名证书，Android App 已通过 `network_security_config.xml` 配置信任；PC 客户端代码中也已配置信任所有证书

## 文档

- [设计文档](docs/superpowers/specs/2026-08-01-quickremote-design.md)
- [Relay Server 实现计划](docs/superpowers/plans/2026-08-01-relay-server.md)
