# QuickRemote 体验速赢批次 实施计划

> 日期：2026-09-11
> 上游规格：`docs/superpowers/specs/2026-09-11-ux-quick-wins.md`（已确认）
> 目标版本：PC v1.1.62 → **v1.1.63** / Android v1.0.69 → **v1.0.70**（versionCode 69 → 70）
> 工作分支：`feat/ux-quick-wins`

本计划写给「热情但没上下文、讨厌写测试」的执行者。每个任务都有精确文件路径、
可粘贴的代码、以及"怎么知道做完了"的验证办法。**不要跳步，不要顺手重构计划外的东西。**

---

## 执行纪律（先读）

1. **TDD 只对纯逻辑强制**（编解码、分片、退避）。UI 与网络路径走人工验收 —— 项目当前零测试基建，Phase 0 先把脚手架补上。
2. **会话级资源必须每次重建**。历史提交 `b837eb1` 已踩过：复用被 `CompleteAdding()`
   关闭的队列导致第二次连接秒断，日志特征 `session started` 紧接 `Encode loop ended`。
   Phase 5 的 `reconnect()` 绝对不要复用上一次的队列/流/解码器。
3. **不改帧头、不加帧类型**。本轮全部复用 `TYPE_CONTROL (0x05)` 的 JSON 分发。
4. **版本号铁律**：功能更新必须递增版本号，禁止覆盖历史发版包。

---

# Phase 0：测试脚手架

> 目的：让 Phase 2/4/5 的纯逻辑能走真 TDD。这两个任务不做完，后续 TDD 就是空话。

### 任务 0.1：Android 建 JVM 单元测试源集

- 文件路径：`android-app/app/src/test/java/com/quickremote/app/SmokeTest.kt`（新建）
- 要做的：先不用加依赖 —— AGP 自带 JUnit4 支持，只需确认 `app/build.gradle.kts` 的
  `dependencies` 块内有：

  ```kotlin
  testImplementation("junit:junit:4.13.2")
  ```

  若尚未存在则加上。然后新建冒烟测试：

  ```kotlin
  package com.quickremote.app

  import org.junit.Assert.assertEquals
  import org.junit.Test

  class SmokeTest {
      @Test
      fun placeholder() {
          assertEquals(2, 1 + 1)
      }
  }
  ```

- 验证：`./gradlew :app:testDebugUnitTest` 输出 `BUILD SUCCESSFUL`，且报告里 `SmokeTest` 通过

### 任务 0.2：PC 建 xunit 测试工程

- 文件路径：`pc-client.Tests/QuickRemote.PCClient.Tests.csproj`（新建）
- 要做的：

  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net8.0-windows</TargetFramework>
      <Nullable>enable</Nullable>
      <IsPackable>false</IsPackable>
      <UseWPF>true</UseWPF>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
      <PackageReference Include="xunit" Version="2.9.2" />
      <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    </ItemGroup>
    <ItemGroup>
      <ProjectReference Include="../pc-client/QuickRemote.PCClient.csproj" />
    </ItemGroup>
  </Project>
  ```

  同目录建 `SmokeTests.cs`：

  ```csharp
  using Xunit;

  namespace QuickRemote.PCClient.Tests;

  public class SmokeTests
  {
      [Fact]
      public void Placeholder() => Assert.Equal(2, 1 + 1);
  }
  ```

- 验证：`dotnet test pc-client.Tests/QuickRemote.PCClient.Tests.csproj` 输出 `Passed!  - Failed: 0`

> ⚠️ 注意：`pc-client/QuickRemote.PCClient.csproj` 是 `WinExe` + `UseWPF`，
> 引用它时可能需要把 `Microsoft.NET.Test.Sdk` 的测试宿主配置为 WPF 友好。
> 若 `dotnet test` 报无法加载 WinExe 输出，退路是把 Phase 2/4/5 的纯逻辑
> 抽到 `pc-client/Interop/` 下不依赖 WPF 的静态类，测试工程用
> `<Compile Include="../pc-client/Interop/PairingPayload.cs" Link="PairingPayload.cs" />`
> 直接编译源文件，而非 ProjectReference。**优先试 ProjectReference。

---

# Phase 1：F1 音频死开关下线

### 任务 1.1：删除 AppSettings.audioRedirect

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/data/models/Models.kt`
- 要做的：删除第 111 行整行：

  ```kotlin
  val audioRedirect: Boolean = false,
  ```

  删完检查 `AppSettings` 的 `@Serializable` 字段列表无语法错误（前一行末尾保留逗号）。

- 验证：`grep -rn "audioRedirect" android-app/app/src/main/java/` 应**只剩** SettingsStore 的 3 处命中（任务 1.2 处理）

### 任务 1.2：删除 SettingsStore 的三处 audioRedirect

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/data/local/SettingsStore.kt`
- 要做的：删三行

  | 行（改前） | 内容 |
  |---|---|
  | 37 | `val AUDIO_REDIRECT = booleanPreferencesKey("audio_redirect")` |
  | 67 | `audioRedirect = prefs[SettingsKeys.AUDIO_REDIRECT] ?: false,` |
  | 115 | `prefs[SettingsKeys.AUDIO_REDIRECT] = settings.audioRedirect` |

  已装用户 DataStore 里残留的 `audio_redirect` 键会被自然忽略，**不需要清理逻辑**。

- 验证：`grep -rn "audioRedirect\|AUDIO_REDIRECT\|audio_redirect" android-app/app/src/main/java/` 应零命中

### 任务 1.3：删除设置页音频分区

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/SettingsScreen.kt`
- 要做的：删除第 366-379 行整块（含前后 Spacer 中的一个）：

  ```kotlin
              Spacer(modifier = Modifier.height(16.dp))

              // 音频
              SectionTitle("音频")
              SettingCard {
                  ToggleRow(
                      label = "音频重定向",
                      checked = settings.audioRedirect,
                      onCheckedChange = { settings = settings.copy(audioRedirect = it) }
                  )
              }
  ```

  保留其后的 `Spacer` 与「更新」分区，保证「显示」与「更新」之间仍有 16dp 间距。

- 验证：编译通过 + 设置页目视确认「显示」下方直接是「更新」

---

# Phase 2：F2 扫码 / 配置串配对

> **共享契约（两端必须逐字节一致，改动需同步两端）**

```
quickremote://pair?d=<base64url_no_padding(UTF-8 JSON)>

JSON 字段：
  v    : Int    协议版本，当前固定 1，不等于 1 一律拒绝
  addr : String 服务器地址，形如 host:port，非空且必须含 ':'
  psk  : String 预共享密钥，非空
  name : String PC 设备名，仅用于提示文案，可空
```

base64url 变体：字母表 `A-Za-z0-9-_`，**去掉尾部 `=` 填充**。

- 二维码内容 = 明文串 = 上面那整条 URL（两者完全相同，导入路径共用同一个解析器 → DRY）

### 任务 2.1：Android 配对载荷编解码（TDD）

- 文件路径：
  - 测试：`android-app/app/src/test/java/com/quickremote/app/data/PairingPayloadTest.kt`（新建）
  - 实现：`android-app/app/src/main/java/com/quickremote/app/data/PairingPayload.kt`（新建）

- **第一步 —— 先写会失败的测试**：

  ```kotlin
  package com.quickremote.app.data

  import org.junit.Assert.assertEquals
  import org.junit.Assert.assertTrue
  import org.junit.Test

  class PairingPayloadTest {

      @Test
      fun `往返编解码保持一致`() {
          val url = PairingPayload.encode("relay.example.com:8444", "abc123", "书房主机")
          val info = PairingPayload.parse(url).getOrThrow()
          assertEquals(1, info.v)
          assertEquals("relay.example.com:8444", info.addr)
          assertEquals("abc123", info.psk)
          assertEquals("书房主机", info.name)
      }

      @Test
      fun `载荷不含 base64 填充等号`() {
          val url = PairingPayload.encode("a:1", "b", "c")
          val d = url.substringAfter("d=")
          assertTrue("不应含 '='：$d", !d.contains('='))
          assertTrue("不应含 '+' 或 '/'：$d", !d.contains('+') && !d.contains('/'))
      }

      @Test
      fun `非 quickremote scheme 被拒绝`() {
          val r = PairingPayload.parse("https://pair?d=eyJ2IjoxfQ")
          assertTrue(r.isFailure)
      }

      @Test
      fun `版本号不为 1 被拒绝`() {
          val raw = """{"v":2,"addr":"a:1","psk":"b","name":"c"}"""
          val url = "quickremote://pair?d=" + PairingPayload.encodeRaw(raw)
          assertTrue(PairingPayload.parse(url).isFailure)
      }

      @Test
      fun `addr 缺少端口被拒绝`() {
          val raw = """{"v":1,"addr":"nohost","psk":"b","name":"c"}"""
          val url = "quickremote://pair?d=" + PairingPayload.encodeRaw(raw)
          assertTrue(PairingPayload.parse(url).isFailure)
      }

      @Test
      fun `psk 为空被拒绝`() {
          val raw = """{"v":1,"addr":"a:1","psk":"","name":"c"}"""
          val url = "quickremote://pair?d=" + PairingPayload.encodeRaw(raw)
          assertTrue(PairingPayload.parse(url).isFailure)
      }

      @Test
      fun `base64 损坏被拒绝且不抛异常`() {
          assertTrue(PairingPayload.parse("quickremote://pair?d=!!!notbase64!!!").isFailure)
      }

      @Test
      fun `缺少 d 参数被拒绝`() {
          assertTrue(PairingPayload.parse("quickremote://pair").isFailure)
      }

      @Test
      fun `无填充 base64url 也能解出`() {
          assertTrue(PairingPayload.parse(PairingPayload.encode("a:1", "b", "")).isSuccess)
      }
  }
  ```

- 验证（红）：`./gradlew :app:testDebugUnitTest --tests "*PairingPayloadTest*"` **应编译失败**（类不存在）。

- **第二步 —— 写最少实现让它变绿**：

  实现文件内容（`data/PairingPayload.kt`）：

  ```kotlin
  package com.quickremote.app.data

  import kotlinx.serialization.Serializable
  import kotlinx.serialization.json.Json
  import android.util.Base64
  import java.net.URLDecoder

  /**
   * 配对载荷编解码，与 PC 端 PairingPayload.cs 共享同一契约。
   * 格式：quickremote://pair?d=<base64url_no_padding(UTF-8 JSON)>
   */
  object PairingPayload {

      const val SCHEME = "quickremote"
      const val HOST = "pair"
      const val VERSION = 1

      @Serializable
      data class PairInfo(
          val v: Int = VERSION,
          val addr: String = "",
          val psk: String = "",
          val name: String = ""
      )

      private val json = Json { ignoreUnknownKeys = true }

      /** 生成完整配对 URL（二维码内容与明文串均为它）。 */
      fun encode(addr: String, psk: String, name: String): String {
          val raw = json.encodeToString(PairInfo.serializer(), PairInfo(VERSION, addr, psk, name))
          return "$SCHEME://$HOST?d=${encodeRaw(raw)}"
      }

      /** 仅编码原始 JSON 串（供测试构造非法载荷用）。 */
      fun encodeRaw(rawJson: String): String =
          Base64.encodeToString(
              rawJson.toByteArray(Charsets.UTF_8),
              Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP
          )

      /** 解析配对 URL，失败返回 Result.failure，绝不抛异常。 */
      fun parse(url: String?): Result<PairInfo> {
          return try {
              if (url.isNullOrBlank()) return Result.failure(IllegalArgumentException("空输入"))
              val trimmed = url.trim()
              if (!trimmed.startsWith("$SCHEME://", ignoreCase = true)) {
                  return Result.failure(IllegalArgumentException("不是 QuickRemote 配对链接"))
              }
              val query = trimmed.substringAfter('?', "")
              if (query.isEmpty()) return Result.failure(IllegalArgumentException("缺少参数"))
              val d = query.split('&')
                  .map { it.split('=', limit = 2) }
                  .firstOrNull { it.size == 2 && URLDecoder.decode(it[0], "UTF-8") == "d" }
                  ?.get(1)
                  ?: return Result.failure(IllegalArgumentException("缺少 d 参数"))

              val bytes = Base64.decode(d, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
              val rawText = String(bytes, Charsets.UTF_8)
              val info = json.decodeFromString(PairInfo.serializer(), rawText)

              if (info.v != VERSION) return Result.failure(IllegalArgumentException("版本不支持：${info.v}"))
              if (info.addr.isBlank() || !info.addr.contains(':')) {
                  return Result.failure(IllegalArgumentException("服务器地址无效"))
              }
              if (info.psk.isBlank()) return Result.failure(IllegalArgumentException("预共享密钥为空"))
              Result.success(info)
          } catch (e: Exception) {
              Result.failure(e)
          }
      }
  }
  ```

- 验证（绿）：`./gradlew :app:testDebugUnitTest --tests "*PairingPayloadTest*"` 全部通过

> ⚠️ `android.util.Base64` 在本地 JVM 单测里是 stub（会抛 `RuntimeException: Method not mocked`）。
> 两种解法，**优先第一种**：
> ① 在 `app/build.gradle.kts` 的 `android { testOptions { unitTests.isReturnDefaultValues = false } }`
>    —— 不够，需改用纯 JVM 实现：把 `android.util.Base64` 换成 `java.util.Base64`
>    （API 26+ 可用，`minSdk = 26` 满足），URL 安全编码用
>    `Base64.getUrlEncoder().withoutPadding()` / `getUrlDecoder()`。
> ② 若坚持 `android.util.Base64`，测试需加 Robolectric —— **不推荐**，为一个编解码引入整套框架不值。
>
> **采用方案 ①**：实现里用 `java.util.Base64`。上面代码中的
> `Base64.encodeToString(...)` / `Base64.decode(...)` 相应替换为：
>
> ```kotlin
> Base64.getUrlEncoder().withoutPadding().encodeToString(rawJson.toByteArray(Charsets.UTF_8))
> Base64.getUrlDecoder().decode(d)
> ```
>
> 并删除 `import android.util.Base64`，加 `import java.util.Base64`。

### 任务 2.2：PC 配对载荷编解码（TDD）

- 文件路径：
  - 实现：`pc-client/Interop/PairingPayload.cs`（新建，**放在 Interop 目录以免依赖 WPF**）
  - 测试：`pc-client.Tests/PairingPayloadTests.cs`（新建）

- **第一步 —— 先写测试**：

  ```csharp
  using QuickRemote.PCClient.Interop;
  using Xunit;

  namespace QuickRemote.PCClient.Tests;

  public class PairingPayloadTests
  {
      [Fact]
      public void RoundTrip()
      {
          var url = PairingPayload.Encode("relay.example.com:8444", "abc123", "书房主机");
          Assert.True(PairingPayload.TryParse(url, out var info, out var err), err);
          Assert.Equal(1, info.Version);
          Assert.Equal("relay.example.com:8444", info.Addr);
          Assert.Equal("abc123", info.Psk);
          Assert.Equal("书房主机", info.Name);
      }

      [Fact]
      public void NoBase64Padding()
      {
          var url = PairingPayload.Encode("a:1", "b", "c");
          var d = url[(url.IndexOf("d=", StringComparison.Ordinal) + 2)..];
          Assert.DoesNotContain("=", d);
          Assert.DoesNotContain("+", d);
          Assert.DoesNotContain("/", d);
      }

      [Theory]
      [InlineData("https://pair?d=eyJ2IjoxfQ")]
      [InlineData("quickremote://pair")]
      [InlineData("quickremote://pair?d=!!!bad!!!")]
      [InlineData("")]
      public void InvalidUrlRejected(string url)
      {
          Assert.False(PairingPayload.TryParse(url, out _, out _));
      }

      [Fact]
      public void VersionMismatchRejected()
          => Assert.False(PairingPayload.TryParse(
              "quickremote://pair?d=" + PairingPayload.EncodeRaw("""{"v":2,"addr":"a:1","psk":"b"}"""),
              out _, out _));

      [Fact]
      public void AddrWithoutPortRejected()
          => Assert.False(PairingPayload.TryParse(
              "quickremote://pair?d=" + PairingPayload.EncodeRaw("""{"v":1,"addr":"nohost","psk":"b"}"""),
              out _, out _));
  }
  ```

- 验证（红）：`dotnet test pc-client.Tests/` 编译失败（`PairingPayload` 不存在）

- **第二步 —— 实现**（`pc-client/Interop/PairingPayload.cs`）：

  ```csharp
  using System;
  using System.Text;
  using System.Text.Json;
  using System.Text.Json.Serialization;

  namespace QuickRemote.PCClient.Interop;

  /// <summary>
  /// 配对载荷编解码，与 Android 端 PairingPayload.kt 共享同一契约。
  /// 格式：quickremote://pair?d=&lt;base64url_no_padding(UTF-8 JSON)&gt;
  /// </summary>
  public static class PairingPayload
  {
      public const string Scheme = "quickremote";
      public const string Host = "pair";
      public const int Version = 1;

      public sealed class PairInfo
      {
          [JsonPropertyName("v")] public int Version { get; set; } = PairingPayload.Version;
          [JsonPropertyName("addr")] public string Addr { get; set; } = string.Empty;
          [JsonPropertyName("psk")] public string Psk { get; set; } = string.Empty;
          [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
      }

      private static readonly JsonSerializerOptions Opts = new()
      {
          Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
          DefaultIgnoreCondition = JsonIgnoreCondition.Never
      };

      /// <summary>生成完整配对 URL（二维码内容与明文串均为它）。</summary>
      public static string Encode(string addr, string psk, string name)
      {
          var raw = JsonSerializer.Serialize(new PairInfo { Addr = addr, Psk = psk, Name = name }, Opts);
          return $"{Scheme}://{Host}?d={EncodeRaw(raw)}";
      }

      /// <summary>仅编码原始 JSON 串。</summary>
      public static string EncodeRaw(string rawJson)
      {
          var bytes = Encoding.UTF8.GetBytes(rawJson);
          return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
      }

      public static bool TryParse(string? url, out PairInfo info, out string error)
      {
          info = new PairInfo();
          error = string.Empty;
          try
          {
              if (string.IsNullOrWhiteSpace(url)) { error = "空输入"; return false; }
              var trimmed = url.Trim();
              if (!trimmed.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
              { error = "不是 QuickRemote 配对链接"; return false; }

              var q = trimmed.IndexOf('?');
              if (q < 0) { error = "缺少参数"; return false; }
              var query = trimmed[(q + 1)..];

              string? d = null;
              foreach (var pair in query.Split('&'))
              {
                  var kv = pair.Split('=', 2);
                  if (kv.Length == 2 && kv[0] == "d") { d = kv[1]; break; }
              }
              if (string.IsNullOrEmpty(d)) { error = "缺少 d 参数"; return false; }

              var b64 = d.Replace('-', '+').Replace('_', '/');
              switch (b64.Length % 4)
              {
                  case 2: b64 += "=="; break;
                  case 3: b64 += "="; break;
                  case 1: error = "base64 长度非法"; return false;
              }

              var rawText = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
              var parsed = JsonSerializer.Deserialize<PairInfo>(rawText, Opts);
              if (parsed == null) { error = "载荷解析失败"; return false; }
              if (parsed.Version != Version) { error = $"版本不支持：{parsed.Version}"; return false; }
              if (string.IsNullOrWhiteSpace(parsed.Addr) || !parsed.Addr.Contains(':'))
              { error = "服务器地址无效"; return false; }
              if (string.IsNullOrWhiteSpace(parsed.Psk)) { error = "预共享密钥为空"; return false; }

              info = parsed;
              return true;
          }
          catch (Exception ex)
          {
              error = "载荷损坏：" + ex.Message;
              return false;
          }
      }
  }
  ```

- 验证（绿）：`dotnet test pc-client.Tests/` 全通过

> ⚠️ **两端互操作验收**：Phase 6 必须用 PC 生成的 URL 喂给 Android 解析（真机扫码），
> 这是唯一能证明两份实现逐字节一致的检验。单测各自绿 ≠ 互通。

### 任务 2.3：Android 注册深链接收

- 文件路径：`android-app/app/src/main/AndroidManifest.xml`
- 要做的：给 `MainActivity` 加 `android:launchMode="singleTop"`，并在其
  `<intent-filter>` 之后**新增**一个 filter（不要塞进现有 LAUNCHER filter）：

  ```xml
  <intent-filter android:autoVerify="false">
      <action android:name="android.intent.action.VIEW" />
      <category android:name="android.intent.category.DEFAULT" />
      <category android:name="android.intent.category.BROWSABLE" />
      <data android:scheme="quickremote" android:host="pair" />
  </intent-filter>
  ```

- 验证：`./gradlew :app:assembleDebug` 通过；
  `adb shell pm dump com.quickremote.app | grep -A3 quickremote` 能看到该 filter

### 任务 2.4：MainActivity 处理配对意图

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/MainActivity.kt`
- 要做的：

1. 顶部加一个跨重建存活的提示（`recreate()` 后仍要能显示）：

   ```kotlin
   companion object {
       /** 配对成功后暂存提示文案，供 recreate() 后的 UI 消费。 */
       var pendingPairMessage: String? = null
   }
   ```

2. `onCreate` 里在 `setContent` 之前处理意图，并重写 `onNewIntent`（`singleTop` 下热启动走这里）：

   ```kotlin
   override fun onCreate(savedInstanceState: Bundle?) {
       super.onCreate(savedInstanceState)
       enableEdgeToEdge()
       handlePairIntent(intent)
       setContent {
           QuickRemoteTheme { AppRoot() }
       }
   }

   override fun onNewIntent(intent: Intent) {
       super.onNewIntent(intent)
       setIntent(intent)
       handlePairIntent(intent)
   }

   /** 解析 quickremote://pair 深链，成功后写入配置并重建 Activity 让起始路由生效。 */
   private fun handlePairIntent(intent: Intent?) {
       val data = intent?.data?.toString() ?: return
       if (!data.startsWith("${PairingPayload.SCHEME}://${PairingPayload.HOST}")) return

       val store = SettingsStore(applicationContext)
       lifecycleScope.launch {
           PairingPayload.parse(data)
               .onSuccess { info ->
                   store.saveServerConfig(ServerConfig(address = info.addr, preSharedKey = info.psk))
                   store.saveToken("")            // 地址/密钥变了，旧令牌作废
                   pendingPairMessage = if (info.name.isBlank()) "已导入配置" else "已导入配置：${info.name}"
                   recreate()
               }
               .onFailure { e ->
                   pendingPairMessage = "配对失败：${e.message ?: "载荷无效"}"
                   recreate()
               }
       }
   }
   ```

3. 补 import：`android.content.Intent`、`androidx.lifecycle.lifecycleScope`、
   `kotlinx.coroutines.launch`、`com.quickremote.app.data.PairingPayload`、
   `com.quickremote.app.data.local.SettingsStore`（已存在）、
   `com.quickremote.app.data.models.ServerConfig`（已存在）。

4. `AppRoot` 里消费并清除提示，复用项目既有 toast 风格（`DeviceListScreen.kt:227` 的
   `ToastHost` 用的是 Snackbar）。最省的做法是在 `AppRoot` 加一个 `LaunchedEffect`：

   ```kotlin
   val pairMessage = MainActivity.pendingPairMessage
   if (pairMessage != null) {
       MainActivity.pendingPairMessage = null
       Toast.makeText(context, pairMessage, Toast.LENGTH_LONG).show()
   }
   ```

   放在 `Box { ... }` 之前即可（`LaunchedEffect(Unit)` 包裹）。需要 `import android.widget.Toast`。

   > 说明：此处用系统 Toast 而非项目自绘 Snackbar，原因是提示发生在 NavGraph 之外、
   > 且需跨 `recreate()` 存活，接入 Snackbar 宿主反而更绕。**一次性的配对确认提示，
   > 系统 Toast 可接受。**

- 验证：
  1. `adb shell am start -a android.intent.action.VIEW -d "quickremote://pair?d=<任务2.2 PC 生成的 d 值>" com.quickremote.app`
  2. App 打开 → 弹 Toast「已导入配置：xxx」→ 设置页「服务器」中地址与密钥已填入
  3. 再发一次同样的 am start（App 已在后台）→ 仍能正确提示（验证 `onNewIntent` 通路）

### 任务 2.5：设置页「粘贴配置导入」兜底入口

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/SettingsScreen.kt`
- 要做的：在 `SectionTitle("服务器")` 卡片内、现有输入框之后加一个次要按钮
  「粘贴配置导入」（`Style` 用项目现有的次要按钮样式，避免引入新样式）：

  ```kotlin
  OutlinedButton(
      onClick = {
          val clip = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
          val text = clip.primaryClip?.getItemAt(0)?.coerceToText(context)?.toString()
          if (text.isNullOrBlank()) {
              statusMessage = "剪贴板为空"
          } else {
              PairingPayload.parse(text)
                  .onSuccess { info ->
                      settings = settings.copy()   // 触发界面刷新
                      scope.launch {
                          settingsStore.saveServerConfig(
                              ServerConfig(address = info.addr, preSharedKey = info.psk)
                          )
                          settingsStore.saveToken("")
                          statusMessage = if (info.name.isBlank()) "已导入配置" else "已导入配置：${info.name}"
                      }
                  }
                  .onFailure { statusMessage = "导入失败：${it.message ?: "内容无效"}" }
          }
      },
      modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
  ) { Text("粘贴配置导入") }
  ```

  > 上面依赖的 `context` / `scope` / `statusMessage` / `settingsStore` 在
  > `SettingsScreen.kt` 里都已存在（该文件已有 `ClipboardManager` 用法，见 `:573-575`）。
  > 若 `scope` 名称不符，按文件内实际命名调整 —— **不要新建第二个协程作用域**。

- 验证：PC 端复制明文串 → Android 设置页粘贴导入 → 地址与密钥正确填入且提示成功；
  粘贴一段乱码 → 提示「导入失败」而非崩溃

### 任务 2.6：PC 引入 QRCoder

- 文件路径：`pc-client/QuickRemote.PCClient.csproj`
- 要做的：在 `Vortice.*` 那组 `PackageReference` 之后，新增一个 `ItemGroup`：

  ```xml
  <!-- 手机配对：生成配对二维码（纯托管，无原生依赖） -->
  <ItemGroup>
    <PackageReference Include="QRCoder" Version="1.6.0" />
  </ItemGroup>
  ```

- 验证：`dotnet build pc-client/QuickRemote.PCClient.csproj` 成功且
  `bin/.../QRCoder.dll` 存在

### 任务 2.7：PC 配对弹窗

- 文件路径：`pc-client/Views/PairingWindow.xaml` + `PairingWindow.xaml.cs`（均新建）
- 要做的：沿用 `DialogWindow` 的窗口骨架（`WindowStyle="None"`、
  `AllowsTransparency="True"`、`CornerRadius="14"`、`BgSecondaryBrush`、
  `TitleCloseButton`、`ActionButton`）。窗口宽 360。

  布局（自上而下）：
  1. 标题栏「手机配对」+ 关闭按钮（复用 `TitleBar_MouseLeftButtonDown` 的 `DragMove`）
  2. **警告条**（仅当 PSK 为 `change-me-please` 时 `Visible`）：
     橙色底 + 文案「当前预共享密钥仍是默认值，请先修改后再配对」，
     横向 `StackPanel`，手动给 `Visibility`
  3. 二维码 `Image`（`x:Name="QrImage"`，`Width/Height=240`，
     `RenderOptions.BitmapScalingMode="NearestNeighbor"` 保证放大不糊）
  4. 明文串 `TextBox`（`IsReadOnly="True"`、`TextWrapping="Wrap"`、
     `MaxHeight="56"`、垂直滚动）
  5. 提示文案「用手机相机扫描二维码，或复制配置串后在 App 设置页导入」
  6. 安全提示「请勿截图分享此二维码，它等同于你的密钥」（`TextMuted` 小字）
  7. 底部按钮：「复制配置串」（`ActionButton`）、「关闭」（`PrimaryActionButton`）

  代码后置关键逻辑：

  ```csharp
  public PairingWindow(string serverAddress, string preSharedKey, string deviceName)
  {
      InitializeComponent();
      var url = Interop.PairingPayload.Encode(serverAddress, preSharedKey, deviceName);
      PayloadBox.Text = url;

      using var gen = new QRCoder.QRCodeGenerator();
      using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
      var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
      var bmp = new BitmapImage();
      bmp.BeginInit();
      bmp.CacheOption = BitmapCacheOption.OnLoad;
      bmp.StreamSource = new MemoryStream(png);
      bmp.EndInit();
      QrImage.Source = bmp;

      WarnBar.Visibility = preSharedKey == "change-me-please"
          ? Visibility.Visible : Visibility.Collapsed;
  }

  private void BtnCopy_Click(object sender, RoutedEventArgs e)
  {
      try { Clipboard.SetText(PayloadBox.Text); }
      catch { /* 剪贴板被占用，忽略 */ }
  }
  ```

  > 二维码配色：QRCoder 默认黑白。全局是深色主题，把白底改为浅灰
  > （`GetGraphic(8, Color.FromArgb(0xF1,0xEF,0xE8), Color.FromArgb(0x14,0x15,0x18), false)`）
  > 观感更统一。**但扫描成功率优先于配色 —— 若真机扫描失败，立即改回默认黑白。**
  > `PngByteQRCode.GetGraphic` 的颜色重载在 1.6.0 上不保证存在，**先用默认黑白跑通，再试配色**。

- 验证：`dotnet build` 通过；手工 `new PairingWindow("a:1","b","c")` 能出图（Phase 6 真机验）

### 任务 2.8：设置中心挂载入口

- 文件路径：`pc-client/Views/SettingsWindow.xaml` + `SettingsWindow.xaml.cs`
- 要做的：在「基本配置」卡片内、设备名称行之后加一行「手机配对」+ 按钮：

  ```xml
  <Button Content="手机配对" Style="{StaticResource ActionButton}"
          Click="BtnPairing_Click"/>
  ```

  代码后置：

  ```csharp
  private void BtnPairing_Click(object sender, RoutedEventArgs e)
  {
      var addr = ServerAddressBox.Text?.Trim() ?? "";
      var psk = PreSharedKeyBox.Text?.Trim() ?? "";
      if (addr.Length == 0 || psk.Length == 0)
      {
          DialogWindow.Show("请先填写服务器地址与预共享密钥", "无法配对", DialogWindow.DialogType.Warning);
          return;
      }
      new PairingWindow(addr, psk, DeviceNameBox.Text?.Trim() ?? "") { Owner = this }.ShowDialog();
  }
  ```

  > `ServerAddressBox` / `PreSharedKeyBox` / `DeviceNameBox` 是占位名，
  > **必须按 SettingsWindow.xaml 内 `x:Name` 的实际命名替换**。修改前先读该文件确认。

- 验证：点「手机配对」弹窗且二维码内容 = 当前配置；地址/密钥为空时弹警告不崩溃

---

# Phase 3：F3 会话内画质快捷面板

> 复用已有 `{"action":"quality","percent":N}` 控制帧 —— **PC 端零改动**。

### 任务 3.1：开放画质发送能力

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/services/RemoteSessionManager.kt`
- 要做的：
  1. `qualityPercent`（`:69`）已是 `var`，保持 `private set` 会导致 ViewModel 改不了 ——
     去掉 `private set`（若存在），保留 `coerceIn(20, 100)` 保护，改成：

     ```kotlin
     var qualityPercent: Int = 80
         set(value) { field = value.coerceIn(20, 100) }
     ```

  2. 把 `sendQualityControl()`（`:264` 附近）的 `private` 去掉，或在下方加公开包装：

     ```kotlin
     /** 会话内调整画质并立即下发（供 UI 调用）。 */
     fun setQualityPercent(percent: Int) {
         qualityPercent = percent
         sendQualityControl()
     }
     ```

- 验证：编译通过

### 任务 3.2：ViewModel 暴露 setQuality

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/viewmodels/SessionViewModel.kt`
- 要做的：加一个方法，并同时持久化到设置（作为下次默认值）：

  ```kotlin
  /** 会话内切换画质档位：立即下发并写回设置作为下次默认。 */
  fun setQuality(percent: Int) {
      _qualityPercent.value = percent.coerceIn(20, 100)
      sessionManager.setQualityPercent(percent)
      viewModelScope.launch {
          val cur = settingsStore.appSettings.first()
          settingsStore.saveAppSettings(cur.copy(qualityPercent = percent.coerceIn(20, 100)))
      }
  }
  ```

  同步加一个可观察状态（供面板高亮当前档位）：

  ```kotlin
  private val _qualityPercent = MutableStateFlow(80)
  val qualityPercent: StateFlow<Int> = _qualityPercent.asStateFlow()
  ```

  并在 `startSession` 里初始化它：

  ```kotlin
  _qualityPercent.value = appSettings.qualityPercent
  ```

- 验证：编译通过

### 任务 3.3：底部工具栏加「画质」按钮 + 浮层

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/RemoteSessionScreen.kt`
- 要做的：

1. 状态：`var showQualityPanel by remember { mutableStateOf(false) }`

2. 在底部工具栏 `Row` 的「全屏」按钮**之前**插入第 5 个按钮：

   ```kotlin
   ToolBarButton(
       icon = Icons.Filled.Tune,
       label = "画质",
       active = showQualityPanel,
       onClick = { showQualityPanel = !showQualityPanel }
   )
   ```

   `SpaceEvenly` 会自动重新分配 5 个按钮的间距，无需手动权重。
   需要 `import androidx.compose.material.icons.filled.Tune`。

3. 浮层放在 `BoxWithConstraints` 内、`RemoteDisplayView` **之上**（保证能遮挡画面），
   顶部对齐：

   ```kotlin
   val quality by viewModel.qualityPercent.collectAsState()
   val preset = listOf(40 to "流畅", 60 to "标准", 80 to "高清", 100 to "原画")

   if (showQualityPanel) {
       Row(
           modifier = Modifier
               .align(Alignment.TopCenter)
               .padding(top = 12.dp)
               .background(Color(0xE625262C), RoundedCornerShape(10.dp))
               .padding(horizontal = 8.dp, vertical = 6.dp),
           horizontalArrangement = Arrangement.spacedBy(6.dp),
           verticalAlignment = Alignment.CenterVertically
       ) {
           preset.forEach { (pct, label) ->
               val selected = quality == pct
               Box(
                   modifier = Modifier
                       .clip(RoundedCornerShape(6.dp))
                       .background(if (selected) Accent else Color.Transparent)
                       .clickable { viewModel.setQuality(pct) }
                       .padding(horizontal = 12.dp, vertical = 6.dp)
               ) {
                   Text(
                       label,
                       style = MaterialTheme.typography.labelMedium,
                       color = if (selected) Color.White else TextPrimary
                   )
               }
           }
       }
   }
   ```

   `Color(0xE625262C)` = 项目卡片色 `#25262C` 加 90% 不透明度，与全局色板一致。

- 验证：见 Phase 6 端到端验收清单（画质切换不重连、PC 日志出现 `Quality adjust request`）

---

# Phase 4：F4 剪贴板双向同步

> **共享契约**：复用 `TYPE_CONTROL (0x05)`，新增 `action:"clipboard"`

```json
{ "action": "clipboard", "id": "a1b2c3d4", "seq": 0, "total": 1, "text": "……" }
```

| 规则 | 值 |
|---|---|
| 单片上限 | 64 KB（**UTF-8 字节数**） |
| 总量上限 | 256 KB，超出直接放弃（不截断） |
| 分片切割 | 必须在**字符边界**切成，不得切出半个码点 |
| 未收齐超时 | 5 秒后丢弃整组 |
| 防回环 | 收到内容与 `lastAppliedHash` 相同则丢弃，不写系统剪贴板 |

### 任务 4.1：Android 分片编解码（TDD）

- 文件路径：
  - 实现：`android-app/app/src/main/java/com/quickremote/app/services/ClipboardChunker.kt`（新建）
  - 测试：`android-app/app/src/test/java/com/quickremote/app/services/ClipboardChunkerTest.kt`（新建）

- **第一步 —— 先写测试**（含全部边界）：

  ```kotlin
  package com.quickremote.app.services

  import org.junit.Assert.assertEquals
  import org.junit.Assert.assertNull
  import org.junit.Assert.assertTrue
  import org.junit.Test

  class ClipboardChunkerTest {

      @Test
      fun `短文本单片`() {
          val c = ClipboardChunker.split("hello")
          assertEquals(1, c.size)
          assertEquals(0, c[0].seq)
          assertEquals(1, c[0].total)
          assertEquals("hello", c[0].text)
      }

      @Test
      fun `超长文本分多片且拼接后完全一致`() {
          val text = "中文abc混合".repeat(20000)   // 明显超过 64KB
          val c = ClipboardChunker.split(text)
          assertTrue("应分多片", c.size > 1)
          assertEquals(c.size, c[0].total)
          assertEquals(text, c.sortedBy { it.seq }.joinToString("") { it.text })
      }

      @Test
      fun `每片 UTF-8 字节数不超过上限`() {
          val text = "中".repeat(50000)
          ClipboardChunker.split(text).forEach {
              assertTrue("分片过大：${it.text.toByteArray(Charsets.UTF_8).size}",
                  it.text.toByteArray(Charsets.UTF_8).size <= ClipboardChunker.MAX_CHUNK_BYTES)
          }
      }

      @Test
      fun `切分不产生替换字符`() {
          val text = "汉字".repeat(30000)
          ClipboardChunker.split(text).forEach {
              assertTrue("出现 U+FFFD，切断了码点", !it.text.contains('\uFFFD'))
          }
      }

      @Test
      fun `正好等于单片的边界`() {
          val text = "a".repeat(ClipboardChunker.MAX_CHUNK_BYTES)
          val c = ClipboardChunker.split(text)
          assertEquals(1, c.size)
          assertEquals(text, c[0].text)
      }

      @Test
      fun `超过总量上限返回空列表`() {
          val text = "a".repeat(ClipboardChunker.MAX_TOTAL_BYTES + 1)
          assertTrue(ClipboardChunker.split(text).isEmpty())
      }

      @Test
      fun `空文本单片返回`() {
          val c = ClipboardChunker.split("")
          assertEquals(1, c.size)
          assertEquals("", c[0].text)
      }

      @Test
      fun `组装器收齐后返回全文`() {
          val asm = ClipboardAssembler()
          val text = "中文abc混合".repeat(20000)
          val parts = ClipboardChunker.split(text)
          var result: String? = null
          parts.forEach { result = asm.add("id1", it.seq, it.total, it.text) }
          assertEquals(text, result)
      }

      @Test
      fun `组装器乱序收齐也正确`() {
          val asm = ClipboardAssembler()
          val text = "abcd".repeat(40000)
          val parts = ClipboardChunker.split(text).reversed()
          var result: String? = null
          parts.forEach { result = asm.add("id2", it.seq, it.total, it.text) }
          assertEquals(text, result)
      }

      @Test
      fun `缺片时不返回结果`() {
          val asm = ClipboardAssembler()
          assertNull(asm.add("id3", 0, 3, "a"))
          assertNull(asm.add("id3", 2, 3, "c"))
      }

      @Test
      fun `超时后丢弃整组`() {
          val asm = ClipboardAssembler()
          assertNull(asm.add("id4", 0, 2, "a", nowMs = 0))
          assertNull(asm.add("id4", 1, 2, "b", nowMs = 10_000))   // 超过 5 秒
      }
  }
  ```

- 验证（红）：`./gradlew :app:testDebugUnitTest --tests "*ClipboardChunkerTest*"` 编译失败

- **第二步 —— 实现**：

  ```kotlin
  package com.quickremote.app.services

  /** 剪贴板文本分片器。切割点必须落在字符边界，否则会产生 U+FFFD 损坏内容。 */
  object ClipboardChunker {

      const val MAX_CHUNK_BYTES = 64 * 1024
      const val MAX_TOTAL_BYTES = 256 * 1024

      data class Chunk(val seq: Int, val total: Int, val text: String)

      /** 分片。总量超限或无法安全切分时返回空列表（调用方应放弃同步）。 */
      fun split(text: String): List<Chunk> {
          val totalBytes = text.toByteArray(Charsets.UTF_8).size
          if (totalBytes > MAX_TOTAL_BYTES) return emptyList()

          val pieces = mutableListOf<String>()
          val sb = StringBuilder()
          var bytes = 0

          var i = 0
          while (i < text.length) {
              val cp = text.codePointAt(i)
              val charCount = Character.charCount(cp)
              val seg = text.substring(i, i + charCount)
              val segBytes = seg.toByteArray(Charsets.UTF_8).size

              if (bytes + segBytes > MAX_CHUNK_BYTES && sb.isNotEmpty()) {
                  pieces.add(sb.toString())
                  sb.setLength(0)
                  bytes = 0
              }
              sb.append(seg)
              bytes += segBytes
              i += charCount
          }
          pieces.add(sb.toString())   // 空文本也产生一片

          val total = pieces.size
          return pieces.mapIndexed { idx, s -> Chunk(idx, total, s) }
      }
  }

  /** 剪贴板分片组装器。5 秒未收齐的组会被丢弃。 */
  class ClipboardAssembler(private val timeoutMs: Long = 5_000) {

      private class Pending(val total: Int, val parts: MutableMap<Int, String>, var startedAt: Long)

      private val pending = mutableMapOf<String, Pending>()

      /** 加入一片。集齐返回全文，否则返回 null。 */
      fun add(id: String, seq: Int, total: Int, text: String, nowMs: Long = System.currentTimeMillis()): String? {
          purgeExpired(nowMs)

          if (total <= 0 || seq < 0 || seq >= total) return null

          val entry = pending.getOrPut(id) { Pending(total, mutableMapOf(), nowMs) }
          if (entry.total != total) {           // 同 id 但 total 不一致 → 视为新组
              pending[id] = Pending(total, mutableMapOf(), nowMs)
          }
          val e = pending[id]!!
          e.parts[seq] = text

          if (e.parts.size < e.total) return null

          val full = StringBuilder()
          for (i in 0 until e.total) {
              val p = e.parts[i] ?: return null
              full.append(p)
          }
          pending.remove(id)
          return full.toString()
      }

      private fun purgeExpired(nowMs: Long) {
          val it = pending.iterator()
          while (it.hasNext()) {
              val ent = it.next().value
              if (nowMs - ent.startedAt > timeoutMs) it.remove()
          }
      }
  }
  ```

- 验证（绿）：`./gradlew :app:testDebugUnitTest --tests "*ClipboardChunkerTest*"` 全通过

### 任务 4.2：PC 分片编解码（TDD）

- 文件路径：
  - 实现：`pc-client/Interop/ClipboardChunker.cs`（新建）
  - 测试：`pc-client.Tests/ClipboardChunkerTests.cs`（新建）

- 逻辑与 Android 端**逐条对应**（同样的上限常量、同样的码点安全切分、同样的 5 秒超时）。
  切分用 `char.IsHighSurrogate` + `char.ConvertToUtf32` 处理代理对；
  字节数用 `Encoding.UTF8.GetByteCount(segment)`。

- 测试用例集与 Android 端一一对应（短文本单片 / 超长多片可逆 / 单片不超上限 /
  不产生 U+FFFD / 正好等于上限 / 超总量返回空 / 空文本一片 / 组装收齐 /
  乱序收齐 / 缺片返回 null / 超时丢弃）。

- 验证（红→绿）：`dotnet test pc-client.Tests/ --filter ClipboardChunkerTests`

### 任务 4.3：PC 剪贴板监听与读写

- 文件路径：`pc-client/Services/ClipboardSync.cs`（新建）
- 要做的：封装 Win32 剪贴板，暴露事件供会话层推送。

  ```csharp
  using System;
  using System.Runtime.InteropServices;
  using System.Security.Cryptography;
  using System.Text;
  using System.Windows;
  using System.Windows.Interop;

  namespace QuickRemote.PCClient.Services;

  /// <summary>
  /// 本机剪贴板监听与读写。仅处理纯文本；
  /// 内容与上次应用过的内容相同则不上报，用于切断两端回环。
  /// </summary>
  public sealed class ClipboardSync : IDisposable
  {
      private const int WM_CLIPBOARDUPDATE = 0x031D;

      [DllImport("user32.dll", SetLastError = true)]
      private static extern bool AddClipboardFormatListener(IntPtr hwnd);

      [DllImport("user32.dll", SetLastError = true)]
      private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

      private readonly HwndSource _source;
      private string _lastHash = string.Empty;

      /// <summary>本机剪贴板文本变化（UI 线程触发）。</summary>
      public event Action<string>? TextChanged;

      public ClipboardSync()
      {
          var parameters = new HwndSourceParameters("QuickRemoteClipboardListener")
          {
              Width = 0, Height = 0, WindowStyle = 0
          };
          _source = new HwndSource(parameters);
          _source.AddHook(WndProc);
          AddClipboardFormatListener(_source.Handle);
      }

      private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
      {
          if (msg == WM_CLIPBOARDUPDATE) TryReadAndRaise();
          return IntPtr.Zero;
      }

      private void TryReadAndRaise()
      {
          var text = ReadText();
          if (text == null) return;
          var h = Hash(text);
          if (h == _lastHash) return;
          _lastHash = h;
          TextChanged?.Invoke(text);
      }

      /// <summary>读取本机剪贴板文本，无文本返回 null。</summary>
      public static string? ReadText()
      {
          try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
          catch { return null; }
      }

      /// <summary>
      /// 由对端推送来的文本写入本机剪贴板。
      /// 记录哈希，避免本机剪贴板变化被再次推回对端形成回环。
      /// </summary>
      public void ApplyRemote(string text)
      {
          _lastHash = Hash(text);
          try { Clipboard.SetText(text); } catch { /* 剪贴板被占用，忽略 */ }
      }

      private static string Hash(string s)
      {
          using var sha = SHA256.Create();
          return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
      }

      public void Dispose()
      {
          try
          {
              RemoveClipboardFormatListener(_source.Handle);
              _source.RemoveHook(WndProc);
              _source.Dispose();
          }
          catch { /* ignore */ }
      }
  }
  ```

- 验证：编译通过；运行后复制一段文字，调试输出能看到 `TextChanged` 触发

### 任务 4.4：PC 收发剪贴板控制帧

- 文件路径：`pc-client/Services/RemoteSessionManager.cs`
- 要做的：

1. 字段与生命周期（会话开始创建、会话结束 `Dispose` —— **切记每次会话重建**）：

   ```csharp
   private ClipboardSync? _clipboardSync;
   private ClipboardAssembler? _clipboardAssembler;
   ```

2. `HandleControlFrame`（`:1471` 一带，`quality` 分支之后）加分支：

   ```csharp
   else if (action == "clipboard" &&
            root.TryGetProperty("id", out var cid) &&
            root.TryGetProperty("seq", out var cseq) &&
            root.TryGetProperty("total", out var ctotal) &&
            root.TryGetProperty("text", out var ctext))
   {
       var full = _clipboardAssembler?.Add(
           cid.GetString() ?? "", cseq.GetInt32(), ctotal.GetInt32(), ctext.GetString() ?? "");
       if (full != null)
       {
           _logger.Info($"Clipboard received from peer: {full.Length} chars");
           _clipboardSync?.ApplyRemote(full);
       }
   }
   ```

3. 本机剪贴板 → 对端（会话建立后接线，`ClipboardSync.TextChanged` 回调里分片并发送）：

   ```csharp
   private void OnLocalClipboardChanged(string text)
   {
       var chunks = ClipboardChunker.Split(text);
       if (chunks.Count == 0)
       {
           _logger.Warn($"Clipboard too large to sync ({text.Length} chars), skipped");
           return;
       }
       var id = Guid.NewGuid().ToString("N")[..8];
       foreach (var c in chunks)
       {
           var json = System.Text.Json.JsonSerializer.Serialize(new
           {
               action = "clipboard", id, seq = c.Seq, total = c.Total, text = c.Text
           });
           SendControlFrame(System.Text.Encoding.UTF8.GetBytes(json));
       }
   }
   ```

   > `SendControlFrame` 是占位名 —— **按 `RemoteSessionManager.cs` 内实际的控制帧发送方法命名**替换。
   > 修改前先读该文件确认已有发送路径（`keyframe` 请求的回复路径可作参考）。

- 验证：见 Phase 6 验收清单（含回环检查）

### 任务 4.5：Android 收发剪贴板控制帧

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/services/RemoteSessionManager.kt`
- 要做的：

1. 加字段（**每次 `start()` 重建，不要复用**）：

   ```kotlin
   private val clipboardAssembler = ClipboardAssembler()
   private var lastAppliedClipHash: String = ""
   ```

   > `start()` 里必须重新赋值，否则重连后残留的未完成分片会串味。

2. 发送（公开方法，供 UI 在前台时调用）：

   ```kotlin
   /** 把本机剪贴板文本推送到对端。内容与上次应用过的相同则跳过（防回环）。 */
   fun syncLocalClipboard() {
       val text = readLocalClipboardText() ?: return
       val hash = sha256Hex(text)
       if (hash == lastAppliedClipHash) return

       val chunks = ClipboardChunker.split(text)
       if (chunks.isEmpty()) {
           logger.warn("Clipboard too large to sync (${text.length} chars), skipped")
           return
       }
       val id = UUID.randomUUID().toString().replace("-", "").take(8)
       inputExecutor.execute {
           try {
               val out = output ?: return@execute
               chunks.forEach { c ->
                   val json = "{\"action\":\"clipboard\",\"id\":\"$id\",\"seq\":${c.seq}," +
                              "\"total\":${c.total},\"text\":${jsonString(c.text)}}"
                   val data = json.toByteArray(Charsets.UTF_8)
                   synchronized(out) {
                       out.write(RemoteFrameProtocol.makeHeader(RemoteFrameProtocol.TYPE_CONTROL, data.size))
                       out.write(data)
                       out.flush()
                   }
               }
           } catch (e: Exception) {
               logger.warn("Clipboard send failed: ${e.message}")
           }
       }
   }
   ```

   `jsonString()` 是一个最小 JSON 字符串转义助手（只需处理 `"`、`\`、控制字符、
   换行/制表），**不要为此引入新依赖**：

   ```kotlin
   private fun jsonString(s: String): String {
       val sb = StringBuilder(s.length + 16)
       sb.append('"')
       for (ch in s) {
           when (ch) {
               '"' -> sb.append("\\\"")
               '\\' -> sb.append("\\\\")
               '\n' -> sb.append("\\n")
               '\r' -> sb.append("\\r")
               '\t' -> sb.append("\\t")
               '\b' -> sb.append("\\b")
               '\u000C' -> sb.append("\\f")
               else -> if (ch < ' ') sb.append("\\u%04x".format(ch.code)) else sb.append(ch)
           }
       }
       sb.append('"')
       return sb.toString()
   }
   ```

3. 接收分支（现有 CONTROL 帧处理处，`quality` / `keyframe` 之类的解析同层）：

   ```kotlin
   val full = clipboardAssembler.add(id, seq, total, text)
   if (full != null) {
       lastAppliedClipHash = sha256Hex(full)
       writeLocalClipboardText(full)
       logger.info("Clipboard received from peer: ${full.length} chars")
   }
   ```

4. 本机剪贴板读写（Android 10+ **只允许前台读取**，绝不做轮询）：

   ```kotlin
   private fun readLocalClipboardText(): String? = try {
       val cm = appContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
       cm.primaryClip?.getItemAt(0)?.coerceToText(appContext)?.toString()
   } catch (_: Exception) { null }

   private fun writeLocalClipboardText(text: String) {
       try {
           val cm = appContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
           cm.setPrimaryClip(ClipData.newPlainText("QuickRemote", text))
       } catch (_: Exception) { }
   }
   ```

   > `appContext` 若不存在，用 `RemoteSessionManager` 构造时传入的 `Context`。
   > **按文件内实际的 Context 来源命名替换**。

5. `sha256Hex`：`MessageDigest.getInstance("SHA-256")` + `String.format("%02x", b)`，
   取前 16 字节比对即可（够用且省内存）。

- 验证：见 Phase 6 验收清单

### 任务 4.6：Android 侧触发时机

- 文件路径：
  - `android-app/app/src/main/java/com/quickremote/app/services/RemoteSessionManager.kt`
  - `android-app/app/src/main/java/com/quickremote/app/ui/screens/RemoteSessionScreen.kt`
- 要做的：在 **App 回到前台**、以及**会话内工具面板（画质/快捷键）操作后**各触发一次
  `sessionManager.syncLocalClipboard()`。

  返回前台：在 `RemoteSessionScreen` 用 `LifecycleEventObserver` 监听 `ON_RESUME`：

  ```kotlin
  val lifecycleOwner = LocalLifecycleOwner.current
  DisposableEffect(lifecycleOwner) {
      val obs = LifecycleEventObserver { _, e ->
          if (e == Lifecycle.Event.ON_RESUME) viewModel.syncClipboard()
      }
      lifecycleOwner.lifecycle.addObserver(obs)
      onDispose { lifecycleOwner.lifecycle.removeObserver(obs) }
  }
  ```

  `SessionViewModel` 加：

  ```kotlin
  fun syncClipboard() { sessionManager.syncLocalClipboard() }
  ```

  画质面板点击后也调一次 `viewModel.syncClipboard()`。

- 验证：手机复制文字 → 切回 App → PC 端能粘贴出同样内容

---

# Phase 5：F5 断线自动重连

### 任务 5.1：重连策略（TDD）

- 文件路径：
  - 实现：`android-app/app/src/main/java/com/quickremote/app/services/ReconnectPolicy.kt`（新建）
  - 测试：`android-app/app/src/test/java/com/quickremote/app/services/ReconnectPolicyTest.kt`（新建）

- **第一步 —— 先写测试**：

  ```kotlin
  package com.quickremote.app.services

  import org.junit.Assert.assertEquals
  import org.junit.Assert.assertFalse
  import org.junit.Assert.assertNull
  import org.junit.Assert.assertTrue
  import org.junit.Test

  class ReconnectPolicyTest {

      @Test
      fun `退避序列为 1 2 4 8 15 15 15 15 秒`() {
          val expected = longArrayOf(1000, 2000, 4000, 8000, 15000, 15000, 15000, 15000)
          expected.forEachIndexed { i, ms ->
              assertEquals("第 $i 次", ms, ReconnectPolicy.delayFor(i))
          }
      }

      @Test
      fun `总等待时间约 75 秒`() {
          var sum = 0L
          for (i in 0 until ReconnectPolicy.MAX_ATTEMPTS) sum += ReconnectPolicy.delayFor(i)!!
          assertEquals(75_000L, sum)
      }

      @Test
      fun `超出上限返回 null`() {
          assertNull(ReconnectPolicy.delayFor(ReconnectPolicy.MAX_ATTEMPTS))
          assertNull(ReconnectPolicy.delayFor(8))
          assertNull(ReconnectPolicy.delayFor(-1))
      }

      @Test
      fun `网络与看门狗断开自动重连`() {
          assertTrue(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.Network))
          assertTrue(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.Watchdog))
      }

      @Test
      fun `用户主动断开与鉴权失败不自动重连`() {
          assertFalse(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.UserInitiated))
          assertFalse(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.AuthFailed))
          assertFalse(ReconnectPolicy.shouldAutoReconnect(ReconnectPolicy.Reason.ServerRejected))
      }
  }
  ```

- 验证（红）：`./gradlew :app:testDebugUnitTest --tests "*ReconnectPolicyTest*"` 编译失败

- **第二步 —— 实现**：

  ```kotlin
  package com.quickremote.app.services

  /** 断线自动重连策略：白名单判定 + 指数退避。 */
  object ReconnectPolicy {

      /** 断线原因。 */
      enum class Reason {
          /** 网络超时 / IO 异常 / 隧道断开。 */
          Network,
          /** 看门狗触发（30s 无数据或无视频帧）。 */
          Watchdog,
          /** 用户主动点断开。 */
          UserInitiated,
          /** 访问验证码错误或验证失败。 */
          AuthFailed,
          /** 服务器明确拒绝、设备不存在。 */
          ServerRejected
      }

      /** 1s → 2s → 4s → 8s → 15s × 4，合计 75 秒。 */
      private val DELAYS_MS = longArrayOf(1000, 2000, 4000, 8000, 15000, 15000, 15000, 15000)

      val MAX_ATTEMPTS: Int get() = DELAYS_MS.size

      /** 第 attempt 次重连（从 0 起）应等待的毫秒数；超出上限返回 null。 */
      fun delayFor(attempt: Int): Long? =
          if (attempt < 0 || attempt >= DELAYS_MS.size) null else DELAYS_MS[attempt]

      /** 该断线原因是否应触发自动重连。 */
      fun shouldAutoReconnect(reason: Reason): Boolean = when (reason) {
          Reason.Network, Reason.Watchdog -> true
          Reason.UserInitiated, Reason.AuthFailed, Reason.ServerRejected -> false
      }
  }
  ```

- 验证（绿）：`./gradlew :app:testDebugUnitTest --tests "*ReconnectPolicyTest*"` 全通过

### 任务 5.2：RemoteSessionManager 支持可重入重连

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/services/RemoteSessionManager.kt`
- 要做的：

1. **确认 `start()` 内所有会话级资源都是新建的**（帧队列、输出流、解码器、
   `ClipboardAssembler`、`lastAppliedClipHash`）。逐项核对，**发现复用就改成新建**。
   这是本任务的核心，历史 bug `b837eb1` 就出在这。

2. 暴露断线原因，供策略判定：

   ```kotlin
   var lastDisconnectReason: ReconnectPolicy.Reason = ReconnectPolicy.Reason.UserInitiated
       private set
   ```

   在下列位置赋值：
   - 看门狗 `fail(...)` 之前 → `Reason.Watchdog`
   - `disconnect()` 用户主动调用 → `Reason.UserInitiated`
   - 验证码失败路径 → `Reason.AuthFailed`
   - 其余 IO/网络异常 → `Reason.Network`

3. 加一个可重入入口（复用与 `start` 相同的参数，内部走全新会话初始化）：

   ```kotlin
   /** 重新连接上一次的设备（会话级资源全部重建）。 */
   suspend fun reconnect() {
       val d = lastDeviceId ?: return
       start(d, lastHostname, lastLanIp, qualityPercent, lastServerConfig!!)
   }
   ```

   > `lastDeviceId` / `lastHostname` / `lastLanIp` / `lastServerConfig` 需在 `start()` 开头记录。
   > 若字段已存在则复用，**不要重复定义**。

- 验证：编译通过；Phase 6 的飞行模式复测

### 任务 5.3：ViewModel 重连状态机

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/viewmodels/SessionViewModel.kt`
- 要做的：

1. 状态与计数：

   ```kotlin
   data class ReconnectState(val attempt: Int, val nextRetryAtMs: Long) {
       val displayAttempt: Int get() = attempt + 1
   }

   private val _reconnecting = MutableStateFlow<ReconnectState?>(null)
   val reconnecting: StateFlow<ReconnectState?> = _reconnecting.asStateFlow()

   private var reconnectJob: Job? = null
   ```

2. 会话状态变化时触发（在收集 `sessionManager.state` 的地方加分支）：

   ```kotlin
   fun onSessionEnded() {
       val reason = sessionManager.lastDisconnectReason
       if (!ReconnectPolicy.shouldAutoReconnect(reason)) {
           _reconnecting.value = null
           return
       }
       scheduleNextReconnect(0)
   }

   private fun scheduleNextReconnect(attempt: Int) {
       val delay = ReconnectPolicy.delayFor(attempt) ?: run {
           _reconnecting.value = null      // 到上限，停下手动模式
           _errorMessage.value = "重连失败（已尝试 ${ReconnectPolicy.MAX_ATTEMPTS} 次），请手动重试"
           return
       }
       _reconnecting.value = ReconnectState(attempt, System.currentTimeMillis() + delay)
       reconnectJob?.cancel()
       reconnectJob = viewModelScope.launch {
           delay(delay)
           withContext(Dispatchers.IO) { sessionManager.reconnect() }
           val ok = sessionManager.state == RemoteSessionManager.SessionState.CONNECTED
           if (ok) {
               _reconnecting.value = null
               // 副作用恢复：把当前画质档位重新下发给 PC
               sessionManager.setQualityPercent(_qualityPercent.value)
           } else {
               scheduleNextReconnect(attempt + 1)
           }
       }
   }

   /** 用户在重连浮层点「立即重试」：跳过等待直接重试。 */
   fun retryNow() {
       val cur = _reconnecting.value ?: return
       reconnectJob?.cancel()
       scheduleNextReconnect(cur.attempt)
   }

   /** 用户在重连浮层点「取消」：停止自动重连。 */
   fun cancelReconnect() {
       reconnectJob?.cancel()
       _reconnecting.value = null
   }
   ```

   `_qualityPercent` 来自任务 3.2（**若 Phase 3 未做，改用 `settingsStore.appSettings.first().qualityPercent`**）。

3. `disconnect()` 里先 `cancelReconnect()`，避免用户主动断开后又自动连回来。

- 验证：见 Phase 6 清单

### 任务 5.4：重连浮层 UI

- 文件路径：`android-app/app/src/main/java/com/quickremote/app/ui/screens/RemoteSessionScreen.kt`
- 要做的：在 `BoxWithConstraints` 内、画面**之上**叠一层遮罩。

**关键要求：保留最后一帧画面，不要黑屏。**
`RemoteDisplayView` 渲染的 Surface 会随断流变黑 —— 需在断开瞬间抓取最后画面。
最小可行做法（YAGNI，不做位图抓帧）：遮罩用**半透明**（`Color(0xCC0D1117)`），
露出底下残留画面，并在遮罩内明确告知状态。若后续确需定格画面，
再单独提需求做 `PixelCopy` 抓帧 —— **本批次不做**。

```kotlin
val reconnecting by viewModel.reconnecting.collectAsState()

reconnecting?.let { rs ->
    val remain by produceState(0L, rs) {
        while (true) {
            value = ((rs.nextRetryAtMs - System.currentTimeMillis()) / 1000).coerceAtLeast(0)
            delay(200)
        }
    }
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xCC0D1117)),
        contentAlignment = Alignment.Center
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            CircularProgressIndicator(color = Accent, strokeWidth = 3.dp)
            Spacer(Modifier.height(16.dp))
            Text(
                "第 ${rs.displayAttempt}/${ReconnectPolicy.MAX_ATTEMPTS} 次重连",
                style = MaterialTheme.typography.titleSmall,
                color = TextPrimary
            )
            Spacer(Modifier.height(6.dp))
            Text(
                if (remain > 0) "$remain 秒后重试" else "正在重连…",
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted
            )
            Spacer(Modifier.height(20.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(onClick = { viewModel.retryNow() }) { Text("立即重试") }
                OutlinedButton(onClick = { viewModel.cancelReconnect() }) { Text("取消") }
            }
        }
    }
}
```

- 验证：见 Phase 6 清单

---

# Phase 6：发版

### 任务 6.1：更新 CHANGELOG

- 文件路径：`CHANGELOG.md`（仓库根，位于 `QuickRemote/`）
- 要做的：在文件顶部插入两段（PC 条目**不带**「(PC客户端)」后缀）：

  ```markdown
  ## v1.1.63 (PC 客户端)
  - **手机配对**：设置中心「基本配置」新增「手机配对」，生成配对二维码与配置串，
    手机扫码即可完成服务器地址与密钥配置
  - **剪贴板同步**：远程会话中 PC 与手机剪贴板双向同步（纯文本）

  ## v1.0.70 (Android 客户端)
  - **手机配对**：支持扫码/粘贴配置串一键导入服务器地址与密钥（相机扫码经系统相机）
  - **剪贴板同步**：会话中与 PC 双向同步剪贴板文本
  - **会话内画质调节**：底部工具栏新增「画质」按钮，可在会话中直接切换
    流畅/标准/高清/原画 4 档，无需断开重连
  - **断线自动重连**：网络瞬断后自动重连（最多 8 次，约 75 秒），重连期间保留画面并显示倒计时，
    可「立即重试」或「取消」；主动断开与验证失败不触发自动重连
  - 移除设置页「音频」分区（音频重定向尚未实现，避免误导）
  ```

### 任务 6.2：同步 APK 内置更新记录

- 文件路径：`android-app/app/src/main/assets/changelog.txt`
- 要做的：把 v1.0.70 的全部条目追加到该文件**顶部**，格式与文件内既有条目保持一致
  （纯文本、无 `##` 前缀的话就跟随既有风格）。
  **该文件与 `CHANGELOG.md` 独立维护，必须手动同步 —— 历史上漏过多次。**

- 验证：安装 APK → 设置页「更新记录」弹窗顶部显示 v1.0.70

### 任务 6.3：版本号三处一致性

- 文件路径：
  - `android-app/app/build.gradle.kts`：`versionCode = 70`、`versionName = "1.0.70"`
  - `android-app/app/src/main/assets/` 下的 manifest / 更新检查源（按项目既有位置）
  - `pc-client/QuickRemote.PCClient.csproj`：`<Version>1.1.63</Version>`
- 要做的：三处改完交叉核对。Android 侧确认 `versionName` = manifest 记录 = 最终 APK 文件名。

- 验证：`grep -rn "1.0.70\|versionCode" android-app/app/build.gradle.kts`；
  APK 产物名称为 `QuickRemote-Android-v1.0.70.apk`

### 任务 6.4：构建与端到端验收

- 构建命令（按项目既有约定，**不要跑 `build-pcclient.ps1`** —— 其 `Remove-Item`
  会触发沙箱 safe-delete 中断）：

  ```bash
  # PC：先 bash 清理再发布
  cd pc-client && dotnet publish -c Release -r win-x64 --self-contained false
  # Android：
  cd android-app && ./gradlew :app:assembleRelease
  ```

  > 若 release 编译出现全文件 `Unresolved theme` 符号（BgCard 等）而 debug 通过，
  > 是 Kotlin 增量缓存损坏，用 `./gradlew :app:compileReleaseKotlin --rerun-tasks` 强制全量重编。
  > PC 打包必须包含 `pc-updater/bin/publish/update.exe`。

- **端到端验收清单（必须全部走真机 + 真实中继）**：

  **F1**
  - [ ] Android 设置页无「音频」分区

  **F2**
  - [ ] 系统相机扫 PC 二维码 → 拉起 App → Toast「已导入配置：xxx」
  - [ ] `adb shell am start -a android.intent.action.VIEW -d "<配对URL>"` 冷启动正确导入
  - [ ] App 在后台时重复上述命令（验证 `onNewIntent`）仍正确
  - [ ] 设置页「粘贴配置导入」用配置串导入成功
  - [ ] 粘贴乱码 → 提示失败，不崩溃
  - [ ] PSK 仍为 `change-me-please` 时弹窗出现橙色警告条
  - [ ] **跨端互通**：PC 生成的 URL 能被 Android 解析（这是唯一证明两端 codec 一致的检验）

  **F3**
  - [ ] 会话中切到「流畅」→ PC 日志出现 `Quality adjust request: 40%`
  - [ ] 切换过程不重连、不黑屏
  - [ ] 断开重连后档位保持上次选择

  **F4**
  - [ ] PC 复制中文 → 手机会话中粘贴内容一致
  - [ ] 手机复制 → 切回 App → PC 可粘贴
  - [ ] 复制 10 万字符 → 分片重组正确
  - [ ] 复制 30 万字符 → 日志出现 `too large to sync`，不崩溃
  - [ ] **无回环**：同一段文本不会两端反复互推（日志中每个分片组只出现一次）
  - [ ] 剪贴板含 emoji / 换行 / 双引号 → 同步后内容完全一致（JSON 转义正确）

  **F5**
  - [ ] 开关飞行模式 → 自动重连恢复，无需手动操作
  - [ ] 重连中显示「第 N/8 次重连 · X 秒后重试」
  - [ ] 点「立即重试」即刻发起
  - [ ] 点「取消」停止自动重连
  - [ ] 连续断网超上限 → 停下并提示手动重试，不无限重试
  - [ ] 验证码错误导致的断开**不**触发自动重连
  - [ ] **二次连接无异常**：断开→重连→再断开→再重连，第二次仍正常出画面
    （验证会话级资源确实重建 —— 历史 bug `b837eb1` 的回归检验）

  **回归**
  - [ ] 局域网直连与公网中继两条路径都能连
  - [ ] 访问验证码流程正常
  - [ ] 悬浮球 / 空白区触摸板 / 双指手势 / 缩放无回归
  - [ ] PC 端设置项保存、置顶、托盘菜单无回归

---

## 变更文件总览

| 端 | 新增 | 修改 |
|---|---|---|
| Android | `data/PairingPayload.kt`<br>`services/ClipboardChunker.kt`<br>`services/ReconnectPolicy.kt`<br>`src/test/**`（3 个测试类 + 冒烟） | `AndroidManifest.xml`<br>`MainActivity.kt`<br>`Models.kt`<br>`SettingsStore.kt`<br>`SettingsScreen.kt`<br>`RemoteSessionManager.kt`<br>`SessionViewModel.kt`<br>`RemoteSessionScreen.kt`<br>`build.gradle.kts`<br>`assets/changelog.txt` |
| PC | `Interop/PairingPayload.cs`<br>`Interop/ClipboardChunker.cs`<br>`Services/ClipboardSync.cs`<br>`Views/PairingWindow.xaml(.cs)`<br>`pc-client.Tests/**` | `Services/RemoteSessionManager.cs`<br>`Views/SettingsWindow.xaml(.cs)`<br>`QuickRemote.PCClient.csproj`<br>`CHANGELOG.md` |

## 未决事项（执行中如遇阻，回改本计划，不要悄悄绕过）

1. `dotnet test` 能否顺利引用 `WinExe` 工程 —— 若不行按任务 0.2 的退路（`Compile Include` 直接编源文件）。
2. QRCoder 二维码在深色主题下的配色重载是否存在 —— 先用默认黑白保证能扫。
3. `SettingsWindow.xaml` 中地址/密钥/设备名输入框的实际 `x:Name` —— 任务 2.8 前必须先读文件确认。
4. PC 端控制帧发送方法的实际名称 —— 任务 4.4 前必须先读文件确认。
5. Android `RemoteSessionManager` 中 `Context` 的实际来源字段名 —— 任务 4.5 前确认。

---

## 执行顺序建议

```
Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 5 → Phase 4 → Phase 6
                   (F1)      (F2)      (F3)      (F5)      (F4)      (发版)

交换 Phase 4 与 Phase 5 的理由：F5 重连会重建会话，若先做 F4，
剪贴板的分片组装器可能被重连逻辑误复用 —— 先把会话生命周期改对，
再往里加有状态的分片组装器，风险更低。
```
