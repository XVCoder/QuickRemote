---
name: quickremote-input-diagnose
description: QuickRemote 远程输入（键盘/鼠标）失效的排查手册。含 PC→PC 与 Android→PC 两条链路的通路模型、按症状快速定位断点的方法、被控端日志判据，以及已知代码级隐患清单。Invoke when user reports 远程键盘没反应／鼠标不能用／按键错乱／组合键失效 等远程输入类问题。
agent_created: true
---

# QuickRemote 远程输入故障排查

> 适用：用户报「远程键盘不能用」「打字没反应」「只有部分键有效」「组合键失效」等输入类问题。
> 目标：**先用症状把断点定位到某一条通路，再去看代码**，不要一上来就泛泛查权限/驱动/网络。

---

## 第 0 步：先拿三句话，别先猜

1. **用的哪一端做主控？** PC 客户端（`RemoteViewerWindow`）还是 Android（`RemoteSessionScreen`）
2. **鼠标能用吗？** → 这是最强的分水岭，见下方判据表
3. **被控端处于什么状态？** 正常桌面 / 锁屏 / 管理员程序或全屏游戏

---

## 核心模型：键盘走两条独立通路（PC 主控端）

`pc-client/Views/RemoteViewerWindow.xaml.cs`：

| 通路 | 覆盖按键 | 主控端行为 | 被控端注入 | 帧类型 |
|---|---|---|---|---|
| **文本通路** | A-Z、D0-D9、**NumPad0-9**、Space、Oem* 标点（且无 Ctrl/Alt/Win） | 不发 VK，靠 WPF `TextInput` → `SendText` | `KEYEVENTF_UNICODE` 逐码元 | `TYPE_INPUT_TEXT`(0x08) |
| **虚拟键通路** | Ctrl/Alt/Win 组合、F1-F12、方向键、Tab/Enter/Esc/Backspace/Del/Home/End、NumPad 的 `+ - * /` | `PreviewKeyDown` → `SendKey(VK)` | `SendInput(wVk, wScan=0)` | `TYPE_INPUT_KEY`(0x03) |

鼠标（`TYPE_INPUT_MOUSE`/`00x04` 滚轮）与上面两条**共用同一条 transport**。

### 判据表（按症状直接定位）

| 症状 | 结论 |
|---|---|
| **鼠标也不能用** | 连接/transport 层，**不是输入问题**；查 relay/LAN 直连/会话是否建立 |
| 鼠标能用，**所有键都不能用** | 主控端窗口**没拿到键盘焦点**（`PreviewKeyDown` 挂 Window 上，非激活态收不到） |
| 鼠标能用，**只有小键盘/功能键能用，字母打不出** | **文本通路断**。最高频原因：**主控端本机输入法处于中文态，把字母吞成拼音组合** |
| 只有**管理员程序/全屏游戏**里不行 | UIPI 权限不对等（低完整性进程无法向高完整性窗口注入）→ 被控端客户端改「以管理员身份运行」 |
| 方向键变成小键盘数字、字母无法「按住」 | `RemoteInputHandler` 缺 `KEYEVENTF_EXTENDEDKEY` + `wScan=0`（见下方隐患清单） |
| 某键一直重复／组合键失效（偶发） | 网络抖动/重连导致 down/up 错配 → 重连一次；顺带看画面卡不卡 |

---

## 决定性的分叉测试（零成本，先做这个）

**在被控端开记事本**，让用户依次试并勾选：

1. **Ctrl+A**（全选）→ 走 VK 通路
2. **方向键 / Tab / Enter** → 走 VK 通路
3. **小键盘数字**（NumLock 开）→ 走**文本通路**
4. **字母 a-z** → 走**文本通路**

- 1、2、3 有效而 4 无效 ⇒ **文本通路被主控端 IME 截走**（3 是照妖镜：小键盘数字与字母在代码里走完全相同的分支，
  表现不同就只可能是 IME 在区别对待——中文输入法拦截主键盘字母与上排数字，但几乎不拦小键盘）
- 1、2、4 有效而 3 无效 ⇒ 反过来怀疑 `IsTextProducingKey` 的分支判定
- 全部都无效 ⇒ 回到「窗口焦点」或连接层

**两个 5 秒佐证**（中文输入法假设成立时两条都应命中）：
- 主键盘**上排数字 1-5** 也打不出（被 IME 拿去选候选词）
- 按 **CapsLock** 后能打出**大写字母**（中文输入法遇 CapsLock 会让路）

**零改代码的绕过**：主控端输入法切英文（`Win + 空格`），或开着 CapsLock。

---

## 日志判据（被控端）

路径：`%APPDATA%\QuickRemote\logs\quickremote-YYYY-MM-DD.log`（保留 7 天）

| 日志内容 | 含义 |
|---|---|
| `SendInput failed: returned 0, lastError=5` | 注入被拒（ERROR_ACCESS_DENIED）→ 权限/安全桌面/安全软件 |
| `Input frame received: type=0x03 len=3 data=[...]` | VK 帧到达。**注意：`OnFrameReceived` 只对 `TYPE_INPUT_KEY` 记日志，TEXT 帧不记** |

**关键技巧**：让用户按**字母**，看被控端日志**有没有新增任何 `Input frame received` 行**。
- 没有 → 主控端**根本没发**该帧（瓶颈在主控端捕获层：焦点 / IME）
- 有 `type=0x03` → 主控端把它当 VK 发了，问题在分流判定或注入
- 需要区分 TEXT 帧时：临时把 `RemoteSessionManager.OnFrameReceived`（约 1524-1550 行）的日志条件
  从 `type == TYPE_INPUT_KEY` 放宽到 `KEY || TEXT`，重编一次再复现

---

## 已知代码级隐患清单（改前先确认是否本次症状相关）

1. **文本键依赖主控端 IME 产生字符**（语义缺陷）
   `RemoteViewerWindow.Window_PreviewKeyDown` 里 `if (!forceRaw && IsTextProducingKey(key)) return;`，
   字符由主控端 `TextInput` 决定 ⇒ **主控端中文输入法会让字母/数字完全打不出去**。
   正规远程控制软件的语义应是「原样转发物理键，字符由被控端输入法产生」。
   修复方向：`InputMethod.SetIsInputMethodEnabled(this, false)` + `PreviewKeyDown` 处理 `Key.ImeProcessed`
   （用 `e.ImeProcessedKey` 还原真实键）；彻底做法是让文本键也走 VK 转发。

2. **`RemoteInputHandler.HandleKey` 缺 `KEYEVENTF_EXTENDEDKEY`(0x0001) 且 `wScan=0`**
   仅定义了 `KEYEVENTF_KEYUP`(0x0002) / `KEYEVENTF_UNICODE`(0x0004)。
   常规 Win32 程序只看 VK 码不受影响；但 DirectInput/RawInput 类程序会把方向键 / Home / End / PgUp / PgDn /
   Insert / Del / 右侧 Ctrl·Alt 按小键盘解释。

3. **文本键无「按住」语义**
   Unicode 注入是瞬时 down+up（`HandleText` 逐字符 down/up），且没有扫描码 ⇒
   远程玩游戏时「按住 W 前进」在原理上不可能生效。

4. **`ConnectAsync` 末尾用 `Focus()` 而非 `ActivateForInput()`**
   `Window.Focus()` 只设窗口内键盘焦点、**不会把窗口提到前台**；窗口非前台时是静默 no-op。
   正解是 `Activate() + Keyboard.Focus(this)`（即已有的 `ActivateForInput()`）。

5. **`InputDialogWindow.Show` 未设 `Owner`**（`new InputDialogWindow(...)` + `win.ShowDialog()`）
   模态弹窗关闭后的焦点归还路径不确定；`OnAuthRequired` 用 `Dispatcher.InvokeAsync` 派发时，
   `ShowDialog()` 的嵌套消息循环可能与 `ConnectAsync` 续体交错。仅在启用访问验证码时才会走到。

---

## 不要浪费时间去查的项

- **键盘驱动 / Windows 更新**：只要主控端本机在别的程序里打字正常，就与驱动无关，直接排除
- **网络延迟**：键盘帧是几字节的 TCP 小包，且与鼠标共用连接。鼠标流畅 ⇒ 网络基本无关
- **`Ctrl+Alt+Del`**：任何软件都注入不了（Windows 硬限制），不算故障
- **`Alt+Tab` / 部分 `Win` 组合**：被主控端系统抢先处理，WPF 拿不到，同样不算故障
