package config

import (
	"os"
	"testing"
)

func TestLoad(t *testing.T) {
	yamlContent := `
server:
  listen: ":9090"
  tls:
    cert: "/path/cert.pem"
    key: "/path/key.pem"
auth:
  pre_shared_key: "test-key"
  jwt_secret: "test-jwt-secret"
storage:
  sqlite_path: "./test.db"
quickdeploy:
  base_url: "https://deploy.example.com"
`
	tmpFile := t.TempDir() + "/config.yaml"
	os.WriteFile(tmpFile, []byte(yamlContent), 0644)

	cfg, err := Load(tmpFile)
	if err != nil {
		t.Fatalf("Load failed: %v", err)
	}
	if cfg.Server.Listen != ":9090" {
		t.Errorf("expected listen ':9090', got '%s'", cfg.Server.Listen)
	}
	if cfg.Auth.PreSharedKey != "test-key" {
		t.Errorf("expected key 'test-key', got '%s'", cfg.Auth.PreSharedKey)
	}
	if cfg.QuickDeploy.BaseURL != "https://deploy.example.com" {
		t.Errorf("expected quickdeploy url, got '%s'", cfg.QuickDeploy.BaseURL)
	}
}

func TestLoadDefaults(t *testing.T) {
	yamlContent := `
server:
  listen: ":8443"
auth:
  pre_shared_key: "key"
  jwt_secret: "secret"
`
	tmpFile := t.TempDir() + "/config.yaml"
	os.WriteFile(tmpFile, []byte(yamlContent), 0644)

	cfg, err := Load(tmpFile)
	if err != nil {
		t.Fatalf("Load failed: %v", err)
	}
	if cfg.Storage.SQLitePath != "./registry.db" {
		t.Errorf("expected default sqlite path, got '%s'", cfg.Storage.SQLitePath)
	}
}
