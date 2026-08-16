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
	Listen string `yaml:"listen"`
	// TunnelListen 是隧道数据端口监听地址（PC 与 App 的数据桥接）。
	// 为空时默认使用 ":8445"。
	TunnelListen string    `yaml:"tunnel_listen"`
	TLS          TLSConfig `yaml:"tls"`
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
	// UploadToken 是 quickdeploy 上传令牌，用于把客户端上报的日志上传到 user_logs 目录。
	UploadToken string `yaml:"upload_token"`
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
