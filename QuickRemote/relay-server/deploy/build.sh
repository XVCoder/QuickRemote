#!/bin/bash

# QuickRemote Relay Server 交叉编译脚本
# 生成 arm64 和 amd64 两个架构的二进制
# 编译后通过 quickdeploy-mcp 上传，并更新 manifest.json

set -e

VERSION=${1:-"1.0.0"}
OUTPUT_DIR="./dist"

mkdir -p "$OUTPUT_DIR"

echo "Building QuickRemote Relay Server v$VERSION"

# amd64
echo "Building for linux/amd64..."
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -ldflags="-s -w -X main.Version=$VERSION" -o "$OUTPUT_DIR/quickremote-relay-v${VERSION}-amd64" ./cmd/server

# arm64
echo "Building for linux/arm64..."
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -ldflags="-s -w -X main.Version=$VERSION" -o "$OUTPUT_DIR/quickremote-relay-v${VERSION}-arm64" ./cmd/server

echo "Build complete:"
ls -lh "$OUTPUT_DIR/"

echo ""
echo "下一步: 通过 quickdeploy-mcp 上传文件"
echo "  1. 上传二进制文件 (permanent_share=true)"
echo "  2. 记录返回的 share_url"
echo "  3. 更新 manifest.json (overwrite=true)"
echo "  4. 上传 install.sh (overwrite=true, 如果脚本有更新)"
echo "  5. 上传 CHANGELOG.md (overwrite=true)"
