# QuickRemote 远程桌面方案设计（向日葵式）

## 背景

FreeRDP + RDP 方案因 TLS 握手间歇性失败（双层 TLS 冲突/CredSSP 兼容性）无法稳定运行。
改用"屏幕捕获 + H.264 编码 + 中继传输"方案，绕开 RDP 协议栈全部复杂性。
现有 RDP 方案保留，改造为仅内网直连使用。

## 整体架构

```
Android App                 中继服务器                  PC 客户端
    |                           |                           |
    |-- 控制连接(8444) ------->|<-------- 控制连接(8444) --|
    |   设备列表/会话请求        |          设备注册           |
    |                           |                           |
    |-- 媒体隧道(8445) ------->|<-------- 媒体隧道(8445) --|
    |   H.264视频帧 <-----------|------------ H.264视频帧 --|
    |   输入事件   ------------>|------------> 输入事件   --|
    |                           |                           |
    | MediaCodec硬解            |                        DXGI捕获
    | Surface渲染               |                        H.264硬编
    | 触摸捕获                  |                        SendInput
```

## 数据帧协议（复用 8445 隧道连接）

隧道为原始字节流，自定义帧协议：

```
帧头: [类型 1字节][长度 4字节(小端)]
载荷: [数据 N字节]

类型:
  0x01 VIDEO_FRAME    H.264 NAL unit（PC→Android）
  0x02 INPUT_MOUSE    鼠标事件（Android→PC）
  0x03 INPUT_KEY      键盘事件（Android→PC）
  0x04 INPUT_WHEEL    滚轮事件（Android→PC）
  0x05 CONTROL        控制帧（分辨率/帧率/握手）
  0x06 HEARTBEAT      心跳
```

### 输入事件载荷

```
INPUT_MOUSE (0x02):
  [flags 1字节][x 2字节][y 2字节]
  flags: bit0=左键 bit1=右键 bit2=中键 bit3=移动

INPUT_KEY (0x03):
  [vkCode 2字节][down 1字节]

INPUT_WHEEL (0x04):
  [delta 2字节][x 2字节][y 2字节]

CONTROL (0x05):
  [action 1字节][...payload]
  action: 0x01=握手 0x02=分辨率变更 0x03=帧率设置
```

## PC 端组件

### 1. ScreenCaptureService（DXGI Desktop Duplication）

- 使用 DXGI Output Duplication API 捕获桌面
- GPU 加速，捕获速度快（<5ms/帧）
- 输出 BGRA 纹理，转给编码器
- 支持 Win10+（Server 2016+）

### 2. H264Encoder（Media Foundation）

- 使用 MFCreateSinkWriterFromByteStream 或 IMFTransform
- 硬件加速编码（NVIDIA NVENC / AMD AMF / Intel QSV）
- 输出 H.264 NAL unit
- 目标码率：4-8 Mbps（1080p），可动态调整

### 3. RemoteInputHandler

- 接收 Android 发来的输入事件
- 用 SendInput API 模拟鼠标/键盘
- 支持绝对坐标映射（屏幕分辨率）

### 4. RemoteSessionManager（替代 TunnelManager 的 RDP 转发）

- 管理远程会话生命周期
- 屏幕捕获 → 编码 → 发送（视频流）
- 接收输入事件 → 模拟（输入流）
- 复用现有隧道连接（8445）

## Android 端组件

### 1. H264Decoder（MediaCodec）

- 硬件解码 H.264
- 输出到 Surface（零拷贝渲染）

### 2. RemoteDisplayView（SurfaceView）

- SurfaceView + Surface 渲染
- 触摸事件捕获（单指移动/点击/长按/双指滚轮）

### 3. RemoteSessionManager（替代 RdpSessionManager + FreeRDP）

- 请求隧道连接
- 接收视频帧 → 解码 → 渲染
- 捕获输入 → 发送

## 内网 RDP 方案（保留现有 FreeRDP）

### 自动检测内网 QuickRemote 主机

- PC 客户端启动时，在局域网广播 UDP（端口 8446）：`[QR_DISCOVER][device_id][hostname][port]`
- Android 端进入设备列表页时，监听 UDP 8446，发现内网主机
- 内网主机标记为"内网直连"，连接时 FreeRDP 直连 `内网IP:3389`（不经中继）

### 连接模式

- **远程模式**（跨网络）：向日葵式截屏方案（中继隧道）
- **内网模式**（同一局域网）：FreeRDP 直连 3389（低延迟，原生体验）

## P2P 接口预留

RemoteSessionManager 的传输层抽象为接口：

```csharp
interface IRemoteTransport {
    Task SendFrameAsync(byte type, byte[] data);
    event Action<byte, byte[]> OnFrameReceived;
}
```

- 当前实现：RelayTransport（中继隧道）
- 未来扩展：P2PTransport（WebRTC/UDP打洞）

## 实现阶段

1. **PC 端屏幕捕获 + H.264 编码**（ScreenCaptureService + H264Encoder）
2. **协议 + 会话管理**（帧协议 + RemoteSessionManager）
3. **Android 端解码 + 渲染**（H264Decoder + RemoteDisplayView）
4. **输入传输**（触摸捕获 + SendInput）
5. **内网 RDP 自动检测**（UDP 发现 + FreeRDP 直连）
