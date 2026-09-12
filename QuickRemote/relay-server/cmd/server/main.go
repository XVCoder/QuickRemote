package main

import (
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"github.com/quickremote/relay-server/internal/api"
	"github.com/quickremote/relay-server/internal/auth"
	"github.com/quickremote/relay-server/internal/config"
	"github.com/quickremote/relay-server/internal/control"
	"github.com/quickremote/relay-server/internal/registry"
	"github.com/quickremote/relay-server/internal/tunnel"
)

// Version 是中转服务器的版本号，可在编译时通过 -ldflags="-X main.Version=x.y.z" 注入。
var Version = "1.0.8"

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

	// 启动隧道数据监听器（PC 与 App 的数据桥接）
	tunnelAddr := cfg.Server.TunnelListen
	if tunnelAddr == "" {
		tunnelAddr = ":8445"
	}
	tunnelListener := tunnel.NewTunnelListener(tunnelMgr, tunnelAddr)
	if err := tunnelListener.Start(); err != nil {
		log.Fatalf("start tunnel listener failed: %v", err)
	}
	defer tunnelListener.Stop()
	log.Printf("tunnel listener started on port %d", tunnelListener.Port())

	// 初始化控制服务器（tunnelListener 供 PC→PC 远程控制的 tunnel_open 注册会话）
	controlSrv := control.NewServer(authService, reg, tunnelMgr, tunnelListener, 9100)

	// 如果配置了 TLS 证书，为控制连接启用 TLS
	if cfg.Server.TLS.Cert != "" && cfg.Server.TLS.Key != "" {
		if err := controlSrv.SetTLSConfig(cfg.Server.TLS.Cert, cfg.Server.TLS.Key); err != nil {
			log.Fatalf("set control TLS config failed: %v", err)
		}
		log.Println("control connection TLS enabled")
	}

	// 启动控制连接监听（PC 的 TLS 连接）
	// 控制连接端口 = HTTP 端口 + 1
	controlAddr := getControlAddr(cfg.Server.Listen)
	if err := controlSrv.Start(controlAddr); err != nil {
		log.Fatalf("start control server failed: %v", err)
	}
	defer controlSrv.Close()

	// 初始化 API 处理器
	apiHandler := api.New(authService, reg, tunnelMgr, tunnelListener, controlSrv, cfg.QuickDeploy.BaseURL, cfg.QuickDeploy.UploadToken)

	// 启动心跳超时检查（同时周期广播设备列表，兜底陈旧清理导致的离线状态变化）
	go func() {
		ticker := time.NewTicker(30 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			reg.MarkStaleOffline(60 * time.Second)
			controlSrv.BroadcastDeviceList()
		}
	}()

	// 配置 TLS（由 nginx 反向代理处理，服务器本身监听明文 HTTP）
	if cfg.Server.TLS.Cert != "" || cfg.Server.TLS.Key != "" {
		log.Println("note: TLS cert/key in config is ignored — terminate TLS at nginx reverse proxy")
	}

	// 启动 HTTP 服务器（明文，由 nginx 反向代理提供 HTTPS）
	server := &http.Server{
		Addr:    cfg.Server.Listen,
		Handler: apiHandler.Routes(),
	}

	log.Printf("QuickRemote Relay Server v%s starting on %s", Version, cfg.Server.Listen)
	log.Printf("control connection listening on %s", controlAddr)
	log.Println("TLS is terminated at reverse proxy (nginx), server listens plain HTTP/TCP")

	go func() {
		if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("HTTP server error: %v", err)
		}
	}()

	// 优雅关闭
	quit := make(chan os.Signal, 1)
	signal.Notify(quit, syscall.SIGINT, syscall.SIGTERM)
	<-quit

	log.Println("shutting down...")
	server.Close()
}

// getControlAddr 根据HTTP监听地址计算控制连接监听地址。
// 控制连接端口 = HTTP 端口 + 1（如 HTTP 是 :8443，控制连接是 :8444）。
func getControlAddr(listen string) string {
	_, port, err := net.SplitHostPort(listen)
	if err != nil {
		return ":8444"
	}
	portNum, err := strconv.Atoi(port)
	if err != nil {
		return ":8444"
	}
	return fmt.Sprintf(":%d", portNum+1)
}
