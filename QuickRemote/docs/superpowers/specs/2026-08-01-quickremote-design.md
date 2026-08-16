# QuickRemote 远程桌面管理工具 - 设计文档

> 日期：2026-08-01
> 状态：已确认

## 1. 项目概述

QuickRemote 是一个安卓远程连接 Windows 桌面的工具，通过自建中转服务器转发流量实现。PC 端安装客户端并配置服务器地址后自动注册，安卓 App 配置相同服务器地址即可获取所有在线 PC 并发起远程桌面连接。

### 1.1 核心特性

- 基于 RDP 协议的远程桌面
- 自建服务器全量转发，无需公网 IP 或 NAT 穿透
- 预共享密钥 + TLS 加密认证
- 三端原生开发（Go / C# / Kotlin）
- quickdeploy 统一版本管理和分发
- 双架构支持（arm64v8 / amd64）
- 自带关于页面，自托管无外部依赖
- 客户端日志上报机制

### 1.2 技术选型

| 组件 | 技术 | 说明 |
|------|------|------|
| 中转服务器 | Go + SQLite | 高性能并发，嵌入式存储无外部依赖 |
| PC 客户端 | C# / .NET 8 WPF | Windows 原生，UI + 后台服务一体 |
| 安卓 App | Kotlin + Jetpack Compose + FreeRDP | 原生体验，NDK 集成 FreeRDP |
| 部署 | quickdeploy-mcp | 统一包管理和分发 |

## 2. 整体架构

```
┌──────────────┐         ┌─────────────────┐         ┌──────────────┐
│  Android App │◄───────►│  Relay Server   │◄───────►│  PC Client   │
│  (Kotlin)    │   TLS   │  (Go)           │   TLS   │  (C#/.NET)   │
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

### 2.1 核心流程

1. **PC 注册**：PC 客户端启动 → 连接中转服务器 → TLS + 预共享密钥认证 → 注册设备信息（机器名、操作系统、RDP 端口）→ 维持心跳
2. **设备发现**：安卓 App 连接服务器 → 认证 → 请求设备列表 → 服务器返回所有在线 PC
3. **远程连接**：安卓选择 PC → 请求建立隧道 → 服务器通知 PC 建数据连接 → PC 连接服务器数据端口 → 安卓连接服务器数据端口 → 服务器桥接双向流量 → 安卓 FreeRDP 通过隧道连接到 PC 的 RDP 服务

### 2.2 项目目录结构

```
QuickRemote/
├── relay-server/          # Go 中转服务器
├── pc-client/             # C#/.NET WPF Windows客户端
├── android-app/           # Kotlin 安卓应用
└── docs/                  # 设计文档
```

## 3. 中转服务器（Relay Server）

### 3.1 模块结构

```
relay-server/
├── cmd/
│   └── server/
│       └── main.go              # 入口
├── internal/
│   ├── config/                  # 配置加载（YAML）
│   ├── auth/                    # 预共享密钥认证
│   ├── registry/                # 设备注册表管理
│   ├── tunnel/                  # TCP隧道桥接引擎
│   ├── api/                     # HTTP API + WebSocket
│   └── web/                     # 关于页面（内嵌静态资源）
├── deploy/
│   ├── install.sh               # 一键部署脚本（安装/升级/配置/卸载）
│   ├── Dockerfile               # 多架构构建
│   └── docker-compose.yml       # 可选Docker部署
└── config.example.yaml          # 配置示例
```

### 3.2 核心功能

#### 3.2.1 设备注册管理

- PC 连接后注册设备：machine_id, hostname, os_info, rdp_port, status
- 心跳机制：每 30 秒心跳，60 秒无心跳标记离线
- SQLite 持久化设备记录，重启后自动恢复

#### 3.2.2 TCP 隧道桥接

- 安卓请求连接 → 服务器生成 session_id → 通知 PC 建立数据连接
- PC 数据连接到达 → 等待安卓数据连接 → 双向 io.Copy 桥接
- 每个隧道独立 goroutine 管理，任一方断开则清理整个 session

#### 3.2.3 HTTP API

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/auth` | POST | 安卓 App 认证，获取 JWT token |
| `/api/devices` | GET | 获取在线设备列表 |
| `/api/tunnel/request` | POST | 请求建立隧道 |
| `/api/logs/upload` | POST | 接收客户端日志上报 |

PC 的控制连接使用 JSON 帧协议（非 HTTP），通过持久 TLS 连接通信。

#### 3.2.4 关于页面

- 内嵌 HTML/CSS/JS 静态资源（Go embed）
- 展示：项目介绍、安装步骤、PC 客户端下载、安卓 App 下载、配置说明
- **更新记录**：展示各组件（服务器、PC 客户端、安卓 App）的版本更新日志，从 quickdeploy 拉取 CHANGELOG
- 路径：`http://server:port/about`
- 自托管，无外部依赖

#### 3.2.5 部署

- **install.sh 功能（交互式，无参数）**：
  - 脚本执行时先检测当前环境是否已安装 QuickRemote
  - **未安装** → 进入安装流程：
    1. 检测系统架构（arm64v8 / amd64）
    2. 提示输入配置项（服务器监听端口、预共享密钥等），每项提供默认值，直接回车采用默认值
    3. 从 quickdeploy 下载对应架构最新版本二进制
    4. 创建配置文件
    5. 注册 systemd 服务并启动
    6. 显示安装结果和服务器地址
  - **已安装** → 显示交互菜单：
    ```
    QuickRemote 已安装 (当前版本: v1.0.2)
    请选择操作:
    1) 升级到最新版本
    2) 修改配置
    3) 卸载
    请输入选项序号 [1-3]:
    ```
    - 选项1（升级）：从 quickdeploy 拉取最新版本 → 停止服务 → 替换二进制 → 重启
    - 选项2（修改配置）：显示当前配置项，逐项提示输入新值（回车保持不变）→ 保存后重启服务
    - 选项3（卸载）：二次确认 → 停止服务 → 删除二进制和配置 → 清理 systemd 注册
- **架构检测**：`uname -m` → 映射到 arm64v8 / amd64
- **Docker 多架构构建**：`docker buildx --platform linux/arm64,linux/amd64`

### 3.3 配置文件

```yaml
server:
  listen: ":8443"
  tls:
    cert: ""      # 为空则自动生成自签名证书
    key: ""
auth:
  pre_shared_key: "your-secret-key"
storage:
  sqlite_path: "/var/lib/quickremote/registry.db"
```

## 4. PC 客户端（PC Client）

### 4.1 技术栈

C# / .NET 8 WPF + 系统托盘应用。具备完整 UI 界面，非纯后台服务。

### 4.2 模块结构

```
pc-client/
├── QuickRemote.PCClient/
│   ├── App.xaml                   # WPF入口
│   ├── MainWindow.xaml            # 主界面
│   ├── ViewModels/
│   │   ├── MainViewModel.cs       # 主界面VM
│   │   ├── SettingsViewModel.cs   # 设置VM
│   │   └── StatusViewModel.cs     # 状态VM
│   ├── Services/
│   │   ├── RelayConnection.cs     # 与中转服务器控制连接
│   │   ├── TunnelManager.cs       # 数据隧道管理
│   │   ├── SystemInfo.cs          # 机器信息收集
│   │   ├── RdpConfigurator.cs     # RDP配置检查
│   │   ├── UpdateChecker.cs       # 版本更新
│   │   └── TrayService.cs         # 系统托盘管理
│   ├── Models/
│   │   ├── Config.cs
│   │   └── SessionInfo.cs         # 远程会话状态
│   └── appsettings.json
├── QuickRemote.PCClient.Installer/  # Inno Setup打包
└── deploy/
    └── build-and-publish.ps1
```

### 4.3 界面功能

主界面包含以下区域：

- **服务器状态**：连接状态指示（已连接/连接中/断开/重连中）、设备 ID、心跳时间
- **当前远程会话列表**：每会话显示设备名、IP、时长、上下行流量
- **RDP 配置状态**：RDP 服务状态、防火墙规则状态
- **设置区域**：服务器地址、预共享密钥、开机自启、启动时检查更新
- **操作按钮**：保存设置、检查更新、更新记录、关于

#### 4.3.1 更新记录页面

- 弹出窗口展示 PC 客户端的版本更新日志
- 从 quickdeploy 拉取 CHANGELOG.md 并渲染展示
- 按版本倒序排列，显示版本号、发布日期、更新内容

### 4.4 核心行为

#### 4.4.1 窗口与托盘

- 点击关闭按钮(✕) → 最小化到系统托盘，不退出
- 托盘图标右键菜单：显示主窗口、退出
- 托盘图标双击 → 显示主窗口
- 托盘图标颜色反映状态：绿色=已连接，黄色=连接中，红色=断开

#### 4.4.2 中转服务器连接

- 建立 TLS 控制连接 → 发送注册信息
- 心跳：每 30 秒发送
- 控制连接断开自动重连（指数退避：1s → 2s → 4s → 8s → 16s → 最大 30s）

#### 4.4.3 数据隧道

- 收到服务器的隧道建立通知 → 获取 session_id
- 向服务器发起数据连接（携带 session_id 认证）
- 连接成功后，RDP 流量由服务器桥接转发
- 支持多并发隧道

#### 4.4.4 RDP 配置检查

- 检查 RDP 是否启用（注册表 `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\fDenyTSConnections`）
- 检查防火墙规则（3389 端口）
- 如未启用，提供一键启用按钮（需 UAC 提权）
- 可配置 RDP 端口（默认 3389）

#### 4.4.5 版本更新

- 启动时检查 quickdeploy 上的最新版本
- 下载新版本 → 停止服务 → 替换二进制 → 重启

#### 4.4.6 开机自启

- 注册到 Windows 启动项（注册表 `HKCU\...\Run`）
- 启动后自动最小化到托盘

### 4.5 配置文件

```json
{
  "relay": {
    "server": "https://relay.example.com:8443",
    "preSharedKey": "your-secret-key"
  },
  "rdp": {
    "port": 3389,
    "autoEnable": true
  },
  "update": {
    "checkOnStart": true,
    "quickDeployUrl": "https://deploy.example.com"
  }
}
```

## 5. 安卓 App（Android App）

### 5.1 技术栈

Kotlin + Jetpack Compose + FreeRDP（通过 NDK/JNI 集成）

### 5.2 模块结构

```
android-app/
├── app/
│   ├── src/main/java/com/quickremote/app/
│   │   ├── MainActivity.kt               # 入口Activity
│   │   ├── ui/
│   │   │   ├── screens/
│   │   │   │   ├── ServerConfigScreen.kt  # 服务器配置页
│   │   │   │   ├── DeviceListScreen.kt    # 设备列表页
│   │   │   │   ├── RemoteSessionScreen.kt # 远程桌面会话页
│   │   │   │   └── SettingsScreen.kt      # 设置页
│   │   │   ├── theme/
│   │   │   └── components/
│   │   ├── data/
│   │   │   ├── RemoteRepository.kt        # 数据仓库
│   │   │   ├── api/                       # 服务器API客户端
│   │   │   └── models/                    # 数据模型
│   │   ├── services/
│   │   │   ├── RelayConnection.kt         # 服务器连接
│   │   │   ├── RdpSessionManager.kt       # RDP会话管理
│   │   │   └── UpdateChecker.kt           # 版本更新
│   │   └── viewmodels/
│   ├── src/main/jniLibs/                  # FreeRDP so库
│   └── build.gradle.kts
└── deploy/
    └── build-and-publish.sh
```

### 5.3 页面与功能

#### 5.3.1 服务器配置页

- 输入中转服务器地址
- 输入预共享密钥
- 测试连接按钮
- 首次启动自动展示

#### 5.3.2 设备列表页（主页）

- 获取服务器上所有在线 PC 设备
- 卡片式展示：机器名、操作系统、在线状态、最后心跳
- 点击设备 → 进入远程会话
- 下拉刷新设备列表
- 支持收藏常用设备

#### 5.3.3 远程桌面会话页

- 通过 FreeRDP 连接到服务器隧道端口
- 触摸交互：单指移动=鼠标移动，单击=左键，双指=右键
- 支持缩放手势
- 顶部工具栏：断开连接、键盘切换、全屏切换
- 连接状态指示（连接中/已连接/断开）

#### 5.3.4 设置页

- 服务器地址修改
- 显示分辨率设置（自适应/原分辨率/指定分辨率）
- 颜色深度（16bit/32bit）
- 音频重定向开关
- 版本检查更新
- 更新记录（查看版本更新日志）
- 日志上传
- 关于

### 5.4 RDP 连接流程

```
App → POST /api/tunnel/request {device_id} → 服务器
服务器 → 返回 {session_id, tunnel_port}
服务器 → 通知PC建立数据连接(session_id)
App → FreeRDP连接到 server:tunnel_port (通过隧道)
服务器 → 桥接 App ↔ PC 的RDP流量
```

### 5.5 版本更新

- 查询 quickdeploy 获取最新 APK 版本
- 下载 APK → 触发安装意图
- 设置中可手动检查更新

## 6. 通信协议

所有通信基于 TLS 加密。

### 6.1 控制连接协议（PC ↔ 服务器）

PC 建立 TLS 连接后，使用 JSON 帧通信。帧格式：`[4字节长度][JSON负载]`

**PC → 服务器：**

```json
{
  "type": "register",
  "machine_id": "uuid",
  "hostname": "PC-OFFICE",
  "os": "Windows 11 Pro",
  "rdp_port": 3389,
  "version": "1.0.0",
  "auth_key": "sha256(pre-shared-key)"
}
```

**服务器 → PC：**

```json
{ "type": "register_ack", "status": "ok", "device_id": "assigned-id" }
```

**心跳（每 30s）：**

```json
{ "type": "heartbeat", "device_id": "xxx", "timestamp": 1234567890 }
```

**隧道建立通知：**

```json
{ "type": "tunnel_request", "session_id": "sess-xxx", "tunnel_port": 9100 }
```

### 6.2 HTTP API（安卓 App ↔ 服务器）

**认证：**

```
POST /api/auth
请求: { "pre_shared_key": "sha256-hash" }
响应: { "token": "jwt-token", "expires": 3600 }
```

**设备列表：**

```
GET /api/devices
Header: Authorization: Bearer <token>
响应: {
  "devices": [
    {
      "device_id": "xxx",
      "hostname": "PC-OFFICE",
      "os": "Win11",
      "status": "online",
      "last_seen": "2026-08-01T10:00:00Z"
    }
  ]
}
```

**请求隧道：**

```
POST /api/tunnel/request
Header: Authorization: Bearer <token>
请求: { "device_id": "xxx" }
响应: { "session_id": "sess-xxx", "tunnel_host": "relay.example.com", "tunnel_port": 9100 }
```

**日志上传：**

```
POST /api/logs/upload
请求: { "client_type": "pc|android", "device_id": "xxx", "logs": "base64-encoded-logs", "level": "error" }
响应: { "status": "ok", "log_id": "xxx" }
```

### 6.3 隧道数据协议

隧道建立阶段（TLS 连接）：

```
App/PC → 服务器:
  [1字节类型][session_id(36字节)]
  类型: 0x01=PC隧道连接, 0x02=App隧道连接
```

服务器验证 session_id 后，双方进入裸数据转发模式，之后所有数据为 RDP 原始协议字节流，服务器仅做 io.Copy 双向桥接。

### 6.4 隧道建立完整时序

```
Android          Relay Server          PC Client
   │                  │                     │
   │ POST /tunnel/req │                     │
   │─────────────────►│                     │
   │                  │ tunnel_request      │
   │                  │────────────────────►│
   │ {session_id,     │                     │
   │  tunnel_port}    │                     │
   │◄─────────────────│                     │
   │                  │     PC connects     │
   │                  │◄────────────────────│
   │                  │ (type=0x01,sess_id) │
   │ connects to      │                     │
   │ tunnel_port      │                     │
   │─────────────────►│                     │
   │(type=0x02,sess_id)│                    │
   │                  │ bridge established  │
   │◄════RDP流量双向转发═══►│◄══════════════►│
```

## 7. 错误处理

### 7.1 中转服务器

- 隧道任一方断开 → 立即关闭另一方连接，清理 session 资源
- PC 离线 → 设备列表标记离线，拒绝新隧道请求
- 服务器崩溃重启 → 从 SQLite 恢复设备记录，PC 重连后更新状态
- TLS 证书问题 → 支持自签名证书自动生成，配置文件可指定自定义证书

### 7.2 PC 客户端

- 控制连接断开 → 指数退避重连（1s→2s→4s→8s→16s→30s），UI 显示"重连中"
- 隧道连接失败 → 通知服务器清理 session，UI 显示错误信息
- RDP 未启用 → UI 红色警告，提供一键启用按钮（需 UAC 提权）
- 崩溃 → 写入 crash dump 到本地日志目录，下次启动提示上传

### 7.3 安卓 App

- 网络断开 → 自动重试连接服务器，显示离线状态
- RDP 连接失败 → 显示错误原因（认证失败/网络超时/PC 离线）
- FreeRDP 库崩溃 → 捕获 native 异常，记录日志，返回设备列表页
- Activity 销毁 → 会话状态保存，恢复后可重连

## 8. 日志与问题上报

### 8.1 日志格式

所有组件统一日志格式：

```
[2026-08-01 10:00:00] [INFO] [RelayConnection] Connected to relay server
```

### 8.2 日志收集与上传

- 本地日志按天滚动，保留最近 7 天
- PC 客户端/安卓 App 设置页有"上传日志"按钮
- 客户端上传日志到中转服务器的 `/api/logs/upload` 端点，中转服务器转发到 quickdeploy 的 `/quickremote/logs/{client_type}/{device_id}/` 目录
- 客户端只需知道中转服务器地址，无需直接访问 quickdeploy
- 支持自动上传 error 级别日志（可配置开关）

## 9. 部署与版本管理

### 9.1 quickdeploy 包管理

quickdeploy 的分享 URL 不含文件名，格式如 `https://quickdeploy.solutionx.top/d/E-urQhil_...`。因此采用以下管理策略：

**目录结构（quickdeploy 内部）：**

```
quickremote/                           # 根目录
├── install.sh                         # 部署脚本（overwrite 保持 URL 不变）
├── manifest.json                      # 版本清单（overwrite 保持 URL 不变）
├── relay-server/
│   ├── quickremote-relay-v1.0.0-amd64
│   ├── quickremote-relay-v1.0.0-arm64
│   ├── quickremote-relay-v1.0.1-amd64
│   └── quickremote-relay-v1.0.1-arm64
├── pc-client/
│   ├── QuickRemotePC-v1.0.0.exe
│   └── QuickRemotePC-v1.0.1.exe
├── android-app/
│   ├── QuickRemote-v1.0.0.apk
│   └── QuickRemote-v1.0.1.apk
├── changelogs/
│   ├── relay-server-CHANGELOG.md      # overwrite 保持 URL 不变
│   ├── pc-client-CHANGELOG.md
│   └── android-app-CHANGELOG.md
└── logs/
    ├── pc/{device_id}/
    └── android/{device_id}/
```

**manifest.json（版本清单，核心文件）：**

每次发布新版本时更新此文件（通过 `overwrite=true` 上传，保持分享 URL 不变）。所有客户端和 install.sh 通过此文件获取最新版本的下载 URL。

```json
{
  "relay-server": {
    "latest_version": "1.0.1",
    "versions": {
      "1.0.1": {
        "amd64": "https://quickdeploy.solutionx.top/d/xxx-relay-amd64-v101",
        "arm64": "https://quickdeploy.solutionx.top/d/xxx-relay-arm64-v101"
      },
      "1.0.0": {
        "amd64": "https://quickdeploy.solutionx.top/d/xxx-relay-amd64-v100",
        "arm64": "https://quickdeploy.solutionx.top/d/xxx-relay-arm64-v100"
      }
    }
  },
  "pc-client": {
    "latest_version": "1.0.1",
    "versions": {
      "1.0.1": { "url": "https://quickdeploy.solutionx.top/d/xxx-pc-v101" },
      "1.0.0": { "url": "https://quickdeploy.solutionx.top/d/xxx-pc-v100" }
    }
  },
  "android-app": {
    "latest_version": "1.0.1",
    "versions": {
      "1.0.1": { "url": "https://quickdeploy.solutionx.top/d/xxx-apk-v101" },
      "1.0.0": { "url": "https://quickdeploy.solutionx.top/d/xxx-apk-v100" }
    }
  },
  "changelogs": {
    "relay-server": "https://quickdeploy.solutionx.top/d/xxx-relay-changelog",
    "pc-client": "https://quickdeploy.solutionx.top/d/xxx-pc-changelog",
    "android-app": "https://quickdeploy.solutionx.top/d/xxx-apk-changelog"
  }
}
```

**固定 URL 文件（通过 `overwrite=true` + `permanent_share=true` 保持 URL 不变）：**

| 文件 | 说明 | 更新时机 |
|------|------|---------|
| `install.sh` | 部署脚本 | 脚本本身更新时 |
| `manifest.json` | 版本清单 | 每次发布新版本时 |
| `changelogs/*` | 更新日志 | 每次发布新版本时 |

### 9.2 install.sh 功能

install.sh 上传到 quickdeploy，使用 `overwrite=true` + `permanent_share=true` 保持分享 URL 不变。用户通过 curl 下载并执行（需 sudo 权限）：

```bash
curl -fsSL -o install.sh https://quickdeploy.solutionx.top/d/<install-share-url> && sudo bash install.sh
```

脚本内置 `MANIFEST_URL` 变量（manifest.json 的固定分享 URL），执行时：

1. 下载 `manifest.json` 获取最新版本号和下载 URL
2. 根据当前架构（arm64v8/amd64）下载对应的二进制文件
3. 安装、配置、注册 systemd 服务

脚本自动检测 root 权限，未使用 sudo 执行时提示并退出。

交互式无参数，执行后自动检测安装状态：

- **未安装时**：自动进入安装流程，交互式输入配置项（提供默认值，回车采用默认）
- **已安装时**：显示交互菜单，输入序号选择操作：
  - `1` 升级到最新版本（重新拉取 manifest.json 获取最新 URL）
  - `2` 修改配置（逐项提示，回车保持不变）
  - `3` 卸载（二次确认）

### 9.3 版本管理规则

- 发布新版本时上传二进制文件（`permanent_share=true`），然后更新 `manifest.json`（`overwrite=true`）
- quickdeploy 保留最近 3 个版本，更旧的版本文件通过 `delete_file` 删除，同时从 manifest.json 中移除
- 客户端启动时下载 `manifest.json` 检查版本 → 有新版本时提示更新
- PC 客户端：从 manifest 获取下载 URL → 下载安装包 → 静默安装 → 重启
- 安卓 App：从 manifest 获取下载 URL → 下载 APK → 触发安装意图
- 中转服务器关于页面：从 manifest 获取 changelog URL → 下载 CHANGELOG.md 展示

## 10. 安全措施

- 所有连接强制 TLS（服务器无证书时自动生成自签名）
- 预共享密钥使用 SHA-256 哈希传输，不明文传递
- 安卓 App 获取 JWT token，有效期 1 小时，自动续期
- 隧道连接需验证 session_id，防止未授权连接
- 服务器 API 限流（每 IP 每分钟 60 次请求）

## 11. UI 设计说明

Android App 和 PC 客户端的 UI 视觉设计将在实现阶段使用专业 UI 设计 skill 进行设计，然后基于设计稿进行开发。当前设计文档聚焦功能架构，UI 视觉设计作为独立步骤处理。
