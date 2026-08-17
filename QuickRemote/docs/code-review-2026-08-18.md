# 代码审查报告：v1.1.10/v1.0.15 → v1.1.11/v1.0.16

**审查范围**：`f59474d..279f3e7`，7 个提交，新增 3173 行
**功能**：向日葵式远程桌面（DXGI 截屏 + H.264 编解码 + 中继隧道 + 输入传输 + 内网发现）

---

## 一、阻断级问题（P0，必须修复才能运行）

### 1.1 [PC] RemoteSessionManager 串行调用导致视频流阻塞

**位置**：`pc-client/Services/RemoteSessionManager.cs:38-83, 128-147`

`StartAsync` 在 UI/调用线程同步执行捕获+编码初始化，`OnFrameReceived`（输入事件）也在捕获循环的同一线程上下文被触发（通过 transport 的读线程 → 事件回调 → SendInput），但捕获循环的 `Thread.Sleep` 与输入处理互不干扰——这点没问题。

**真正的问题**：`CaptureLoop` 里 `_encoder.EncodeFrame` 是同步阻塞调用，单帧 1080p H.264 编码耗时 10-50ms，加上 `_transport.Send` 同步写 socket，**15fps 的间隔 66ms 内可能挤不上**，导致实际帧率远低于 15fps，画面卡顿。

**建议**：
- 编码和发送拆到独立线程，用 `BlockingCollection<byte[]>` 做帧队列
- 或降低默认 fps 到 10，码率提升到 6Mbps 保证质量
- 捕获线程只负责抓帧入队，编码线程消费

### 1.2 [PC] H264Encoder MF vtable 布局风险（运行时崩）

**位置**：`pc-client/Services/MFInterop.cs`

手写 COM 接口 vtable 容错性极差。IMFAttributes 的 33 槽、IMFMediaType 的 +5、IMFSample 的 +14、IMFTransform 的 26 槽，**任何一个占位方法签名不对都会导致后续真实方法调用到错误的函数指针**，运行时直接 AccessViolation 崩溃。

**当前风险点**：
- `IMFAttributes.SetUInt` 实际是 `SetUINT32`，槽位 22——但占位方法 `q1/q2/q3` 用了 `[PreserveSig] int q1()` 这种无参签名，而 IUnknown 的 3 个方法（QueryInterface/AddRef/Release）是有参的，**vtable 槽位偏移可能错位**
- `IMFSample` 继承 `IMFMediaBuffer`，但 IMFMediaBuffer 只有 8 个槽，IMFSample 文档定义是继承 IMFAttributes（不是 IMFMediaBuffer）——**继承关系写错了**

**建议**（二选一）：
- **推荐**：放弃手写 COM，改用 `Vortice.MediaFoundation` 的 `MFMediaFactory.CreateSinkWriterFromURL` 写到内存流，或用 `IMFTransform` 但通过 Vortice 的封装调用
- **次选**：严格按 Microsoft 文档核对每个接口的继承链和槽位，写单元测试用一个简单 H.264 编码场景验证 vtable 正确性

### 1.3 [Android] H264Decoder 缺少 CSD（SPS/PPS）处理

**位置**：`android-app/.../H264Decoder.kt:21-33, 36-58`

MediaCodec H.264 解码器需要 SPS/PPS 作为 CSD-0/CSD-1 配置信息，通常在第一个 IDR 帧之前到达。当前 `start()` 只配置了分辨率，**没有处理 CSD**。PC 端 MF 编码器输出的第一个 sample 通常包含 SPS/PPS+IDR，但 MediaCodec 有时需要显式 `queueInputBuffer` 带 `BUFFER_FLAG_CODEC_CONFIG` 标志。

**建议**：
```kotlin
// 检测 NAL 类型，SPS(7)/PPS(8) 用 BUFFER_FLAG_CODEC_CONFIG
val nalType = nalData[0] and 0x1F  // 或 nalData[4] and 0x1F 取决于起始码
val flags = if (nalType == 7 || nalType == 8) MediaCodec.BUFFER_FLAG_CODEC_CONFIG else 0
dec.queueInputBuffer(inputIndex, 0, nalData.size, ptsUs, flags)
```

### 1.4 [Android] RemoteSessionManager 连接成功前 Surface 可能未就绪

**位置**：`android-app/.../RemoteSessionManager.kt:76-131, 161-175`

时序问题：
1. `start()` 先发 CONNECTING 状态
2. 后台线程认证 → 请求隧道 → 连接 → 发 CONNECTED
3. `handleControl` 收到 PC 的 CONTROL 帧时调 `decoder.start(surface, w, h)`
4. 但 `surface` 可能还是 null（SurfaceView 尚未回调 `surfaceCreated`）

当前 `setSurface` 有补偿逻辑（CONNECTED 时启动解码器），但 **CONTROL 帧到达时 surface 为 null 会静默丢弃分辨率信息**，后续 surface 就绪后用默认 1280x720 启动解码器，与实际视频流不匹配。

**建议**：
- `handleControl` 里即使 surface 为 null 也先保存 `videoWidth/videoHeight`
- `setSurface` 里用保存的 `videoWidth/videoHeight` 而非默认值（当前代码已这么做，但需确认 CONTROL 帧到达早于 surface 就绪时，videoWidth 已更新）
- 加日志：CONTROL 帧到达时记录 surface 是否就绪

---

## 二、重要问题（P1，影响稳定性/体验）

### 2.1 [PC] RelayRemoteTransport 读线程异常被静默吞掉

**位置**：`pc-client/Services/RelayRemoteTransport.cs:79-108`

`ReadLoop` 的 `catch` 块完全静默，连日志都没有。连接异常断开时无法定位原因。

**建议**：至少 `catch (Exception ex) { /* 日志 */ }`，区分 EOF（正常断开）和其他异常。

### 2.2 [双端] 帧协议缺少版本号/魔数

**位置**：`RemoteFrameProtocol.cs` / `RemoteFrameProtocol.kt`

帧头只有 `[类型][长度]`，没有协议版本。两端版本不一致时（如 PC 新版加了新帧类型）无法协商，Android 旧版会静默丢弃未知类型。

**建议**：握手阶段（CONTROL 帧）交换协议版本，不兼容时拒绝连接并提示升级。

### 2.3 [PC] RemoteInputHandler 鼠标坐标范围不一致

**位置**：`pc-client/Services/RemoteInputHandler.cs:84-122`

`MOUSEEVENTF_ABSOLUTE` 要求坐标范围 0-65535（映射到整个虚拟桌面），但 Android 端发送的是**远程桌面像素坐标**（如 1920x1080），直接当 dx/dy 传给 SendInput 会**定位到屏幕左上角小区域**。

**建议**：
```csharp
// 像素坐标 → 归一化坐标
var normX = (int)((ulong)pixelX * 65535 / videoWidth);
var normY = (int)((ulong)pixelY * 65535 / videoHeight);
```
需要 RemoteInputHandler 知道 `videoWidth/videoHeight`（CONTROL 帧解析后传入）。

### 2.4 [Android] RemoteDisplayView 双指滚轮会误触发

**位置**：`android-app/.../RemoteDisplayView.kt:110-127`

双指垂直移动判断阈值 `abs(dy) > 10` 太小，双指缩放或普通双指操作会误触发滚轮。且 `scaleDetector` 的 `onScale` 返回 true 但**没有实际处理缩放**，缩放手势被吞掉但无效果。

**建议**：
- 提高滚轮阈值到 30-50
- 明确区分双指模式：两指距离变化=缩放，两指同步垂直移动=滚轮
- 或暂时移除 scaleDetector（当前无实际缩放功能）

### 2.5 [PC] LanDiscoveryService 广播目标 IP 问题

**位置**：`pc-client/Services/LanDiscoveryService.cs:42-49`

`IPAddress.Broadcast`（255.255.255.255）在多网卡环境下可能只从默认网卡发出，且部分路由器/防火墙会丢弃全网广播。

**建议**：遍历所有网卡，对每个网卡的广播地址发送（`netInterface.GetIPProperties().GatewayAddresses` 获取网段后计算广播地址）。

### 2.6 [Android] LanDiscovery 用全局 object 单例不可靠

**位置**：`android-app/.../LanDiscovery.kt` + `DeviceListScreen.kt:74-82`

`LanDiscovery` 是 `object`（单例），`onDeviceFound` 回调在 `DisposableEffect` 里赋值。**如果用户在设备列表页和其他页面间切换**，`onDispose` 会调 `stop()` 清空 devices，再回来时 `start()` 重新监听，但已发现的设备丢失。且 `onDeviceFound` 是单一回调，多页面观察时会被覆盖。

**建议**：改用 `StateFlow<List<LanDevice>>` 暴露设备列表，多观察者安全；生命周期绑定 Application 而非页面。

---

## 三、改进建议（P2，代码质量/可维护性）

### 3.1 [PC] H264Encoder 每帧创建新 buffer/sample 浪费

**位置**：`pc-client/Services/H264Encoder.cs:103-143`

`EncodeFrame` 每次都 `MFCreateMemoryBuffer` + `MFCreateSample`，用完 ReleaseComObject。COM 对象创建开销大，15fps 下每秒 15 次。

**建议**：复用 buffer/sample，仅在尺寸变化时重建。

### 3.2 [双端] 大量 `catch (_ : Exception)` 吞异常

Android 端 `RemoteSessionManager.kt` 多处 `catch (_: Exception)`，PC 端 `RelayRemoteTransport.ReadLoop` 同样。问题排查时无任何线索。

**建议**：至少 `logger.warn(e.message)`，关键路径完整 `logger.error` + 堆栈。

### 3.3 [Android] RdpSessionScreen/RdpSessionViewModel 从 git 恢复后未清理冗余

**位置**：`android-app/.../RdpSessionScreen.kt`, `RdpSessionViewModel.kt`

从历史恢复的代码保留了 FreeRDP 时代的占位模式逻辑（.so 未加载时显示隧道信息），内网直连场景下这段逻辑意义不大。

**建议**：内网直连失败时直接提示"FreeRDP 库未加载，无法内网直连"，而非进入占位"已连接"状态。

### 3.4 [PC] RemoteSessionManager 构造函数参数不可配置

**位置**：`pc-client/Services/RemoteSessionManager.cs:31`

`fps=15, bitrateKbps=4000` 硬编码在构造函数默认值，`MainViewModel` 用默认值创建。用户无法调整画质/帧率。

**建议**：从 `AppSettings` 读取，设置页加"远程画质"选项（流畅 10fps/2M / 平衡 15fps/4M / 高清 20fps/6M）。

### 3.5 [双端] 帧协议未做 CRC/校验

帧载荷无校验，网络传输中字节错误会导致解码失败或输入错乱。

**建议**：视频帧可不校验（H.264 有容错），输入帧加 1 字节简单 XOR 校验或 CRC8。

### 3.6 [PC] ScreenCaptureService 不支持多显示器

`EnumOutputs(0)` 只取主显示器，多显示器环境只捕获主屏。

**建议**：后续支持显示器选择（设置页下拉），或拼接所有显示器画面。

---

## 四、架构层面观察

### 4.1 IRemoteTransport 抽象设计良好

预留 P2P 扩展的接口设计干净，`Send(type,data)` + 事件回调，未来加 P2PTransport 不影响上层。✅

### 4.2 帧协议设计简洁

`[类型1B][长度4B][载荷]` 足够简单，但缺少版本号（见 2.2）。建议握手帧携带版本。

### 4.3 内网/远程双模式路由清晰

NavGraph 按模式分流（内网=RdpSessionScreen，远程=RemoteSessionScreen），代码隔离好。✅

### 4.4 缺少端到端测试方案

3173 行新代码无任何测试，全部依赖真机实测。建议：
- PC 端加单元测试：`ScreenCaptureService.CaptureFrame` 能否出帧、`H264Encoder.EncodeFrame` 输出非空
- 集成测试：本地起一个 mock transport，PC 捕获→编码→发送到内存流→验证帧格式

---

## 五、修复优先级建议

| 优先级 | 问题 | 修复工作量 |
|--------|------|-----------|
| P0 | 1.2 MF vtable 布局风险 | 大（改用 Vortice 封装或严格核对） |
| P0 | 1.3 MediaCodec CSD 处理 | 小（加 BUFFER_FLAG_CODEC_CONFIG） |
| P0 | 1.4 Surface 时序竞争 | 小（加日志+确认补偿逻辑） |
| P0 | 1.1 编码阻塞捕获循环 | 中（拆线程+队列） |
| P1 | 2.3 鼠标坐标归一化 | 小（像素→65535 映射） |
| P1 | 2.1 读线程吞异常 | 小（加日志） |
| P1 | 2.4 滚轮误触发 | 小（调阈值） |
| P1 | 2.6 LanDiscovery 单例 | 中（改 StateFlow） |
| P2 | 其余 | 按需 |

---

## 六、总结

本次改动是一次**大型架构升级**（从 FreeRDP 转向截屏方案），3173 行新代码覆盖完整链路，架构设计（传输抽象、双模式路由）值得肯定。但**运行时正确性风险高**：MF COM vtable 手写易错、MediaCodec CSD 处理缺失、坐标归一化错误，这三个问题会直接导致"画面不显示"或"点击位置不对"。

**建议实测顺序**：
1. 先验证 PC 端能捕获+编码（日志看 `Screen capture started` / `H.264 encoder initialized` / `Video frame sent`）
2. 再验证 Android 端能解码（日志看 `H264 decoder started` / 有无 `decoder error`）
3. 最后验证输入（点击位置是否正确）

前两步通了，方案就成功了 80%。输入问题可以后续迭代。
