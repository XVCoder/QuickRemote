# 内网直连失败诊断报告

日期：2026-08-18 08:14
数据源：安卓端上传日志（quickremote-2026-08-18.log 部分）

## 一、结论速览

| 检查项 | 结果 | 证据 |
|--------|------|------|
| 内网发现（UDP 8446） | ✅ **正常** | `08:07:15.143 LanDevice discovered: DESKTOP-1OKSAB0 @ 192.168.0.2:3389` |
| TCP 直连 3389 | ✅ **成功** | `08:07:17.720 FreeRDP connection success` |
| FreeRDP 会话建立 | ✅ **成功** | `08:07:17.720 FreeRDP connected` |
| 连接保持 | ❌ **14 秒后被主动断开** | `08:07:31.383 RDP session disconnect` → `The connection was cancelled.` |

**核心结论：内网发现和直连链路本身是通的，问题出在「连接建立后 14 秒内被主动断开」，画面大概率没有正常渲染出来，用户看不到桌面，以为连接失败而退出。**

## 二、日志时间线还原（08:07 这次尝试）

```
08:07:14.420 LanDiscovery start, listening on port 8446        ← 内网发现启动
08:07:15.143 LanDevice discovered: DESKTOP-1OKSAB0 @ 192.168.0.2:3389  ← 发现 PC
08:07:16.397 RdpSurfaceView: surface created 1440x2403
08:07:17.332 RDP LAN direct: host=192.168.0.2 port=3389        ← 用户点击内网设备，开始直连
08:07:17.337 FreeRDP connecting
08:07:17.720 FreeRDP connection success                        ← TCP+TLS+RDP 协商通过
08:07:17.720 FreeRDP connected                                 ← 会话建立
08:07:31.383 RDP session disconnect: device=lan_192.168.0.2    ← 14 秒后主动断开
08:07:31.384 Disconnecting FreeRDP instance
08:07:31.390 ERROR FreeRDP connection failed: The connection was cancelled.
08:07:31.392 LanDiscovery start                                ← 回到设备列表页
08:07:32.126 RdpSurfaceView: surface destroyed
08:07:32.131 Releasing FreeRDP instance
```

## 三、关键证据解读

### 1. 内网发现功能正常（用户上一轮担心的点已排除）

日志明确显示发现流程完整跑通：
```
08:07:14.420 LanDiscovery start（手机开始监听 8446）
08:07:15.143 LanDevice discovered: DESKTOP-1OKSAB0 @ 192.168.0.2:3389（收到 PC 广播）
```
设备列表页的「内网直连」分区逻辑上已经能显示 PC（DESKTOP-1OKSAB0）。

### 2. 连接建立成功，不是连接不上

`FreeRDP connection success` + `FreeRDP connected` 说明：
- TCP 连上了 192.168.0.2:3389
- RDP 协议协商（含 TLS）通过
- FreeRDP 会话已建立

对比 01:46 / 01:48 的两次尝试（15 秒超时 `The connection failed`），08:07 这次是**显著进步**——说明 PC 端 RDP 服务状态恢复（PC 端 01:28 执行过「恢复控制台会话」），直连链路本身没问题。

### 3. 断开是「主动断开」，不是网络/服务端断连

断开日志序列：
```
RDP session disconnect: device=lan_192.168.0.2   ← 代码主动调用 disconnect()
Disconnecting FreeRDP instance
ERROR FreeRDP connection failed: The connection was cancelled.  ← FreeRDP 回调（因主动断开）
```
`RDP session disconnect` 是 `RdpSessionManager.disconnect()` 的第一条日志——这是**用户退出会话页触发的**（顶部断开按钮 / 返回），不是 FreeRDP 自身失败。

## 四、根因推断

连接成功但用户 14 秒内退出，最可能的场景：

**PC 端 RDP 处于「登录/锁屏界面」或「NLA 凭据等待」状态，FreeRDP 连接建立后画面没有推送到手机（黑屏），用户看不到桌面 → 以为失败 → 返回退出。**

具体嫌疑点（按可能性排序）：

1. **NLA 凭据缺失/错误**：Windows 10/11 默认启用 NLA。若 PC 处于锁屏或已注销状态，RDP 连接后停在登录界面，需要正确凭据。FreeRDP 的 `OnAuthenticate` 返回 `false`（FreeRdpClient.kt:336），且连接参数里 `-p` 仅在用户填了密码时才传——若用户没填或凭据不对，画面就不会正常出现。
2. **画面渲染链路无日志佐证**：日志中 `FreeRDP connected` 之后**没有任何 `OnGraphicsUpdate` 相关输出**（该回调当前不打日志），无法确认画面是否真的渲染。若渲染异常（surface 时序/宽高比），会黑屏。
3. **FreeRDP native DEBUG 日志未透传到应用日志**：`/log-level:DEBUG` 已加，但 native 日志走 logcat，未接入应用内 Logger——导致 TLS/NLA 协商的详细过程在用户上传的日志里完全不可见，无法进一步定位。

## 五、修复建议（按优先级）

### P0：让画面渲染链路可观测（下次必改）

在 `FreeRdpClient.kt` 的 `OnGraphicsUpdate` 和 `OnSettingsChanged` 中加入应用日志：
```kotlin
override fun OnGraphicsUpdate(inst: Long, x: Int, y: Int, width: Int, height: Int) {
    logger.info("Graphics update: ($x,$y) ${width}x${height}")
    // ...原有逻辑
}
override fun OnSettingsChanged(inst: Long, width: Int, height: Int, bpp: Int) {
    logger.info("Settings changed: ${width}x${height} bpp=$bpp")
}
```
这样连接后只要收到第一帧就能确认画面链路正常；收不到则说明停在登录/认证阶段。

### P1：FreeRDP native 日志接入应用 Logger

将 logcat 中 FreeRDP 的 DEBUG 输出转发到应用日志（上传时可见），或至少把 `freerdp_get_last_error_string` 的完整错误码（ERRCONNECT_*）展示给用户。当前 `The connection was cancelled.` 信息量不足。

### P1：内网直连时明确提示「凭据必填」

PC 端 RDP 若启用 NLA，内网直连也必须填 Windows 登录凭据。建议：
- 内网直连表单中把用户名/密码标为「必填」（当前 `enabled = username.isNotBlank() && password.isNotBlank()` 已隐含，但无明确提示文案）
- 连接成功后若 5 秒内未收到任何图形更新，UI 提示「正在等待 Windows 登录界面，请确认凭据正确」并附当前状态

### P2：连接后保活与超时提示

连接成功（CONNECTED）后启动一个 10 秒观察窗口，期间若 `OnGraphicsUpdate` 一次都没触发，向 UI 推送「连接已建立，但未收到画面（可能停在登录界面）」的中间态，避免用户误判为失败直接退出。

## 六、下一步

1. 按 P0 加渲染日志 → 打包 v1.0.19
2. 用户重测内网直连，把新日志发回
3. 若确认「无图形更新」→ 重点排查 NLA 凭据与登录界面场景（PC 端是否锁屏、凭据是否正确）
4. 若「有图形更新但黑屏」→ 排查 surface 渲染/宽高比

---

附：本次涉及的尝试对比

| 时间 | 结果 | 耗时 |
|------|------|------|
| 01:44 | 连接后 8 秒断开（无 ERROR） | 8s |
| 01:46 | `The connection failed.`（超时） | 15s |
| 01:48 | `The connection failed.`（超时） | 15s |
| 08:07 | ✅ 连接成功，14 秒后主动退出 | 14s |
