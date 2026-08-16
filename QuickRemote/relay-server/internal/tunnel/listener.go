package tunnel

import (
	"io"
	"log"
	"net"
	"sync"
)

const (
	TypePCTunnel  = 0x01
	TypeAppTunnel = 0x02
	// SessionIDLen 是 session ID 的字节长度。
	// tunnel.go 中 CreateSession 生成 "sess-" + hex.EncodeToString(16 bytes)，
	// 即 "sess-" (5 字节) + 32 hex 字符 = 37 字节。
	SessionIDLen = 37
)

// pendingSession 跟踪等待两端连接的隧道会话。
type pendingSession struct {
	session *Session
	pcConn  net.Conn
	appConn net.Conn
	ready   chan struct{}
	mu      sync.Mutex
}

// TunnelListener 监听 PC 和 App 的隧道数据连接。
type TunnelListener struct {
	mgr      *Manager
	addr     string
	listener net.Listener
	port     int
	pending  sync.Map // map[string]*pendingSession
}

// NewTunnelListener 创建一个新的隧道监听器。
func NewTunnelListener(mgr *Manager, addr string) *TunnelListener {
	return &TunnelListener{
		mgr:  mgr,
		addr: addr,
	}
}

// Start 启动隧道监听器。
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

// Port 返回监听端口。
func (tl *TunnelListener) Port() int {
	return tl.port
}

// RegisterSession 注册一个待连接的隧道会话。
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

	// 读取 session_id（37 字节）
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
		pending.mu.Unlock()

		tl.pending.Delete(sessionID)
		go tl.mgr.Bridge(sessionID, pcConn, appConn)
	} else {
		pending.mu.Unlock()
	}
}

// Stop 关闭隧道监听器。
func (tl *TunnelListener) Stop() error {
	if tl.listener != nil {
		return tl.listener.Close()
	}
	return nil
}

// WriteTunnelHeader 写入隧道连接头（类型 + session_id）。
func WriteTunnelHeader(conn net.Conn, connType byte, sessionID string) error {
	header := make([]byte, 1+SessionIDLen)
	header[0] = connType
	copy(header[1:], sessionID)
	_, err := conn.Write(header)
	return err
}
