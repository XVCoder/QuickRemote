package auth

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

type Claims struct {
	DeviceID string `json:"device_id"`
	jwt.RegisteredClaims
}

type Service struct {
	jwtSecret    string
	preSharedKey string
	tokenTTL     time.Duration
}

func New(jwtSecret, preSharedKey string, tokenTTL time.Duration) *Service {
	return &Service{
		jwtSecret:    jwtSecret,
		preSharedKey: preSharedKey,
		tokenTTL:     tokenTTL,
	}
}

func VerifyPreSharedKey(hashHex, secret string) bool {
	hash := sha256.Sum256([]byte(secret))
	return hex.EncodeToString(hash[:]) == hashHex
}

func (s *Service) VerifyPreSharedKey(hashHex string) bool {
	return VerifyPreSharedKey(hashHex, s.preSharedKey)
}

func (s *Service) GenerateToken(deviceID string) (string, error) {
	claims := Claims{
		DeviceID: deviceID,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(time.Now().Add(s.tokenTTL)),
			IssuedAt:  jwt.NewNumericDate(time.Now()),
		},
	}
	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	return token.SignedString([]byte(s.jwtSecret))
}

func (s *Service) ValidateToken(tokenString string) (*Claims, error) {
	claims := &Claims{}
	token, err := jwt.ParseWithClaims(tokenString, claims, func(token *jwt.Token) (interface{}, error) {
		if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, errors.New("unexpected signing method")
		}
		return []byte(s.jwtSecret), nil
	})
	if err != nil {
		return nil, err
	}
	if !token.Valid {
		return nil, errors.New("invalid token")
	}
	return claims, nil
}
