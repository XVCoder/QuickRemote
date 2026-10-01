# 更新记录

## v1.0.9 (2026-10-01)

### 新增
- 管理员模式（配合 PC v1.1.77 / Android v1.0.87）：
  - config.yaml 新增 `admin.password`（缺失时回填默认 88888888，存量部署零配置可用）
  - 新增 `POST /api/admin/verify`（密码校验）与 `POST /api/admin/device/delete`（设备物理删除），均需 JWT + 密码双重验证，密码比较为常量时间
  - 新增 `deleted_devices` 黑名单表：物理删除的设备若仍用旧 machine_id 注册，register_ack 返回 `device_deleted`，客户端需重新生成设备 ID；换新 ID 注册时上报 `previous_machine_id` 即清除黑名单记录
  - install.sh：新装随机生成 6 位数字管理员密码（可自定义），存量升级提示设置（默认 88888888），「修改配置」菜单支持修改管理员密码


## v1.0.0 (2026-08-01)

### 新增
- 中转服务器核心功能：设备注册管理、TCP隧道桥接
- HTTP API：认证、设备列表、隧道请求、日志上传
- PC 控制连接协议：注册、心跳、隧道通知
- 关于页面：项目介绍、安装步骤、下载链接、更新记录
- 交互式 install.sh 部署脚本（安装/升级/配置/卸载）
- Docker 多架构构建支持（arm64v8 / amd64）
- TLS 自动生成自签名证书
- SQLite 设备注册表持久化
- 心跳超时自动标记离线
