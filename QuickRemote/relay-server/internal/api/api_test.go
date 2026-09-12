package api

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
)

func setupTestAPI(t *testing.T) (*Handler, *registry.Registry, func()) {
	t.Helper()
	authService := auth.New("test-jwt-secret", "test-secret", time.Hour)
	reg, err := registry.New(t.TempDir() + "/test.db")
	if err != nil {
		t.Fatalf("create registry: %v", err)
	}
	tunnelMgr := tunnel.NewManager()
	handler := New(authService, reg, tunnelMgr, nil, nil, "", "")

	cleanup := func() {
		reg.Close()
	}
	return handler, reg, cleanup
}

func TestAuth(t *testing.T) {
	handler, _, cleanup := setupTestAPI(t)
	defer cleanup()

	// 正确的密钥：sha256("test-secret")
	hash := sha256.Sum256([]byte("test-secret"))
	body, _ := json.Marshal(map[string]string{
		"pre_shared_key": hex.EncodeToString(hash[:]),
	})

	// 错误的密钥
	badBody, _ := json.Marshal(map[string]string{
		"pre_shared_key": "wrong",
	})

	req := httptest.NewRequest("POST", "/api/auth", bytes.NewReader(badBody))
	w := httptest.NewRecorder()
	handler.HandleAuth(w, req)
	if w.Code != http.StatusUnauthorized {
		t.Errorf("expected 401, got %d", w.Code)
	}

	// 正确的密钥
	req = httptest.NewRequest("POST", "/api/auth", bytes.NewReader(body))
	w = httptest.NewRecorder()
	handler.HandleAuth(w, req)
	if w.Code != http.StatusOK {
		t.Errorf("expected 200, got %d", w.Code)
	}

	var resp map[string]interface{}
	json.NewDecoder(w.Body).Decode(&resp)
	if resp["token"] == nil {
		t.Error("expected token in response")
	}
}

func TestGetDevices(t *testing.T) {
	handler, reg, cleanup := setupTestAPI(t)
	defer cleanup()

	// 注册一个设备
	reg.Register(&registry.Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	})

	// 生成 token
	token, _ := handler.authService.GenerateToken("test-device")

	req := httptest.NewRequest("GET", "/api/devices", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	w := httptest.NewRecorder()
	handler.HandleGetDevices(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected 200, got %d", w.Code)
	}

	var resp struct {
		Devices []registry.Device `json:"devices"`
	}
	json.NewDecoder(w.Body).Decode(&resp)
	if len(resp.Devices) != 1 {
		t.Errorf("expected 1 device, got %d", len(resp.Devices))
	}
}

func TestGetDevices_Unauthorized(t *testing.T) {
	handler, _, cleanup := setupTestAPI(t)
	defer cleanup()

	// 通过 requireAuth 中间件调用，验证未携带 token 时返回 401
	req := httptest.NewRequest("GET", "/api/devices", nil)
	w := httptest.NewRecorder()
	handler.requireAuth(handler.HandleGetDevices)(w, req)
	if w.Code != http.StatusUnauthorized {
		t.Errorf("expected 401, got %d", w.Code)
	}
}

func TestUploadLogs(t *testing.T) {
	handler, _, cleanup := setupTestAPI(t)
	defer cleanup()

	body, _ := json.Marshal(map[string]string{
		"client_type": "pc",
		"device_id":   "m1",
		"logs":        "base64encodedlogs",
		"level":       "error",
	})

	req := httptest.NewRequest("POST", "/api/logs/upload", bytes.NewReader(body))
	w := httptest.NewRecorder()
	handler.HandleUploadLogs(w, req)

	if w.Code != http.StatusOK {
		t.Errorf("expected 200, got %d", w.Code)
	}
}

// TestGetDevices_All 覆盖 ?all=1 参数：
// 不带参数时只返回在线设备（旧客户端行为，回归保护）；
// 带 all=1 时返回在线 + 离线，且在线排在前。
func TestGetDevices_All(t *testing.T) {
	handler, reg, cleanup := setupTestAPI(t)
	defer cleanup()

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

	// 默认（不带参数）：只返回在线设备
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
	if err := json.NewDecoder(w.Body).Decode(&only); err != nil {
		t.Fatalf("default: decode response: %v", err)
	}
	if len(only.Devices) != 1 || only.Devices[0].DeviceID != onlineID {
		t.Fatalf("default: expected only online device %s, got %+v", onlineID, only.Devices)
	}

	// all=1：返回在线 + 离线
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
	if err := json.NewDecoder(w.Body).Decode(&all); err != nil {
		t.Fatalf("all=1: decode response: %v", err)
	}
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
