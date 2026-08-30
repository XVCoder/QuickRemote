package control

import (
	"crypto/tls"
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
	MaxMessageSize   = 65536
	HeartbeatTimeout = 60 * time.Second
)

// Message 是控制连接协议的消息结构体。
// Status 字段用于在 ack 消息中返回操作状态（如 "ok"、"auth_failed" 等）。
type Message struct {
	Type       string `json:"type"`
	Status     string `json:"status,omitempty"`
	MachineID  string `json:"machine_id,omitempty"`
	Hostname   string `json:"hostname,omitempty"`
	OS         string `json:"os,omitempty"`
	LanIP      string `json:"lan_ip,omitempty"`
	RDPPort    int    `json:"rdp_port,omitempty"`
	Version    string `json:"version,omitempty"`
	AuthKey    string `json:"auth_key,omitempty"`
	DeviceID   string `json:"device_id,omitempty"`
	SessionID  string `json:"session_id,omitempty"`
	TunnelPort int    `json:"tunnel_port,omitempty"`
	Timestamp  int64  `json:"timestamp,omitempty"`
}

// ClientConn 表示一个已注册 PC 的控制连接。
type ClientConn struct {
	DeviceID string
	Conn     net.Conn
	mu       sync.Mutex
}

// SendMessage 向 PC 客户端发送一条消息（线程安全）。
func (c *ClientConn) SendMessage(msg Message) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return writeMessage(c.Conn, msg)
}

// Server 是 PC 控制连接服务器，负责处理注册、心跳和隧道通知。
type Server struct {
	authService *auth.Service
	registry    *registry.Registry
	tunnelMgr   *tunnel.Manager
	listener    net.Listener
	clients     sync.Map // map[string]*ClientConn (deviceID -> connection)
	tunnelPort  int      // 下一个可用的隧道端口
	tunnelStart int
	mu          sync.Mutex
	tlsConfig   *tls.Config // TLS 配置（可选，nil 表示明文）
}

// NewServer 创建一个新的控制连接服务器。
func NewServer(authService *auth.Service, reg *registry.Registry, tunnelMgr *tunnel.Manager, tunnelPortStart int) *Server {
	return &Server{
		authService: authService,
		registry:    reg,
		tunnelMgr:   tunnelMgr,
		tunnelStart: tunnelPortStart,
		tunnelPort:  tunnelPortStart,
	}
}

// SetTLSConfig 设置 TLS 配置，启用 TLS 加密的控制连接。
func (s *Server) SetTLSConfig(certFile, keyFile string) error {
	cert, err := tls.LoadX509KeyPair(certFile, keyFile)
	if err != nil {
		return fmt.Errorf("load TLS cert/key: %w", err)
	}
	s.tlsConfig = &tls.Config{Certificates: []tls.Certificate{cert}}
	return nil
}

// Start 启动控制连接监听。如果配置了 TLS，使用 TLS 监听。
func (s *Server) Start(addr string) error {
	var err error
	if s.tlsConfig != nil {
		s.listener, err = tls.Listen("tcp", addr, s.tlsConfig)
		if err != nil {
			return err
		}
		log.Printf("control server listening on %s (TLS)", addr)
	} else {
		s.listener, err = net.Listen("tcp", addr)
		if err != nil {
			return err
		}
		log.Printf("control server listening on %s (plain)", addr)
	}

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
		writeMessage(conn, Message{Type: "register_ack", Status: "bad_request"})
		return
	}

	// 验证预共享密钥
	if !s.authService.VerifyPreSharedKey(msg.AuthKey) {
		writeMessage(conn, Message{Type: "register_ack", Status: "auth_failed"})
		return
	}

	// 注册设备
	dev := &registry.Device{
		MachineID: msg.MachineID,
		Hostname:  msg.Hostname,
		OS:        msg.OS,
		LanIP:     msg.LanIP,
		RDPPort:   msg.RDPPort,
		Version:   msg.Version,
	}
	deviceID, err := s.registry.Register(dev)
	if err != nil {
		log.Printf("register device failed: %v", err)
		writeMessage(conn, Message{Type: "register_ack", Status: "register_failed"})
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
			client.SendMessage(Message{Type: "heartbeat_ack", Status: "ok"})

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

// NotifyTunnelRequest 通知 PC 客户端建立隧道数据连接。
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

// Close 关闭控制连接服务器。
func (s *Server) Close() error {
	if s.listener != nil {
		return s.listener.Close()
	}
	return nil
}

// Addr 返回监听地址。
func (s *Server) Addr() string {
	if s.listener != nil {
		return s.listener.Addr().String()
	}
	return ""
}

// readMessage 从连接读取一条消息（4 字节大端长度前缀 + JSON 载荷）。
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

// writeMessage 向连接写入一条消息（4 字节大端长度前缀 + JSON 载荷）。
// 直接序列化 Message 结构体，确保 Status 等所有字段都被正确编码。
func writeMessage(conn net.Conn, msg Message) error {
	data, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	lenBuf := make([]byte, 4)
	binary.BigEndian.PutUint32(lenBuf, uint32(len(data)))
	_, err = conn.Write(append(lenBuf, data...))
	return err
}
