package tunnel

import (
	"crypto/rand"
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
