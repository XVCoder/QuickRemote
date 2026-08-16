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
