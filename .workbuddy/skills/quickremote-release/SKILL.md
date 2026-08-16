---
name: quickremote-release
description: QuickRemote 项目一键打包发布：编译 relay-server/pc-client/android-app 三端产物，通过 quickdeploy MCP（文件服务）上传到对应子目录，通过 qdrl MCP（托管平台）部署 about 页面，更新 manifest.json 和 CHANGELOG.md，清理旧版本只保留最近3个。Invoke when user asks to build and publish QuickRemote releases, upload new versions, or manage release packages.
agent_created: true
---

# QuickRemote Release 打包发布工具

本 skill 封装 QuickRemote 项目三端组件的编译、上传、版本管理完整流程。

## 两个 MCP 的分工（关键）

发布流程涉及**两个独立的 MCP**，职责不同，切勿混用：

| MCP | 端点 | 用途 | 工具前缀 |
|-----|------|------|---------|
| `quickdeploy` | `https://quickdeploy.solutionx.top/mcp` | **文件分享服务**：三端产物、manifest.json、CHANGELOG.md、install.sh 的上传/列表/删除 | `mcp__quickdeploy__*` |
| `qdrl` | `https://qd.solutionx.top/mcp` | **托管应用平台**：about 页面的部署/升级/状态查询 | `mcp__qdrl__*` |

- **三端产物**（APK / ZIP / relay 二进制）+ manifest + changelog → 走 `quickdeploy`（文件服务），上传域名 `https://quickdeploy.solutionx.top/api/upload/{token}`
- **about 页面**（托管应用 `quickremote-about`）→ 走 `qdrl`（托管平台），上传域名 `https://qd.solutionx.top/api/upload/{token}`
- 两个 MCP 是**不同的账号空间**，目录 ID 互不通用（quickdeploy 的 quickremote 与 qdrl 的 quickremote 不是同一个）

## MCP 工具命名（WorkBuddy 约定）

| 操作 | quickdeploy（文件） | qdrl（托管） |
|------|------|------|
| 创建上传令牌 | `mcp__quickdeploy__create_upload_token` | `mcp__qdrl__create_upload_token` |
| 列出目录 | `mcp__quickdeploy__list_files` | `mcp__qdrl__list_files` |
| 删除文件/目录 | `mcp__quickdeploy__delete_file` | `mcp__qdrl__delete_file` |
| 列出分享 | `mcp__quickdeploy__list_shares` | `mcp__qdrl__list_shares` |
| 部署托管应用 | — | `mcp__qdrl__deploy_app` |
| 升级托管应用 | — | `mcp__qdrl__upgrade_app` |
| 查询应用状态 | — | `mcp__qdrl__get_app_status` |
| 列出应用 | — | `mcp__qdrl__list_apps` |
| 上传文件（base64，小文件） | `mcp__quickdeploy__upload_file` | `mcp__qdrl__upload_file` |

## 项目结构

```
QuickRemote/
├── relay-server/     # Go 中转服务器
├── pc-client/        # C#/.NET WPF PC客户端
├── android-app/      # Kotlin Android 应用
├── manifest.json     # 版本清单（各组件版本号+下载链接）
└── CHANGELOG.md      # 更新记录

QuickRemote-about/    # 关于页面（独立托管应用，发布时必须同步更新下载链接，见步骤 4.5）
```

## 目录结构（两个 MCP 各自的 quickremote 目录）

### quickdeploy（文件服务）的 quickremote 目录

三端产物和清单文件都在这里（已核对，目录 ID 正确）：

| 目录 | 目录 ID | 存放内容 |
|---------|---------|---------|
| `quickremote` (根) | `5bc66dc8-a607-4384-93a7-1158bf43aed3` | manifest.json, CHANGELOG.md, install.sh |
| `quickremote/pc-client` | `70fe2927-9101-4aa8-9f7a-f5b4d344ee48` | PC客户端 ZIP 包 |
| `quickremote/relay-server` | `299b53f5-3472-47fc-963d-3ec0a66d6184` | 中转服务器二进制 |
| `quickremote/android-app` | `5a9ded9b-f935-4a3c-86cd-0532462c3d25` | Android APK |

> 当前已上传版本（与本地 manifest.json 一致）：relay-server 1.0.1/1.0.2/1.0.3（各 amd64+arm64）、pc-client 1.1.2/1.1.3/1.1.4、android-app 1.0.11/1.0.12/1.0.13，均保留最近 3 个。

### qdrl（托管平台）的 quickremote 目录

about 页面的 tar.gz 包在这里：

| 目录 | 目录 ID | 存放内容 |
|---------|---------|---------|
| `quickremote` (根) | `5b681a68-ff94-4a14-bc89-8aab6a200a53` | QuickRemote-about-*.tar.gz（about 页部署包） |

> ⚠️ 注意：`qdrl` 的 `quickremote` 根目录 ID（`5b681a68...`）与 `quickdeploy` 的（`5bc66dc8...`）**不同**，上传 about 包时务必用 qdrl 的 ID。

## 发布流程

### 步骤 1：确认版本号

从各项目的配置文件读取当前版本号：

- **relay-server**: `relay-server/cmd/server/main.go` 中的 `var Version = "x.x.x"`
- **pc-client**: `pc-client/QuickRemote.PCClient.csproj` 中的 `<Version>x.x.x</Version>`
- **android-app**: `android-app/app/build.gradle.kts` 中的 `versionName = "x.x.x"` 和 `versionCode = N`

如果用户要发布新版本，先更新这些版本号（三端可以独立版本号）。

#### ⚠️ 版本号一致性校验（强制）

**Android 端保存在三处，必须完全一致，否则虽然 APK 文件名带版本号，但安装后显示的却是旧版本号（如 0.1.0）：**

1. `android-app/app/build.gradle.kts` 的 `versionName`（安装后显示的实际版本号）
2. `manifest.json` 中 `android-app.latest_version`
3. APK 文件名 `QuickRemote-Android-vX.X.X.apk`

**准则：**
- 发布前必须确认三处版本号一致；`versionName` 是唯一真实来源，文件名和 manifest 必须与它对齐
- 每次发布递增 `versionCode`（Android 官方要求新版本 versionCode 必须大于旧版本，否则无法覆盖安装）
- 上传前用 aapt 验证 APK 内版本号：
  ```powershell
  $aapt = Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\build-tools\" -Recurse -Filter "aapt.exe" | Sort-Object FullName -Descending | Select-Object -First 1
  & $aapt.FullName dump badging "app\build\outputs\apk\debug\app-debug.apk" | Select-String "package:|versionName|versionCode"
  ```
  确认输出为 `versionCode='N' versionName='X.X.X'` 后再上传。

**PC Client 端：**
- 版本号统一从 `csproj` 的 `<Version>` 读取，通过程序集自动读取，不要硬编码字符串
- 若用 `build-pcclient.ps1` 打包，脚本会自动从 csproj 读取，无需手动改

**relay-server 端：**
- 版本号在 `main.go` 的 `var Version`，通过 `-X main.Version=` 注入编译产物

### 步骤 2：编译三端产物

#### 2.1 Relay Server（Go 交叉编译）

在 `relay-server/` 目录下执行，生成 amd64 和 arm64 两个 Linux 二进制：

```bash
# Windows 上用 PowerShell 执行：
$version = "1.0.3"  # 替换为实际版本号
$env:CGO_ENABLED = "0"

# amd64
$env:GOOS = "linux"; $env:GOARCH = "amd64"
go build -ldflags="-s -w -X main.Version=$version" -o "dist/quickremote-relay-v$version-amd64" ./cmd/server

# arm64
$env:GOARCH = "arm64"
go build -ldflags="-s -w -X main.Version=$version" -o "dist/quickremote-relay-v$version-arm64" ./cmd/server
```

#### 2.2 PC Client（.NET 发布 + ZIP 打包）

执行已有的打包脚本：

```powershell
powershell -ExecutionPolicy Bypass -File "pc-client\build-pcclient.ps1"
```

脚本会自动从 csproj 读取版本号，执行 `dotnet publish`，生成扁平 ZIP 包到项目根目录：`QuickRemote-PCClient-v{version}.zip`

ZIP 包结构（扁平，无顶层目录）：
- `QuickRemote.PCClient.exe`
- `QuickRemote.PCClient.dll`
- `QuickRemote.PCClient.deps.json`
- `QuickRemote.PCClient.runtimeconfig.json`
- `appsettings.json`

#### 2.3 Android App（Gradle 构建）

在 `android-app/` 目录下执行：

```powershell
.\gradlew.bat assembleRelease
```

产出 APK 位于：`app\build\outputs\apk\release\app-release.apk`

重命名为：`QuickRemote-Android-v{version}.apk`

> **⚠️ 签名（强制）**：`build.gradle.kts` 的 release buildType 已配置 `signingConfigs.release`（复用 `~/.android/debug.keystore`，密码 `android` / alias `androiddebugkey`），产物是**已签名、可安装**的 APK。**禁止再上传未签名的 `app-release-unsigned.apk`**——未签名 APK 安装时系统解析包信息失败（用户侧表现为 "package info is null" / 解析包时出现问题）。上传前必须用 apksigner 验证：
>   ```powershell
>   & "$env:LOCALAPPDATA\Android\Sdk\build-tools\36.0.0\apksigner.bat" verify --print-certs "app-release.apk"
>   # 预期输出 Signer #1 certificate DN: ... CN=Android Debug；出现 DOES NOT VERIFY / Missing META-INF 即为未签名
>   ```

> 注意：Android 构建需要 Android SDK 和 JDK 17。如果环境未配置，跳过此组件并告知用户。

### 步骤 3：通过 quickdeploy（文件服务）上传三端产物

#### 3.1 上传组件包到对应子目录

对每个组件，使用 `mcp__quickdeploy__create_upload_token` 创建上传令牌（指定正确的 `target_dir_id`），然后用 `curl.exe` 上传：

**关键：`target_dir_id` 必须是对应子目录的 ID，不是根目录 ID。**

```
# relay-server 二进制（两个架构分别上传）
target_dir_id = "299b53f5-3472-47fc-963d-3ec0a66d6184"  # relay-server 目录
permanent_share = true
allow_overwrite = true
allowed_extensions = ""  # 二进制无扩展名

# pc-client ZIP
target_dir_id = "70fe2927-9101-4aa8-9f7a-f5b4d344ee48"  # pc-client 目录
permanent_share = true
allow_overwrite = true
allowed_extensions = "zip"

# android-app APK
target_dir_id = "5a9ded9b-f935-4a3c-86cd-0532462c3d25"  # android-app 目录
permanent_share = true
allow_overwrite = true
allowed_extensions = "apk"
```

上传命令（注意用 `curl.exe` 不是 `curl`，避免 PowerShell 别名冲突；域名是 quickdeploy.solutionx.top）：

```powershell
curl.exe -X POST "https://quickdeploy.solutionx.top/api/upload/{token}" -F "file=@{文件路径}"
```

记录每个文件返回的 `share_url`。

> 重要：PowerShell 中不要用 `&&` 链接命令，用 `;` 分隔。

#### 3.2 上传 manifest.json 和 CHANGELOG.md 到根目录

manifest.json 和 CHANGELOG.md 上传到 quickdeploy 的根目录（`5bc66dc8-a607-4384-93a7-1158bf43aed3`），使用 `overwrite=true` 保持分享链接不变。

### 步骤 4：更新 manifest.json

在 `manifest.json` 中为每个组件：
1. 更新 `latest_version` 为新版本号
2. 更新 `changelog` 为本次更新说明
3. 在 `versions` 对象最前面添加新版本条目，包含下载链接
4. **删除超出3个的旧版本条目**（只保留最近3个版本）

manifest.json 结构：

```json
{
  "relay-server": {
    "latest_version": "1.0.3",
    "changelog": "更新说明",
    "versions": {
      "1.0.3": { "amd64": "url", "arm64": "url" },
      "1.0.2": { "amd64": "url", "arm64": "url" },
      "1.0.1": { "amd64": "url", "arm64": "url" }
    }
  },
  "pc-client": {
    "latest_version": "1.1.4",
    "changelog": "更新说明",
    "versions": {
      "1.1.4": { "zip": "url" },
      "1.1.3": { "zip": "url" },
      "1.1.2": { "zip": "url" }
    }
  },
  "android-app": {
    "latest_version": "1.0.13",
    "changelog": "更新说明",
    "versions": {
      "1.0.13": { "apk": "url" },
      "1.0.12": { "apk": "url" },
      "1.0.11": { "apk": "url" }
    }
  }
}
```

### 步骤 4.5：同步更新 about 页面下载链接（强制，走 qdrl）

每次发布新版本（特别是 PC 客户端 / Android App），**必须**同步更新关于页面上的下载链接与版本号，否则用户从 about 页面下载到的仍是旧版本。

about 页面是独立项目 `QuickRemote-about/`（托管应用 `app_id=quickremote-about`，托管在 **qdrl** 平台），位于项目根目录同级：

```
<项目根>/
├── QuickRemote/         # 三端源码
└── QuickRemote-about/   # 关于页面（静态页 + node server）
    ├── public/index.html   # 页面本体（含下载链接）
    ├── server.js
    └── package.json
```

#### 4.5.1 更新 index.html 中的下载链接

编辑 `QuickRemote-about/public/index.html`，搜索 `v{旧版本号}` 和旧的 `quickdeploy.solutionx.top/d/p/...` 链接，同步替换为本次发布的**新版本号**与**新的下载 URL**（来自步骤 3 上传返回的 `share_url`）。需要更新的位置：

1. **下载卡片**（`.dl-card`）：`PC 客户端`（`.dl-version` + `下载 ZIP` 按钮 href）与 `Android App`（`.dl-version` + `下载 APK` 按钮 href）
2. **快速上手** 部分：`PC 注册` / `下载安装 APK` 步骤里的版本号和链接
3. **PC 客户端（Windows）** 部分：`点击下载 PC 客户端 vX.X.X`（ZIP）链接
4. **Android App** 部分：`点击下载 Android App vX.X.X`（APK）链接

> 用 Grep 搜 `quickdeploy.solutionx.top/d/p/` 与 `v[0-9]` 找出所有待更新点，逐处替换，避免遗漏。

#### 4.5.2 递增 about 页面版本号

编辑 `QuickRemote-about/package.json`，将 `version` 递增（如 `1.0.7` → `1.0.8`）。

#### 4.5.3 打包并升级 about 页面应用（用 qdrl）

1. 用 `tar.gz` 打包源码（**不含 node_modules**，平台会自动 `npm install --omit=dev`）：
   - 内容：`public/`、`server.js`、`package.json`
   - 文件名：`QuickRemote-about-v{version}.tar.gz`（旧包保留在 `QuickRemote-about/` 目录留档）
2. 通过 `mcp__qdrl__create_upload_token` 创建上传令牌（`target_dir_id` = **qdrl 的 quickremote 根目录 `5b681a68-ff94-4a14-bc89-8aab6a200a53`**，`allowed_extensions="tar.gz"`、`permanent_share=true`、`allow_overwrite=true`），用 `curl.exe -X POST "https://qd.solutionx.top/api/upload/{token}" -F "file=@<路径>"` 上传，拿到 `file_id`
3. 部署/升级托管应用：
   - 若应用**首次部署**：调用 `mcp__qdrl__deploy_app`，参数 `app_id="quickremote-about"`、`version={新版本号}`（与 package.json 一致）、`package_content="file://<file_id>"`、`auto_start=true`
   - 若应用**已存在**：调用 `mcp__qdrl__upgrade_app`（参数类似 deploy_app，蓝绿部署）
4. 调用 `mcp__qdrl__get_app_status`（`app_id="quickremote-about"`）确认新版本已运行，并用浏览器访问 about 页面验证下载链接可点、版本号已更新

> ⚠️ 注意：qdrl 平台的 `list_apps` 当前返回空，说明 `quickremote-about` 托管应用尚未在 qdrl 下部署（或曾部署在旧 key）。首次发布 about 页时需用 `deploy_app` 重新创建。若只改了关于页文案/链接而未发布三端新版本，同样需要走本流程部署新版本。

### 步骤 5：更新 CHANGELOG.md

在 `CHANGELOG.md` 最顶部（`# QuickRemote 更新记录` 标题之后）添加新版本条目：

```markdown
## vX.X.X (组件名)

- 更新内容1
- 更新内容2
```

### 步骤 6：清理旧版本（只保留最近3个，走 quickdeploy）

对 quickdeploy 文件服务的每个子目录执行清理：

1. 调用 `mcp__quickdeploy__list_files` 列出子目录内容
2. 按版本号排序，识别超出3个的旧版本文件
3. 对每个要删除的文件，调用 `mcp__quickdeploy__delete_file` 删除
4. 同时从 manifest.json 的 `versions` 中移除对应条目

**清理规则**：
- 每个子目录只保留最近3个版本的包文件
- manifest.json 中每个组件的 `versions` 只保留最近3个版本记录
- 被删除版本的下载链接将不再可用，确保 manifest.json 中没有残留引用

### 步骤 7：上传更新后的 manifest.json 和 CHANGELOG.md（走 quickdeploy）

使用 `mcp__quickdeploy__create_upload_token`（target_dir_id 为 quickdeploy 根目录 `5bc66dc8-a607-4384-93a7-1158bf43aed3`）创建令牌，然后 `curl.exe` 上传覆盖。

## MCP 工具速查

| 操作 | 工具 | 关键参数 |
|------|------|---------|
| 列出目录 | `mcp__quickdeploy__list_files` / `mcp__qdrl__list_files` | `dir_id`（可选，默认根目录） |
| 创建上传令牌 | `mcp__quickdeploy__create_upload_token` / `mcp__qdrl__create_upload_token` | `target_dir_id`, `permanent_share=true`, `allow_overwrite=true`, `allowed_extensions` |
| 上传文件 | `curl.exe` | `curl.exe -X POST "url" -F "file=@path"` |
| 上传文件（小文件备选） | `mcp__quickdeploy__upload_file` / `mcp__qdrl__upload_file` | `file_name`, `file_content`(base64), `parent_dir_id`, `permanent_share` |
| 删除文件 | `mcp__quickdeploy__delete_file` | `file_id` |
| 列出分享 | `mcp__quickdeploy__list_shares` / `mcp__qdrl__list_shares` | 无参数 |
| 部署托管应用 | `mcp__qdrl__deploy_app` | `app_id`, `version`, `package_content`, `auto_start` |
| 升级托管应用 | `mcp__qdrl__upgrade_app` | `app_id`, `version`, `package_content` |
| 查询应用状态 | `mcp__qdrl__get_app_status` | `app_id` |
| 列出应用 | `mcp__qdrl__list_apps` | 无参数 |

## 注意事项

1. **两个 MCP 勿混用**：三端产物+manifest 走 `quickdeploy`（quickdeploy.solutionx.top），about 页托管走 `qdrl`（qd.solutionx.top）。两者目录 ID 独立，upload URL 域名不同。
2. **curl.exe vs curl**：PowerShell 中 `curl` 是 `Invoke-WebRequest` 的别名，必须用 `curl.exe` 执行真正的 curl 命令
3. **命令分隔符**：PowerShell 不支持 `&&`，用 `;` 分隔命令
4. **PC客户端 ZIP 结构**：必须是扁平结构（无顶层目录），否则自动更新解压后文件路径错误
5. **manifest.json 和 CHANGELOG.md**：上传到 quickdeploy 的根目录（`5bc66dc8...`），不是子目录
6. **版本号一致性（易踩坑）**：Android 安装后显示的是 `build.gradle.kts` 的 `versionName`，不是文件名！曾出现 APK 文件名带 1.0.2 但安装显示 0.1.0 的问题。发布前必须核对 `versionName` / manifest.json / 文件名三处一致，并递增 `versionCode`（详见步骤 1 的校验章节）
7. **relay-server 二进制无扩展名**：上传时 `allowed_extensions` 留空或不传
8. **上传验证**：APK 上传成功后可再用 `aapt dump badging` 复核线上版本号是否正确
9. **about 页面同步（易遗漏）**：发布新版本后必须更新 `QuickRemote-about/public/index.html` 的版本号与下载链接，并升级 `quickremote-about` 托管应用（qdrl），否则用户从 about 页面下载到的还是旧版本（详见步骤 4.5）
10. **about 应用未部署（当前状态）**：qdrl 的 `list_apps` 当前为空，首次发布需用 `deploy_app` 重建 `quickremote-about`
11. **大文件用 curl 直传**：APK/ZIP 体积大（50MB+），不要用 `upload_file`（base64 内联会超大）；统一走 `create_upload_token` + `curl.exe`。若沙箱拦截 curl 网络访问（如 exit 23），小文件（manifest.json / CHANGELOG.md / install.sh）可改用 `upload_file`，大文件需在非沙箱环境执行 curl 或请求放行网络
