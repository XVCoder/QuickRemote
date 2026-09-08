package control

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"io"
	"net"
	"testing"
	"time"

	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
)

// testServer 包装 Server 和相关依赖，便于测试清理。
type testServer struct {
	*Server
	reg *registry.Registry
}

// NewTestServer 创建一个完整的测试环境（auth, registry, tunnel）。
func NewTestServer(t *testing.T) *testServer {
	t.Helper()
	authService := auth.New("test-jwt-secret", "test-secret", time.Hour)
	reg, err := registry.New(t.TempDir() + "/test.db")
	if err != nil {
		t.Fatalf("create registry: %v", err)
	}
	tunnelMgr := tunnel.NewManager()
	srv := NewServer(authService, reg, tunnelMgr, nil, 9100)

	if err := srv.Start("127.0.0.1:0"); err != nil {
		t.Fatalf("start server: %v", err)
	}
	return &testServer{Server: srv, reg: reg}
}

func (s *testServer) Close() {
	s.Server.Close()
	s.reg.Close()
}

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
	sendTestMessage(t, conn, regMsg)

	// 接收注册确认
	resp := readTestMessage(t, conn)
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
	sendTestMessage(t, conn, map[string]interface{}{
		"type":       "register",
		"machine_id": "machine-001",
		"hostname":   "PC-TEST",
		"os":         "Windows 11",
		"rdp_port":   3389,
		"version":    "1.0.0",
		"auth_key":   hex.EncodeToString(hash[:]),
	})
	readTestMessage(t, conn) // register_ack

	// 发送心跳
	sendTestMessage(t, conn, map[string]interface{}{
		"type":      "heartbeat",
		"device_id": "machine-001",
	})
	// 注册后服务器会广播 device_list，循环读到 heartbeat_ack 为止
	resp := readUntilType(t, conn, "heartbeat_ack")
	if resp == nil {
		t.Error("expected heartbeat_ack, got timeout")
	}
}

func TestHandleConnection_AuthFailed(t *testing.T) {
	server := NewTestServer(t)
	defer server.Close()

	conn, err := net.Dial("tcp", server.Addr())
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	defer conn.Close()

	// 发送错误的认证密钥
	sendTestMessage(t, conn, map[string]interface{}{
		"type":       "register",
		"machine_id": "machine-002",
		"hostname":   "PC-BAD",
		"os":         "Windows 11",
		"rdp_port":   3389,
		"version":    "1.0.0",
		"auth_key":   "wrong-hash",
	})
	resp := readTestMessage(t, conn)
	if resp["type"] != "register_ack" {
		t.Errorf("expected register_ack, got %v", resp["type"])
	}
	if resp["status"] != "auth_failed" {
		t.Errorf("expected status auth_failed, got %v", resp["status"])
	}
}

// readUntilType 循环读消息直到遇到指定类型（最多 10 条），找不到返回 nil。
func readUntilType(t *testing.T, conn net.Conn, wantType string) map[string]interface{} {
	t.Helper()
	for i := 0; i < 10; i++ {
		msg := readTestMessage(t, conn)
		if msg["type"] == wantType {
			return msg
		}
	}
	return nil
}

// sendTestMessage 使用 4 字节大端长度前缀 + JSON 载荷发送消息。
func sendTestMessage(t *testing.T, conn net.Conn, msg map[string]interface{}) {
	t.Helper()
	data, _ := json.Marshal(msg)
	lenBuf := make([]byte, 4)
	binary.BigEndian.PutUint32(lenBuf, uint32(len(data)))
	if _, err := conn.Write(append(lenBuf, data...)); err != nil {
		t.Fatalf("write message failed: %v", err)
	}
}

// readTestMessage 使用 4 字节大端长度前缀 + JSON 载荷读取消息。
func readTestMessage(t *testing.T, conn net.Conn) map[string]interface{} {
	t.Helper()
	lenBuf := make([]byte, 4)
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := io.ReadFull(conn, lenBuf); err != nil {
		t.Fatalf("read length failed: %v", err)
	}
	msgLen := binary.BigEndian.Uint32(lenBuf)

	data := make([]byte, msgLen)
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := io.ReadFull(conn, data); err != nil {
		t.Fatalf("read body failed: %v", err)
	}

	var msg map[string]interface{}
	if err := json.Unmarshal(data, &msg); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}
	return msg
}
