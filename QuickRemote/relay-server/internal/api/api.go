package api

import (
	"bytes"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"mime/multipart"
	"net/http"
	"strings"
	"time"

	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/control"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
)

// Handler 是 HTTP API 的处理器集合。
type Handler struct {
	authService    *auth.Service
	registry       *registry.Registry
	tunnelMgr      *tunnel.Manager
	tunnelListener *tunnel.TunnelListener
	controlSrv     *control.Server
	quickDeployURL string
	uploadToken    string
	// adminPassword 是管理员模式密码（v1.0.9）。来自 config.yaml 的 admin.password：
	// 全新安装由 install.sh 随机生成 6 位数字（或用户自定义），存量部署回填默认 88888888。
	adminPassword string
}

// New 创建一个新的 API 处理器。
func New(authService *auth.Service, reg *registry.Registry, tunnelMgr *tunnel.Manager, tunnelListener *tunnel.TunnelListener, controlSrv *control.Server, quickDeployURL string, uploadToken string, adminPassword string) *Handler {
	return &Handler{
		authService:    authService,
		registry:       reg,
		tunnelMgr:      tunnelMgr,
		tunnelListener: tunnelListener,
		controlSrv:     controlSrv,
		quickDeployURL: quickDeployURL,
		uploadToken:    uploadToken,
		adminPassword:  adminPassword,
	}
}

// Routes 返回 HTTP 路由器。
func (h *Handler) Routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/auth", h.HandleAuth)
	mux.HandleFunc("/api/devices", h.requireAuth(h.HandleGetDevices))
	mux.HandleFunc("/api/tunnel/request", h.requireAuth(h.HandleTunnelRequest))
	mux.HandleFunc("/api/logs/upload", h.HandleUploadLogs)
	mux.HandleFunc("/api/changelog", h.HandleChangelog)
	// 管理员模式（v1.0.9）：密码校验 + 设备物理删除。仍需 JWT（requireAuth），
	// 密码是第二道门——JWT 只证明"是本服务的合法客户端"，不证明"是管理员"。
	mux.HandleFunc("/api/admin/verify", h.requireAuth(h.HandleAdminVerify))
	mux.HandleFunc("/api/admin/device/delete", h.requireAuth(h.HandleAdminDeleteDevice))
	mux.HandleFunc("/", h.HandleHealth)
	return mux
}

func (h *Handler) requireAuth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		authHeader := r.Header.Get("Authorization")
		if !strings.HasPrefix(authHeader, "Bearer ") {
			http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
			return
		}
		token := strings.TrimPrefix(authHeader, "Bearer ")
		_, err := h.authService.ValidateToken(token)
		if err != nil {
			http.Error(w, `{"error":"invalid_token"}`, http.StatusUnauthorized)
			return
		}
		next(w, r)
	}
}

// HandleAdminVerify 校验管理员密码。
//
// 客户端（PC/安卓）点「管理员模式」后弹窗收密码，调此接口；通过后在本地维持
// 管理员态（进程内有效，不落盘 token）——服务端不签发管理员 token 是有意的：
// 管理员操作频次极低，多一个长期有效的凭证反而多一份泄露面。
func (h *Handler) HandleAdminVerify(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		Password string `json:"password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"bad_request"}`, http.StatusBadRequest)
		return
	}

	if !h.verifyAdminPassword(req.Password) {
		// 不区分"密码错"与"未配置"，避免给爆破试探提供信息
		http.Error(w, `{"error":"invalid_password"}`, http.StatusUnauthorized)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{"ok": true})
}

// HandleAdminDeleteDevice 物理删除设备（管理员模式）。
//
// 与客户端本地软删除的区别：这里把 devices 记录真正删掉，并把 machine_id 记入
// deleted_devices 黑名单——该设备下次用旧 ID 注册会被拒（register_ack.status=device_deleted），
// 必须重新生成设备 ID 才能再次上线。
func (h *Handler) HandleAdminDeleteDevice(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		Password string `json:"password"`
		DeviceID string `json:"device_id"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"bad_request"}`, http.StatusBadRequest)
		return
	}
	if req.DeviceID == "" {
		http.Error(w, `{"error":"device_id_required"}`, http.StatusBadRequest)
		return
	}
	if !h.verifyAdminPassword(req.Password) {
		http.Error(w, `{"error":"invalid_password"}`, http.StatusUnauthorized)
		return
	}

	if err := h.registry.DeleteDevice(req.DeviceID); err != nil {
		log.Printf("admin delete device %s failed: %v", req.DeviceID, err)
		http.Error(w, `{"error":"delete_failed"}`, http.StatusInternalServerError)
		return
	}

	log.Printf("admin deleted device %s permanently", req.DeviceID)
	// 设备记录已变：立即向所有在线 PC 广播最新列表，管理员界面不用等下一个 30s 周期
	if h.controlSrv != nil {
		h.controlSrv.BroadcastDeviceList()
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{"ok": true})
}

// verifyAdminPassword 常量时间比较，避免按字节逐个比较泄露前缀长度信息。
func (h *Handler) verifyAdminPassword(password string) bool {
	if h.adminPassword == "" || password == "" {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(password), []byte(h.adminPassword)) == 1
}

// HandleAuth 处理认证请求，验证预共享密钥并返回 JWT。
func (h *Handler) HandleAuth(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		PreSharedKey string `json:"pre_shared_key"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"bad_request"}`, http.StatusBadRequest)
		return
	}

	if !h.authService.VerifyPreSharedKey(req.PreSharedKey) {
		http.Error(w, `{"error":"invalid_key"}`, http.StatusUnauthorized)
		return
	}

	token, err := h.authService.GenerateToken("app-client")
	if err != nil {
		http.Error(w, `{"error":"token_generation_failed"}`, http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"token":   token,
		"expires": 3600,
	})
}

// HandleGetDevices 返回设备列表。
//
// 默认只返回在线设备（历史行为，旧客户端不带参数时行为不变）；
// 带 ?all=1 时返回全部设备（含离线），供需要展示离线主机的新客户端使用。
//
// 为什么用可选参数而不是直接改默认行为：Android 旧版本不认离线设备
// （会显示成一批点不动的僵尸主机），必须保持零破坏升级。
func (h *Handler) HandleGetDevices(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var (
		devices []registry.Device
		err     error
	)
	if r.URL.Query().Get("all") == "1" {
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

// HandleTunnelRequest 处理 App 发起的隧道请求。
func (h *Handler) HandleTunnelRequest(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		DeviceID string `json:"device_id"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"bad_request"}`, http.StatusBadRequest)
		return
	}

	// 验证设备存在且在线
	dev, err := h.registry.GetByDeviceID(req.DeviceID)
	if err != nil || dev.Status != "online" {
		http.Error(w, `{"error":"device_offline"}`, http.StatusServiceUnavailable)
		return
	}

	// 创建隧道会话
	session, err := h.tunnelMgr.CreateSession(req.DeviceID)
	if err != nil {
		http.Error(w, `{"error":"tunnel_creation_failed"}`, http.StatusInternalServerError)
		return
	}

	// 将会话注册到隧道监听器，等待 PC 与 App 建立数据连接
	tunnelPort := 0
	if h.tunnelListener != nil {
		h.tunnelListener.RegisterSession(session.ID, session)
		tunnelPort = h.tunnelListener.Port()
	}

	// 通知 PC 建立数据连接
	if h.controlSrv != nil {
		if err := h.controlSrv.NotifyTunnelRequest(req.DeviceID, session.ID, tunnelPort); err != nil {
			log.Printf("notify tunnel request failed: %v", err)
		}
	}

	host := r.Host
	if host == "" {
		host = "localhost"
	}
	// 去掉端口号
	if idx := strings.LastIndex(host, ":"); idx > 0 {
		host = host[:idx]
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"session_id":  session.ID,
		"tunnel_host": host,
		"tunnel_port": tunnelPort,
	})
}

// HandleUploadLogs 接收客户端上传的日志，并转发到 quickdeploy 的 user_logs 目录。
func (h *Handler) HandleUploadLogs(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	var req struct {
		ClientType string `json:"client_type"`
		DeviceID   string `json:"device_id"`
		Logs       string `json:"logs"`
		Level      string `json:"level"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, `{"error":"bad_request"}`, http.StatusBadRequest)
		return
	}

	log.Printf("logs received: client=%s device=%s level=%s size=%d",
		req.ClientType, req.DeviceID, req.Level, len(req.Logs))

	// 转发到 quickdeploy（未配置 URL 或令牌时仅记录到服务器日志）
	if h.quickDeployURL != "" && h.uploadToken != "" {
		if err := h.uploadLogToQuickDeploy(req); err != nil {
			log.Printf("upload logs to quickdeploy failed: %v", err)
		}
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"status": "ok",
	})
}

// uploadLogToQuickDeploy 把日志内容上传到 quickdeploy 的 user_logs 目录。
func (h *Handler) uploadLogToQuickDeploy(req struct {
	ClientType string `json:"client_type"`
	DeviceID   string `json:"device_id"`
	Logs       string `json:"logs"`
	Level      string `json:"level"`
}) error {
	// 解码日志内容（客户端为 base64 编码）
	content, err := base64.StdEncoding.DecodeString(req.Logs)
	if err != nil {
		content = []byte(req.Logs)
	}

	// 文件名：client_device_时间戳.log，避免同名冲突
	deviceID := strings.TrimSpace(req.DeviceID)
	if deviceID == "" {
		deviceID = "unknown"
	}
	clientType := strings.TrimSpace(req.ClientType)
	if clientType == "" {
		clientType = "client"
	}
	filename := fmt.Sprintf("%s_%s_%s.log", clientType, deviceID, time.Now().Format("20060102_150405"))

	uploadURL := strings.TrimRight(h.quickDeployURL, "/") + "/api/upload/" + h.uploadToken

	var buf bytes.Buffer
	mw := multipart.NewWriter(&buf)
	fw, err := mw.CreateFormFile("file", filename)
	if err != nil {
		return err
	}
	if _, err := fw.Write(content); err != nil {
		return err
	}
	if err := mw.Close(); err != nil {
		return err
	}

	httpReq, err := http.NewRequest(http.MethodPost, uploadURL, &buf)
	if err != nil {
		return err
	}
	httpReq.Header.Set("Content-Type", mw.FormDataContentType())

	client := &http.Client{Timeout: 30 * time.Second}
	resp, err := client.Do(httpReq)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("quickdeploy returned %d: %s", resp.StatusCode, string(body))
	}
	log.Printf("logs uploaded to quickdeploy: %s (%s)", filename, strings.TrimSpace(string(body)))
	return nil
}

// HandleChangelog 返回更新记录。
// TODO: 当 quickDeployURL 配置时从 quickdeploy 拉取。
func (h *Handler) HandleChangelog(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"changelogs": []interface{}{},
	})
}

// HandleHealth 返回健康检查响应，供就绪探测使用（根路径 /）。
func (h *Handler) HandleHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	w.Write([]byte("ok"))
}
