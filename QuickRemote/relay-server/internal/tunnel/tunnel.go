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
