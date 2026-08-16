#!/bin/bash

# QuickRemote Relay Server 部署脚本
# 支持: 安装 / 升级 / 修改配置 / 卸载
# 使用方式:
#   curl -fsSL -o install.sh <install-share-url> && sudo bash install.sh
#   或 curl -fsSL <install-share-url> | sudo bash
#
# 注意: quickdeploy 分享 URL 不含文件名，建议先下载到本地再执行
# manifest.json 的分享 URL 在发布时写入下方 MANIFEST_URL 变量

set -euo pipefail

# 如果 stdin 不是终端（如 curl | bash 模式），重定向到 /dev/tty 以支持交互输入。
# 某些环境下（sudo 包装、容器无 tty） /dev/tty 可能不可用，此时提示用户下载后运行。
if [ ! -t 0 ]; then
    if [ -e /dev/tty ] && [ -r /dev/tty ]; then
        exec </dev/tty
    else
        echo "===========================================================" >&2
        echo "错误：当前环境无法访问终端（/dev/tty 不可用）。" >&2
        echo "curl | bash 模式需要终端支持交互输入。" >&2
        echo "" >&2
        echo "请改用以下方式安装：" >&2
        echo "  curl -fsSL -o install.sh <install-share-url>" >&2
        echo "  sudo bash install.sh" >&2
        echo "===========================================================" >&2
        exit 1
    fi
fi

INSTALL_DIR="/opt/quickremote"
CONFIG_DIR="/etc/quickremote"
DATA_DIR="/var/lib/quickremote"
SERVICE_NAME="quickremote-relay"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
CONFIG_FILE="${CONFIG_DIR}/config.yaml"

# manifest.json 的固定分享 URL（通过 overwrite=true 保持不变）
MANIFEST_URL="https://quickdeploy.solutionx.top/d/p/f9af7eed-f7e6-4ffa-a645-0d7d1eaa15c3"

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m'

info()  { echo -e "${GREEN}[INFO]${NC} $1"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $1"; }
error() { echo -e "${RED}[ERROR]${NC} $1"; }
prompt(){ echo -ne "${BLUE}$1${NC}"; }

# 带默认值的交互式读取：输出用户输入或默认值
prompt_with_default() {
    local msg="$1" default="$2" input
    read -rp "${msg} [${default}]: " input
    echo "${input:-$default}"
}

# 隐藏输入的密钥读取（可选，回车则留空由调用方自动生成）。
# 注意：提示语与换行必须输出到 stderr（>&2），只有最终密钥输出到 stdout，
# 因为调用方用 var="$(prompt_secret ...)" 捕获 stdout，若换行也进 stdout，
# 变量会变成 "\n\nxxx" 而非 "xxx"。
prompt_secret() {
    local msg="$1" input
    read -rsp "${msg}" input
    echo >&2
    printf '%s' "$input"
}

# 检查 root 权限
check_root() {
    if [ "$(id -u)" -ne 0 ]; then
        error "此脚本需要 root 权限运行"
        echo "请使用: curl -fsSL -o install.sh <install-share-url> && sudo bash install.sh"
        exit 1
    fi
}

# 检查并安装 jq（用于解析 manifest.json）
ensure_jq() {
    if ! command -v jq &> /dev/null; then
        info "安装 jq..."
        if command -v apt-get &> /dev/null; then
            apt-get update -qq && apt-get install -y -qq jq
        elif command -v yum &> /dev/null; then
            yum install -y -q jq
        elif command -v apk &> /dev/null; then
            apk add --no-cache jq
        else
            error "无法安装 jq，请手动安装后重试"
            exit 1
        fi
    fi
}

# 检测系统架构
detect_arch() {
    local arch=$(uname -m)
    case "$arch" in
        aarch64|arm64) echo "arm64" ;;
        x86_64|amd64)  echo "amd64" ;;
        *) error "不支持的架构: $arch"; exit 1 ;;
    esac
}

# 获取服务器主 IP（用于默认展示）
get_server_ip() {
    local ip
    ip="$(hostname -I 2>/dev/null | awk '{print $1}')" || true
    if [ -z "$ip" ]; then
        ip="$(ip -4 addr show 2>/dev/null | grep -oP 'inet \K[\d.]+' | grep -v '^127\.' | head -1)" || true
    fi
    echo "${ip:-127.0.0.1}"
}

# 下载并解析 manifest.json
fetch_manifest() {
    local tmpfile=$(mktemp)
    if ! curl -fsSL -o "$tmpfile" "$MANIFEST_URL"; then
        error "无法下载版本清单 (manifest.json)"
        rm -f "$tmpfile"
        exit 1
    fi
    echo "$tmpfile"
}

# 从 manifest.json 获取最新版本号
get_latest_version() {
    jq -r '.["relay-server"].latest_version' "$1"
}

# 从 manifest.json 获取最新版本更新说明
get_latest_changelog() {
    jq -r '.["relay-server"].changelog // "无"' "$1"
}

# 获取指定版本的下载 URL
get_download_url() {
    local manifest_file=$1 version=$2 arch=$3
    jq -r --arg ver "$version" --arg arch "$arch" '.["relay-server"].versions[$ver][$arch]' "$manifest_file"
}

# 语义化版本比较：0 相等，1 表示 v1>v2，2 表示 v1<v2
compare_versions() {
    local a1 b1 c1 a2 b2 c2
    IFS=. read -r a1 b1 c1 <<< "$1"
    IFS=. read -r a2 b2 c2 <<< "$2"
    a1=${a1:-0}; b1=${b1:-0}; c1=${c1:-0}
    a2=${a2:-0}; b2=${b2:-0}; c2=${c2:-0}
    if [ "$a1" -gt "$a2" ]; then echo 1
    elif [ "$a1" -lt "$a2" ]; then echo 2
    elif [ "$b1" -gt "$b2" ]; then echo 1
    elif [ "$b1" -lt "$b2" ]; then echo 2
    elif [ "$c1" -gt "$c2" ]; then echo 1
    elif [ "$c1" -lt "$c2" ]; then echo 2
    else echo 0; fi
}

# 检查服务是否已安装
is_installed() {
    systemctl list-unit-files 2>/dev/null | grep -q "^${SERVICE_NAME}\.service" && return 0
    [ -f "$SERVICE_FILE" ] && return 0
    [ -x "${INSTALL_DIR}/quickremote-relay" ] && return 0
    return 1
}

# 获取当前安装版本
get_current_version() {
    if [ -f "$INSTALL_DIR/VERSION" ]; then
        cat "$INSTALL_DIR/VERSION"
    else
        echo "未知"
    fi
}

# 从 config.yaml 指定 block 下读取字段值
# 用法: read_yaml_block_field <file> <block> <key>
read_yaml_block_field() {
    local file="$1" block="$2" key="$3"
    awk -v b="$block" -v k="$key" '
        $0 == b ":" { in_block=1; next }
        in_block && /^[^ ]/ { in_block=0 }
        in_block && $0 ~ "^  " k ":" {
            sub(/^  [^:]*: */, "")
            gsub(/^"|"$/, "")
            print
            exit
        }
    ' "$file"
}

# 获取 HTTP 监听端口（去掉前缀冒号，缺省 8443）
get_listen_port() {
    local v
    v="$(read_yaml_block_field "$CONFIG_FILE" server listen | sed 's/.*://')"
    echo "${v:-8443}"
}

# 更新 config.yaml 指定 block 下的字段。
# 用 awk 避免 sed 对 $ & 等特殊字符的转义问题。
# 用法: update_yaml_block_field <file> <block> <key> <value>
update_yaml_block_field() {
    local file="$1" block="$2" key="$3" value="$4"
    awk -v b="$block" -v k="$key" -v v="$value" '
        $0 == b ":" { in_block=1; print; next }
        in_block && /^[^ ]/ { in_block=0 }
        in_block && $0 ~ "^  " k ":" {
            print "  " k ": \"" v "\""
            next
        }
        {print}
    ' "$file" > "${file}.tmp" && mv "${file}.tmp" "$file"
}

# 修复二进制在 SELinux 下的执行上下文。
# 在 RHEL/CentOS/Oracle/Rocky 9 等启用 SELinux enforcing 的系统上，/opt 下的二进制默认
# 被标记为 var_t/usr_t，systemd 从该上下文中执行会报：
#   "Failed at step EXEC spawning ...: Permission denied"（错误码 203/EXEC，服务反复重启）
# 需将 SELinux 类型改为 bin_t 才能被执行。非 SELinux 系统静默跳过。
fix_selinux_context() {
    local bin="${INSTALL_DIR}/quickremote-relay"
    [ -x "$bin" ] || return 0
    # 非 SELinux 系统（无 getenforce 且无 selinuxfs）直接跳过
    if ! command -v getenforce >/dev/null 2>&1 && [ ! -d /sys/fs/selinux ]; then
        return 0
    fi
    # SELinux 已禁用时无需处理
    if [ "$(getenforce 2>/dev/null || echo Disabled)" = "Disabled" ]; then
        return 0
    fi
    # 当前上下文已是 bin_t 则无需处理
    if ls -Z "$bin" 2>/dev/null | grep -q ':bin_t:'; then
        return 0
    fi
    echo ">>> 检测到 SELinux，修复二进制执行上下文（→ bin_t）..."
    if command -v semanage >/dev/null 2>&1; then
        semanage fcontext -a -t bin_t "$bin" 2>/dev/null || true
    fi
    if command -v restorecon >/dev/null 2>&1; then
        restorecon -v "$bin" >/dev/null 2>&1 || true
    fi
    # 修复后复核
    if ! ls -Z "$bin" 2>/dev/null | grep -q ':bin_t:'; then
        echo "    警告：SELinux 上下文修复可能未生效，若服务启动报 Permission denied 请手动执行：" >&2
        echo "    semanage fcontext -a -t bin_t ${bin}" >&2
        echo "    restorecon -v ${bin}" >&2
    fi
}

# 健康检查：等待 HTTP 服务就绪（最多 6 次，间隔 2 秒）。
# relay-server 根路径 / 返回关于页，可作就绪探测。
health_check() {
    local port="$1" max_retries=6 retry=0 code
    info "健康检查 http://127.0.0.1:${port}/ ..."
    while [ "$retry" -lt "$max_retries" ]; do
        code="$(curl -s -o /dev/null -w '%{http_code}' --connect-timeout 2 "http://127.0.0.1:${port}/" 2>/dev/null || echo 000)"
        if [ "$code" != "000" ]; then
            info "服务已就绪 (HTTP ${code})"
            return 0
        fi
        retry=$((retry+1))
        [ "$retry" -lt "$max_retries" ] && sleep 2
    done
    error "健康检查失败：服务未在 ${port} 端口正常响应"
    return 1
}

# 下载二进制到指定路径
download_binary() {
    local version=$1 arch=$2 manifest_file=$3 dest=$4
    local download_url
    download_url=$(get_download_url "$manifest_file" "$version" "$arch")
    if [ -z "$download_url" ] || [ "$download_url" = "null" ]; then
        error "无法找到版本 $version ($arch) 的下载 URL"
        return 1
    fi
    info "下载版本: $version ($arch)"
    if ! curl -fSL -o "$dest" "$download_url"; then
        error "下载失败: $download_url"
        return 1
    fi
    chmod +x "$dest"
    return 0
}

# 安装
do_install() {
    info "开始安装 QuickRemote Relay Server"

    local arch=$(detect_arch)
    info "检测到架构: $arch"

    ensure_jq

    local manifest_file latest_version
    manifest_file=$(fetch_manifest)
    latest_version=$(get_latest_version "$manifest_file")
    info "最新版本: $latest_version"
    info "更新说明: $(get_latest_changelog "$manifest_file")"

    local listen_port
    listen_port=$(prompt_with_default "请输入监听端口" "8443")

    local pre_shared_key
    pre_shared_key="$(prompt_secret "请输入预共享密钥 [回车自动生成]: ")"
    if [ -z "$pre_shared_key" ]; then
        pre_shared_key=$(openssl rand -hex 16)
        info "已生成预共享密钥"
    fi

    local jwt_secret
    jwt_secret="$(prompt_secret "请输入JWT密钥 [回车自动生成]: ")"
    if [ -z "$jwt_secret" ]; then
        jwt_secret=$(openssl rand -hex 16)
        info "已生成JWT密钥"
    fi

    local quickdeploy_url
    prompt "请输入quickdeploy地址 (可选) []: "
    read -r quickdeploy_url

    # 创建目录
    mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$DATA_DIR"

    # 下载二进制
    if ! download_binary "$latest_version" "$arch" "$manifest_file" "$INSTALL_DIR/quickremote-relay"; then
        rm -f "$manifest_file"
        exit 1
    fi
    echo "$latest_version" > "$INSTALL_DIR/VERSION"
    rm -f "$manifest_file"

    # 创建配置文件
    cat > "$CONFIG_FILE" <<EOF
server:
  listen: ":$listen_port"
  tunnel_listen: ":8445"
auth:
  pre_shared_key: "$pre_shared_key"
  jwt_secret: "$jwt_secret"
storage:
  sqlite_path: "$DATA_DIR/registry.db"
quickdeploy:
  base_url: "$quickdeploy_url"
EOF

    # 创建 systemd 服务
    cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=QuickRemote Relay Server
After=network.target

[Service]
Type=simple
ExecStart=$INSTALL_DIR/quickremote-relay $CONFIG_DIR/config.yaml
Restart=always
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

    # 修复 SELinux 上下文（关键：CentOS/RHEL 上二进制默认不可执行）
    fix_selinux_context

    systemctl daemon-reload
    systemctl enable $SERVICE_NAME
    systemctl start $SERVICE_NAME

    if health_check "$listen_port"; then
        info "安装完成!"
    else
        error "服务已启动但健康检查失败，请查看日志: journalctl -u $SERVICE_NAME -f"
    fi
    info "服务器地址: http://$(get_server_ip):$listen_port"
    info "建议在 nginx 反向代理层配置 TLS 证书以启用 HTTPS"
    info "预共享密钥: $pre_shared_key"
    info "配置文件: $CONFIG_FILE"
    info "管理命令: sudo systemctl {start|stop|restart|status} $SERVICE_NAME"
}

# 已安装时在进入操作菜单前自动检查是否有可用更新（仅提示，不自动更新）。
# 网络不可用或版本源不可达时静默跳过，不阻塞脚本运行。
check_update_notice() {
    local current_version latest_version cmp manifest_file changelog
    current_version="$(get_current_version)"
    [ "$current_version" = "未知" ] && return 0
    manifest_file="$(fetch_manifest 2>/dev/null || true)"
    [ -z "$manifest_file" ] && return 0
    latest_version="$(get_latest_version "$manifest_file")"
    changelog="$(get_latest_changelog "$manifest_file")"
    rm -f "$manifest_file"
    cmp="$(compare_versions "$current_version" "$latest_version")"
    if [ "$cmp" = 2 ]; then
        echo ""
        echo "=============================================================="
        echo "  有新版本可用：${current_version} → ${latest_version}"
        echo "=============================================================="
        echo "【更新说明】"
        echo "    · ${changelog}"
        echo "=============================================================="
        echo "  可进入菜单选择「1) 升级到最新版本」。"
        echo "=============================================================="
        echo ""
    fi
}

# 升级
do_upgrade() {
    ensure_jq

    local manifest_file latest_version current_version arch tmp_binary cmp
    current_version=$(get_current_version)
    manifest_file=$(fetch_manifest)
    latest_version=$(get_latest_version "$manifest_file")

    info "当前版本: $current_version"
    info "最新版本: $latest_version"
    info "更新说明: $(get_latest_changelog "$manifest_file")"

    # 语义化版本比较：当前版本未知时按需升级处理
    if [ "$current_version" = "未知" ]; then
        cmp=2
    else
        cmp=$(compare_versions "$current_version" "$latest_version")
    fi
    if [ "$cmp" = 0 ]; then
        info "已是最新版本 ($current_version)"
        rm -f "$manifest_file"
        return
    fi
    if [ "$cmp" = 1 ]; then
        info "当前版本 ($current_version) 高于最新版本 ($latest_version)，无需升级"
        rm -f "$manifest_file"
        return
    fi

    prompt "确认升级? [y/N]: "
    read -r confirm
    if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
        info "已取消升级"
        rm -f "$manifest_file"
        return
    fi

    arch=$(detect_arch)

    # 先下载到临时文件，成功后再停服替换。
    # 下载失败则服务保持运行，不做任何变更，避免不必要的停机。
    tmp_binary=$(mktemp "${INSTALL_DIR}/.quickremote.new.XXXXXX")
    if ! download_binary "$latest_version" "$arch" "$manifest_file" "$tmp_binary"; then
        rm -f "$tmp_binary" "$manifest_file"
        error "下载失败，版本未变更，服务保持运行"
        exit 1
    fi
    rm -f "$manifest_file"

    systemctl stop $SERVICE_NAME

    # 备份现有二进制（用于失败回滚）
    if [ -f "$INSTALL_DIR/quickremote-relay" ]; then
        info "备份现有二进制"
        cp "$INSTALL_DIR/quickremote-relay" "$INSTALL_DIR/quickremote-relay.bak"
    fi

    mv "$tmp_binary" "$INSTALL_DIR/quickremote-relay"
    echo "$latest_version" > "$INSTALL_DIR/VERSION"
    fix_selinux_context

    systemctl start $SERVICE_NAME

    if health_check "$(get_listen_port)"; then
        info "升级完成: $current_version -> $latest_version"
        rm -f "$INSTALL_DIR/quickremote-relay.bak"
    else
        # 失败回滚到旧版本
        error "健康检查失败，正在回滚到 $current_version"
        systemctl stop $SERVICE_NAME
        if [ -f "$INSTALL_DIR/quickremote-relay.bak" ]; then
            mv "$INSTALL_DIR/quickremote-relay.bak" "$INSTALL_DIR/quickremote-relay"
        fi
        echo "$current_version" > "$INSTALL_DIR/VERSION"
        fix_selinux_context
        systemctl start $SERVICE_NAME
        info "已回滚到 $current_version"
    fi
}

# 修改配置
do_config() {
    if [ ! -f "$CONFIG_FILE" ]; then
        error "配置文件不存在: $CONFIG_FILE"
        return
    fi

    info "当前配置:"
    cat "$CONFIG_FILE"
    echo ""

    local listen_port tunnel_port pre_shared_key jwt_secret base_url upload_token

    listen_port=$(prompt_with_default "新的监听端口" "$(get_listen_port)")
    tunnel_port=$(prompt_with_default "新的隧道数据端口" "$(read_yaml_block_field "$CONFIG_FILE" server tunnel_listen | sed 's/.*://')")
    prompt "新的预共享密钥 (回车保持不变): "
    read -r pre_shared_key
    prompt "新的JWT密钥 (回车保持不变): "
    read -r jwt_secret
    base_url=$(prompt_with_default "新的quickdeploy地址" "$(read_yaml_block_field "$CONFIG_FILE" quickdeploy base_url)")
    prompt "新的上传令牌 (回车保持不变): "
    read -r upload_token

    update_yaml_block_field "$CONFIG_FILE" server listen ":$listen_port"
    update_yaml_block_field "$CONFIG_FILE" server tunnel_listen ":$tunnel_port"
    [ -n "$pre_shared_key" ] && update_yaml_block_field "$CONFIG_FILE" auth pre_shared_key "$pre_shared_key"
    [ -n "$jwt_secret" ]       && update_yaml_block_field "$CONFIG_FILE" auth jwt_secret "$jwt_secret"
    update_yaml_block_field "$CONFIG_FILE" quickdeploy base_url "$base_url"
    [ -n "$upload_token" ]     && update_yaml_block_field "$CONFIG_FILE" quickdeploy upload_token "$upload_token"

    info "新配置:"
    cat "$CONFIG_FILE"

    prompt "重启服务使配置生效? [Y/n]: "
    read -r restart_confirm
    if [ "$restart_confirm" != "n" ] && [ "$restart_confirm" != "N" ]; then
        systemctl restart $SERVICE_NAME
        info "服务已重启"
    fi
}

# 卸载
do_uninstall() {
    warn "即将卸载 QuickRemote Relay Server"
    warn "这将删除: 二进制文件、配置文件、数据文件"

    prompt "确认卸载? 输入 'yes' 确认: "
    read -r confirm
    if [ "$confirm" != "yes" ]; then
        info "已取消卸载"
        return
    fi

    systemctl stop $SERVICE_NAME 2>/dev/null || true
    systemctl disable $SERVICE_NAME 2>/dev/null || true
    rm -f "$SERVICE_FILE"
    systemctl daemon-reload

    rm -rf "$INSTALL_DIR" "$CONFIG_DIR" "$DATA_DIR"

    info "卸载完成"
}

# 主入口
main() {
    check_root

    echo ""
    echo "=========================================="
    echo "  QuickRemote Relay Server 部署工具"
    echo "=========================================="
    echo ""

    if is_installed; then
        local version=$(get_current_version)
        info "QuickRemote 已安装 (当前版本: $version)"
        echo ""
        # 进入菜单前自动检查更新，有更新则提示（网络不可用时静默跳过）
        check_update_notice
        echo "请选择操作:"
        echo "  1) 升级到最新版本"
        echo "  2) 修改配置"
        echo "  3) 卸载"
        echo ""
        prompt "请输入选项序号 [1-3]: "
        read -r choice

        case "$choice" in
            1) do_upgrade ;;
            2) do_config ;;
            3) do_uninstall ;;
            *) error "无效选项"; exit 1 ;;
        esac
    else
        do_install
    fi

    echo ""
}

main