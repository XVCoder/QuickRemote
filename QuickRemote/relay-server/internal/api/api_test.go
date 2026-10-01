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
	handler := New(authService, reg, tunnelMgr, nil, nil, "", "", "admin-pw")

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

// ===== 管理员模式（v1.0.9）=====

func TestAdminVerify(t *testing.T) {
	handler, _, cleanup := setupTestAPI(t)
	defer cleanup()

	// 错误密码
	body, _ := json.Marshal(map[string]string{"password": "nope"})
	req := httptest.NewRequest("POST", "/api/admin/verify", bytes.NewReader(body))
	w := httptest.NewRecorder()
	handler.HandleAdminVerify(w, req)
	if w.Code != http.StatusUnauthorized {
		t.Errorf("wrong password: expected 401, got %d", w.Code)
	}

	// 空密码（配置缺失时绝不放行）
	body, _ = json.Marshal(map[string]string{"password": ""})
	req = httptest.NewRequest("POST", "/api/admin/verify", bytes.NewReader(body))
	w = httptest.NewRecorder()
	handler.HandleAdminVerify(w, req)
	if w.Code != http.StatusUnauthorized {
		t.Errorf("empty password: expected 401, got %d", w.Code)
	}

	// 正确密码（setupTestAPI 注入的是 admin-pw）
	body, _ = json.Marshal(map[string]string{"password": "admin-pw"})
	req = httptest.NewRequest("POST", "/api/admin/verify", bytes.NewReader(body))
	w = httptest.NewRecorder()
	handler.HandleAdminVerify(w, req)
	if w.Code != http.StatusOK {
		t.Errorf("correct password: expected 200, got %d", w.Code)
	}
}

func TestAdminDeleteDevice(t *testing.T) {
	handler, reg, cleanup := setupTestAPI(t)
	defer cleanup()

	dev := &registry.Device{MachineID: "m-1", Hostname: "PC-1", OS: "Windows 11", RDPPort: 3389}
	deviceID, _, err := reg.Register(dev)
	if err != nil {
		t.Fatalf("register: %v", err)
	}

	// 密码错 → 拒绝且设备仍在
	body, _ := json.Marshal(map[string]string{"password": "bad", "device_id": deviceID})
	req := httptest.NewRequest("POST", "/api/admin/device/delete", bytes.NewReader(body))
	w := httptest.NewRecorder()
	handler.HandleAdminDeleteDevice(w, req)
	if w.Code != http.StatusUnauthorized {
		t.Errorf("expected 401, got %d", w.Code)
	}
	if _, err := reg.GetByDeviceID(deviceID); err != nil {
		t.Errorf("device should survive failed delete: %v", err)
	}

	// 密码正确 → 物理删除 + machine_id 进黑名单
	body, _ = json.Marshal(map[string]string{"password": "admin-pw", "device_id": deviceID})
	req = httptest.NewRequest("POST", "/api/admin/device/delete", bytes.NewReader(body))
	w = httptest.NewRecorder()
	handler.HandleAdminDeleteDevice(w, req)
	if w.Code != http.StatusOK {
		t.Errorf("expected 200, got %d", w.Code)
	}
	if _, err := reg.GetByDeviceID(deviceID); err == nil {
		t.Error("device record should be gone after physical delete")
	}
	deleted, err := reg.IsDeleted("m-1")
	if err != nil || !deleted {
		t.Errorf("machine_id should be in deleted blacklist: deleted=%v err=%v", deleted, err)
	}

	// 换新 ID 注册成功后清除旧 ID 黑名单
	if err := reg.ClearDeleted("m-1"); err != nil {
		t.Fatalf("clear deleted: %v", err)
	}
	deleted, _ = reg.IsDeleted("m-1")
	if deleted {
		t.Error("blacklist entry should be cleared")
	}
}
