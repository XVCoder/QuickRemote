# Relay Server 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 QuickRemote 中转服务器，负责设备注册管理、TCP隧道桥接、HTTP API 和关于页面。

**Architecture:** Go 单二进制服务，内嵌 SQLite 存储，TLS 加密通信。PC 通过持久 TLS 控制连接注册和心跳，安卓 App 通过 HTTP API 获取设备列表和请求隧道。隧道采用每会话独立 TCP 连接，服务器双向 io.Copy 桥接。

**Tech Stack:** Go 1.22+, modernc.org/sqlite (纯Go SQLite), golang-jwt/jwt/v5, gopkg.in/yaml.v3, Go embed

---

## 文件结构

```
relay-server/
├── cmd/server/main.go                  # 入口，启动HTTP和控制连接监听
├── internal/
│   ├── config/config.go                # 配置结构和YAML加载
│   ├── config/config_test.go
│   ├── auth/auth.go                    # 预共享密钥验证和JWT生成
│   ├── auth/auth_test.go
│   ├── registry/registry.go            # 设备注册表（SQLite CRUD）
│   ├── registry/registry_test.go
│   ├── tunnel/tunnel.go                # 隧道会话管理和TCP桥接
│   ├── tunnel/tunnel_test.go
│   ├── api/api.go                      # HTTP API 处理器
│   ├── api/api_test.go
│   ├── control/control.go              # PC控制连接处理（JSON帧协议）
│   ├── control/control_test.go
│   └── web/web.go                      # 关于页面（embed静态资源）
│   └── web/static/about.html           # 关于页面HTML
├── deploy/
│   ├── install.sh                      # 交互式部署脚本
│   ├── Dockerfile                      # 多架构构建
│   └── docker-compose.yml
├── config.example.yaml
├── go.mod
└── go.sum
```

---

### Task 1: 项目初始化

**Files:**
- Create: `relay-server/go.mod`
- Create: `relay-server/cmd/server/main.go`
- Create: `relay-server/config.example.yaml`

- [ ] **Step 1: 初始化 Go module**

Run:
```bash
cd relay-server
go mod init github.com/quickremote/relay-server
```

- [ ] **Step 2: 添加依赖**

Run:
```bash
go get modernc.org/sqlite
go get github.com/golang-jwt/jwt/v5
go get gopkg.in/yaml.v3
```

- [ ] **Step 3: 创建最小入口**

`cmd/server/main.go`:
```go
package main

import (
	"fmt"
	"os"
)

const Version = "0.1.0"

func main() {
	fmt.Printf("QuickRemote Relay Server v%s\n", Version)
	os.Exit(0)
}
```

- [ ] **Step 4: 创建配置示例**

`config.example.yaml`:
```yaml
server:
  listen: ":8443"
  tls:
    cert: ""
    key: ""
auth:
  pre_shared_key: "change-me-please"
  jwt_secret: "jwt-signing-secret"
storage:
  sqlite_path: "./registry.db"
quickdeploy:
  base_url: ""
```

- [ ] **Step 5: 验证编译运行**

Run: `go build -o quickremote-relay ./cmd/server && ./quickremote-relay`
Expected: 输出 `QuickRemote Relay Server v0.1.0`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: initialize relay-server project structure"
```

---

### Task 2: 配置模块

**Files:**
- Create: `relay-server/internal/config/config.go`
- Create: `relay-server/internal/config/config_test.go`

- [ ] **Step 1: 编写配置加载测试**

`internal/config/config_test.go`:
```go
package config

import (
	"os"
	"testing"
)

func TestLoad(t *testing.T) {
	yamlContent := `
server:
  listen: ":9090"
  tls:
    cert: "/path/cert.pem"
    key: "/path/key.pem"
auth:
  pre_shared_key: "test-key"
  jwt_secret: "test-jwt-secret"
storage:
  sqlite_path: "./test.db"
quickdeploy:
  base_url: "https://deploy.example.com"
`
	tmpFile := t.TempDir() + "/config.yaml"
	os.WriteFile(tmpFile, []byte(yamlContent), 0644)

	cfg, err := Load(tmpFile)
	if err != nil {
		t.Fatalf("Load failed: %v", err)
	}
	if cfg.Server.Listen != ":9090" {
		t.Errorf("expected listen ':9090', got '%s'", cfg.Server.Listen)
	}
	if cfg.Auth.PreSharedKey != "test-key" {
		t.Errorf("expected key 'test-key', got '%s'", cfg.Auth.PreSharedKey)
	}
	if cfg.QuickDeploy.BaseURL != "https://deploy.example.com" {
		t.Errorf("expected quickdeploy url, got '%s'", cfg.QuickDeploy.BaseURL)
	}
}

func TestLoadDefaults(t *testing.T) {
	yamlContent := `
server:
  listen: ":8443"
auth:
  pre_shared_key: "key"
  jwt_secret: "secret"
`
	tmpFile := t.TempDir() + "/config.yaml"
	os.WriteFile(tmpFile, []byte(yamlContent), 0644)

	cfg, err := Load(tmpFile)
	if err != nil {
		t.Fatalf("Load failed: %v", err)
	}
	if cfg.Storage.SQLitePath != "./registry.db" {
		t.Errorf("expected default sqlite path, got '%s'", cfg.Storage.SQLitePath)
	}
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/config/ -v`
Expected: FAIL (config.go 不存在)

- [ ] **Step 3: 实现配置加载**

`internal/config/config.go`:
```go
package config

import (
	"os"

	"gopkg.in/yaml.v3"
)

type Config struct {
	Server      ServerConfig      `yaml:"server"`
	Auth        AuthConfig        `yaml:"auth"`
	Storage     StorageConfig     `yaml:"storage"`
	QuickDeploy QuickDeployConfig `yaml:"quickdeploy"`
}

type ServerConfig struct {
	Listen string     `yaml:"listen"`
	TLS    TLSConfig  `yaml:"tls"`
}

type TLSConfig struct {
	Cert string `yaml:"cert"`
	Key  string `yaml:"key"`
}

type AuthConfig struct {
	PreSharedKey string `yaml:"pre_shared_key"`
	JWTSecret    string `yaml:"jwt_secret"`
}

type StorageConfig struct {
	SQLitePath string `yaml:"sqlite_path"`
}

type QuickDeployConfig struct {
	BaseURL string `yaml:"base_url"`
}

func Load(path string) (*Config, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	cfg := &Config{
		Storage: StorageConfig{SQLitePath: "./registry.db"},
	}
	if err := yaml.Unmarshal(data, cfg); err != nil {
		return nil, err
	}
	return cfg, nil
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `go test ./internal/config/ -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add internal/config/
git commit -m "feat: add config module with YAML loading"
```

---

### Task 3: 认证模块

**Files:**
- Create: `relay-server/internal/auth/auth.go`
- Create: `relay-server/internal/auth/auth_test.go`

- [ ] **Step 1: 编写认证测试**

`internal/auth/auth_test.go`:
```go
package auth

import (
	"crypto/sha256"
	"encoding/hex"
	"testing"
	"time"
)

func TestVerifyPreSharedKey(t *testing.T) {
	secret := "my-secret-key"
	hash := sha256.Sum256([]byte(secret))
	hashHex := hex.EncodeToString(hash[:])

	if !VerifyPreSharedKey(hashHex, secret) {
		t.Error("expected verification to succeed")
	}
	if VerifyPreSharedKey("wrong-hash", secret) {
		t.Error("expected verification to fail with wrong hash")
	}
}

func TestGenerateToken(t *testing.T) {
	service := New("jwt-secret", "my-secret-key", time.Hour)
	token, err := service.GenerateToken("device-123")
	if err != nil {
		t.Fatalf("GenerateToken failed: %v", err)
	}
	if token == "" {
		t.Error("expected non-empty token")
	}

	claims, err := service.ValidateToken(token)
	if err != nil {
		t.Fatalf("ValidateToken failed: %v", err)
	}
	if claims.DeviceID != "device-123" {
		t.Errorf("expected device ID 'device-123', got '%s'", claims.DeviceID)
	}
}

func TestValidateToken_Invalid(t *testing.T) {
	service := New("jwt-secret", "my-secret-key", time.Hour)
	_, err := service.ValidateToken("invalid-token")
	if err == nil {
		t.Error("expected error for invalid token")
	}
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/auth/ -v`
Expected: FAIL

- [ ] **Step 3: 实现认证模块**

`internal/auth/auth.go`:
```go
package auth

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

type Claims struct {
	DeviceID string `json:"device_id"`
	jwt.RegisteredClaims
}

type Service struct {
	jwtSecret    string
	preSharedKey string
	tokenTTL     time.Duration
}

func New(jwtSecret, preSharedKey string, tokenTTL time.Duration) *Service {
	return &Service{
		jwtSecret:    jwtSecret,
		preSharedKey: preSharedKey,
		tokenTTL:     tokenTTL,
	}
}

func VerifyPreSharedKey(hashHex, secret string) bool {
	hash := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(hash[:]) == hashHex
}

func (s *Service) VerifyPreSharedKey(hashHex string) bool {
	return VerifyPreSharedKey(hashHex, s.preSharedKey)
}

func (s *Service) GenerateToken(deviceID string) (string, error) {
	claims := Claims{
		DeviceID: deviceID,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(time.Now().Add(s.tokenTTL)),
			IssuedAt:  jwt.NewNumericDate(time.Now()),
		},
	}
	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	return token.SignedString([]byte(s.jwtSecret))
}

func (s *Service) ValidateToken(tokenString string) (*Claims, error) {
	claims := &Claims{}
	token, err := jwt.ParseWithClaims(tokenString, claims, func(token *jwt.Token) (interface{}, error) {
		if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, errors.New("unexpected signing method")
		}
		return []byte(s.jwtSecret), nil
	})
	if err != nil {
		return nil, err
	}
	if !token.Valid {
		return nil, errors.New("invalid token")
	}
	return claims, nil
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `go test ./internal/auth/ -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add internal/auth/
git commit -m "feat: add auth module with pre-shared key and JWT"
```

---

### Task 4: 设备注册表模块

**Files:**
- Create: `relay-server/internal/registry/registry.go`
- Create: `relay-server/internal/registry/registry_test.go`

- [ ] **Step 1: 编写注册表测试**

`internal/registry/registry_test.go`:
```go
package registry

import (
	"testing"
	"time"
)

func TestRegisterAndGetDevice(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	dev := &Device{
		MachineID: "machine-001",
		Hostname:  "PC-OFFICE",
		OS:        "Windows 11 Pro",
		RDPPort:   3389,
		Version:   "1.0.0",
	}

	deviceID, err := reg.Register(dev)
	if err != nil {
		t.Fatalf("Register failed: %v", err)
	}
	if deviceID == "" {
		t.Error("expected non-empty device ID")
	}

	// 同一台机器再次注册应该更新而非新增
	deviceID2, err := reg.Register(dev)
	if err != nil {
		t.Fatalf("Re-register failed: %v", err)
	}
	if deviceID2 != deviceID {
		t.Errorf("expected same device ID on re-register, got '%s' vs '%s'", deviceID2, deviceID)
	}
}

func TestListOnlineDevices(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	reg.Register(&Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	})
	reg.Register(&Device{
		MachineID: "m2", Hostname: "PC2", OS: "Win10", RDPPort: 3389, Version: "1.0.0",
	})

	devices, err := reg.ListOnline()
	if err != nil {
		t.Fatalf("ListOnline failed: %v", err)
	}
	if len(devices) != 2 {
		t.Errorf("expected 2 devices, got %d", len(devices))
	}
}

func TestMarkOffline(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	dev := &Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	}
	deviceID, _ := reg.Register(dev)

	err := reg.MarkOffline(deviceID)
	if err != nil {
		t.Fatalf("MarkOffline failed: %v", err)
	}

	devices, _ := reg.ListOnline()
	if len(devices) != 0 {
		t.Errorf("expected 0 online devices, got %d", len(devices))
	}
}

func TestUpdateHeartbeat(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	dev := &Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	}
	deviceID, _ := reg.Register(dev)

	oldTime := time.Now()
	time.Sleep(10 * time.Millisecond)
	reg.UpdateHeartbeat(deviceID)

	devices, _ := reg.ListOnline()
	if len(devices) != 1 {
		t.Fatalf("expected 1 online device")
	}
	if !devices[0].LastSeen.After(oldTime) {
		t.Error("expected LastSeen to be updated")
	}
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/registry/ -v`
Expected: FAIL

- [ ] **Step 3: 实现注册表**

`internal/registry/registry.go`:
```go
package registry

import (
	"database/sql"
	"fmt"
	"time"

	_ "modernc.org/sqlite"
)

type Device struct {
	DeviceID  string    `json:"device_id"`
	MachineID string    `json:"machine_id"`
	Hostname  string    `json:"hostname"`
	OS        string    `json:"os"`
	RDPPort   int       `json:"rdp_port"`
	Version   string    `json:"version"`
	Status    string    `json:"status"`
	LastSeen  time.Time `json:"last_seen"`
}

type Registry struct {
	db *sql.DB
}

func New(dbPath string) (*Registry, error) {
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	r := &Registry{db: db}
	if err := r.initSchema(); err != nil {
		db.Close()
		return nil, err
	}
	return r, nil
}

func (r *Registry) initSchema() error {
	_, err := r.db.Exec(`
		CREATE TABLE IF NOT EXISTS devices (
			device_id   TEXT PRIMARY KEY,
			machine_id  TEXT UNIQUE NOT NULL,
			hostname    TEXT NOT NULL,
			os          TEXT NOT NULL,
			rdp_port    INTEGER NOT NULL,
			version     TEXT NOT NULL,
			status      TEXT NOT NULL DEFAULT 'online',
			last_seen   DATETIME NOT NULL
		)
	`)
	return err
}

func (r *Registry) Register(dev *Device) (string, error) {
	deviceID := dev.MachineID
	now := time.Now()

	_, err := r.db.Exec(`
		INSERT INTO devices (device_id, machine_id, hostname, os, rdp_port, version, status, last_seen)
		VALUES (?, ?, ?, ?, ?, ?, 'online', ?)
		ON CONFLICT(machine_id) DO UPDATE SET
			hostname = excluded.hostname,
			os = excluded.os,
			rdp_port = excluded.rdp_port,
			version = excluded.version,
			status = 'online',
			last_seen = excluded.last_seen
	`, deviceID, dev.MachineID, dev.Hostname, dev.OS, dev.RDPPort, dev.Version, now)

	if err != nil {
		return "", fmt.Errorf("register device: %w", err)
	}
	return deviceID, nil
}

func (r *Registry) ListOnline() ([]Device, error) {
	rows, err := r.db.Query(`
		SELECT device_id, machine_id, hostname, os, rdp_port, version, status, last_seen
		FROM devices WHERE status = 'online'
		ORDER BY hostname
	`)
	if err != nil {
		return nil, fmt.Errorf("list devices: %w", err)
	}
	defer rows.Close()

	var devices []Device
	for rows.Next() {
		var d Device
		if err := rows.Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.OS, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen); err != nil {
			return nil, err
		}
		devices = append(devices, d)
	}
	return devices, nil
}

func (r *Registry) UpdateHeartbeat(deviceID string) error {
	_, err := r.db.Exec(`
		UPDATE devices SET last_seen = ?, status = 'online' WHERE device_id = ?
	`, time.Now(), deviceID)
	return err
}

func (r *Registry) MarkOffline(deviceID string) error {
	_, err := r.db.Exec(`
		UPDATE devices SET status = 'offline' WHERE device_id = ?
	`, deviceID)
	return err
}

func (r *Registry) GetByDeviceID(deviceID string) (*Device, error) {
	var d Device
	err := r.db.QueryRow(`
		SELECT device_id, machine_id, hostname, os, rdp_port, version, status, last_seen
		FROM devices WHERE device_id = ?
	`, deviceID).Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.OS, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen)
	if err != nil {
		return nil, err
	}
	return &d, nil
}

func (r *Registry) MarkStaleOffline(timeout time.Duration) {
	cutoff := time.Now().Add(-timeout)
	r.db.Exec(`UPDATE devices SET status = 'offline' WHERE last_seen < ? AND status = 'online'`, cutoff)
}

func (r *Registry) Close() error {
	return r.db.Close()
}
```

- [ ] **Step 4: 创建测试辅助函数**

在 `registry_test.go` 顶部添加：
```go
func NewTestRegistry(t *testing.T) *Registry {
	t.Helper()
	reg, err := New(t.TempDir() + "/test.db")
	if err != nil {
		t.Fatalf("failed to create test registry: %v", err)
	}
	return reg
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `go test ./internal/registry/ -v`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add internal/registry/
git commit -m "feat: add device registry with SQLite storage"
```

---

### Task 5: 隧道模块

**Files:**
- Create: `relay-server/internal/tunnel/tunnel.go`
- Create: `relay-server/internal/tunnel/tunnel_test.go`

- [ ] **Step 1: 编写隧道测试**

`internal/tunnel/tunnel_test.go`:
```go
package tunnel

import (
	"crypto/rand"
	"io"
	"net"
	"testing"
	"time"
)

func TestCreateSession(t *testing.T) {
	mgr := NewManager()

	session, err := mgr.CreateSession("device-001")
	if err != nil {
		t.Fatalf("CreateSession failed: %v", err)
	}
	if session.ID == "" {
		t.Error("expected non-empty session ID")
	}
	if session.DeviceID != "device-001" {
		t.Errorf("expected device 'device-001', got '%s'", session.DeviceID)
	}
}

func TestGetSession(t *testing.T) {
	mgr := NewManager()

	session, _ := mgr.CreateSession("device-001")
	found := mgr.GetSession(session.ID)
	if found == nil {
		t.Error("expected to find session")
	}
	if found.ID != session.ID {
		t.Error("session ID mismatch")
	}

	missing := mgr.GetSession("nonexistent")
	if missing != nil {
		t.Error("expected nil for nonexistent session")
	}
}

func TestBridge(t *testing.T) {
	mgr := NewManager()
	session, _ := mgr.CreateSession("device-001")

	// 创建两个 net.Pipe 连接模拟 PC 和 App
	pcConn, pcSide := net.Pipe()
	appConn, appSide := net.Pipe()

	// 启动桥接
	go mgr.Bridge(session.ID, pcSide, appSide)

	// 通过 PC 端发送数据，App 端应该收到
	data := make([]byte, 1024)
	rand.Read(data)

	go pcConn.Write(data)
	received := make([]byte, 1024)
	appConn.SetReadDeadline(time.Now().Add(time.Second))
	n, err := appConn.Read(received)
	if err != nil {
		t.Fatalf("read failed: %v", err)
	}
	if n != 1024 {
		t.Errorf("expected 1024 bytes, got %d", n)
	}

	// 反向也测试
	go appConn.Write(data)
	pcConn.SetReadDeadline(time.Now().Add(time.Second))
	n, err = pcConn.Read(received)
	if err != nil {
		t.Fatalf("reverse read failed: %v", err)
	}
	if n != 1024 {
		t.Errorf("expected 1024 bytes reverse, got %d", n)
	}

	pcConn.Close()
	appConn.Close()
}

func TestCloseSession(t *testing.T) {
	mgr := NewManager()
	session, _ := mgr.CreateSession("device-001")

	mgr.CloseSession(session.ID)

	if mgr.GetSession(session.ID) != nil {
		t.Error("expected session to be removed")
	}
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/tunnel/ -v`
Expected: FAIL

- [ ] **Step 3: 实现隧道管理**

`internal/tunnel/tunnel.go`:
```go
package tunnel

import (
	"crypto/rand"
	"encoding/hex"
	"io"
	"log"
	"net"
	"sync"
)

type Session struct {
	ID       string
	DeviceID string
	pcConn   net.Conn
	appConn  net.Conn
	mu       sync.Mutex
	done     chan struct{}
}

type Manager struct {
	sessions sync.Map // map[string]*Session
}

func NewManager() *Manager {
	return &Manager{}
}

func (m *Manager) CreateSession(deviceID string) (*Session, error) {
	idBytes := make([]byte, 16)
	if _, err := rand.Read(idBytes); err != nil {
		return nil, err
	}
	sessionID := "sess-" + hex.EncodeToString(idBytes)

	s := &Session{
		ID:       sessionID,
		DeviceID: deviceID,
		done:     make(chan struct{}),
	}
	m.sessions.Store(sessionID, s)
	return s, nil
}

func (m *Manager) GetSession(sessionID string) *Session {
	val, ok := m.sessions.Load(sessionID)
	if !ok {
		return nil
	}
	return val.(*Session)
}

func (m *Manager) Bridge(sessionID string, pcConn, appConn net.Conn) {
	session := m.GetSession(sessionID)
	if session == nil {
		pcConn.Close()
		appConn.Close()
		return
	}

	session.mu.Lock()
	session.pcConn = pcConn
	session.appConn = appConn
	session.mu.Unlock()

	log.Printf("tunnel bridge established: session=%s", sessionID)

	// 双向数据转发
	var wg sync.WaitGroup
	wg.Add(2)

	go func() {
		defer wg.Done()
		io.Copy(appConn, pcConn)
		// PC端断开，关闭App端
		appConn.Close()
	}()

	go func() {
		defer wg.Done()
		io.Copy(pcConn, appConn)
		// App端断开，关闭PC端
		pcConn.Close()
	}()

	wg.Wait()
	close(session.done)
	m.CloseSession(sessionID)
	log.Printf("tunnel bridge closed: session=%s", sessionID)
}

func (m *Manager) CloseSession(sessionID string) {
	val, ok := m.sessions.LoadAndDelete(sessionID)
	if !ok {
		return
	}
	session := val.(*Session)
	session.mu.Lock()
	if session.pcConn != nil {
		session.pcConn.Close()
	}
	if session.appConn != nil {
		session.appConn.Close()
	}
	session.mu.Unlock()
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `go test ./internal/tunnel/ -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add internal/tunnel/
git commit -m "feat: add tunnel manager with TCP bridge"
```

---

### Task 6: 控制连接模块（PC ↔ 服务器）

**Files:**
- Create: `relay-server/internal/control/control.go`
- Create: `relay-server/internal/control/control_test.go`

- [ ] **Step 1: 编写控制连接测试**

`internal/control/control_test.go`:
```go
package control

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net"
	"testing"
	"time"
)

func TestHandleConnection_Register(t *testing.T) {
	server := NewTestServer(t)
	defer server.Close()

	conn, err := net.Dial("tcp", server.Addr())
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	defer conn.Close()

	// 发送注册消息
	hash := sha256.Sum256([]byte("test-secret"))
	regMsg := map[string]interface{}{
		"type":       "register",
		"machine_id": "machine-001",
		"hostname":   "PC-TEST",
		"os":         "Windows 11",
		"rdp_port":   3389,
		"version":    "1.0.0",
		"auth_key":   hex.EncodeToString(hash[:]),
	}
	sendMessage(t, conn, regMsg)

	// 接收注册确认
	resp := readMessage(t, conn)
	if resp["type"] != "register_ack" {
		t.Errorf("expected register_ack, got %v", resp["type"])
	}
	if resp["status"] != "ok" {
		t.Errorf("expected status ok, got %v", resp["status"])
	}
	if resp["device_id"] == nil {
		t.Error("expected device_id in response")
	}
}

func TestHandleConnection_Heartbeat(t *testing.T) {
	server := NewTestServer(t)
	defer server.Close()

	conn, err := net.Dial("tcp", server.Addr())
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	defer conn.Close()

	// 先注册
	hash := sha256.Sum256([]byte("test-secret"))
	sendMessage(t, conn, map[string]interface{}{
		"type":       "register",
		"machine_id": "machine-001",
		"hostname":   "PC-TEST",
		"os":         "Windows 11",
		"rdp_port":   3389,
		"version":    "1.0.0",
		"auth_key":   hex.EncodeToString(hash[:]),
	})
	readMessage(t, conn) // register_ack

	// 发送心跳
	sendMessage(t, conn, map[string]interface{}{
		"type":      "heartbeat",
		"device_id": "machine-001",
	})
	resp := readMessage(t, conn)
	if resp["type"] != "heartbeat_ack" {
		t.Errorf("expected heartbeat_ack, got %v", resp["type"])
	}
}

func sendMessage(t *testing.T, conn net.Conn, msg map[string]interface{}) {
	t.Helper()
	data, _ := json.Marshal(msg)
	lenBytes := []byte(fmt.Sprintf("%04d", len(data)))
	conn.Write(append(lenBytes, data...))
}

func readMessage(t *testing.T, conn net.Conn) map[string]interface{} {
	t.Helper()
	lenBuf := make([]byte, 4)
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := conn.Read(lenBuf); err != nil {
		t.Fatalf("read length failed: %v", err)
	}
	var msgLen int
	fmt.Sscanf(string(lenBuf), "%d", &msgLen)

	data := make([]byte, msgLen)
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := conn.Read(data); err != nil {
		t.Fatalf("read body failed: %v", err)
	}

	var msg map[string]interface{}
	json.Unmarshal(data, &msg)
	return msg
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/control/ -v`
Expected: FAIL

- [ ] **Step 3: 实现控制连接**

`internal/control/control.go`:
```go
package control

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"sync"
	"time"

	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
)

const (
	MaxMessageSize = 65536
	HeartbeatTimeout = 60 * time.Second
)

type Message struct {
	Type      string `json:"type"`
	MachineID string `json:"machine_id,omitempty"`
	Hostname  string `json:"hostname,omitempty"`
	OS        string `json:"os,omitempty"`
	RDPPort   int    `json:"rdp_port,omitempty"`
	Version   string `json:"version,omitempty"`
	AuthKey   string `json:"auth_key,omitempty"`
	DeviceID  string `json:"device_id,omitempty"`
	SessionID string `json:"session_id,omitempty"`
	TunnelPort int   `json:"tunnel_port,omitempty"`
	Timestamp int64  `json:"timestamp,omitempty"`
}

type ClientConn struct {
	DeviceID string
	Conn     net.Conn
	mu       sync.Mutex
}

func (c *ClientConn) SendMessage(msg Message) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	data, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	lenBuf := make([]byte, 4)
	binary.BigEndian.PutUint32(lenBuf, uint32(len(data)))
	_, err = c.Conn.Write(append(lenBuf, data...))
	return err
}

type Server struct {
	authService *auth.Service
	registry    *registry.Registry
	tunnelMgr   *tunnel.Manager
	listener    net.Listener
	clients     sync.Map // map[string]*ClientConn (deviceID -> connection)
	tunnelPort  int      // 下一个可用的隧道端口
	tunnelStart int
	mu          sync.Mutex
}

func NewServer(authService *auth.Service, reg *registry.Registry, tunnelMgr *tunnel.Manager, tunnelPortStart int) *Server {
	return &Server{
		authService: authService,
		registry:    reg,
		tunnelMgr:   tunnelMgr,
		tunnelStart: tunnelPortStart,
		tunnelPort:  tunnelPortStart,
	}
}

func (s *Server) Start(addr string) error {
	var err error
	s.listener, err = net.Listen("tcp", addr)
	if err != nil {
		return err
	}
	log.Printf("control server listening on %s", addr)

	go s.acceptLoop()
	return nil
}

func (s *Server) acceptLoop() {
	for {
		conn, err := s.listener.Accept()
		if err != nil {
			log.Printf("accept error: %v", err)
			return
		}
		go s.handleConnection(conn)
	}
}

func (s *Server) handleConnection(conn net.Conn) {
	defer conn.Close()

	// 读取注册消息
	msg, err := readMessage(conn)
	if err != nil {
		log.Printf("read register message failed: %v", err)
		return
	}

	if msg.Type != "register" {
		conn.Close()
		return
	}

	// 验证预共享密钥
	if !s.authService.VerifyPreSharedKey(msg.AuthKey) {
		writeMessage(conn, Message{Type: "register_ack", DeviceID: "", })
		// can't use simple struct, send JSON directly
		resp := map[string]string{"type": "register_ack", "status": "auth_failed"}
		data, _ := json.Marshal(resp)
		lenBuf := make([]byte, 4)
		binary.BigEndian.PutUint32(lenBuf, uint32(len(data)))
		conn.Write(append(lenBuf, data...))
		return
	}

	// 注册设备
	dev := &registry.Device{
		MachineID: msg.MachineID,
		Hostname:  msg.Hostname,
		OS:        msg.OS,
		RDPPort:   msg.RDPPort,
		Version:   msg.Version,
	}
	deviceID, err := s.registry.Register(dev)
	if err != nil {
		log.Printf("register device failed: %v", err)
		conn.Close()
		return
	}

	client := &ClientConn{
		DeviceID: deviceID,
		Conn:     conn,
	}
	s.clients.Store(deviceID, client)

	// 发送注册确认
	writeMessage(conn, Message{
		Type:     "register_ack",
		Status:   "ok",
		DeviceID: deviceID,
	})

	log.Printf("device registered: %s (%s)", deviceID, msg.Hostname)

	// 进入消息循环
	s.messageLoop(client, conn)
}

func (s *Server) messageLoop(client *ClientConn, conn net.Conn) {
	lastHeartbeat := time.Now()

	for {
		conn.SetReadDeadline(time.Now().Add(HeartbeatTimeout))
		msg, err := readMessage(conn)
		if err != nil {
			if err == io.EOF {
				log.Printf("device %s disconnected", client.DeviceID)
			} else {
				log.Printf("read error from %s: %v", client.DeviceID, err)
			}
			s.handleDisconnect(client.DeviceID)
			return
		}

		switch msg.Type {
		case "heartbeat":
			s.registry.UpdateHeartbeat(client.DeviceID)
			lastHeartbeat = time.Now()
			client.SendMessage(Message{Type: "heartbeat_ack"})

		case "tunnel_response":
			// PC 响应隧道建立请求
			log.Printf("tunnel response from %s: session=%s", client.DeviceID, msg.SessionID)

		default:
			log.Printf("unknown message type: %s from %s", msg.Type, client.DeviceID)
		}

		// 检查心跳超时
		if time.Since(lastHeartbeat) > HeartbeatTimeout {
			log.Printf("heartbeat timeout for %s", client.DeviceID)
			s.handleDisconnect(client.DeviceID)
			return
		}
	}
}

func (s *Server) handleDisconnect(deviceID string) {
	s.clients.Delete(deviceID)
	s.registry.MarkOffline(deviceID)
	log.Printf("device %s marked offline", deviceID)
}

func (s *Server) NotifyTunnelRequest(deviceID string, sessionID string, tunnelPort int) error {
	val, ok := s.clients.Load(deviceID)
	if !ok {
		return fmt.Errorf("device %s not connected", deviceID)
	}
	client := val.(*ClientConn)
	return client.SendMessage(Message{
		Type:       "tunnel_request",
		SessionID:  sessionID,
		TunnelPort: tunnelPort,
	})
}

func (s *Server) Close() error {
	if s.listener != nil {
		return s.listener.Close()
	}
	return nil
}

func (s *Server) Addr() string {
	if s.listener != nil {
		return s.listener.Addr().String()
	}
	return ""
}

func readMessage(conn net.Conn) (*Message, error) {
	lenBuf := make([]byte, 4)
	if _, err := io.ReadFull(conn, lenBuf); err != nil {
		return nil, err
	}
	msgLen := binary.BigEndian.Uint32(lenBuf)
	if msgLen > MaxMessageSize {
		return nil, fmt.Errorf("message too large: %d", msgLen)
	}

	data := make([]byte, msgLen)
	if _, err := io.ReadFull(conn, data); err != nil {
		return nil, err
	}

	var msg Message
	if err := json.Unmarshal(data, &msg); err != nil {
		return nil, err
	}
	return &msg, nil
}

func writeMessage(conn net.Conn, msg Message) error {
	// 使用临时结构以包含 Status 字段
	type responseMsg struct {
		Type     string `json:"type"`
		Status   string `json:"status,omitempty"`
		DeviceID string `json:"device_id,omitempty"`
	}
	resp := responseMsg{
		Type:     msg.Type,
		Status:   msg.Status,
		DeviceID: msg.DeviceID,
	}
	data, err := json.Marshal(resp)
	if err != nil {
		return err
	}
	lenBuf := make([]byte, 4)
	binary.BigEndian.PutUint32(lenBuf, uint32(len(data)))
	_, err = conn.Write(append(lenBuf, data...))
	return err
}
```

- [ ] **Step 4: 在 Message 结构体添加 Status 字段并创建测试辅助**

在 `control.go` 的 `Message` 结构体中添加 `Status string` 字段：
```go
type Message struct {
	Type      string `json:"type"`
	Status    string `json:"status,omitempty"`
	MachineID string `json:"machine_id,omitempty"`
	// ... 其余字段
}
```

在 `control_test.go` 添加测试辅助：
```go
type testServer struct {
	*Server
}

func NewTestServer(t *testing.T) *testServer {
	t.Helper()
	authService := auth.New("test-jwt-secret", "test-secret", time.Hour)
	reg, err := registry.New(t.TempDir() + "/test.db")
	if err != nil {
		t.Fatalf("create registry: %v", err)
	}
	tunnelMgr := tunnel.NewManager()
	srv := NewServer(authService, reg, tunnelMgr, 9100)

	if err := srv.Start("127.0.0.1:0"); err != nil {
		t.Fatalf("start server: %v", err)
	}
	return &testServer{Server: srv}
}

func (s *testServer) Close() {
	s.Server.Close()
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `go test ./internal/control/ -v`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add internal/control/
git commit -m "feat: add control connection handler for PC registration"
```

---

### Task 7: HTTP API 模块

**Files:**
- Create: `relay-server/internal/api/api.go`
- Create: `relay-server/internal/api/api_test.go`

- [ ] **Step 1: 编写 API 测试**

`internal/api/api_test.go`:
```go
package api

import (
	"bytes"
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
	handler := New(authService, reg, tunnelMgr, nil, "")

	cleanup := func() {
		reg.Close()
	}
	return handler, reg, cleanup
}

func TestAuth(t *testing.T) {
	handler, _, cleanup := setupTestAPI(t)
	defer cleanup()

	body, _ := json.Marshal(map[string]string{
		"pre_shared_key": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", // sha256("test")
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

	req := httptest.NewRequest("GET", "/api/devices", nil)
	w := httptest.NewRecorder()
	handler.HandleGetDevices(w, req)
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
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/api/ -v`
Expected: FAIL

- [ ] **Step 3: 实现 API 处理器**

`internal/api/api.go`:
```go
package api

import (
	"encoding/json"
	"log"
	"net/http"
	"strings"

	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/control"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
)

type Handler struct {
	authService *auth.Service
	registry    *registry.Registry
	tunnelMgr   *tunnel.Manager
	controlSrv  *control.Server
	quickDeployURL string
}

func New(authService *auth.Service, reg *registry.Registry, tunnelMgr *tunnel.Manager, controlSrv *control.Server, quickDeployURL string) *Handler {
	return &Handler{
		authService:    authService,
		registry:       reg,
		tunnelMgr:      tunnelMgr,
		controlSrv:     controlSrv,
		quickDeployURL: quickDeployURL,
	}
}

func (h *Handler) Routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/auth", h.HandleAuth)
	mux.HandleFunc("/api/devices", h.requireAuth(h.HandleGetDevices))
	mux.HandleFunc("/api/tunnel/request", h.requireAuth(h.HandleTunnelRequest))
	mux.HandleFunc("/api/logs/upload", h.HandleUploadLogs)
	mux.HandleFunc("/about", h.HandleAbout)
	mux.HandleFunc("/", h.HandleAbout)
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

	json.NewEncoder(w).Encode(map[string]interface{}{
		"token":   token,
		"expires": 3600,
	})
}

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

	json.NewEncoder(w).Encode(map[string]interface{}{
		"devices": devices,
	})
}

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

	// 通知 PC 建立数据连接
	if h.controlSrv != nil {
		if err := h.controlSrv.NotifyTunnelRequest(req.DeviceID, session.ID, 0); err != nil {
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

	json.NewEncoder(w).Encode(map[string]interface{}{
		"session_id":  session.ID,
		"tunnel_host": host,
		"tunnel_port": 0, // 实际端口在隧道监听器启动后确定
	})
}

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

	// TODO: 转发到 quickdeploy（当 quickDeployURL 配置时）

	json.NewEncoder(w).Encode(map[string]interface{}{
		"status": "ok",
	})
}

func (h *Handler) HandleAbout(w http.ResponseWriter, r *http.Request) {
	// 委托给 web 模块
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	// web 模块会接管，这里只是占位
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `go test ./internal/api/ -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add internal/api/
git commit -m "feat: add HTTP API handlers"
```

---

### Task 8: 隧道数据连接监听器

**Files:**
- Modify: `relay-server/internal/tunnel/tunnel.go`
- Create: `relay-server/internal/tunnel/listener.go`
- Create: `relay-server/internal/tunnel/listener_test.go`

- [ ] **Step 1: 编写隧道监听器测试**

`internal/tunnel/listener_test.go`:
```go
package tunnel

import (
	"testing"
	"time"
)

func TestTunnelListener_PCAndAppConnect(t *testing.T) {
	mgr := NewManager()

	// 创建会话
	session, _ := mgr.CreateSession("device-001")

	// 启动隧道监听器
	listener := NewTunnelListener(mgr, "127.0.0.1:0")
	if err := listener.Start(); err != nil {
		t.Fatalf("start listener failed: %v", err)
	}
	defer listener.Stop()

	port := listener.Port()
	if port == 0 {
		t.Fatal("expected non-zero port")
	}

	// 注册会话到监听器
	listener.RegisterSession(session.ID, session)

	// 等待连接建立并桥接
	time.Sleep(100 * time.Millisecond)

	// 验证会话仍然存在
	if mgr.GetSession(session.ID) == nil {
		t.Error("expected session to exist before connections")
	}
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `go test ./internal/tunnel/ -run TestTunnelListener -v`
Expected: FAIL

- [ ] **Step 3: 实现隧道监听器**

`internal/tunnel/listener.go`:
```go
package tunnel

import (
	"encoding/binary"
	"fmt"
	"io"
	"log"
	"net"
	"sync"
)

const (
	TypePCTunnel  = 0x01
	TypeAppTunnel = 0x02
	SessionIDLen  = 36 // "sess-" + 32 hex chars
)

type pendingSession struct {
	session  *Session
	pcConn   net.Conn
	appConn  net.Conn
	ready    chan struct{}
	mu       sync.Mutex
}

type TunnelListener struct {
	mgr       *Manager
	addr      string
	listener  net.Listener
	port      int
	pending   sync.Map // map[string]*pendingSession
}

func NewTunnelListener(mgr *Manager, addr string) *TunnelListener {
	return &TunnelListener{
		mgr:  mgr,
		addr: addr,
	}
}

func (tl *TunnelListener) Start() error {
	var err error
	tl.listener, err = net.Listen("tcp", tl.addr)
	if err != nil {
		return err
	}
	tl.port = tl.listener.Addr().(*net.TCPAddr).Port
	log.Printf("tunnel listener started on port %d", tl.port)
	go tl.acceptLoop()
	return nil
}

func (tl *TunnelListener) Port() int {
	return tl.port
}

func (tl *TunnelListener) RegisterSession(sessionID string, session *Session) {
	tl.pending.Store(sessionID, &pendingSession{
		session: session,
		ready:   make(chan struct{}),
	})
}

func (tl *TunnelListener) acceptLoop() {
	for {
		conn, err := tl.listener.Accept()
		if err != nil {
			log.Printf("tunnel listener accept error: %v", err)
			return
		}
		go tl.handleTunnelConn(conn)
	}
}

func (tl *TunnelListener) handleTunnelConn(conn net.Conn) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("panic in handleTunnelConn: %v", r)
			conn.Close()
		}
	}()

	// 读取类型字节
	typeBuf := make([]byte, 1)
	if _, err := io.ReadFull(conn, typeBuf); err != nil {
		log.Printf("read tunnel type failed: %v", err)
		conn.Close()
		return
	}
	connType := typeBuf[0]

	// 读取 session_id
	idBuf := make([]byte, SessionIDLen)
	if _, err := io.ReadFull(conn, idBuf); err != nil {
		log.Printf("read session_id failed: %v", err)
		conn.Close()
		return
	}
	sessionID := string(idBuf)

	val, ok := tl.pending.Load(sessionID)
	if !ok {
		log.Printf("unknown session_id: %s", sessionID)
		conn.Close()
		return
	}
	pending := val.(*pendingSession)

	pending.mu.Lock()
	switch connType {
	case TypePCTunnel:
		pending.pcConn = conn
		log.Printf("PC tunnel connected: session=%s", sessionID)
	case TypeAppTunnel:
		pending.appConn = conn
		log.Printf("App tunnel connected: session=%s", sessionID)
	default:
		pending.mu.Unlock()
		log.Printf("unknown tunnel type: %d", connType)
		conn.Close()
		return
	}

	// 两端都连接后启动桥接
	if pending.pcConn != nil && pending.appConn != nil {
		pcConn := pending.pcConn
		appConn := pending.appConn
		session := pending.session
		pending.mu.Unlock()

		tl.pending.Delete(sessionID)
		go tl.mgr.Bridge(sessionID, pcConn, appConn)
	} else {
		pending.mu.Unlock()
	}
}

func (tl *TunnelListener) Stop() error {
	if tl.listener != nil {
		return tl.listener.Close()
	}
	return nil
}

// writeTunnelHeader 写入隧道连接头（类型 + session_id）
func WriteTunnelHeader(conn net.Conn, connType byte, sessionID string) error {
	header := make([]byte, 1+SessionIDLen)
	header[0] = connType
	copy(header[1:], sessionID)
	_, err := conn.Write(header)
	return err
}

// 为兼容编译，确保 binary 包被引用
var _ = binary.BigEndian
```

- [ ] **Step 4: 运行测试确认通过**

Run: `go test ./internal/tunnel/ -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add internal/tunnel/
git commit -m "feat: add tunnel listener for PC and App connections"
```

---

### Task 9: 关于页面

**Files:**
- Create: `relay-server/internal/web/web.go`
- Create: `relay-server/internal/web/static/about.html`

- [ ] **Step 1: 创建关于页面 HTML**

`internal/web/static/about.html`:
```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>QuickRemote - 关于</title>
    <style>
        :root {
            --primary: #2563eb;
            --bg: #f8fafc;
            --card: #ffffff;
            --text: #1e293b;
            --border: #e2e8f0;
        }
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            background: var(--bg);
            color: var(--text);
            line-height: 1.6;
        }
        .container { max-width: 800px; margin: 0 auto; padding: 2rem 1rem; }
        .header { text-align: center; margin-bottom: 2rem; }
        .header h1 { font-size: 2rem; color: var(--primary); }
        .header p { color: #64748b; margin-top: 0.5rem; }
        .card {
            background: var(--card);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 1.5rem;
            margin-bottom: 1rem;
        }
        .card h2 { font-size: 1.25rem; margin-bottom: 1rem; }
        .card ol, .card ul { padding-left: 1.5rem; margin-top: 0.5rem; }
        .card li { margin-bottom: 0.5rem; }
        .card code {
            background: #f1f5f9;
            padding: 0.125rem 0.375rem;
            border-radius: 4px;
            font-size: 0.875rem;
        }
        .downloads { display: flex; gap: 1rem; flex-wrap: wrap; }
        .download-btn {
            display: inline-flex;
            align-items: center;
            gap: 0.5rem;
            padding: 0.75rem 1.5rem;
            background: var(--primary);
            color: white;
            text-decoration: none;
            border-radius: 6px;
            font-weight: 500;
        }
        .download-btn:hover { opacity: 0.9; }
        .changelog { max-height: 300px; overflow-y: auto; }
        .changelog-item { margin-bottom: 1rem; padding-bottom: 1rem; border-bottom: 1px solid var(--border); }
        .changelog-item:last-child { border-bottom: none; }
        .version { font-weight: 600; color: var(--primary); }
        .date { color: #94a3b8; font-size: 0.875rem; }
    </style>
</head>
<body>
    <div class="container">
        <div class="header">
            <h1>QuickRemote</h1>
            <p>安卓远程连接 Windows 桌面管理工具</p>
        </div>

        <div class="card">
            <h2>项目介绍</h2>
            <p>QuickRemote 通过自建中转服务器转发 RDP 流量，实现安卓设备远程连接 Windows 桌面。PC 端安装客户端并配置服务器地址后自动注册，安卓 App 配置相同服务器地址即可获取所有在线 PC 并发起远程桌面连接。</p>
        </div>

        <div class="card">
            <h2>安装步骤</h2>
            <ol>
                <li>在服务器上运行 <code>install.sh</code> 部署中转服务器</li>
                <li>在 Windows PC 上安装 PC 客户端，配置服务器地址和预共享密钥</li>
                <li>在安卓设备上安装 App，配置相同的服务器地址和预共享密钥</li>
                <li>在 App 中选择 PC 设备，点击连接即可远程桌面</li>
            </ol>
        </div>

        <div class="card">
            <h2>下载</h2>
            <div class="downloads">
                <a class="download-btn" id="download-pc">PC 客户端</a>
                <a class="download-btn" id="download-android">安卓 App</a>
            </div>
        </div>

        <div class="card">
            <h2>配置说明</h2>
            <ul>
                <li><strong>服务器监听端口</strong>：默认 8443，可通过配置文件修改</li>
                <li><strong>预共享密钥</strong>：用于 PC 和安卓 App 认证，请妥善保管</li>
                <li><strong>TLS 证书</strong>：未配置时自动生成自签名证书</li>
                <li><strong>RDP 端口</strong>：默认 3389，确保 Windows RDP 服务已启用</li>
            </ul>
        </div>

        <div class="card">
            <h2>更新记录</h2>
            <div class="changelog" id="changelog">
                <p style="color: #94a3b8;">加载中...</p>
            </div>
        </div>
    </div>

    <script>
        // 动态加载更新记录
        fetch('/api/changelog')
            .then(r => r.json())
            .then(data => {
                const container = document.getElementById('changelog');
                if (data.changelogs && data.changelogs.length > 0) {
                    container.innerHTML = data.changelogs.map(item =>
                        `<div class="changelog-item">
                            <span class="version">${item.version}</span>
                            <span class="date">${item.date}</span>
                            <div style="margin-top: 0.5rem;">${item.content}</div>
                        </div>`
                    ).join('');
                } else {
                    container.innerHTML = '<p style="color: #94a3b8;">暂无更新记录</p>';
                }
            })
            .catch(() => {
                document.getElementById('changelog').innerHTML = '<p style="color: #94a3b8;">无法加载更新记录</p>';
            });
    </script>
</body>
</html>
```

- [ ] **Step 2: 实现 web 模块**

`internal/web/web.go`:
```go
package web

import (
	_ "embed"
	"net/http"
)

//go:embed static/about.html
var aboutHTML []byte

func AboutHandler(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Write(aboutHTML)
}
```

- [ ] **Step 3: Commit**

```bash
git add internal/web/
git commit -m "feat: add about page with embedded HTML"
```

---

### Task 10: 主入口和集成

**Files:**
- Modify: `relay-server/cmd/server/main.go`
- Modify: `relay-server/internal/api/api.go` (添加 changelog 和 about 路由)

- [ ] **Step 1: 在 API 中添加 changelog 端点和 about 页面路由**

在 `internal/api/api.go` 的 `Routes()` 方法中添加：
```go
mux.HandleFunc("/api/changelog", h.HandleChangelog)
```

并添加处理函数：
```go
func (h *Handler) HandleChangelog(w http.ResponseWriter, r *http.Request) {
	// 返回更新记录（从 quickdeploy 拉取或返回空列表）
	// TODO: 当 quickDeployURL 配置时从 quickdeploy 拉取
	json.NewEncoder(w).Encode(map[string]interface{}{
		"changelogs": []interface{}{},
	})
}
```

在 `Routes()` 中将 `/about` 和 `/` 路由改为调用 web 模块：
```go
mux.HandleFunc("/about", web.AboutHandler)
mux.HandleFunc("/", web.AboutHandler)
```

需要在 `api.go` 中 import web 包。

- [ ] **Step 2: 实现完整的主入口**

`cmd/server/main.go`:
```go
package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"log"
	"math/big"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/quickremote/relay-server/internal/api"
	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/config"
	"github.com/quickremote/relay-server/internal/control"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
	"github.com/quickremote/relay-server/internal/web"
)

const Version = "1.0.0"

func main() {
	configPath := "config.yaml"
	if len(os.Args) > 1 {
		configPath = os.Args[1]
	}

	cfg, err := config.Load(configPath)
	if err != nil {
		log.Fatalf("load config failed: %v", err)
	}

	// 初始化注册表
	reg, err := registry.New(cfg.Storage.SQLitePath)
	if err != nil {
		log.Fatalf("init registry failed: %v", err)
	}
	defer reg.Close()

	// 初始化认证服务
	authService := auth.New(cfg.Auth.JWTSecret, cfg.Auth.PreSharedKey, time.Hour)

	// 初始化隧道管理器
	tunnelMgr := tunnel.NewManager()

	// 初始化控制服务器
	controlSrv := control.NewServer(authService, reg, tunnelMgr, 9100)

	// 启动控制连接监听（PC 的 TLS 连接）
	controlAddr := getAddrWithoutPort(cfg.Server.Listen, 8444)
	if err := controlSrv.Start(controlAddr); err != nil {
		log.Fatalf("start control server failed: %v", err)
	}
	defer controlSrv.Close()

	// 初始化 API 处理器
	apiHandler := api.New(authService, reg, tunnelMgr, controlSrv, cfg.QuickDeploy.BaseURL)

	// 启动心跳超时检查
	go func() {
		ticker := time.NewTicker(30 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			reg.MarkStaleOffline(60 * time.Second)
		}
	}()

	// 配置 TLS
	tlsConfig, err := setupTLS(cfg.Server.TLS.Cert, cfg.Server.TLS.Key)
	if err != nil {
		log.Fatalf("setup TLS failed: %v", err)
	}

	// 启动 HTTP 服务器
	server := &http.Server{
		Addr:      cfg.Server.Listen,
		Handler:   apiHandler.Routes(),
		TLSConfig: tlsConfig,
	}

	log.Printf("QuickRemote Relay Server v%s starting on %s", Version, cfg.Server.Listen)

	go func() {
		if cfg.Server.TLS.Cert != "" && cfg.Server.TLS.Key != "" {
			if err := server.ListenAndServeTLS(cfg.Server.TLS.Cert, cfg.Server.TLS.Key); err != nil && err != http.ErrServerClosed {
				log.Fatalf("HTTP server error: %v", err)
			}
		} else {
			if err := server.ListenAndServeTLS("", ""); err != nil && err != http.ErrServerClosed {
				log.Fatalf("HTTP server error: %v", err)
			}
		}
	}()

	// 优雅关闭
	quit := make(chan os.Signal, 1)
	signal.Notify(quit, syscall.SIGINT, syscall.SIGTERM)
	<-quit

	log.Println("shutting down...")
	server.Close()
}

func getAddrWithoutPort(listen string, defaultPort int) string {
	_, port, err := net.SplitHostPort(listen)
	if err != nil {
		return fmt.Sprintf(":8444")
	}
	// 控制连接端口 = HTTP 端口 + 1
	portNum, err := fmt.Atoi(port)
	if err != nil {
		return fmt.Sprintf(":8444")
	}
	return fmt.Sprintf(":%d", portNum+1)
}

func setupTLS(certPath, keyPath string) (*tls.Config, error) {
	if certPath != "" && keyPath != "" {
		cert, err := tls.LoadX509KeyPair(certPath, keyPath)
		if err != nil {
			return nil, fmt.Errorf("load cert: %w", err)
		}
		return &tls.Config{Certificates: []tls.Certificate{cert}}, nil
	}

	// 自动生成自签名证书
	cert, err := generateSelfSignedCert()
	if err != nil {
		return nil, fmt.Errorf("generate self-signed cert: %w", err)
	}
	log.Println("using auto-generated self-signed certificate")
	return &tls.Config{Certificates: []tls.Certificate{cert}}, nil
}

func generateSelfSignedCert() (tls.Certificate, error) {
	priv, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}

	template := x509.Certificate{
		SerialNumber: big.NewInt(1),
		Subject: pkix.Name{
			Organization: []string{"QuickRemote"},
		},
		NotBefore:             time.Now(),
		NotAfter:              time.Now().Add(10 * 365 * 24 * time.Hour),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		BasicConstraintsValid: true,
	}

	derBytes, err := x509.CreateCertificate(rand.Reader, &template, &template, &priv.PublicKey, priv)
	if err != nil {
		return tls.Certificate{}, err
	}

	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: derBytes})
	keyBytes, err := x509.MarshalECPrivateKey(priv)
	if err != nil {
		return tls.Certificate{}, err
	}
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyBytes})

	return tls.X509KeyPair(certPEM, keyPEM)
}
```

- [ ] **Step 3: 确保编译通过**

Run:
```bash
cd relay-server
go build -o quickremote-relay ./cmd/server
```
Expected: 编译成功

- [ ] **Step 4: 创建配置文件并测试启动**

```bash
cp config.example.yaml config.yaml
./quickremote-relay config.yaml &
sleep 2
curl -k https://localhost:8443/about | head -5
kill %1
```
Expected: 返回 HTML 内容

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: integrate all modules in main entry point"
```

---

### Task 11: 部署脚本

**Files:**
- Create: `relay-server/deploy/install.sh`
- Create: `relay-server/deploy/Dockerfile`
- Create: `relay-server/deploy/docker-compose.yml`

- [ ] **Step 1: 创建交互式 install.sh**

`deploy/install.sh`:
```bash
#!/bin/bash

# QuickRemote Relay Server 部署脚本
# 支持: 安装 / 升级 / 修改配置 / 卸载
# 使用方式: curl -fsSL -o install.sh <install-share-url> && sudo bash install.sh
#
# 注意: quickdeploy 分享 URL 不含文件名，需先下载到本地再执行
# manifest.json 的分享 URL 在发布时写入下方 MANIFEST_URL 变量

set -e

INSTALL_DIR="/opt/quickremote"
CONFIG_DIR="/etc/quickremote"
DATA_DIR="/var/lib/quickremote"
SERVICE_NAME="quickremote-relay"

# manifest.json 的固定分享 URL（通过 overwrite=true 保持不变）
# 发布时替换为实际 URL
MANIFEST_URL="__MANIFEST_URL_PLACEHOLDER__"

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

# 获取最新版本号
get_latest_version() {
    local manifest_file=$1
    jq -r '.relay-server.latest_version' "$manifest_file"
}

# 获取指定版本的下载 URL
get_download_url() {
    local manifest_file=$1
    local version=$2
    local arch=$3
    jq -r ".relay-server.versions[\"$version\"].$arch" "$manifest_file"
}

# 检查是否已安装
is_installed() {
    [ -f "$INSTALL_DIR/quickremote-relay" ] && systemctl is-enabled "$SERVICE_NAME" >/dev/null 2>&1
}

# 获取当前版本
get_current_version() {
    if [ -f "$INSTALL_DIR/VERSION" ]; then
        cat "$INSTALL_DIR/VERSION"
    else
        echo "未知"
    fi
}

# 下载并安装二进制
download_binary() {
    local version=$1
    local arch=$2
    local manifest_file=$3

    local download_url=$(get_download_url "$manifest_file" "$version" "$arch")
    if [ -z "$download_url" ] || [ "$download_url" = "null" ]; then
        error "无法找到版本 $version ($arch) 的下载 URL"
        exit 1
    fi

    info "下载版本: $version ($arch)"
    curl -fSL -o "$INSTALL_DIR/quickremote-relay" "$download_url"
    chmod +x "$INSTALL_DIR/quickremote-relay"
    echo "$version" > "$INSTALL_DIR/VERSION"
}

# 安装
do_install() {
    info "开始安装 QuickRemote Relay Server"

    local arch=$(detect_arch)
    info "检测到架构: $arch"

    ensure_jq

    # 获取版本清单
    local manifest_file=$(fetch_manifest)
    local latest_version=$(get_latest_version "$manifest_file")
    info "最新版本: $latest_version"

    # 交互式配置
    local listen_port
    prompt "请输入监听端口 [8443]: "
    read listen_port
    listen_port=${listen_port:-8443}

    local pre_shared_key
    prompt "请输入预共享密钥 [自动生成]: "
    read pre_shared_key
    if [ -z "$pre_shared_key" ]; then
        pre_shared_key=$(openssl rand -hex 16)
        info "已生成预共享密钥: $pre_shared_key"
    fi

    local jwt_secret
    prompt "请输入JWT密钥 [自动生成]: "
    read jwt_secret
    if [ -z "$jwt_secret" ]; then
        jwt_secret=$(openssl rand -hex 16)
    fi

    local quickdeploy_url
    prompt "请输入quickdeploy地址 (可选) []: "
    read quickdeploy_url

    # 创建目录
    mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$DATA_DIR"

    # 下载二进制
    download_binary "$latest_version" "$arch" "$manifest_file"
    rm -f "$manifest_file"

    # 创建配置文件
    tee "$CONFIG_DIR/config.yaml" > /dev/null <<EOF
server:
  listen: ":$listen_port"
  tls:
    cert: ""
    key: ""
auth:
  pre_shared_key: "$pre_shared_key"
  jwt_secret: "$jwt_secret"
storage:
  sqlite_path: "$DATA_DIR/registry.db"
quickdeploy:
  base_url: "$quickdeploy_url"
EOF

    # 创建 systemd 服务
    tee /etc/systemd/system/$SERVICE_NAME.service > /dev/null <<EOF
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

    systemctl daemon-reload
    systemctl enable $SERVICE_NAME
    systemctl start $SERVICE_NAME

    info "安装完成!"
    info "服务器地址: https://$(hostname -I | awk '{print $1}'):$listen_port"
    info "预共享密钥: $pre_shared_key"
    info "配置文件: $CONFIG_DIR/config.yaml"
    info "管理命令: sudo systemctl {start|stop|restart|status} $SERVICE_NAME"
}

# 升级
do_upgrade() {
    ensure_jq

    local manifest_file=$(fetch_manifest)
    local current=$(get_current_version)
    local latest=$(get_latest_version "$manifest_file")

    info "当前版本: $current"
    info "最新版本: $latest"

    if [ "$current" = "$latest" ]; then
        info "已是最新版本"
        rm -f "$manifest_file"
        return
    fi

    prompt "确认升级? [y/N]: "
    read confirm
    if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
        info "已取消升级"
        rm -f "$manifest_file"
        return
    fi

    local arch=$(detect_arch)

    systemctl stop $SERVICE_NAME

    download_binary "$latest" "$arch" "$manifest_file"
    rm -f "$manifest_file"

    systemctl start $SERVICE_NAME

    info "升级完成: $current -> $latest"
}

# 修改配置
do_config() {
    local config_file="$CONFIG_DIR/config.yaml"

    if [ ! -f "$config_file" ]; then
        error "配置文件不存在: $config_file"
        return
    fi

    info "当前配置:"
    cat "$config_file"
    echo ""

    local listen_port
    prompt "请输入新的监听端口 (回车保持不变): "
    read listen_port

    local pre_shared_key
    prompt "请输入新的预共享密钥 (回车保持不变): "
    read pre_shared_key

    if [ -n "$listen_port" ]; then
        sed -i "s/listen: \":[0-9]*\"/listen: \":$listen_port\"/" "$config_file"
    fi

    if [ -n "$pre_shared_key" ]; then
        sed -i "s/pre_shared_key: \".*\"/pre_shared_key: \"$pre_shared_key\"/" "$config_file"
    fi

    info "新配置:"
    cat "$config_file"

    prompt "重启服务使配置生效? [Y/n]: "
    read confirm
    if [ "$confirm" != "n" ] && [ "$confirm" != "N" ]; then
        systemctl restart $SERVICE_NAME
        info "服务已重启"
    fi
}

# 卸载
do_uninstall() {
    warn "即将卸载 QuickRemote Relay Server"
    warn "这将删除: 二进制文件、配置文件、数据文件"

    prompt "确认卸载? 输入 'yes' 确认: "
    read confirm
    if [ "$confirm" != "yes" ]; then
        info "已取消卸载"
        return
    fi

    systemctl stop $SERVICE_NAME 2>/dev/null || true
    systemctl disable $SERVICE_NAME 2>/dev/null || true
    rm -f /etc/systemd/system/$SERVICE_NAME.service
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
        echo "请选择操作:"
        echo "  1) 升级到最新版本"
        echo "  2) 修改配置"
        echo "  3) 卸载"
        echo ""
        prompt "请输入选项序号 [1-3]: "
        read choice

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
```

- [ ] **Step 2: 创建 Dockerfile**

`deploy/Dockerfile`:
```dockerfile
FROM --platform=$BUILDPLATFORM golang:1.22-alpine AS builder

ARG TARGETOS
ARG TARGETARCH

WORKDIR /build

COPY go.mod go.sum ./
RUN go mod download

COPY . .

RUN CGO_ENABLED=0 GOOS=$TARGETOS GOARCH=$TARGETARCH go build -ldflags="-s -w" -o quickremote-relay ./cmd/server

FROM alpine:latest

RUN apk --no-cache add ca-certificates tzdata

WORKDIR /app
COPY --from=builder /build/quickremote-relay .
COPY --from=builder /build/config.example.yaml .

RUN mkdir -p /etc/quickremote /var/lib/quickremote

EXPOSE 8443 8444

ENTRYPOINT ["./quickremote-relay"]
CMD ["/etc/quickremote/config.yaml"]
```

- [ ] **Step 3: 创建 docker-compose.yml**

`deploy/docker-compose.yml`:
```yaml
version: "3.8"

services:
  quickremote-relay:
    build:
      context: ..
      dockerfile: deploy/Dockerfile
    ports:
      - "8443:8443"
      - "8444:8444"
    volumes:
      - relay-data:/var/lib/quickremote
      - ./config.yaml:/etc/quickremote/config.yaml
    restart: always

volumes:
  relay-data:
```

- [ ] **Step 4: 赋予执行权限**

Run: `chmod +x deploy/install.sh`

- [ ] **Step 5: Commit**

```bash
git add deploy/
git commit -m "feat: add install.sh, Dockerfile, and docker-compose.yml"
```

---

### Task 12: 交叉编译脚本

**Files:**
- Create: `relay-server/deploy/build.sh`

- [ ] **Step 1: 创建交叉编译脚本**

`deploy/build.sh`:
```bash
#!/bin/bash

# QuickRemote Relay Server 交叉编译脚本
# 生成 arm64 和 amd64 两个架构的二进制
# 编译后通过 quickdeploy-mcp 上传，并更新 manifest.json

set -e

VERSION=${1:-"1.0.0"}
OUTPUT_DIR="./dist"

mkdir -p "$OUTPUT_DIR"

echo "Building QuickRemote Relay Server v$VERSION"

# amd64
echo "Building for linux/amd64..."
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -ldflags="-s -w -X main.Version=$VERSION" -o "$OUTPUT_DIR/quickremote-relay-v${VERSION}-amd64" ./cmd/server

# arm64
echo "Building for linux/arm64..."
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -ldflags="-s -w -X main.Version=$VERSION" -o "$OUTPUT_DIR/quickremote-relay-v${VERSION}-arm64" ./cmd/server

echo "Build complete:"
ls -lh "$OUTPUT_DIR/"

echo ""
echo "下一步: 通过 quickdeploy-mcp 上传文件"
echo "  1. 上传二进制文件 (permanent_share=true)"
echo "  2. 记录返回的 share_url"
echo "  3. 更新 manifest.json (overwrite=true)"
echo "  4. 上传 install.sh (overwrite=true, 如果脚本有更新)"
echo "  5. 上传 CHANGELOG.md (overwrite=true)"
```

- [ ] **Step 2: 测试编译**

Run:
```bash
cd relay-server
chmod +x deploy/build.sh
./deploy/build.sh 1.0.0
```
Expected: 生成 `dist/quickremote-relay-amd64` 和 `dist/quickremote-relay-arm64`

- [ ] **Step 3: Commit**

```bash
git add deploy/build.sh
git commit -m "feat: add cross-compilation build script"
```

---

### Task 13: CHANGELOG 和最终验证

**Files:**
- Create: `relay-server/CHANGELOG.md`

- [ ] **Step 1: 创建 CHANGELOG**

`CHANGELOG.md`:
```markdown
# 更新记录

## v1.0.0 (2026-08-01)

### 新增
- 中转服务器核心功能：设备注册管理、TCP隧道桥接
- HTTP API：认证、设备列表、隧道请求、日志上传
- PC 控制连接协议：注册、心跳、隧道通知
- 关于页面：项目介绍、安装步骤、下载链接、更新记录
- 交互式 install.sh 部署脚本（安装/升级/配置/卸载）
- Docker 多架构构建支持（arm64v8 / amd64）
- TLS 自动生成自签名证书
- SQLite 设备注册表持久化
- 心跳超时自动标记离线
```

- [ ] **Step 2: 运行所有测试**

Run:
```bash
cd relay-server
go test ./... -v
```
Expected: 所有测试 PASS

- [ ] **Step 3: 验证编译和启动**

Run:
```bash
go build -o quickremote-relay ./cmd/server
cp config.example.yaml config.yaml
./quickremote-relay config.yaml &
sleep 2
# 测试 API
curl -k https://localhost:8443/api/auth -d '{"pre_shared_key":"wrong"}' 
curl -k https://localhost:8443/about | head -5
kill %1
```
Expected: API 返回 401，About 页面返回 HTML

- [ ] **Step 4: 最终 Commit**

```bash
git add CHANGELOG.md
git commit -m "feat: add changelog and finalize relay-server v1.0.0"
```

---

## 自审

### 1. Spec 覆盖

| 规范要求 | 对应 Task |
|---------|-----------|
| 设备注册管理 + 心跳 | Task 4 (registry) + Task 6 (control) |
| TCP 隧道桥接 | Task 5 (tunnel) + Task 8 (listener) |
| HTTP API | Task 7 (api) |
| 关于页面 | Task 9 (web) |
| install.sh 交互式 | Task 11 (deploy) |
| Docker 多架构 | Task 11 (Dockerfile) |
| 配置文件 YAML | Task 2 (config) |
| TLS 自签名证书 | Task 10 (main) |
| 更新记录页面 | Task 9 (about.html) + Task 7 (changelog API) |
| 日志上报 API | Task 7 (HandleUploadLogs) |
| quickdeploy 包结构 | Task 12 (build.sh) |
| CHANGELOG.md | Task 13 |

### 2. 类型一致性
- `Message` 结构体在 control 模块中统一定义，包含 Status 字段
- `Device` 结构体在 registry 模块定义，api 模块直接引用
- `Session` 结构体在 tunnel 模块定义，control 和 api 模块通过 tunnel.Manager 交互

### 3. 遗漏项
- quickdeploy 集成（日志转发、changelog 拉取）标记为 TODO，在 quickdeploy MCP 集成时实现
- 服务器版本检查和自动升级在 PC 客户端计划中实现
