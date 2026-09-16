# QuickRemote

自建跨端远程控制工具：用安卓手机远程控制 Windows 电脑。**不依赖 RDP / 向日葵 / ToDesk**——屏幕画面由 PC 端 DXGI 采集、Media Foundation H.264 硬件编码，经自定义帧协议传输，Android 端 MediaCodec 零拷贝硬解到 Surface；触摸与键盘操作实时回传。同网段走**局域网直连**（低延迟），跨网络回落到**自建 Go 中继服务器**，绕过公网 IP / NAT 限制。

已开源：[github.com/XVCoder/QuickRemote](https://github.com/XVCoder/QuickRemote)（AGPL-3.0）

## 架构

```
┌──────────────────┐   屏幕流/输入   ┌──────────────────┐   HTTP/API   ┌──────────────────┐
│  PC 客户端        │◄─────────────►│  Android App      │◄───────────►│  Relay Server     │
│  (C#/.NET 8 WPF) │               │  (Kotlin/Compose) │             │  (Go + SQLite)    │
│                  │               │                   │             │                   │
│ - DXGI 屏幕采集   │               │ - 设备列表/备注    │             │ - 设备注册管理     │
│ - H.264 硬件编码  │               │ - MediaCodec 硬解 │             │ - TCP 隧道桥接    │
│ - 鼠标/键盘接收   │               │ - 触摸板手势      │             │ - 认证鉴权        │
│ - 剪贴板同步     │               │ - 虚拟键盘        │             │ - JWT 签发        │
│ - 被控安全(验证码)│               │ - 剪贴板同步      │             │ - 日志接收        │
│ - LAN 直连监听   │               │ - 后台保活        │             │ - 关于页面        │
└──────────────────┘               └──────────────────┘             └──────────────────┘
        ▲                                                                  ▲
        └────────────── 局域网直连 (8447) 优先，公网中继回落 ────────────────┘
```

### 核心流程

1. **PC 注册**：PC 客户端启动 → TCP 控制连接 → 预共享密钥认证 → 注册设备信息 → 心跳保活
2. **设备发现**：Android App 认证获取 JWT → 请求设备列表 → 返回在线/离线 PC
3. **远程连接**：Android 选择 PC → 请求隧道 → 服务器通知 PC 建数据连接 → 双向桥接（屏幕流下行 + 输入/剪贴板上行）
4. **屏幕流**：DXGI 采集 BGRA → H.264 硬件编码（`mfh264enc.dll`，编码器不可用时回落 JPEG）→ 帧协议传输 → MediaCodec 硬解渲染
5. **连接策略**：Android 主控端局域网直连优先（目标 PC 的 8447 监听 + `auth_key` 认证），直连不可达时自动回落公网中继

### 端口一览

| 端口 | 用途 |
|------|------|
| 8443 | HTTP/API：仅 Android 使用（认证 / 设备列表 / 隧道请求 / 日志） |
| 8444 | 控制连接：仅 PC 使用，**自动为 HTTP 端口 + 1**，无需配置 |
| 8445 | 隧道数据：PC 与 Android 共用（屏幕画面 / 输入数据桥接） |
| 8447 | LAN 直连：PC 端监听，主控端同网段直连优先 |

### 安全机制

- 预共享密钥 SHA-256 哈希传输，JWT 令牌认证（Android）
- 控制连接（8444）可选服务器原生 TLS；隧道数据为明文转发，公网场景建议源站前置 TLS 或使用局域网直连
- 访问验证码：被控 PC 可开启访问验证，主控端连接时须先提交验证码（15 秒超时、错 3 次断开）；验证通过前 PC 不采集、不编码
- 隧道 session_id 一次性校验，防连接劫持
- 剪贴板同步双端 SHA-256 指纹防回环
- 断开延迟锁屏：意外断开 30 秒后才锁定被控电脑，期间自动重连成功则取消

## 项目结构

```
QuickRemote/
├── relay-server/          # Go 中继服务器（含 deploy/install.sh 一键安装脚本）
├── pc-client/             # C#/.NET 8 WPF Windows 客户端（被控 + 主控）
├── pc-client.Tests/       # PC 客户端单元测试
├── pc-updater/            # PC 更新器（含配置保护逻辑）
├── android-app/           # Kotlin 安卓应用
├── docs/                  # 设计文档与实现计划
└── manifest.json          # 版本清单（客户端检查更新依据）
```

## 技术栈

| 组件 | 技术 | 说明 |
|------|------|------|
| 中继服务器 | Go 1.22+ + SQLite (modernc.org/sqlite) | 高并发，纯 Go SQLite 无 CGO 依赖 |
| PC 客户端 | C# / .NET 8 WPF + DXGI + Media Foundation | 屏幕采集、H.264 硬编、WPF 界面一体 |
| Android App | Kotlin + Jetpack Compose + MediaCodec + OkHttp | Surface 零拷贝硬解，Material 3 设计 |
| 分发与更新 | manifest.json + 关于页（Node.js） | 客户端经 manifest 检查更新，关于页提供下载 |

## 下载

最新版本（发布详情见[关于页](https://quickremote.solutionx.top/about)或 [CHANGELOG.md](CHANGELOG.md)）：

| 组件 | 当前版本 | 获取方式 |
|------|---------|---------|
| PC 客户端 | v1.1.67 | 关于页下载，或客户端内「检查更新」 |
| Android App | v1.0.80 | 关于页下载，或 App 内「检查更新」 |
| 中继服务器 | v1.0.8 | 见下方一键安装 |
| 源码 | — | [GitHub 仓库](https://github.com/XVCoder/QuickRemote) |

> PC 客户端为**框架依赖** ZIP（约 1.65 MB），目标机需预装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)。

## 部署

### 部署中继服务器（Linux）

**一键安装 / 升级（推荐）**：

```bash
curl -fsSL -o install.sh https://qd.solutionx.top/d/p/2430959d-0e8d-4647-9c89-0c671141709b && sudo bash install.sh
```

脚本为交互式，无需传参：

- **首次安装**：提示输入 HTTP/API 端口（默认 8443，控制连接自动 +1）、隧道数据端口（默认 8445）、预共享密钥、JWT 密钥（均可回车用默认值）→ 下载对应架构二进制 → 创建配置 → 注册 systemd 服务并启动
- **已安装**：进入交互菜单（升级 / 修改配置 / 卸载），数据库结构自动迁移

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

**配置示例**（`/etc/quickremote/config.yaml`，完整见 [relay-server/config.example.yaml](relay-server/config.example.yaml)）：

```yaml
server:
  listen: ":8443"           # HTTP/API，仅 Android 使用；控制连接自动为该端口 + 1
  tunnel_listen: ":8445"    # 隧道数据端口
  tls:
    cert: ""                # 填写证书路径则控制连接启用原生 TLS，留空为明文
    key: ""
auth:
  pre_shared_key: "change-me-please"
  jwt_secret: "jwt-signing-secret"
storage:
  sqlite_path: "/var/lib/quickremote/registry.db"
```

也支持 Docker 部署（见 [relay-server/deploy/docker-compose.yml](relay-server/deploy/docker-compose.yml)）。

### 部署 PC 客户端（Windows）

1. 安装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 从关于页下载 ZIP 并解压，运行 `QuickRemote.PCClient.exe`
3. 在「设置中心 → 基本配置」填入：
   - 服务器地址：`relay.example.com:8444`（明文控制连接）或 `https://relay.example.com`（服务器原生 TLS）
   - 预共享密钥：与服务器配置一致
4. 建议勾选「开机自启」与「启动时检查更新」

**配置文件**（`appsettings.json`，与 exe 同目录）：

```json
{
  "Server": {
    "Address": "relay.example.com:8444",
    "PreSharedKey": "change-me-please"
  },
  "AutoStart": false,
  "CheckUpdateOnStart": true,
  "AutoUploadLogs": false,
  "QuickDeploy": {
    "ManifestUrl": "https://qd.solutionx.top/d/p/<manifest-uuid>",
    "ChangelogUrl": "https://qd.solutionx.top/d/p/<changelog-uuid>"
  },
  "MachineId": ""
}
```

### 部署 Android App

1. 手机允许「安装未知来源应用」
2. 从关于页下载 APK 安装
3. 打开 App → 设置页填入中继服务器地址（如 `http://relay.example.com:8443`）与预共享密钥，测试连接后保存

## 使用说明

### PC 客户端

- **主界面**：实时显示服务器连接状态、设备 ID、心跳时间、当前远程会话
- **设置中心**：左侧导航分 基本配置 / 远程配置 / 被远程安全 / 版本更新 / 意见反馈 五页
- **被远程安全**：可开启访问验证码；断开延迟锁屏（30 秒缓冲，重连成功自动取消）
- **系统托盘**：关闭窗口最小化到托盘而非退出，托盘气泡提示连接状态变化
- **局域网直连**：自动监听 8447 并尝试添加防火墙入站规则（失败不影响公网中继链路）
- **版本更新**：设置中心内检查更新、阅读更新记录

### Android App

- **设备列表**：在线 / 离线设备分组展示，离线设备显示最后心跳时间，可备注（✎）、移除（✕，仅本机隐藏）；点击在线设备发起远程连接
- **远程会话**：
  - 画面区：单指滑动平移画面、点按操作鼠标、双指捏合缩放（跟手、锁定捏合中心）、双击后按住拖动 = 按住左键拖动窗口
  - 空白区触摸板（对齐 Win11 精确式触摸板）：双击拖动、三指手势（多任务 / 显示桌面 / 切换应用 / 搜索）、四指手势（切换虚拟桌面 / 通知中心），可在设置中逐项开关
  - 工具栏：断开、键盘、旋转、画质（流畅 / 标准 / 高清 / 原画，会话内即时切换）、全屏
  - 键盘弹起时底部出现 Ctrl / Shift 修饰键（单击修饰下一输入，快速再点长按锁定）与「中英」键（切换远程电脑输入法）
  - 后台保活：会话期间启用前台服务，切到其他应用、锁屏不断线
- **设置页**：服务器配置、画质、触摸板（光标速度 / 手势开关）、版本检查更新、更新记录、日志上传、意见反馈

## 版本管理与更新

### 版本清单机制

所有版本信息统一记录在 `manifest.json`（三端各自独立递增版本号）：`latest_version`、各版本下载 URL、`changelog`。manifest.json 托管在 qd 的分享 URL 固定不变（overwrite 机制），PC / Android 客户端与 install.sh 均通过该 URL 感知新版本。

### 升级方式

- **中继服务器**：重新执行一键安装命令，选择升级
- **PC 客户端**：设置中心「版本更新」页检查更新，下载替换后重启
- **Android App**：设置页「检查更新」，下载 APK 后触发系统安装

## 日志与反馈

- 日志统一格式：`[2026-09-16 22:00:00] [INFO] [RelayConnection] Connected to relay server`
- 本地日志按天滚动，保留最近 7 天
  - PC：`%AppData%\QuickRemote\logs\`
  - Android：应用内部存储
  - 中继：`sudo journalctl -u quickremote-relay`
- **意见反馈**：PC 设置中心 / Android 设置页填写反馈，可选附带最近 1000 行运行日志；反馈直传分发平台，**不依赖中继服务器是否在线**

## 通信协议

### PC ↔ 服务器（控制连接，8444）

TCP 长连接，JSON 帧协议 `[4字节长度][JSON负载]`（可选原生 TLS）：

| 消息 | 说明 |
|------|------|
| `register` / `register_ack` | PC 注册，服务器返回 device_id |
| `heartbeat` | 心跳保活 |
| `tunnel_request` | 服务器通知 PC 建立数据隧道 |

### Android ↔ 服务器（HTTP API，8443）

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/auth` | POST | 认证获取 JWT token |
| `/api/devices` | GET | 设备列表（`?all=1` 含离线设备） |
| `/api/tunnel/request` | POST | 请求建立隧道 |
| `/api/logs/upload` | POST | 上传客户端日志 |

### 屏幕流帧协议

```
[类型 1B][长度 4B LE][载荷]
```

载荷为 H.264 硬编码数据（编码器不可用时回落 JPEG）；Android 端 MediaCodec 直接送 Surface 渲染。

### 隧道握手

```
[1字节类型][session_id(37字节)]
类型: 0x01 = PC 隧道连接, 0x02 = App 隧道连接
```

session_id 校验通过后进入双向裸数据转发。

## 从源码构建

**中继服务器**：

```bash
cd relay-server
go test ./...                                # 运行测试
go build -o quickremote-relay ./cmd/server   # 本地编译
GOOS=linux GOARCH=amd64 go build -ldflags "-s -w" -o quickremote-relay-amd64 ./cmd/server
GOOS=linux GOARCH=arm64 go build -ldflags "-s -w" -o quickremote-relay-arm64 ./cmd/server
```

**PC 客户端**：

```bash
cd pc-client
dotnet test ../pc-client.Tests               # 单元测试
dotnet build                                 # 调试编译
dotnet publish -c Release -r win-x64 --self-contained false -o publish   # 框架依赖发布
```

**Android App**：

```bash
cd android-app
./gradlew assembleDebug                      # 调试 APK
./gradlew assembleRelease                    # 发布 APK
```

**发布新版本**：

1. 更新代码与 `CHANGELOG.md`（Android 发版同步更新 `android-app/app/src/main/assets/changelog.txt`）
2. 三端版本号各自递增，编译产物
3. 更新 `manifest.json` 的版本号、下载 URL、changelog，同步关于页 `server.js` 的 `CLIENTS`
4. 上传分发平台并线上校验

## 已知限制

- **PC 为框架依赖发布**：目标机需预装 .NET 8 Desktop Runtime (x64)，不支持 ARM64 Windows
- **Android 硬解依赖**：需要设备具备 H.264 硬件解码（MediaCodec），极老旧设备可能回落异常
- **隧道数据明文**：公网中继链路的隧道数据为明文转发，敏感环境建议仅在内网使用或前置加密代理

## 许可

[AGPL-3.0](../LICENSE)（GNU Affero General Public License v3）。可自由使用、修改与再分发；若将修改后的版本作为网络服务提供给他人，需公开对应源码。

## 文档

- [总体设计](docs/superpowers/specs/2026-08-01-quickremote-design.md)
- [远程屏幕方案](docs/remote-screen-design.md)
- [Relay Server 实现计划](docs/superpowers/plans/2026-08-01-relay-server.md)
