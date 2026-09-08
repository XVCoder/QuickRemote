package registry

import (
	"database/sql"
	"fmt"
	"time"

	_ "modernc.org/sqlite"
)

type Device struct {
	DeviceID    string    `json:"device_id"`
	MachineID   string    `json:"machine_id"`
	Hostname    string    `json:"hostname"`
	DisplayName string    `json:"display_name"`
	OS          string    `json:"os"`
	LanIP       string    `json:"lan_ip"`
	RDPPort     int       `json:"rdp_port"`
	Version     string    `json:"version"`
	Status      string    `json:"status"`
	LastSeen    time.Time `json:"last_seen"`
}

type Registry struct {
	db *sql.DB
}

func New(dbPath string) (*Registry, error) {
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	r := &Registry{db: db}
	if err := r.initSchema(); err != nil {
		db.Close()
		return nil, err
	}
	return r, nil
}

func (r *Registry) initSchema() error {
	_, err := r.db.Exec(`
		CREATE TABLE IF NOT EXISTS devices (
			device_id   TEXT PRIMARY KEY,
			machine_id  TEXT UNIQUE NOT NULL,
			hostname    TEXT NOT NULL,
			os          TEXT NOT NULL,
			lan_ip      TEXT NOT NULL DEFAULT '',
			rdp_port    INTEGER NOT NULL,
			version     TEXT NOT NULL,
			status      TEXT NOT NULL DEFAULT 'online',
			last_seen   DATETIME NOT NULL
		)
	`)
	if err != nil {
		return err
	}
	// 兼容旧库：已有表缺 lan_ip 列时补充（SQLite 不支持 ADD COLUMN IF NOT EXISTS）
	var count int
	if err := r.db.QueryRow(`SELECT COUNT(*) FROM pragma_table_info('devices') WHERE name = 'lan_ip'`).Scan(&count); err != nil {
		return fmt.Errorf("check lan_ip column: %w", err)
	}
	if count == 0 {
		if _, err := r.db.Exec(`ALTER TABLE devices ADD COLUMN lan_ip TEXT NOT NULL DEFAULT ''`); err != nil {
			return fmt.Errorf("add lan_ip column: %w", err)
		}
	}
	// 兼容旧库：display_name（PC 端自定义设备名，空 = 未分配默认名）
	if err := r.db.QueryRow(`SELECT COUNT(*) FROM pragma_table_info('devices') WHERE name = 'display_name'`).Scan(&count); err != nil {
		return fmt.Errorf("check display_name column: %w", err)
	}
	if count == 0 {
		if _, err := r.db.Exec(`ALTER TABLE devices ADD COLUMN display_name TEXT NOT NULL DEFAULT ''`); err != nil {
			return fmt.Errorf("add display_name column: %w", err)
		}
	}
	return nil
}

// Register 注册/刷新设备。返回 (deviceID, displayName)。
// display_name 语义：客户端上报非空 = 用户设置的自定义名（覆盖旧值）；
// 上报空 = 客户端未设置（保留库中原值；库中也为空则首次注册分配默认名
// "PC客户端<rowid>"，rowid 为插入序号，编号唯一稳定且永不复用）。
func (r *Registry) Register(dev *Device) (string, string, error) {
	deviceID := dev.MachineID
	now := time.Now()

	_, err := r.db.Exec(`
		INSERT INTO devices (device_id, machine_id, hostname, display_name, os, lan_ip, rdp_port, version, status, last_seen)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'online', ?)
		ON CONFLICT(machine_id) DO UPDATE SET
			hostname = excluded.hostname,
			display_name = CASE WHEN excluded.display_name != '' THEN excluded.display_name ELSE devices.display_name END,
			os = excluded.os,
			lan_ip = excluded.lan_ip,
			rdp_port = excluded.rdp_port,
			version = excluded.version,
			status = 'online',
			last_seen = excluded.last_seen
	`, deviceID, dev.MachineID, dev.Hostname, dev.DisplayName, dev.OS, dev.LanIP, dev.RDPPort, dev.Version, now)

	if err != nil {
		return "", "", fmt.Errorf("register device: %w", err)
	}

	// 首次注册（或从未分配过名字的旧设备）：按插入序号分配默认名
	_, err = r.db.Exec(`
		UPDATE devices SET display_name = 'PC客户端' || rowid
		WHERE device_id = ? AND display_name = ''
	`, deviceID)
	if err != nil {
		return "", "", fmt.Errorf("assign default display_name: %w", err)
	}

	var displayName string
	if err := r.db.QueryRow(`SELECT display_name FROM devices WHERE device_id = ?`, deviceID).Scan(&displayName); err != nil {
		return "", "", fmt.Errorf("read display_name: %w", err)
	}
	return deviceID, displayName, nil
}

func (r *Registry) ListOnline() ([]Device, error) {
	rows, err := r.db.Query(`
		SELECT device_id, machine_id, hostname, display_name, os, lan_ip, rdp_port, version, status, last_seen
		FROM devices WHERE status = 'online'
		ORDER BY hostname
	`)
	if err != nil {
		return nil, fmt.Errorf("list devices: %w", err)
	}
	defer rows.Close()

	var devices []Device
	for rows.Next() {
		var d Device
		if err := rows.Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.DisplayName, &d.OS, &d.LanIP, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen); err != nil {
			return nil, err
		}
		devices = append(devices, d)
	}
	return devices, nil
}

// ListAll 返回全部设备（含离线），在线在前、名称序。PC 端"远程设备"列表数据源。
func (r *Registry) ListAll() ([]Device, error) {
	rows, err := r.db.Query(`
		SELECT device_id, machine_id, hostname, display_name, os, lan_ip, rdp_port, version, status, last_seen
		FROM devices
		ORDER BY CASE status WHEN 'online' THEN 0 ELSE 1 END, display_name
	`)
	if err != nil {
		return nil, fmt.Errorf("list all devices: %w", err)
	}
	defer rows.Close()

	var devices []Device
	for rows.Next() {
		var d Device
		if err := rows.Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.DisplayName, &d.OS, &d.LanIP, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen); err != nil {
			return nil, err
		}
		devices = append(devices, d)
	}
	return devices, nil
}

// Rename 更新设备自定义名称（PC 端设置即时生效）。
func (r *Registry) Rename(deviceID string, displayName string) error {
	_, err := r.db.Exec(`UPDATE devices SET display_name = ? WHERE device_id = ?`, displayName, deviceID)
	return err
}

func (r *Registry) UpdateHeartbeat(deviceID string) error {
	_, err := r.db.Exec(`
		UPDATE devices SET last_seen = ?, status = 'online' WHERE device_id = ?
	`, time.Now(), deviceID)
	return err
}

func (r *Registry) MarkOffline(deviceID string) error {
	_, err := r.db.Exec(`
		UPDATE devices SET status = 'offline' WHERE device_id = ?
	`, deviceID)
	return err
}

func (r *Registry) GetByDeviceID(deviceID string) (*Device, error) {
	var d Device
	err := r.db.QueryRow(`
		SELECT device_id, machine_id, hostname, display_name, os, lan_ip, rdp_port, version, status, last_seen
		FROM devices WHERE device_id = ?
	`, deviceID).Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.DisplayName, &d.OS, &d.LanIP, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen)
	if err != nil {
		return nil, err
	}
	return &d, nil
}

func (r *Registry) MarkStaleOffline(timeout time.Duration) {
	cutoff := time.Now().Add(-timeout)
	r.db.Exec(`UPDATE devices SET status = 'offline' WHERE last_seen < ? AND status = 'online'`, cutoff)
}

func (r *Registry) Close() error {
	return r.db.Close()
}
