package control

import (
	"crypto/tls"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"strings"
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
	Type        string            `json:"type"`
	Status      string            `json:"status,omitempty"`
	MachineID   string            `json:"machine_id,omitempty"`
	Hostname    string            `json:"hostname,omitempty"`
	DisplayName string            `json:"display_name,omitempty"`
	OS          string            `json:"os,omitempty"`
	LanIP       string            `json:"lan_ip,omitempty"`
	RDPPort     int               `json:"rdp_port,omitempty"`
	Version     string            `json:"version,omitempty"`
	AuthKey     string            `json:"auth_key,omitempty"`
	DeviceID    string            `json:"device_id,omitempty"`
	TargetDevice string           `json:"target_device,omitempty"`
	SessionID   string            `json:"session_id,omitempty"`
	TunnelPort  int               `json:"tunnel_port,omitempty"`
	TunnelHost  string            `json:"tunnel_host,omitempty"`
	Timestamp   int64             `json:"timestamp,omitempty"`
	// Devices 仅在 device_list 推送消息中携带（全部设备，含离线）。
	Devices []registry.Device `json:"devices,omitempty"`
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
	authService    *auth.Service
	registry       *registry.Registry
	tunnelMgr      *tunnel.Manager
	tunnelListener *tunnel.TunnelListener // PC→PC 隧道请求注册用（tunnel_open）
	listener       net.Listener
	clients        sync.Map // map[string]*ClientConn (deviceID -> connection)
	tunnelPort     int      // 下一个可用的隧道端口
	tunnelStart    int
	mu             sync.Mutex
	tlsConfig      *tls.Config // TLS 配置（可选，nil 表示明文）
}

// NewServer 创建一个新的控制连接服务器。
// tunnelListener 用于 PC→PC 远程控制：处理 tunnel_open 时把新会话注册到隧道监听器。
func NewServer(authService *auth.Service, reg *registry.Registry, tunnelMgr *tunnel.Manager, tunnelListener *tunnel.TunnelListener, tunnelPortStart int) *Server {
	return &Server{
		authService:    authService,
		registry:       reg,
		tunnelMgr:      tunnelMgr,
		tunnelListener: tunnelListener,
		tunnelStart:    tunnelPortStart,
		tunnelPort:     tunnelPortStart,
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
		MachineID:   msg.MachineID,
		Hostname:    msg.Hostname,
		DisplayName: msg.DisplayName,
		OS:          msg.OS,
		LanIP:       msg.LanIP,
		RDPPort:     msg.RDPPort,
		Version:     msg.Version,
	}
	deviceID, displayName, err := s.registry.Register(dev)
	if err != nil {
		log.Printf("register device failed: %v", err)
		writeMessage(conn, Message{Type: "register_ack", Status: "register_failed"})
		return
	}

	client := &ClientConn{
		DeviceID: deviceID,
		Conn:     conn,
	}
	// 同设备重连时替换旧连接并主动关闭旧连接：加速旧连接的消息循环退出，
	// 避免其 60 秒读超时后才清理（清理时只删自己的注册，见 handleDisconnect）
	if prev, loaded := s.clients.Swap(deviceID, client); loaded {
		if old, ok := prev.(*ClientConn); ok {
			log.Printf("device %s re-registered, closing stale connection", deviceID)
			tryClose(old.Conn)
		}
	}

	// 发送注册确认（display_name = 服务器侧最终生效名：客户端 UI 展示/回填）
	writeMessage(conn, Message{
		Type:        "register_ack",
		Status:      "ok",
		DeviceID:    deviceID,
		DisplayName: displayName,
	})

	log.Printf("device registered: %s (%s, display=%s)", deviceID, msg.Hostname, displayName)

	// 设备上线：向所有已连接 PC 广播最新设备列表
	s.BroadcastDeviceList()

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
			s.handleDisconnect(client)
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

		case "rename":
			// PC 修改自己（或同密钥团队内其他设备）的显示名称
			s.handleRename(client, msg)

		case "tunnel_open":
			// PC→PC 远程控制：主控端请求到目标设备的隧道
			s.handleTunnelOpen(client, msg)

		default:
			log.Printf("unknown message type: %s from %s", msg.Type, client.DeviceID)
		}

		// 检查心跳超时
		if time.Since(lastHeartbeat) > HeartbeatTimeout {
			log.Printf("heartbeat timeout for %s", client.DeviceID)
			s.handleDisconnect(client)
			return
		}
	}
}

// handleDisconnect 清理已断开的连接注册。
// 必须校验内存表中该设备的连接仍是自己才删除：
// 同设备重连后（Swap 替换），旧连接的延迟清理（60 秒读超时/EOF）
// 若无条件 Delete，会把新连接的注册误删——此后设备心跳正常（数据库 online）
// 但 NotifyTunnelRequest 找不到连接，App 请求隧道后永远等不到 PC 加入（无限黑屏）。
func (s *Server) handleDisconnect(client *ClientConn) {
	if s.clients.CompareAndDelete(client.DeviceID, client) {
		s.registry.MarkOffline(client.DeviceID)
		log.Printf("device %s marked offline", client.DeviceID)
		// 设备离线：向所有已连接 PC 广播最新设备列表
		s.BroadcastDeviceList()
	} else {
		log.Printf("device %s connection replaced by newer registration, skip cleanup", client.DeviceID)
	}
}

// handleRename 处理设备改名：DeviceID 指定目标设备（空 = 自己），DisplayName 为新名称。
func (s *Server) handleRename(client *ClientConn, msg *Message) {
	target := msg.DeviceID
	if target == "" {
		target = client.DeviceID
	}
	name := strings.TrimSpace(msg.DisplayName)
	if name == "" || len(name) > 64 {
		client.SendMessage(Message{Type: "rename_ack", Status: "invalid_name", DeviceID: target})
		return
	}
	if err := s.registry.Rename(target, name); err != nil {
		log.Printf("rename device %s failed: %v", target, err)
		client.SendMessage(Message{Type: "rename_ack", Status: "rename_failed", DeviceID: target})
		return
	}
	log.Printf("device %s renamed to %q (by %s)", target, name, client.DeviceID)
	client.SendMessage(Message{Type: "rename_ack", Status: "ok", DeviceID: target, DisplayName: name})
	s.BroadcastDeviceList()
}

// handleTunnelOpen 处理 PC→PC 远程控制隧道请求：
// 1. 校验目标设备在线；2. 创建隧道会话并注册到监听器；
// 3. 通知目标设备（被控端）以 PC 角色（0x01）连入；
// 4. 回 tunnel_ack，主控端以 App 角色（0x02）连入同一 session_id。
// 两端连入后由 TunnelListener 桥接，走既有 RemoteFrameProtocol。
func (s *Server) handleTunnelOpen(client *ClientConn, msg *Message) {
	target := msg.TargetDevice
	ack := Message{Type: "tunnel_ack", TargetDevice: target}

	if target == "" || target == client.DeviceID {
		ack.Status = "bad_request"
		client.SendMessage(ack)
		return
	}

	dev, err := s.registry.GetByDeviceID(target)
	if err != nil || dev == nil {
		ack.Status = "device_not_found"
		client.SendMessage(ack)
		return
	}
	if dev.Status != "online" {
		ack.Status = "device_offline"
		client.SendMessage(ack)
		return
	}

	session, err := s.tunnelMgr.CreateSession(target)
	if err != nil {
		log.Printf("create tunnel session failed: %v", err)
		ack.Status = "tunnel_creation_failed"
		client.SendMessage(ack)
		return
	}

	tunnelPort := 0
	if s.tunnelListener != nil {
		s.tunnelListener.RegisterSession(session.ID, session)
		tunnelPort = s.tunnelListener.Port()
	} else {
		ack.Status = "tunnel_unavailable"
		client.SendMessage(ack)
		return
	}

	// 通知被控端连入（复用 Android 隧道请求通道）
	if err := s.NotifyTunnelRequest(target, session.ID, tunnelPort); err != nil {
		log.Printf("notify target device %s failed: %v", target, err)
		ack.Status = "target_unreachable"
		client.SendMessage(ack)
		return
	}

	log.Printf("PC-to-PC tunnel opened: %s -> %s, session=%s", client.DeviceID, target, session.ID)
	ack.Status = "ok"
	ack.SessionID = session.ID
	ack.TunnelPort = tunnelPort
	client.SendMessage(ack)
}

// BroadcastDeviceList 向所有已连接 PC 推送全部设备列表（含离线）。
// 触发时机：设备上/下线、改名、周期兜底（陈旧清理把僵尸连接标记离线）。
func (s *Server) BroadcastDeviceList() {
	devices, err := s.registry.ListAll()
	if err != nil {
		log.Printf("broadcast device list: list all failed: %v", err)
		return
	}
	if devices == nil {
		devices = []registry.Device{}
	}
	msg := Message{Type: "device_list", Devices: devices}
	s.clients.Range(func(_, v any) bool {
		if c, ok := v.(*ClientConn); ok {
			if err := c.SendMessage(msg); err != nil {
				log.Printf("broadcast device list to %s failed: %v", c.DeviceID, err)
			}
		}
		return true
	})
}

// tryClose 尽力关闭连接（忽略错误，用于清理旧连接；Close 会安全唤醒阻塞中的读写）。
func tryClose(c net.Conn) {
	if c == nil {
		return
	}
	_ = c.Close()
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
