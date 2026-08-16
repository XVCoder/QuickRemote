package tunnel

import (
	"crypto/rand"
	"io"
	"net"
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

	// 等待监听器就绪
	time.Sleep(50 * time.Millisecond)

	// 验证会话仍然存在
	if mgr.GetSession(session.ID) == nil {
		t.Error("expected session to exist before connections")
	}
}

func TestTunnelListener_BridgeData(t *testing.T) {
	mgr := NewManager()
	session, _ := mgr.CreateSession("device-002")

	listener := NewTunnelListener(mgr, "127.0.0.1:0")
	if err := listener.Start(); err != nil {
		t.Fatalf("start listener failed: %v", err)
	}
	defer listener.Stop()

	listener.RegisterSession(session.ID, session)
	time.Sleep(50 * time.Millisecond)

	// PC 连接
	pcConn, err := net.Dial("tcp", listener.listener.Addr().String())
	if err != nil {
		t.Fatalf("pc dial failed: %v", err)
	}
	defer pcConn.Close()

	if err := WriteTunnelHeader(pcConn, TypePCTunnel, session.ID); err != nil {
		t.Fatalf("write pc header failed: %v", err)
	}

	// App 连接
	appConn, err := net.Dial("tcp", listener.listener.Addr().String())
	if err != nil {
		t.Fatalf("app dial failed: %v", err)
	}
	defer appConn.Close()

	if err := WriteTunnelHeader(appConn, TypeAppTunnel, session.ID); err != nil {
		t.Fatalf("write app header failed: %v", err)
	}

	// 等待桥接建立
	time.Sleep(100 * time.Millisecond)

	// 通过 PC 端发送数据，App 端应该收到
	data := make([]byte, 256)
	rand.Read(data)

	pcConn.SetWriteDeadline(time.Now().Add(2 * time.Second))
	if _, err := pcConn.Write(data); err != nil {
		t.Fatalf("pc write failed: %v", err)
	}

	received := make([]byte, 256)
	appConn.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := io.ReadFull(appConn, received); err != nil {
		t.Fatalf("app read failed: %v", err)
	}

	// 验证数据一致
	for i := range data {
		if data[i] != received[i] {
			t.Fatalf("data mismatch at byte %d: sent %d, got %d", i, data[i], received[i])
		}
	}
}

func TestTunnelListener_UnknownSession(t *testing.T) {
	mgr := NewManager()

	listener := NewTunnelListener(mgr, "127.0.0.1:0")
	if err := listener.Start(); err != nil {
		t.Fatalf("start listener failed: %v", err)
	}
	defer listener.Stop()

	// 用不存在的 session ID 连接
	conn, err := net.Dial("tcp", listener.listener.Addr().String())
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	defer conn.Close()

	// 写入头：类型 + 37 字节 session ID
	header := make([]byte, 1+SessionIDLen)
	header[0] = TypePCTunnel
	copy(header[1:], "sess-00000000000000000000000000000000")
	conn.SetWriteDeadline(time.Now().Add(2 * time.Second))
	conn.Write(header)

	// 服务器应该关闭连接（未知 session）
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 1)
	_, err = conn.Read(buf)
	if err == nil {
		// 如果没出错，继续读应该会 EOF
		_, err = conn.Read(buf)
	}
	if err == nil {
		t.Error("expected connection to be closed for unknown session")
	}
}

func TestSessionIDLen_MatchesGeneratedID(t *testing.T) {
	mgr := NewManager()
	session, _ := mgr.CreateSession("device-check")

	if len(session.ID) != SessionIDLen {
		t.Errorf("session ID length mismatch: got %d, expected %d (SessionIDLen)", len(session.ID), SessionIDLen)
	}
}
