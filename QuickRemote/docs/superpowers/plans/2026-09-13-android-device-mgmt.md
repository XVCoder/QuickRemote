# QuickRemote 安卓端设备管理 实施计划

> 日期：2026-09-13
> 上游规格：`docs/superpowers/specs/2026-09-13-android-device-mgmt.md`
> 目标版本：relay v1.0.7 → **v1.0.8** / Android v1.0.76 → **v1.0.77**（versionCode 76 → 77）
> 工作分支：`ux-quick-wins`（见下方「偏差说明」）

本计划写给「热情但没上下文、讨厌写测试」的执行者。每个任务都有精确文件路径、可粘贴的代码、
以及"怎么知道做完了"的验证办法。**不要跳步，不要顺手重构计划外的东西。**

---

## 执行纪律（先读）

1. **不写测试不算实现**。Task 1.1 / 2.1 / 2.2 / 2.3 都是「先写测试 → 看它红 → 再写实现 → 看它绿」。
   顺序颠倒的话，测试就只是在给已有代码背书。
2. **两端语义必须一致**。备注语义、软删除语义、上线复活顺序，都以 PC 端
   `pc-client/ViewModels/MainViewModel.cs` 为唯一参照，不要自己发挥。
3. **只做三项功能**。不顺手加重命名、不加搜索排序、不动 UI 主题色、不重构无关文件。
4. **版本号铁律**：功能更新必须递增版本号，禁止覆盖历史发版包。
5. **注释写「为什么」，不写「做了什么」**。项目现有代码风格是中文注释 + 解释性说明。

### 偏差说明（偏离 superpowers 默认流程，已知情）

superpowers 第 2 步默认「新开 worktree 做隔离」。本项目**不适用**：

- `main` 分支停在 `5f5f24e release: pc-client v1.1.62`，落后实际线上 14 个版本；
- 近 5 个月的开发全部发生在扁平分支 `ux-quick-wins`（扁平命名是环境硬要求：
  嵌套 refs 子目录会被环境清理，导致 commit 假成功）；
- 既有发布技能 `.workbuddy/skills/quickremote-release/SKILL.md` 的编排假设就在这条线上。

因此本轮**继续在 `ux-quick-wins` 上工作**。隔离性由「不动 main」保证。

---

## 环境备忘（执行者必读，踩过的坑）

- 沙箱 bash 的 `PATH` 是坏的（`dirname: command not found`），**每条 bash 命令都要先修 PATH**：
  ```bash
  export PATH="/usr/bin:/bin:/mingw64/bin:/c/Windows/System32:/c/Windows:$PATH"
  ```
- 原生 Windows 程序（`gradlew.bat` / `go.exe` / `apksigner.bat`）路径一律用 **Windows 风格**
  `C:/…`、`E:/…`；MSYS 风格 `/c/…` 会**静默失败**。
- dotnet / gradle 相关坑见 `.workbuddy/memory/MEMORY.md`，本轮只涉及 gradle。

---

# Phase 0：基线与脚手架

### 任务 0.1：确认分支与「全绿基线」

- 命令：
  ```bash
  export PATH="/usr/bin:/bin:/mingw64/bin:/c/Windows/System32:/c/Windows:$PATH"
  cd /e/000_AI/QuickRemote/QuickRemote
  git status --short          # 应为空（或仅 .workbuddy 未跟踪）
  git rev-parse --abbrev-ref HEAD   # 应为 ux-quick-wins
  cd relay-server && go test ./...
  ```
- 验证：`go test ./...` 全部 `ok`，无 FAIL。若已红，**先停下来报告**，不要在红基线上叠改动。

### 任务 0.2：确认 Android 单测基线

- 命令：
  ```bash
  export PATH="/usr/bin:/bin:/mingw64/bin:/c/Windows/System32:/c/Windows:$PATH"
  cd /e/000_AI/QuickRemote/QuickRemote/android-app
  ./gradlew :app:testDebugUnitTest
  ```
- 验证：`BUILD SUCCESSFUL`，报告里 `SmokeTest` / `PairingPayloadTest` / `ReconnectPolicyTest` /
  `ClipboardChunkerTest` / `ModifierStateTest` 全部通过。
- 若报 dex 文件锁（`AccessDeniedException`）：先 `./gradlew --stop`，删
  `app/build/intermediates/project_dex_archive` 与 `app/build/intermediates/dex` 后重试。
  （此坑与版本号变更无关，别再靠重跑碰运气。）

---

# Phase 1：服务端放开全量（relay）

### 任务 1.1：先写会失败的 Go 测试

- 文件路径：`relay-server/internal/api/api_test.go`（在文件末尾追加）
- 要做的：

  ```go
  func TestGetDevices_All(t *testing.T) {
  	handler, reg, cleanup := setupTestAPI(t)
  	defer cleanup()

  	// 两台设备，其中一台标为离线
  	onlineID, _, err := reg.Register(&registry.Device{
  		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
  	})
  	if err != nil {
  		t.Fatalf("register m1: %v", err)
  	}
  	offlineID, _, err := reg.Register(&registry.Device{
  		MachineID: "m2", Hostname: "PC2", OS: "Win10", RDPPort: 3389, Version: "1.0.0",
  	})
  	if err != nil {
  		t.Fatalf("register m2: %v", err)
  	}
  	if err := reg.MarkOffline(offlineID); err != nil {
  		t.Fatalf("mark offline: %v", err)
  	}

  	token, _ := handler.authService.GenerateToken("test-device")

  	// 默认（不带参数）：只返回在线设备 —— 旧客户端行为回归保护
  	req := httptest.NewRequest("GET", "/api/devices", nil)
  	req.Header.Set("Authorization", "Bearer "+token)
  	w := httptest.NewRecorder()
  	handler.HandleGetDevices(w, req)
  	if w.Code != http.StatusOK {
  		t.Fatalf("default: expected 200, got %d", w.Code)
  	}
  	var only struct {
  		Devices []registry.Device `json:"devices"`
  	}
  	json.NewDecoder(w.Body).Decode(&only)
  	if len(only.Devices) != 1 || only.Devices[0].DeviceID != onlineID {
  		t.Fatalf("default: expected only online device %s, got %+v", onlineID, only.Devices)
  	}

  	// all=1：返回在线 + 离线，且在线在前
  	req = httptest.NewRequest("GET", "/api/devices?all=1", nil)
  	req.Header.Set("Authorization", "Bearer "+token)
  	w = httptest.NewRecorder()
  	handler.HandleGetDevices(w, req)
  	if w.Code != http.StatusOK {
  		t.Fatalf("all=1: expected 200, got %d", w.Code)
  	}
  	var all struct {
  		Devices []registry.Device `json:"devices"`
  	}
  	json.NewDecoder(w.Body).Decode(&all)
  	if len(all.Devices) != 2 {
  		t.Fatalf("all=1: expected 2 devices, got %d", len(all.Devices))
  	}
  	if all.Devices[0].DeviceID != onlineID || all.Devices[0].Status != "online" {
  		t.Errorf("all=1: expected online device first, got %s (%s)",
  			all.Devices[0].DeviceID, all.Devices[0].Status)
  	}
  	if all.Devices[1].DeviceID != offlineID || all.Devices[1].Status != "offline" {
  		t.Errorf("all=1: expected offline device second, got %s (%s)",
  			all.Devices[1].DeviceID, all.Devices[1].Status)
  	}
  }
  ```

- 验证：`cd relay-server && go test ./internal/api/ -run TestGetDevices_All -v`
  **必须 FAIL**，失败信息应形如 `all=1: expected 2 devices, got 1`。
  **如果这一步直接通过，说明你写错了测试，不是实现已经好了。**

### 任务 1.2：实现 `?all=1`

- 文件路径：`relay-server/internal/api/api.go`
- 要做的：把 `HandleGetDevices`（约 :107-128）替换为：

  ```go
  // HandleGetDevices 返回设备列表。
  //
  // 默认只返回在线设备（历史行为，旧客户端不带参数时行为不变）；
  // 带 ?all=1 时返回全部设备（含离线），供需要展示离线主机的新客户端使用。
  //
  // 为什么用可选参数而不是直接改默认行为：Android 旧版本不支持离线设备
  // （会显示成一批点不动的僵尸主机），必须保持零破坏升级。
  func (h *Handler) HandleGetDevices(w http.ResponseWriter, r *http.Request) {
  	if r.Method != "GET" {
  		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
  		return
  	}

  	listAll := r.URL.Query().Get("all") == "1"
  	var (
  		devices []registry.Device
  		err     error
  	)
  	if listAll {
  		devices, err = h.registry.ListAll()
  	} else {
  		devices, err = h.registry.ListOnline()
  	}
  	if err != nil {
  		http.Error(w, `{"error":"internal_error"}`, http.StatusInternalServerError)
  		return
  	}

  	if devices == nil {
  		devices = []registry.Device{}
  	}

  	w.Header().Set("Content-Type", "application/json")
  	json.NewEncoder(w).Encode(map[string]interface{}{
  		"devices": devices,
  	})
  }
  ```

- 验证：
  1. `go test ./internal/api/ -run TestGetDevices_All -v` → **PASS**
  2. `go test ./...` → 全绿（`TestGetDevices` / `TestGetDevices_Unauthorized` 未被破坏）

### 任务 1.3：relay 版本号递增

- 文件路径：`relay-server/cmd/server/main.go`（:23）
- 要做的：`var Version = "1.0.7"` → `var Version = "1.0.8"`
- 验证：`cd relay-server && go build ./...` 成功；`grep -n 'Version =' cmd/server/main.go` 显示 `1.0.8`

---

# Phase 2：Android 纯逻辑（强制 TDD）

### 任务 2.1：`Device` 补 `display_name` 与展示标题

- 文件路径：`android-app/app/src/test/java/com/quickremote/app/data/models/DeviceDisplayTitleTest.kt`（**新建**）
- 先写测试：

  ```kotlin
  package com.quickremote.app.data.models

  import org.junit.Assert.assertEquals
  import org.junit.Assert.assertFalse
  import org.junit.Assert.assertTrue
  import org.junit.Test

  /** 设备展示标题的回退链与在线判定（与 PC 端 RemoteDeviceInfo.DisplayName 同语义）。 */
  class DeviceDisplayTitleTest {

      @Test
      fun `优先使用服务端 display_name`() {
          val d = Device(device_id = "id1", display_name = "书房主机", hostname = "DESKTOP-ABC")
          assertEquals("书房主机", d.displayTitle)
      }

      @Test
      fun `display_name 为空时回退 hostname`() {
          val d = Device(device_id = "id1", display_name = "", hostname = "DESKTOP-ABC")
          assertEquals("DESKTOP-ABC", d.displayTitle)
      }

      @Test
      fun `两者都为空时回退 device_id`() {
          val d = Device(device_id = "id1", display_name = "", hostname = "")
          assertEquals("id1", d.displayTitle)
      }

      @Test
      fun `isOnline 跟随 status 字段`() {
          assertTrue(Device(device_id = "a", status = "online").isOnline)
          assertFalse(Device(device_id = "b", status = "offline").isOnline)
      }
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest --tests '*DeviceDisplayTitleTest*'` → **编译失败或 FAIL**
  （`display_name` / `displayTitle` / `isOnline` 还不存在）—— 这就是红。

- 再改文件路径：`android-app/app/src/main/java/com/quickremote/app/data/models/Models.kt`
- 要做的：把 `Device` 替换为：

  ```kotlin
  // 设备（字段与 relay-server registry.Device 对齐；含离线设备）
  @Serializable
  data class Device(
      val device_id: String = "",
      val machine_id: String = "",
      val hostname: String = "",
      /** 服务端分配或自定义的设备名；空 = 未分配（回退 hostname）。 */
      val display_name: String = "",
      val os: String = "",
      val lan_ip: String = "",
      val rdp_port: Int = 3389,
      val version: String = "",
      val status: String = "online",
      val last_seen: String = ""
  ) {
      /** 展示标题：display_name → hostname → device_id 逐级回退（与 PC 端 DisplayName 一致）。 */
      val displayTitle: String
          get() = display_name.ifBlank { hostname.ifBlank { device_id } }

      val isOnline: Boolean get() = status == "online"
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest --tests '*DeviceDisplayTitleTest*'` → **PASS**

### 任务 2.2：备注规范化与备注表编解码

- 文件路径：`android-app/app/src/test/java/com/quickremote/app/data/local/DeviceRemarkCodecTest.kt`（**新建**）
- 先写测试：

  ```kotlin
  package com.quickremote.app.data.local

  import org.junit.Assert.assertEquals
  import org.junit.Assert.assertTrue
  import org.junit.Test

  /** 备注规范化 + 备注表 JSON 编解码（纯逻辑，必须 TDD）。 */
  class DeviceRemarkCodecTest {

      @Test
      fun `规范化去除首尾空白`() {
          assertEquals("书房主机", normalizeRemark("  书房主机  "))
      }

      @Test
      fun `规范化截断到 64 字符`() {
          val long = "A".repeat(100)
          assertEquals(64, normalizeRemark(long).length)
      }

      @Test
      fun `纯空白规范化后为空串`() {
          assertEquals("", normalizeRemark("   "))
      }

      @Test
      fun `编解码往返一致`() {
          val map = mapOf("id1" to "书房主机", "id2" to "客厅小主机")
          assertEquals(map, decodeRemarks(encodeRemarks(map)))
      }

      @Test
      fun `空表编码为空串且解码回来是空表`() {
          assertEquals("", encodeRemarks(emptyMap()))
          assertTrue(decodeRemarks("").isEmpty())
      }

      @Test
      fun `脏数据不抛异常而是返回空表`() {
          assertTrue(decodeRemarks("{不是合法 json").isEmpty())
          assertTrue(decodeRemarks("[]").isEmpty())
      }
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest --tests '*DeviceRemarkCodecTest*'` → **编译失败**（红）

- 再新建：`android-app/app/src/main/java/com/quickremote/app/data/local/DeviceStateCodec.kt`

  ```kotlin
  package com.quickremote.app.data.local

  import kotlinx.serialization.json.Json

  /** 备注最大长度（与 PC 端 InputDialogWindow 的 64 字符上限一致）。 */
  const val MAX_REMARK_LENGTH = 64

  /** 规范化备注：去首尾空白 + 超长截断；结果为空串表示「删除该条备注」。 */
  fun normalizeRemark(raw: String): String = raw.trim().take(MAX_REMARK_LENGTH)

  private val remarksJson = Json { ignoreUnknownKeys = true }

  /** 备注表 → JSON 字符串（DataStore Preferences 无 Map 类型，故用单键存 JSON）。 */
  fun encodeRemarks(map: Map<String, String>): String =
      if (map.isEmpty()) "" else remarksJson.encodeToString(map)

  /** JSON 字符串 → 备注表；脏数据/空串一律返回空表，绝不让列表整体崩掉。 */
  fun decodeRemarks(raw: String): Map<String, String> {
      if (raw.isBlank()) return emptyMap()
      return try {
          remarksJson.decodeFromString<Map<String, String>>(raw)
      } catch (_: Exception) {
          emptyMap()
      }
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest --tests '*DeviceRemarkCodecTest*'` → **PASS**

### 任务 2.3：设备列表装配器

- 文件路径：`android-app/app/src/test/java/com/quickremote/app/data/models/DeviceListAssemblerTest.kt`（**新建**）
- 先写测试：

  ```kotlin
  package com.quickremote.app.data.models

  import org.junit.Assert.assertEquals
  import org.junit.Assert.assertTrue
  import org.junit.Test

  /** 设备列表装配：在线/离线分组、备注附加、隐藏过滤、上线复活。 */
  class DeviceListAssemblerTest {

      private fun dev(id: String, status: String, name: String = "") =
          Device(device_id = id, display_name = name, hostname = "host-$id", status = status)

      @Test
      fun `空输入产出空结果`() {
          val r = assembleDeviceList(emptyList())
          assertTrue(r.online.isEmpty())
          assertTrue(r.offline.isEmpty())
          assertTrue(r.revived.isEmpty())
      }

      @Test
      fun `按状态分成两段且保持输入顺序`() {
          val r = assembleDeviceList(
              listOf(dev("a", "online"), dev("b", "offline"), dev("c", "online"))
          )
          assertEquals(listOf("a", "c"), r.online.map { it.device.device_id })
          assertEquals(listOf("b"), r.offline.map { it.device.device_id })
      }

      @Test
      fun `备注按 device_id 附加 无备注为空串`() {
          val r = assembleDeviceList(
              listOf(dev("a", "online"), dev("b", "offline")),
              remarks = mapOf("b" to "客厅小主机")
          )
          assertEquals("", r.online[0].remark)
          assertEquals("客厅小主机", r.offline[0].remark)
      }

      @Test
      fun `隐藏的离线设备不展示`() {
          val r = assembleDeviceList(
              listOf(dev("a", "online"), dev("b", "offline")),
              hidden = setOf("b")
          )
          assertEquals(listOf("a"), r.online.map { it.device.device_id })
          assertTrue(r.offline.isEmpty())
          assertTrue(r.revived.isEmpty())
      }

      @Test
      fun `隐藏设备重新上线时复活并展示`() {
          val r = assembleDeviceList(
              listOf(dev("a", "online"), dev("b", "online")),
              hidden = setOf("b")
          )
          assertEquals(listOf("a", "b"), r.online.map { it.device.device_id })
          assertEquals(setOf("b"), r.revived)
      }

      @Test
      fun `device_id 为空的脏数据被丢弃`() {
          val r = assembleDeviceList(listOf(Device(device_id = ""), dev("a", "online")))
          assertEquals(listOf("a"), r.online.map { it.device.device_id })
          assertEquals(0, r.offline.size)
      }
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest --tests '*DeviceListAssemblerTest*'` → **编译失败**（红）

- 再新建：`android-app/app/src/main/java/com/quickremote/app/data/models/DeviceListAssembler.kt`

  ```kotlin
  package com.quickremote.app.data.models

  /** 设备列表中的一行：设备 + 本机备注（备注仅本机可见）。 */
  data class DeviceListItem(
      val device: Device,
      val remark: String = ""
  )

  /**
   * 装配结果：在线/离线两段，以及本次需要「解除隐藏」的设备 ID。
   *
   * [revived] 由调用方写回本地隐藏集合 —— 软删除的设备重新上线即自动恢复，这是
   * 与 PC 端一致的语义（pc-client MainViewModel.OnDeviceListUpdated）。
   */
  data class DeviceListResult(
      val online: List<DeviceListItem> = emptyList(),
      val offline: List<DeviceListItem> = emptyList(),
      val revived: Set<String> = emptySet()
  ) {
      val total: Int get() = online.size + offline.size
      val isEmpty: Boolean get() = total == 0
  }

  /**
   * 把服务端设备表（含离线）与本机私有状态（备注、隐藏集合）装配成展示列表。
   *
   * 规则（与 PC 端逐条一致，顺序不可调换）：
   * 1. device_id 为空的脏数据丢弃；
   * 2. 隐藏集合中的设备：在线 → 记为复活并照常展示；离线 → 跳过；
   * 3. 其余按 status 分入在线/离线两段，段内保持输入顺序（服务端已排序）。
   */
  fun assembleDeviceList(
      devices: List<Device>,
      remarks: Map<String, String> = emptyMap(),
      hidden: Set<String> = emptySet()
  ): DeviceListResult {
      val online = ArrayList<DeviceListItem>()
      val offline = ArrayList<DeviceListItem>()
      val revived = LinkedHashSet<String>()

      for (device in devices) {
          if (device.device_id.isBlank()) continue
          val isHidden = device.device_id in hidden
          if (device.isOnline) {
              if (isHidden) revived += device.device_id
          } else if (isHidden) {
              continue
          }
          val item = DeviceListItem(device, remarks[device.device_id].orEmpty())
          if (device.isOnline) online += item else offline += item
      }
      return DeviceListResult(online = online, offline = offline, revived = revived)
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest` → 本轮新增三个测试类全绿，且既有测试未被破坏

---

# Phase 3：本机持久化

### 任务 3.1：`SettingsStore` 新增备注与隐藏集合

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/data/local/SettingsStore.kt`
- 要做的（4 处改动）：

  **a. 补 import：**
  ```kotlin
  import androidx.datastore.preferences.core.stringSetPreferencesKey
  ```

  **b. 在 `SettingsKeys` 之后新增一个键对象：**
  ```kotlin
  private object DeviceKeys {
      /** 设备备注表（JSON：device_id → 备注文本）。仅本机可见，不上传服务端。 */
      val REMARKS = stringPreferencesKey("device_remarks")

      /** 软删除（本机隐藏）的设备 ID 集合；设备再次上线时移除。 */
      val HIDDEN = stringSetPreferencesKey("hidden_devices")
  }
  ```

  **c. 在 `manifestUrl` 之后新增两个 Flow：**
  ```kotlin
  /** 设备备注表（仅本机可见）。 */
  val deviceRemarks: Flow<Map<String, String>> = context.dataStore.data.map { prefs ->
      decodeRemarks(prefs[DeviceKeys.REMARKS].orEmpty())
  }

  /** 被本机软删除的设备 ID 集合。 */
  val hiddenDevices: Flow<Set<String>> = context.dataStore.data.map { prefs ->
      prefs[DeviceKeys.HIDDEN].orEmpty()
  }
  ```

  **d. 在 `saveManifestUrl` 之前新增三个写方法：**
  ```kotlin
  /** 设置/清除设备备注：规范化后为空则删除该条（「清空即删除备注」语义）。 */
  suspend fun setDeviceRemark(deviceId: String, remark: String) {
      if (deviceId.isBlank()) return
      context.dataStore.edit { prefs ->
          val map = decodeRemarks(prefs[DeviceKeys.REMARKS].orEmpty()).toMutableMap()
          val normalized = normalizeRemark(remark)
          if (normalized.isEmpty()) map.remove(deviceId) else map[deviceId] = normalized
          prefs[DeviceKeys.REMARKS] = encodeRemarks(map)
      }
  }

  /** 软删除：把设备加入本机隐藏集合（不动服务端记录，不影响其它客户端）。 */
  suspend fun hideDevices(deviceIds: Set<String>) {
      val ids = deviceIds.filter { it.isNotBlank() }.toSet()
      if (ids.isEmpty()) return
      context.dataStore.edit { prefs ->
          prefs[DeviceKeys.HIDDEN] = prefs[DeviceKeys.HIDDEN].orEmpty() + ids
      }
  }

  /** 解除隐藏（设备重新上线时调用）；集合清空则移除整个键，不留空集合。 */
  suspend fun restoreDevices(deviceIds: Set<String>) {
      val ids = deviceIds.filter { it.isNotBlank() }.toSet()
      if (ids.isEmpty()) return
      context.dataStore.edit { prefs ->
          val left = prefs[DeviceKeys.HIDDEN].orEmpty() - ids
          if (left.isEmpty()) prefs.remove(DeviceKeys.HIDDEN) else prefs[DeviceKeys.HIDDEN] = left
      }
  }
  ```

- 验证：`./gradlew :app:compileDebugKotlin` → `BUILD SUCCESSFUL`

> ⚠️ DataStore 的 `Preferences` 不接受写入**空集合**（语义不明），所以 `restoreDevices`
> 必须用 `remove` 收尾。这是本项目新引入的坑，别省这一步。

---

# Phase 4：网络与 ViewModel

### 任务 4.1：设备列表请求带上 `all=1`

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/data/api/RelayApi.kt`
- 要做的：把 `getDevices` 替换为：

  ```kotlin
  /**
   * 获取设备列表。
   *
   * [includeOffline] = true 时带 `?all=1`，服务端返回全部设备（含离线）；
   * 旧版服务端会忽略该参数、只返回在线设备 —— 属于设计内的优雅降级。
   */
  fun getDevices(
      serverAddress: String,
      token: String,
      includeOffline: Boolean = true
  ): DeviceListResponse {
      val base = baseUrl(serverAddress)
      val url = if (includeOffline) "$base/api/devices?all=1" else "$base/api/devices"
      val request = Request.Builder()
          .url(url)
          .header("Authorization", "Bearer $token")
          .get()
          .build()
      client.newCall(request).execute().use { resp ->
          val respBody = resp.body?.string().orEmpty()
          if (!resp.isSuccessful) {
              throw ApiException(resp.code, "获取设备列表失败: $respBody")
          }
          return json.decodeFromString(DeviceListResponse.serializer(), respBody)
      }
  }
  ```

- `RelayConnection.getDevices()` **无需改动**（走默认参数 `true`）。
- 验证：`./gradlew :app:compileDebugKotlin` → `BUILD SUCCESSFUL`

### 任务 4.2：`MainViewModel` 装配设备列表

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/viewmodels/MainViewModel.kt`
- 要做的（6 处改动）：

  **a. 补 import：**
  ```kotlin
  import com.quickremote.app.data.local.normalizeRemark
  import com.quickremote.app.data.models.DeviceListResult
  import com.quickremote.app.data.models.assembleDeviceList
  ```

  **b. 在 `_devices` 之后新增装配结果状态与两个本机私有缓存：**
  ```kotlin
  /** 装配后的展示列表（在线/离线两段 + 备注）。UI 只消费它。 */
  private val _deviceList = MutableStateFlow(DeviceListResult())
  val deviceList: StateFlow<DeviceListResult> = _deviceList.asStateFlow()

  /** 本机私有状态缓存：DataStore 的 Flow 到达时用它做装配。 */
  @Volatile private var remarksCache: Map<String, String> = emptyMap()
  @Volatile private var hiddenCache: Set<String> = emptySet()
  ```

  **c. 在 `init` 块末尾（`appSettings` 收集之后）新增两个收集：**
  ```kotlin
  viewModelScope.launch {
      settingsStore.deviceRemarks.collect {
          remarksCache = it
          rebuildDeviceList()
      }
  }
  viewModelScope.launch {
      settingsStore.hiddenDevices.collect {
          hiddenCache = it
          rebuildDeviceList()
      }
  }
  ```

  **d. 在 `loadDevices` 之后新增装配与两个用户操作：**
  ```kotlin
  /**
   * 用最新原始设备表 + 本机私有状态重算展示列表。
   *
   * 顺带处理「软删除设备重新上线自动恢复」：装配器把这类设备列进 revived，
   * 这里把它写回本地隐藏集合（移除）。
   */
  private fun rebuildDeviceList() {
      val result = assembleDeviceList(_devices.value, remarksCache, hiddenCache)
      _deviceList.value = result
      if (result.revived.isNotEmpty()) {
          viewModelScope.launch { settingsStore.restoreDevices(result.revived) }
      }
  }

  /** 设置/清除设备备注（仅本机可见，不影响设备自身名称）。 */
  fun setDeviceRemark(deviceId: String, remark: String) {
      viewModelScope.launch {
          settingsStore.setDeviceRemark(deviceId, remark)
          _toast.value = if (normalizeRemark(remark).isEmpty()) "备注已清除" else "备注已保存"
      }
  }

  /** 软删除离线设备：仅本机隐藏，设备再次上线时自动恢复显示。 */
  fun removeOfflineDevice(deviceId: String) {
      viewModelScope.launch {
          settingsStore.hideDevices(setOf(deviceId))
          _toast.value = "已从列表移除，设备上线后自动恢复"
      }
  }

  /** 由 UI 主动弹一条提示（如点击离线设备、长文本输入超限）。 */
  fun showToast(message: String) { _toast.value = message }
  ```

  **e. `loadDevices` 的 `_devices.value = ...` 三处（认证失败 / 成功 / 异常）之后各补一行：**
  ```kotlin
  rebuildDeviceList()
  ```

  **f. `loadDevices` 内成功分支的提示文案调整：**
  ```kotlin
  // 原：if (list.isEmpty()) _toast.value = "暂无在线设备"
  if (list.isEmpty()) _toast.value = "暂无设备"
  ```

- 验证：
  ```bash
  ./gradlew :app:compileDebugKotlin
  ./gradlew :app:testDebugUnitTest
  ```
  两条都 `BUILD SUCCESSFUL`

---

# Phase 5：UI

### 任务 5.1：把 `CompactIconButton` 抽成公共组件

> 背景：`RemoteSessionScreen.kt:1710` 已有一个 `private fun CompactIconButton`，为绕开
> Material3 `IconButton` 的 48dp 最小触控限制而自写。设备卡片也要用同一套尺寸，
> 抽出来避免两处各写一份。

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/components/CompactIconButton.kt`（**新建**）

  ```kotlin
  package com.quickremote.app.ui.components

  import androidx.compose.foundation.clickable
  import androidx.compose.foundation.layout.Box
  import androidx.compose.foundation.layout.size
  import androidx.compose.foundation.shape.CircleShape
  import androidx.compose.material3.Icon
  import androidx.compose.runtime.Composable
  import androidx.compose.ui.Alignment
  import androidx.compose.ui.Modifier
  import androidx.compose.ui.draw.clip
  import androidx.compose.ui.graphics.Color
  import androidx.compose.ui.graphics.vector.ImageVector
  import androidx.compose.ui.unit.Dp
  import androidx.compose.ui.unit.dp

  /**
   * 紧凑圆形图标按钮。
   *
   * 为什么不用 Material3 的 IconButton：它自带 48dp 最小触控尺寸，`Modifier.size()` 压不下去，
   * 在密集工具栏/列表行里会撑破布局。这里自写 Box + clickable 精确控制尺寸。
   */
  @Composable
  fun CompactIconButton(
      icon: ImageVector,
      contentDescription: String,
      tint: Color,
      size: Dp = 38.dp,
      iconSize: Dp = 22.dp,
      onClick: () -> Unit
  ) {
      Box(
          modifier = Modifier
              .size(size)
              .clip(CircleShape)
              .clickable(onClick = onClick),
          contentAlignment = Alignment.Center
      ) {
          Icon(
              icon,
              contentDescription = contentDescription,
              tint = tint,
              modifier = Modifier.size(iconSize)
          )
      }
  }
  ```

- 再改文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/RemoteSessionScreen.kt`
  - 删除 `:1708-1732` 的私有 `CompactIconButton`（含其上方注释行）
  - 新增 import：`import com.quickremote.app.ui.components.CompactIconButton`
  - 若删除后 `CircleShape` / `Alignment` 等 import 变为未使用，**保留不动**
    （它们在本文件其它地方仍有引用；编译器只警告不报错）

- 验证：`./gradlew :app:compileDebugKotlin` → `BUILD SUCCESSFUL`（证明 3 处调用点仍能解析）

### 任务 5.2：`DeviceCard` 支持备注与操作按钮

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/components/DeviceCard.kt`（整文件替换）
- 要做的：

  ```kotlin
  package com.quickremote.app.ui.components

  import androidx.compose.foundation.background
  import androidx.compose.foundation.border
  import androidx.compose.foundation.clickable
  import androidx.compose.foundation.layout.Box
  import androidx.compose.foundation.layout.Column
  import androidx.compose.foundation.layout.Row
  import androidx.compose.foundation.layout.Spacer
  import androidx.compose.foundation.layout.fillMaxWidth
  import androidx.compose.foundation.layout.height
  import androidx.compose.foundation.layout.padding
  import androidx.compose.foundation.layout.size
  import androidx.compose.foundation.layout.width
  import androidx.compose.foundation.shape.RoundedCornerShape
  import androidx.compose.material.icons.Icons
  import androidx.compose.material.icons.filled.Close
  import androidx.compose.material.icons.filled.Computer
  import androidx.compose.material.icons.filled.Edit
  import androidx.compose.material3.Icon
  import androidx.compose.material3.MaterialTheme
  import androidx.compose.material3.Text
  import androidx.compose.runtime.Composable
  import androidx.compose.ui.Alignment
  import androidx.compose.ui.Modifier
  import androidx.compose.ui.draw.clip
  import androidx.compose.ui.graphics.Color
  import androidx.compose.ui.text.font.FontWeight
  import androidx.compose.ui.text.style.TextOverflow
  import androidx.compose.ui.unit.dp
  import com.quickremote.app.data.models.Device
  import com.quickremote.app.data.models.DeviceListItem
  import com.quickremote.app.ui.theme.Accent
  import com.quickremote.app.ui.theme.BgCard
  import com.quickremote.app.ui.theme.Border
  import com.quickremote.app.ui.theme.Success
  import com.quickremote.app.ui.theme.TextMuted
  import com.quickremote.app.ui.theme.TextPrimary
  import com.quickremote.app.ui.theme.TextSecondary

  /**
   * 设备卡片：设备名 / 系统与版本 / 本机备注 / 连接类型 / 在线状态 / 最近心跳，
   * 右侧操作列为「设置备注」（常驻）与「移除」（仅离线设备）。
   */
  @Composable
  fun DeviceCard(
      item: DeviceListItem,
      onClick: (Device) -> Unit,
      onEditRemark: (Device) -> Unit,
      onRemove: (Device) -> Unit,
      modifier: Modifier = Modifier
  ) {
      val device = item.device
      val isOnline = device.isOnline
      val statusColor = if (isOnline) StatusColor.GREEN else StatusColor.YELLOW
      val isLan = isOnline && com.quickremote.app.services.LanUtils.isSameSubnet(device.lan_ip)

      Row(
          modifier = modifier
              .fillMaxWidth()
              .clip(RoundedCornerShape(8.dp))
              .background(BgCard)
              .border(1.dp, Border, RoundedCornerShape(8.dp))
              .clickable { onClick(device) }
              .padding(start = 14.dp, end = 6.dp, top = 12.dp, bottom = 12.dp),
          verticalAlignment = Alignment.CenterVertically
      ) {
          Box(
              modifier = Modifier
                  .size(32.dp)
                  .clip(RoundedCornerShape(6.dp))
                  .background(Color(0xFF1E3A5F)),
              contentAlignment = Alignment.Center
          ) {
              Icon(
                  imageVector = Icons.Filled.Computer,
                  contentDescription = null,
                  tint = TextPrimary,
                  modifier = Modifier.size(18.dp)
              )
          }

          Spacer(modifier = Modifier.width(12.dp))

          Column(modifier = Modifier.weight(1f)) {
              Row(verticalAlignment = Alignment.CenterVertically) {
                  Text(
                      text = device.displayTitle,
                      style = MaterialTheme.typography.titleSmall,
                      color = if (isOnline) TextPrimary else TextSecondary,
                      fontWeight = FontWeight.Medium,
                      maxLines = 1,
                      overflow = TextOverflow.Ellipsis
                  )
                  Spacer(modifier = Modifier.width(8.dp))
                  StatusIndicator(color = statusColor, size = 6.dp)
              }
              Spacer(modifier = Modifier.height(2.dp))
              Text(
                  text = buildString {
                      append(device.os.ifBlank { "Unknown OS" })
                      if (device.version.isNotBlank()) {
                          append(" · v")
                          append(device.version)
                      }
                  },
                  style = MaterialTheme.typography.bodySmall,
                  color = TextMuted,
                  maxLines = 1,
                  overflow = TextOverflow.Ellipsis
              )
              // 本机备注：仅非空时占一行（仅本机可见，不改设备名）
              if (item.remark.isNotBlank()) {
                  Spacer(modifier = Modifier.height(2.dp))
                  Text(
                      text = item.remark,
                      style = MaterialTheme.typography.bodySmall,
                      color = Accent,
                      maxLines = 1,
                      overflow = TextOverflow.Ellipsis
                  )
              }
          }

          Column(horizontalAlignment = Alignment.End) {
              Text(
                  text = when {
                      !isOnline -> "离线"
                      isLan -> "局域网"
                      else -> "公网"
                  },
                  style = MaterialTheme.typography.labelSmall,
                  color = TextMuted,
                  fontWeight = FontWeight.Medium
              )
              Spacer(modifier = Modifier.height(2.dp))
              Text(
                  text = if (isOnline) "在线" else "离线",
                  style = MaterialTheme.typography.labelMedium,
                  color = if (isOnline) Success else TextMuted,
                  fontWeight = FontWeight.Medium
              )
              Spacer(modifier = Modifier.height(2.dp))
              Text(
                  text = formatLastSeen(device.last_seen),
                  style = MaterialTheme.typography.bodySmall,
                  color = TextMuted
              )
          }

          // 操作列：备注常驻；移除只在离线设备上出现（在线设备不可移除）
          Column(horizontalAlignment = Alignment.CenterHorizontally) {
              CompactIconButton(
                  icon = Icons.Filled.Edit,
                  contentDescription = "设置备注",
                  tint = TextMuted,
                  size = 32.dp,
                  iconSize = 18.dp,
                  onClick = { onEditRemark(device) }
              )
              if (!isOnline) {
                  CompactIconButton(
                      icon = Icons.Filled.Close,
                      contentDescription = "移除设备",
                      tint = TextMuted,
                      size = 32.dp,
                      iconSize = 18.dp,
                      onClick = { onRemove(device) }
                  )
              }
          }
      }
  }

  /** 格式化最后心跳时间为简短显示。 */
  private fun formatLastSeen(lastSeen: String): String {
      if (lastSeen.isBlank()) return "--"
      return try {
          val t = lastSeen.indexOf('T')
          if (t > 0) lastSeen.substring(t + 1, minOf(t + 9, lastSeen.length)) else lastSeen.take(8)
      } catch (_: Exception) {
          lastSeen.take(8)
      }
  }
  ```

- 验证：`./gradlew :app:compileDebugKotlin` → 会因 `DeviceListScreen` 仍用旧签名而**报错**，
  这是预期的（任务 5.3 修好）；只要错误只出现在 `DeviceListScreen.kt` 即为正确。

### 任务 5.3：`DeviceListScreen` 分组 + 两个对话框

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/DeviceListScreen.kt`（整文件替换）
- 要做的：

  ```kotlin
  package com.quickremote.app.ui.screens

  import androidx.compose.foundation.background
  import androidx.compose.foundation.clickable
  import androidx.compose.foundation.layout.Arrangement
  import androidx.compose.foundation.layout.Box
  import androidx.compose.foundation.layout.Column
  import androidx.compose.foundation.layout.PaddingValues
  import androidx.compose.foundation.layout.Row
  import androidx.compose.foundation.layout.Spacer
  import androidx.compose.foundation.layout.WindowInsets
  import androidx.compose.foundation.layout.fillMaxSize
  import androidx.compose.foundation.layout.fillMaxWidth
  import androidx.compose.foundation.layout.height
  import androidx.compose.foundation.layout.padding
  import androidx.compose.foundation.layout.size
  import androidx.compose.foundation.layout.statusBars
  import androidx.compose.foundation.layout.windowInsetsPadding
  import androidx.compose.foundation.lazy.LazyColumn
  import androidx.compose.foundation.lazy.items
  import androidx.compose.foundation.rememberScrollState
  import androidx.compose.foundation.shape.RoundedCornerShape
  import androidx.compose.foundation.text.KeyboardOptions
  import androidx.compose.foundation.verticalScroll
  import androidx.compose.material.icons.Icons
  import androidx.compose.material.icons.filled.Refresh
  import androidx.compose.material.icons.filled.Settings
  import androidx.compose.material.pullrefresh.PullRefreshIndicator
  import androidx.compose.material.pullrefresh.pullRefresh
  import androidx.compose.material.pullrefresh.rememberPullRefreshState
  import androidx.compose.material3.AlertDialog
  import androidx.compose.material3.CircularProgressIndicator
  import androidx.compose.material3.Icon
  import androidx.compose.material3.IconButton
  import androidx.compose.material3.MaterialTheme
  import androidx.compose.material3.OutlinedTextField
  import androidx.compose.material3.Scaffold
  import androidx.compose.material3.Text
  import androidx.compose.material3.TextButton
  import androidx.compose.runtime.Composable
  import androidx.compose.runtime.LaunchedEffect
  import androidx.compose.runtime.collectAsState
  import androidx.compose.runtime.getValue
  import androidx.compose.runtime.mutableStateOf
  import androidx.compose.runtime.remember
  import androidx.compose.runtime.setValue
  import androidx.compose.ui.Alignment
  import androidx.compose.ui.Modifier
  import androidx.compose.ui.draw.clip
  import androidx.compose.ui.text.font.FontWeight
  import androidx.compose.ui.unit.dp
  import com.quickremote.app.data.local.MAX_REMARK_LENGTH
  import com.quickremote.app.data.models.Device
  import com.quickremote.app.data.models.DeviceListItem
  import com.quickremote.app.ui.components.DeviceCard
  import com.quickremote.app.ui.components.StatusColor
  import com.quickremote.app.ui.components.StatusIndicator
  import com.quickremote.app.ui.theme.Accent
  import com.quickremote.app.ui.theme.BgCard
  import com.quickremote.app.ui.theme.TextMuted
  import com.quickremote.app.ui.theme.TextPrimary
  import com.quickremote.app.ui.theme.TextSecondary
  import com.quickremote.app.viewmodels.ConnectionState
  import com.quickremote.app.viewmodels.MainViewModel

  /**
   * 设备列表页（主页）：服务器状态、在线/离线两段设备卡片、下拉刷新、设置入口。
   *
   * 设备列表来自服务端全量数据（含离线），本机备注与软删除在 ViewModel 层装配；
   * 本页只负责展示与两个对话框（设置备注 / 移除离线设备）。
   */
  @OptIn(androidx.compose.material.ExperimentalMaterialApi::class)
  @Composable
  fun DeviceListScreen(
      viewModel: MainViewModel,
      onDeviceClick: (Device) -> Unit,
      onSettingsClick: () -> Unit
  ) {
      val deviceList by viewModel.deviceList.collectAsState()
      val connectionState by viewModel.connectionState.collectAsState()
      val isRefreshing by viewModel.isRefreshing.collectAsState()
      val serverConfig by viewModel.serverConfig.collectAsState()

      // 对话框目标：非空即弹出对应对话框
      var remarkTarget by remember { mutableStateOf<Device?>(null) }
      var removeTarget by remember { mutableStateOf<Device?>(null) }

      val statusColor = when (connectionState) {
          ConnectionState.CONNECTED -> StatusColor.GREEN
          ConnectionState.CONNECTING -> StatusColor.YELLOW
          ConnectionState.ERROR -> StatusColor.RED
          ConnectionState.DISCONNECTED -> StatusColor.RED
      }
      val statusText = when (connectionState) {
          ConnectionState.CONNECTED -> "已连接"
          ConnectionState.CONNECTING -> "连接中"
          ConnectionState.ERROR -> "连接错误"
          ConnectionState.DISCONNECTED -> "未连接"
      }

      // 首次进入若已配置则自动拉取设备
      LaunchedEffect(serverConfig.address) {
          if (serverConfig.address.isNotBlank() &&
              deviceList.isEmpty &&
              connectionState != ConnectionState.CONNECTING
          ) {
              viewModel.refreshDevices()
          }
      }

      Scaffold(
          containerColor = MaterialTheme.colorScheme.background,
          topBar = {
              Row(
                  modifier = Modifier
                      .fillMaxWidth()
                      .background(MaterialTheme.colorScheme.surface)
                      .windowInsetsPadding(WindowInsets.statusBars)
                      .padding(horizontal = 16.dp, vertical = 12.dp),
                  verticalAlignment = Alignment.CenterVertically
              ) {
                  Column(modifier = Modifier.weight(1f)) {
                      Row(verticalAlignment = Alignment.CenterVertically) {
                          Text(
                              "QuickRemote",
                              style = MaterialTheme.typography.titleMedium,
                              color = Accent,
                              fontWeight = FontWeight.SemiBold
                          )
                          Text(
                              " · 设备",
                              style = MaterialTheme.typography.titleMedium,
                              color = TextSecondary
                          )
                      }
                      Spacer(modifier = Modifier.height(2.dp))
                      Row(verticalAlignment = Alignment.CenterVertically) {
                          StatusIndicator(color = statusColor, size = 7.dp)
                          Spacer(modifier = Modifier.size(6.dp))
                          Text(statusText, style = MaterialTheme.typography.bodySmall, color = TextSecondary)
                          if (serverConfig.address.isNotBlank()) {
                              Text(
                                  " · ${serverConfig.address}",
                                  style = MaterialTheme.typography.bodySmall,
                                  color = TextMuted
                              )
                          }
                      }
                  }
                  IconButton(onClick = { viewModel.refreshDevices() }, enabled = !isRefreshing) {
                      if (isRefreshing) {
                          CircularProgressIndicator(
                              modifier = Modifier.size(18.dp),
                              strokeWidth = 2.dp,
                              color = TextPrimary
                          )
                      } else {
                          Icon(Icons.Filled.Refresh, contentDescription = "刷新", tint = TextPrimary)
                      }
                  }
                  IconButton(onClick = onSettingsClick) {
                      Icon(Icons.Filled.Settings, contentDescription = "设置", tint = TextPrimary)
                  }
              }
          }
      ) { padding ->
          val pullRefreshState = rememberPullRefreshState(
              refreshing = isRefreshing,
              onRefresh = { viewModel.refreshDevices() }
          )
          Box(
              modifier = Modifier
                  .fillMaxSize()
                  .padding(padding)
                  .pullRefresh(pullRefreshState)
          ) {
              if (deviceList.isEmpty && !isRefreshing) {
                  EmptyState(modifier = Modifier.verticalScroll(rememberScrollState()))
              } else {
                  LazyColumn(
                      modifier = Modifier.fillMaxSize(),
                      contentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp),
                      verticalArrangement = Arrangement.spacedBy(8.dp)
                  ) {
                      if (deviceList.online.isNotEmpty()) {
                          item { SectionHeader("在线设备 (${deviceList.online.size})") }
                          items(deviceList.online, key = { it.device.device_id }) { listItem ->
                              DeviceCard(
                                  item = listItem,
                                  onClick = { device ->
                                      if (device.isOnline) {
                                          onDeviceClick(device)
                                      } else {
                                          viewModel.showToast("设备「${device.displayTitle}」当前离线，无法远程控制")
                                      }
                                  },
                                  onEditRemark = { remarkTarget = it },
                                  onRemove = { removeTarget = it }
                              )
                          }
                      }
                      if (deviceList.offline.isNotEmpty()) {
                          item { SectionHeader("离线设备 (${deviceList.offline.size})") }
                          items(deviceList.offline, key = { it.device.device_id }) { listItem ->
                              DeviceCard(
                                  item = listItem,
                                  onClick = {
                                      viewModel.showToast("设备「${listItem.device.displayTitle}」当前离线，无法远程控制")
                                  },
                                  onEditRemark = { remarkTarget = it },
                                  onRemove = { removeTarget = it }
                              )
                          }
                      }
                  }
              }
              PullRefreshIndicator(
                  refreshing = isRefreshing,
                  state = pullRefreshState,
                  modifier = Modifier.align(Alignment.TopCenter),
                  backgroundColor = BgCard,
                  contentColor = Accent,
                  scale = true
              )
          }
      }

      remarkTarget?.let { device ->
          RemarkDialog(
              device = device,
              currentRemark = deviceList.lineRemark(device.device_id),
              onDismiss = { remarkTarget = null },
              onConfirm = { text ->
                  viewModel.setDeviceRemark(device.device_id, text)
                  remarkTarget = null
              }
          )
      }

      removeTarget?.let { device ->
          RemoveDialog(
              device = device,
              onDismiss = { removeTarget = null },
              onConfirm = {
                  viewModel.removeOfflineDevice(device.device_id)
                  removeTarget = null
              }
          )
      }

      ToastHost(viewModel)
  }

  /** 从当前装配结果里取某设备的备注（对话框回填用）。 */
  private fun com.quickremote.app.data.models.DeviceListResult.lineRemark(deviceId: String): String =
      (online + offline).firstOrNull { it.device.device_id == deviceId }?.remark.orEmpty()

  @Composable
  private fun SectionHeader(text: String) {
      Text(
          text,
          style = MaterialTheme.typography.labelMedium,
          color = TextMuted,
          modifier = Modifier.padding(start = 2.dp, bottom = 4.dp)
      )
  }

  /** 设备备注编辑对话框：仅本机可见，最长 64 字符；清空即删除备注。 */
  @Composable
  private fun RemarkDialog(
      device: Device,
      currentRemark: String,
      onDismiss: () -> Unit,
      onConfirm: (String) -> Unit
  ) {
      var text by remember(device.device_id) { mutableStateOf(currentRemark) }

      AlertDialog(
          onDismissRequest = onDismiss,
          title = { Text("设备备注", color = TextPrimary) },
          text = {
              Column {
                  Text(
                      "为「${device.displayTitle}」设置备注（仅本机可见，最长 $MAX_REMARK_LENGTH 字符；清空则删除备注）",
                      style = MaterialTheme.typography.bodySmall,
                      color = TextSecondary
                  )
                  Spacer(modifier = Modifier.height(10.dp))
                  OutlinedTextField(
                      value = text,
                      onValueChange = { if (it.length <= MAX_REMARK_LENGTH) text = it },
                      singleLine = true,
                      modifier = Modifier.fillMaxWidth(),
                      placeholder = { Text("如：书房主机", color = TextMuted) }
                  )
                  Spacer(modifier = Modifier.height(4.dp))
                  Text(
                      "${text.length} / $MAX_REMARK_LENGTH",
                      style = MaterialTheme.typography.labelSmall,
                      color = TextMuted
                  )
              }
          },
          confirmButton = {
              TextButton(onClick = { onConfirm(text) }) { Text("保存", color = Accent) }
          },
          dismissButton = {
              TextButton(onClick = onDismiss) { Text("取消", color = TextMuted) }
          },
          containerColor = BgCard,
          titleContentColor = TextPrimary,
          textContentColor = TextPrimary
      )
  }

  /** 移除离线设备确认对话框：仅本机隐藏（软删除），上线自动恢复。 */
  @Composable
  private fun RemoveDialog(
      device: Device,
      onDismiss: () -> Unit,
      onConfirm: () -> Unit
  ) {
      AlertDialog(
          onDismissRequest = onDismiss,
          title = { Text("移除设备", color = TextPrimary) },
          text = {
              Text(
                  "确定将「${device.displayTitle}」从列表移除？\n\n仅在当前手机隐藏（软删除），不影响其它设备；" +
                      "该设备再次上线后将自动恢复显示。",
                  style = MaterialTheme.typography.bodyMedium,
                  color = TextSecondary
              )
          },
          confirmButton = {
              TextButton(onClick = onConfirm) { Text("移除", color = Accent) }
          },
          dismissButton = {
              TextButton(onClick = onDismiss) { Text("取消", color = TextMuted) }
          },
          containerColor = BgCard,
          titleContentColor = TextPrimary,
          textContentColor = TextPrimary
      )
  }

  @Composable
  private fun EmptyState(modifier: Modifier = Modifier) {
      Column(
          modifier = modifier.fillMaxSize().padding(32.dp),
          horizontalAlignment = Alignment.CenterHorizontally,
          verticalArrangement = Arrangement.Center
      ) {
          Box(
              modifier = Modifier
                  .size(64.dp)
                  .clip(RoundedCornerShape(16.dp))
                  .background(BgCard),
              contentAlignment = Alignment.Center
          ) {
              Text("🖥", color = TextMuted)
          }
          Spacer(modifier = Modifier.height(16.dp))
          Text("暂无设备", style = MaterialTheme.typography.titleSmall, color = TextPrimary)
          Spacer(modifier = Modifier.height(6.dp))
          Text(
              "请确认 PC 客户端已连接到中转服务器",
              style = MaterialTheme.typography.bodyMedium,
              color = TextMuted
          )
      }
  }

  @Composable
  private fun ToastHost(viewModel: MainViewModel) {
      val toast by viewModel.toast.collectAsState()
      LaunchedEffect(toast) {
          if (toast != null) viewModel.consumeToast()
      }
  }
  ```

- **注意：现有代码里没有 toast 渲染组件**，`ToastHost` 只是消费掉消息。任务 5.4 补上真正的渲染。

- 验证：`./gradlew :app:compileDebugKotlin` → 仍会报「未使用 import」类警告可忽略，
  但不能有 error。

### 任务 5.4：让 toast / 离线提示真正可见

> ⚠️ **执行前先确认**：`ToastHost` 现状只消费消息不渲染。先在真机跑一遍任务 5.3 的成果，
> 若「点击离线设备」与「备注已保存」确实没有任何可见反馈，则执行本任务。
> **如果反馈已经存在（例如项目另有全局 toast 实现），跳过本任务，并在计划里注明。**

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/DeviceListScreen.kt`
- 要做的：把 `ToastHost` 替换为：

  ```kotlin
  /** 轻量提示浮层：底部居中、自动消失（替代会被系统主题割裂的原生 Toast）。 */
  @Composable
  private fun ToastHost(viewModel: MainViewModel) {
      val toast by viewModel.toast.collectAsState()
      val hostView = androidx.compose.ui.platform.LocalView.current
      val context = androidx.compose.ui.platform.LocalContext.current

      LaunchedEffect(toast) {
          val message = toast ?: return@LaunchedEffect
          android.widget.Toast.makeText(context, message, android.widget.Toast.LENGTH_SHORT).show()
          viewModel.consumeToast()
      }
  }
  ```

  （`hostView` 变量如报未使用则删掉该行。）

- 验证：真机上点击离线设备出现提示文案；保存备注出现「备注已保存」。

---

# Phase 6：发版

### 任务 6.1：版本号三处递增 + changelog

- 文件路径 1：`android-app/app/build.gradle.kts`（:15-16）
  - `versionCode = 76` → `77`
  - `versionName = "1.0.76"` → `"1.0.77"`
- 文件路径 2：`android-app/app/src/main/assets/changelog.txt`（在标题块之后插入）

  ```
  v1.0.77
  - 新增「设备备注」：设备卡片右上角 ✎ 可为任意设备写一条本机备注（最长 64 字符，清空即删除），仅本机可见、不改变设备名、不上传服务器
  - 新增「离线设备」展示：设备列表分「在线设备 / 离线设备」两段，长期关机的主机不再凭空消失，显示其最后一次心跳时间
  - 新增「移除离线设备」：离线设备卡片右侧 ✕ 可将其从列表移除（仅本机隐藏，软删除），该设备再次上线后自动恢复显示
  - 点击离线设备会直接提示「当前离线，无法远程控制」，不再白跑一次连接
  - 设备标题改用服务器下发的设备名（未设置时回退主机名），与 PC 端显示一致
  - 配合中继服务器 v1.0.8（旧版中继仍可用，只是看不到离线设备）
  ```

- 文件路径 3：`CHANGELOG.md`（在 `# QuickRemote 更新记录` 之后、`## v1.0.76` 之前插入）

  ```markdown
  ## v1.0.77 (Android App) + v1.0.8 (中继服务器)

  - **新增「设备备注」**：设备卡片可为任意设备（在线/离线都可）写一条本机备注，最长 64 字符，清空即删除；备注仅本机可见，不改变设备自身名称、不上传服务器
  - **新增离线设备展示**：设备列表分「在线设备 (n)」「离线设备 (n)」两段，长期关机的主机不再凭空消失，并显示最后一次心跳时间；设备标题改用服务器下发的设备名（未设置时回退主机名），与 PC 端显示一致
  - **新增移除离线设备**：离线设备卡片右侧 ✕ 可将其从列表移除（仅本机隐藏的软删除，不动服务器记录、不影响其它客户端），该设备再次上线后自动恢复显示
  - 点击离线设备直接提示「当前离线，无法远程控制」，不再白跑一次隧道请求
  - 中继服务器 v1.0.8：`GET /api/devices` 新增可选参数 `?all=1` 返回全部设备（含离线），不带参数时行为与旧版完全一致，旧客户端升级零影响
  ```

- 验证：
  ```bash
  cd android-app
  # 三处版本号一致
  grep -n 'versionName' app/build.gradle.kts
  grep -n 'v1.0.77' app/src/main/assets/changelog.txt
  ```

### 任务 6.2：构建 + 人工验收

- 命令：
  ```bash
  export PATH="/usr/bin:/bin:/mingw64/bin:/c/Windows/System32:/c/Windows:$PATH"
  cd /e/000_AI/QuickRemote/QuickRemote/android-app
  ./gradlew --stop
  ./gradlew :app:testDebugUnitTest
  ./gradlew :app:assembleRelease
  ```
- 验收清单（**真机 + 真实中继，缺一不可**）：
  - [ ] `relay-server` 部署 v1.0.8 后，`curl -s -H "Authorization: Bearer <token>" 'https://<host>/api/devices?all=1'` 能返回离线设备
  - [ ] PC 客户端退出 → App 下拉刷新 → 设备落在「离线设备」段，心跳时间为真实退出时刻
  - [ ] PC 客户端重新登录 → 刷新后该设备回到「在线设备」段
  - [ ] 设置备注「书房主机」→ 卡片出现该行；杀进程重进仍在
  - [ ] 清空备注 → 该行消失
  - [ ] 输入 100 字符 → 只能保存 64 个字符
  - [ ] 离线设备 ✕ → 确认 → 消失；再刷新仍不出现
  - [ ] 该设备重新上线 → 自动回到在线段（隐藏被解除）
  - [ ] 设备备注改动后，PC 端看到的设备名**没有变化**
  - [ ] 点击离线设备 → 提示「当前离线，无法远程控制」

### 任务 6.3：发布（**需 X 明确说「发布吧」才执行**）

- 顺序：
  1. 先发 relay v1.0.8（应用 id `9b4af080-…`），走
     `curl -fsSL https://qd.solutionx.top/d/p/<uuid> | sudo bash`
  2. 再发 Android v1.0.77（应用 id `ef060395-…`）
  3. 重发 `pc-updater` 之外需同步的 manifest 项：Android 条目更新即可（PC 端本轮无改动，
     **不需要**重发 pc-updater）
- 验证：`curl -sI -L https://qd.solutionx.top/d/p/609d4fcb-7415-4d70-96d1-18f2201631b6` 返回 200，
  且 manifest 中 android-app 的 `latest_version` 为 `1.0.77`
- 版本链接一律用**完整 UUID**（截断短 ID 会返回「链接无效」）

---

## 收尾（superpowers 第 7 步）

全部任务完成后：

1. 跑一遍完整测试：`cd relay-server && go test ./...` + `cd android-app && ./gradlew :app:testDebugUnitTest`
2. 向 X 给出 4 个选项：合并到 main / 保留在 `ux-quick-wins` / 丢弃 / 继续在此基础上迭代
   （本项目历史惯例：继续保留在 `ux-quick-wins`，main 在批次收口时统一推进）
3. 提交信息格式参照既有风格：`feat(android): …` / `feat(relay): …` / `release: …`

---

# 执行记录（2026-09-13 完成）

全部 6 个 Phase 已执行完毕。实际结果与计划的三处偏差，逐条记录：

| # | 偏差 | 处理 |
|---|---|---|
| 1 | **任务 5.4 是必需而非可选**。核实后确认项目里没有任何全局 toast 渲染：`DeviceListScreen` / `ServerConfigScreen` / `SettingsScreen` 三处 `ToastHost` 都只调 `consumeToast()` 把消息丢掉，所以「认证失败 / 加载失败」等提示一直是静默的 | 按 `MainActivity` 处理配对提示的同一写法（`Toast.makeText`）补上渲染，仅改 `DeviceListScreen`；另两个屏幕的同类缺口**不在本轮范围**，留作后续 |
| 2 | `Json.encodeToString(map)` 的 reified 重载编译不过（解析成了需要 `SerializationStrategy` 参数的重载） | 在 `DeviceStateCodec.kt` 补 `import kotlinx.serialization.encodeToString` |
| 3 | 计划里写的 `rm -rf app/build/intermediates/project_dex_archive app/build/intermediates/dex` **会被批量删除护栏拦下**（529 个目标 > 50 阈值，命令静默不生效），导致已知的 `dexBuilderRelease` AccessDeniedException 依旧复现 | 改为**只删报错点名的那个 .dex 文件**（单文件不触发护栏），再单独重跑 `:app:assembleRelease` 即成功。下次直接用这招，不要试图整目录删 |

另外纠正一条环境认知：该 `AccessDeniedException` 发生时报错文件**并未真的被进程占用**（无 java 进程、文件可正常删除），更像是杀软/索引器对新写入 .dex 的瞬时占用 —— 定向删除 + 重跑是有效解法，不必怀疑工具链。

## 完成情况

| Phase | 结果 |
|---|---|
| 0 基线 | relay `go test ./...` 全绿；Android 基线 `BUILD SUCCESSFUL` |
| 1 relay | `TestGetDevices_All` 先红（`expected 2 devices, got 1`）→ 实现 `?all=1` → 三个 devices 测试全绿；`Version = "1.0.8"`；`go build` / `go vet` 干净 |
| 2 纯逻辑 TDD | 三个测试类先红（Unresolved reference）→ 实现后转绿：`DeviceDisplayTitleTest` 4 / `DeviceRemarkCodecTest` 6 / `DeviceListAssemblerTest` 6 |
| 3 持久化 | `SettingsStore` 新增 `device_remarks`（JSON 单键）与 `hidden_devices`（stringSet）；`restoreDevices` 用 `remove` 收尾空集合 |
| 4 网络/VM | `RelayApi.getDevices(includeOffline = true)` 走 `?all=1`；`MainViewModel` 新增 `deviceList` / `setDeviceRemark` / `removeOfflineDevice` / `showToast` / `rebuildDeviceList`（含复活回写） |
| 5 UI | `CompactIconButton` 抽为公共组件（`RemoteSessionScreen` 改引用）；`DeviceCard` 加备注行与 ✎/✕ 操作列；`DeviceListScreen` 两段分组 + 两个对话框 + 离线点击拦截 + toast 渲染 |
| 6 版本/构建 | `versionCode 77` / `versionName 1.0.77`；两份 changelog 同步；`assembleRelease` 产出 APK（12,018,727 字节）；APK 内 `versionCode='77' versionName='1.0.77'` 与源码一致 |

**单测总计 48 个用例、0 失败**（新增 16 个，既有 32 个无回归）。

## 未完成项（有意留白）

- `ServerConfigScreen` / `SettingsScreen` 的 toast 同样是「只消费不渲染」，本轮未动。
- `MainViewModel.devices`（原始表）在 UI 层已无消费者，保留作为调试/后续用途；若判为死代码可另行清理。
- 真机 + 真实中继的人工验收清单（计划 §任务 6.2）与发版（§任务 6.3）**尚未执行**，等 X 的「发布吧」。
