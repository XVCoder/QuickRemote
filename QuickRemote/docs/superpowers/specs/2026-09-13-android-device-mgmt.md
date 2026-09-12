# QuickRemote 安卓端设备管理 设计文档

> 日期：2026-09-13
> 状态：**待确认**（4 项决策已拍板，正文待 X 过目）
> 基线版本：relay v1.0.7 / Android v1.0.76（versionCode 76）
> 上游：2026-09-13 头脑风暴（决策见 §11）
> 下游：`docs/superpowers/plans/2026-09-13-android-device-mgmt.md`

## 1. 背景与目标

Android 端设备列表目前有三个缺口：**只能看到在线设备**、**标题只能用服务器下发的 hostname**、
**长期离线的僵尸主机无法清理**。而 PC 端「远程设备」列表早已具备这三项能力（本机备注 /
含离线全量 / 软删除），Android 端应当补齐到同一语义，而不是另发明一套。

本轮只做 3 件事，不动协议主干、不加 Android 依赖：

| 编号 | 功能 | 类型 |
|---|---|---|
| F1 | 设备本机备注（可增、可改、清空即删） | 新增 |
| F2 | 展示离线设备（含标题改用服务端 display_name） | 新增 |
| F3 | 软删除离线设备（设备上线自动恢复） | 新增 |

**明确不在本轮范围**：设备重命名（写服务端 `display_name`）、服务端真删设备记录、
备注多端同步/共享、备注搜索与排序、离线告警推送、WOL 唤醒。
（重命名与真删已评估并主动排除，理由见 §11 决策 2 / 决策 4。）

---

## 2. 现状勘查

| 能力 | PC 端实现 | Android 端现状 | 差距 |
|---|---|---|---|
| 设备备注 | `AppConfig.DeviceRemarks`：`device_id → 文本`，本机私有、上限 64 字符、trim、清空即删（`MainViewModel.cs:735`） | 无 | 需本地存储 + 展示 + 编辑入口 |
| 离线设备数据 | 控制连接 `device_list` 广播，服务端走 `registry.ListAll()`（含离线） | `GET /api/devices` → `HandleGetDevices` 写死 `ListOnline()` | 服务端 HTTP 层需放开全量 |
| 设备标题 | `RemoteDeviceInfo.DisplayName`：`display_name` 空则回退 `hostname` | 只用 `hostname`；`display_name` 因 `Json{ignoreUnknownKeys}` 被静默丢弃 | 需补模型字段 |
| 删除离线设备 | `AppConfig.HiddenDevices` 软删除，设备上线自动移出并恢复显示（`MainViewModel.cs:755`） | 无 | 需本地存储 + 交互 + 复活逻辑 |
| 点击离线设备 | 弹提示「设备当前离线，无法远程控制」（`MainViewModel.cs:698`） | 会直接发起隧道请求 → 服务端 503 `device_offline` → 报错文案含糊 | 需前置拦截 |

**关键结论**：服务端 `registry` 的 `display_name`、`ListAll()`、`Rename()` **都已就绪**，
唯一缺口是 HTTP 层 `HandleGetDevices` 只调 `ListOnline()`。而 Android 走的正是这条 HTTP 通道
（PC 走控制 TCP 的 `device_list` 广播，因此 PC 完全不受影响）。

---

## 3. F1 设备本机备注

### 语义（与 PC 端逐条对齐）

- **仅本机可见**：存本地 DataStore，不上传服务端、不同步其它设备。
- **不改变设备名**：备注与设备自身名称（服务端 `display_name`）是两回事，互不覆盖。
- **上限 64 字符**：trim 后超过 64 截断（PC 端 `InputDialogWindow.Show(..., 64)` 同款上限）。
- **清空即删除**：提交空串/纯空白 → 删除该条备注，而不是存一个空字符串。

### 存储

DataStore `Preferences` 无 Map 类型，因此备注表以 **JSON 字符串**存进单个键；
隐藏集合用 DataStore 原生的 `stringSetPreferencesKey`（见 §6）。

| 键 | 类型 | 内容 |
|---|---|---|
| `device_remarks` | String | `{"<device_id>":"<备注>"}`；空表存空串 |

### 交互

- 设备卡片右侧新增 ✎ 按钮（在线/离线设备都有）。
- 点击弹出 Compose `AlertDialog`（沿用 `SettingsScreen` 既有深色 `AlertDialog` 风格：
  `containerColor = BgCard`），内含单行 `OutlinedTextField`，标题写明
  「为「<设备名>」设置备注（仅本机可见，最长 64 字符；清空则删除备注）」。
- 确认后即时刷新列表，并弹 toast「备注已保存」/「备注已清除」。
- 卡片上备注显示在标题下方一行（第三条信息行），仅在非空时占位。

### 验收

- 给设备 A 设备注「书房主机」→ 卡片上出现该行；重进 App 仍在（持久化）。
- 把备注清空 → 该行消失，且 DataStore 中该 `device_id` 条目被删除（非空串残留）。
- 输入 100 个字符 → 保存后只剩 64 个字符。
- 备注改动**不影响** PC 端看到的设备名。

---

## 4. F2 展示离线设备

### 服务端：`/api/devices?all=1`

`HandleGetDevices` 增加**可选**查询参数：

| 请求 | 行为 |
|---|---|
| `GET /api/devices` | 只返回在线（**行为不变**，旧客户端不受影响） |
| `GET /api/devices?all=1` | 返回全部设备（在线在前、名称序，即 `registry.ListAll()`） |

**为什么用可选参数而不是直接改默认行为**：Android 旧版本（≤ v1.0.76）不带参数，
必须保持「只看到在线设备」的既有行为，否则旧版本会显示出一批点不动的离线设备。
用 opt-in 参数实现零破坏升级。

### Android：数据源与标题

- `RelayApi.getDevices()` 请求路径改为 `/api/devices?all=1`。
- `Device` 模型补 `display_name` 字段（服务端本就返回，之前被 `ignoreUnknownKeys` 丢弃）。
- 卡片标题改用 `display_name.ifBlank { hostname.ifBlank { device_id } }`，与 PC 端
  `RemoteDeviceInfo.DisplayName` 的回退链完全一致。
- 列表分两段：`在线设备 (n)` / `离线设备 (n)`；离线卡片整体降饱和（状态点与文案用
  `TextMuted`，与 PC 端离线灰点一致）。
- 点击离线设备 → 前置拦截，toast「设备「X」当前离线，无法远程控制」，不再白跑一次隧道请求。

### 验收

- PC 客户端退出 → Android 下拉刷新后该设备出现在「离线设备」段，`last_seen` 显示真实心跳时刻。
- PC 客户端重新登录 → 无需手动操作，设备在下次刷新后回到「在线设备」段。
- 点击离线设备得到明确提示，日志中不出现 `device_offline` 的 503。
- 旧 relay + 新 App：离线段为空、其余功能正常（优雅降级，不报错、不崩）。

---

## 5. F3 软删除离线设备

### 语义（与 PC 端一致）

- **仅离线设备可删**：在线设备的 ✕ 按钮不可见（PC 端点在线设备删会弹提示拒绝，Android
  直接从 UI 上隐藏，不留死路）。
- **软删除 = 本机隐藏**：只在本机不显示，不动服务端设备记录，不影响其它客户端。
- **上线自动恢复**：被隐藏的设备再次出现在服务端列表且状态为 online → 自动移出隐藏集合、
  重新显示，并 toast 提示已恢复。
- 删除前需二次确认，文案写明「再次上线后将自动恢复显示」。

### 数据表

| 键 | 类型 | 内容 |
|---|---|---|
| `hidden_devices` | String Set | 被本机隐藏的 `device_id` 集合 |

### 验收

- 对离线设备 A 点 ✕ → 确认后从列表消失；再次下拉刷新仍不出现。
- 设备 A 重新上线 → 自动回到「在线设备」段，且 DataStore 中隐藏集合已移除该 ID。
- 隐藏集合清空后，DataStore 中该键被移除（不残留空集合）。
- 删除操作**不**影响 PC 端与其它 Android 设备的可见性。

---

## 6. 装配规则（唯一算法）

设备列表的装配是**纯函数**，与 UI / 网络完全解耦，必须走 TDD：

```
输入：设备表（含离线） × 本机备注表 × 本机隐藏集合
输出：{ 在线段, 离线段, 本次需解除隐藏的 ID 集合 }
```

规则，按顺序执行：

1. `device_id` 为空的条目直接丢弃（防御脏数据）。
2. 若设备在隐藏集合中：
   - **在线** → 记为「复活」，加入需解除隐藏集合，**照常显示**在在线段；
   - **离线** → 跳过，不显示。
3. 其余设备按 `status == "online"` 分入在线段/离线段。
4. 备注按 `device_id` 附加，无备注为空串。
5. 段内保持输入顺序（服务端 `ListAll()` 已按「在线优先 + 名称序」排好）。

> 复活判定必须发生在「隐藏过滤」之前，且只对在线设备成立 —— PC 端
> `MainViewModel.cs:657-664` 是同一顺序，两端语义必须一致。

---

## 7. 兼容性与降级

| 组合 | 行为 |
|---|---|
| 旧 relay（无 `all` 参数支持）+ 新 App | 参数被忽略 → 只返回在线设备 → 离线段为空，其余功能正常 |
| 新 relay + 旧 App（≤ v1.0.76） | 旧 App 不带参数 → 行为与升级前完全一致 |
| 任意 relay + 旧 App + 新 PC | 互不影响（PC 走控制 TCP，与本次 HTTP 改动无关） |

**结论：本次改动全线向后兼容，不强制两端同时升级。**

---

## 8. UI 规格

- 沿用现有深色主题（`Accent` / `BgCard` / `Border` / `TextPrimary` / `TextSecondary` / `TextMuted`），
  **不引入新色值**，不使用系统原生对话框。
- 卡片信息层次（左侧三行）：
  1. 设备名（`display_name`，回退 `hostname`）+ 状态点
  2. `OS · v版本`
  3. 备注（仅非空时显示，`Accent` 色，单行省略）
- 卡片右侧：原有的「局域网/公网」标签 + 在线状态 + `last_seen`，再靠右是操作列
  （✎ 常驻；✕ 仅离线设备显示），图标按钮用 28dp 紧凑尺寸。
- 列表分段标题：`在线设备 (n)` / `离线设备 (n)`，样式沿用现有 `labelMedium` + `TextMuted`。
- 空态（两段皆空）：沿用现有 EmptyState，文案由「暂无在线设备」改为「暂无设备」。

---

## 9. 测试策略

项目已有 JVM 单测基建（`android-app/app/src/test`，含 `SmokeTest` / `PairingPayloadTest` /
`ReconnectPolicyTest` / `ClipboardChunkerTest` / `ModifierStateTest`），本轮纯逻辑强制 TDD：

**Android（JVM 单测，先红后绿）**

| 测试 | 覆盖 |
|---|---|
| `DeviceDisplayTitleTest` | 标题回退链 `display_name → hostname → device_id`；`isOnline` 判定 |
| `DeviceRemarkCodecTest` | `normalizeRemark` 的 trim / 64 截断 / 空判定；备注表 JSON 编解码往返；脏数据与空串容错 |
| `DeviceListAssemblerTest` | 在线/离线分组；备注附加；隐藏过滤；**上线复活**；空 `device_id` 丢弃；空输入 |

**Go（`go test ./...`）**

| 测试 | 覆盖 |
|---|---|
| `TestGetDevices_All` | 默认只返回在线（回归保护）；`?all=1` 返回在线 + 离线，且在线在前 |

**人工验收（真机 + 真实中继）**：§3/§4/§5 各自的验收清单；Compose UI 与网络链路
不做自动化。

> 遵循项目铁律：**不发版就不算完成**。

---

## 10. 发版规划

| 端 | 基线 | 目标 | 说明 |
|---|---|---|---|
| relay | v1.0.7 | **v1.0.8** | `HandleGetDevices` 支持 `?all=1` |
| Android | v1.0.76 | **v1.0.77** | F1 + F2 + F3 全部（versionCode 76 → 77） |

发版前置动作（每次必做）：

- [ ] Android `assets/changelog.txt` 新增 `v1.0.77` 段（与主 `CHANGELOG.md` 独立维护）
- [ ] 根 `CHANGELOG.md` 新增 `## v1.0.77 (Android App) + v1.0.8 (中继服务器)`（最新在前）
- [ ] 三处版本号一致：`build.gradle.kts versionName` = manifest = APK 文件名
- [ ] `versionCode` 76 → 77、`main.go Version` 1.0.7 → 1.0.8
- [ ] relay 升级走 `curl -fsSL https://qd.solutionx.top/d/p/<uuid> | sudo bash`

**Android 端不需要 PC 端配合升级**（PC 走控制 TCP，本次只改 HTTP 设备列表接口）。

---

## 11. 决策记录（已拍板）

| # | 议题 | 决策 | 日期 |
|---|---|---|---|
| 1 | 离线设备数据来源 | **服务端放开全量**：`/api/devices?all=1` 走 `ListAll()`，与 PC 端数据源一致；接受 relay 发一版 | 2026-09-13 |
| 2 | 备注作用域 | **本机私有备注**，与 PC 端完全同语义；不走服务端 `display_name` | 2026-09-13 |
| 3 | 列表形态 | **在线/离线两段分组**（PC 端是单列混排，Android 屏小易累积，分段更清爽） | 2026-09-13 |
| 4 | 是否加设备重命名 | **不加**，本轮范围收敛为三项；但顺手把标题从 `hostname` 改为服务端 `display_name`（回退 `hostname`），与 PC 对齐 | 2026-09-13 |

---

## 附：确认后进入下一步

本文档确认后，按 superpowers 流程进入 **写实施计划**：
把 F1/F2/F3 拆成 2-5 分钟粒度任务，每个任务给出精确文件路径、完整代码与验证步骤，
存放于 `docs/superpowers/plans/2026-09-13-android-device-mgmt.md`。
