package api

import (
	"bytes"
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
	"github.com/quickremote/relay-server/internal/web"
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
}

// New 创建一个新的 API 处理器。
func New(authService *auth.Service, reg *registry.Registry, tunnelMgr *tunnel.Manager, tunnelListener *tunnel.TunnelListener, controlSrv *control.Server, quickDeployURL string, uploadToken string) *Handler {
	return &Handler{
		authService:    authService,
		registry:       reg,
		tunnelMgr:      tunnelMgr,
		tunnelListener: tunnelListener,
		controlSrv:     controlSrv,
		quickDeployURL: quickDeployURL,
		uploadToken:    uploadToken,
	}
}

// Routes 返回 HTTP 路由器。
// 注意：Task 7 阶段 HandleAbout 内联返回简单 HTML，
// Task 9 的 web 模块会接管 "/" 和 "/about" 路由。
func (h *Handler) Routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/auth", h.HandleAuth)
	mux.HandleFunc("/api/devices", h.requireAuth(h.HandleGetDevices))
	mux.HandleFunc("/api/tunnel/request", h.requireAuth(h.HandleTunnelRequest))
	mux.HandleFunc("/api/logs/upload", h.HandleUploadLogs)
	mux.HandleFunc("/api/changelog", h.HandleChangelog)
	mux.HandleFunc("/about", web.AboutHandler)
	mux.HandleFunc("/", web.AboutHandler)
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

// HandleGetDevices 返回在线设备列表。
func (h *Handler) HandleGetDevices(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		http.Error(w, `{"error":"method_not_allowed"}`, http.StatusMethodNotAllowed)
		return
	}

	devices, err := h.registry.ListOnline()
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

// HandleAbout 返回关于页面。
// Task 7 阶段内联返回简单 HTML，Task 9 的 web 模块会接管此路由。
func (h *Handler) HandleAbout(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	html := `<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <title>QuickRemote</title>
</head>
<body>
    <h1>QuickRemote Relay Server</h1>
    <p>About page placeholder.</p>
</body>
</html>`
	w.Write([]byte(html))
}
