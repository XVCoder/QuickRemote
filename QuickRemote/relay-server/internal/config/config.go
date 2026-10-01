package config

import (
	"os"

	"gopkg.in/yaml.v3"
)

type Config struct {
	Server      ServerConfig      `yaml:"server"`
	Auth        AuthConfig        `yaml:"auth"`
	Admin       AdminConfig       `yaml:"admin"`
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

// AdminConfig 管理员模式配置（v1.0.9）。
//
// Password 是管理员密码，客户端（PC/安卓）输入正确后才能进入管理员模式，
// 查看被软删除的设备并对设备执行物理删除。
//
// 默认值的取舍：存量部署的 config.yaml 里没有 admin 段，若按"空密码"处理会让
// 管理员模式对任何人开放（等于所有主机都能被物理删除），风险过大；所以 Load 时
// 缺失即回填 DefaultAdminPassword("88888888")，保证老用户升级后功能可用且行为可预期。
// 全新安装由 deploy/install.sh 随机生成 6 位数字（或用户自定义），不走这个默认值。
type AdminConfig struct {
	Password string `yaml:"password"`
}

// DefaultAdminPassword 是存量部署（config.yaml 无 admin.password）使用的默认管理员密码。
const DefaultAdminPassword = "88888888"

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
		Admin:   AdminConfig{Password: DefaultAdminPassword},
	}
	if err := yaml.Unmarshal(data, cfg); err != nil {
		return nil, err
	}
	// 存量配置缺 admin.password（空串）→ 回填默认密码，避免管理员模式对全网开放
	if cfg.Admin.Password == "" {
		cfg.Admin.Password = DefaultAdminPassword
	}
	return cfg, nil
}
