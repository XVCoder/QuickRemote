package auth

import (
	"crypto/sha256"
	"encoding/hex"
	"testing"
	"time"
)

func TestVerifyPreSharedKey(t *testing.T) {
	secret := "my-secret-key"
	hash := sha256.Sum256([]byte(secret))
	hashHex := hex.EncodeToString(hash[:])

	if !VerifyPreSharedKey(hashHex, secret) {
		t.Error("expected verification to succeed")
	}
	if VerifyPreSharedKey("wrong-hash", secret) {
		t.Error("expected verification to fail with wrong hash")
	}
}

func TestGenerateToken(t *testing.T) {
	service := New("jwt-secret", "my-secret-key", time.Hour)
	token, err := service.GenerateToken("device-123")
	if err != nil {
		t.Fatalf("GenerateToken failed: %v", err)
	}
	if token == "" {
		t.Error("expected non-empty token")
	}

	claims, err := service.ValidateToken(token)
	if err != nil {
		t.Fatalf("ValidateToken failed: %v", err)
	}
	if claims.DeviceID != "device-123" {
		t.Errorf("expected device ID 'device-123', got '%s'", claims.DeviceID)
	}
}

func TestValidateToken_Invalid(t *testing.T) {
	service := New("jwt-secret", "my-secret-key", time.Hour)
	_, err := service.ValidateToken("invalid-token")
	if err == nil {
		t.Error("expected error for invalid token")
	}
}
