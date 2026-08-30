package registry

import (
	"database/sql"
	"fmt"
	"time"

	_ "modernc.org/sqlite"
)

type Device struct {
	DeviceID  string    `json:"device_id"`
	MachineID string    `json:"machine_id"`
	Hostname  string    `json:"hostname"`
	OS        string    `json:"os"`
	LanIP     string    `json:"lan_ip"`
	RDPPort   int       `json:"rdp_port"`
	Version   string    `json:"version"`
	Status    string    `json:"status"`
	LastSeen  time.Time `json:"last_seen"`
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
	return nil
}

func (r *Registry) Register(dev *Device) (string, error) {
	deviceID := dev.MachineID
	now := time.Now()

	_, err := r.db.Exec(`
		INSERT INTO devices (device_id, machine_id, hostname, os, lan_ip, rdp_port, version, status, last_seen)
		VALUES (?, ?, ?, ?, ?, ?, ?, 'online', ?)
		ON CONFLICT(machine_id) DO UPDATE SET
			hostname = excluded.hostname,
			os = excluded.os,
			lan_ip = excluded.lan_ip,
			rdp_port = excluded.rdp_port,
			version = excluded.version,
			status = 'online',
			last_seen = excluded.last_seen
	`, deviceID, dev.MachineID, dev.Hostname, dev.OS, dev.LanIP, dev.RDPPort, dev.Version, now)

	if err != nil {
		return "", fmt.Errorf("register device: %w", err)
	}
	return deviceID, nil
}

func (r *Registry) ListOnline() ([]Device, error) {
	rows, err := r.db.Query(`
		SELECT device_id, machine_id, hostname, os, lan_ip, rdp_port, version, status, last_seen
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
		if err := rows.Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.OS, &d.LanIP, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen); err != nil {
			return nil, err
		}
		devices = append(devices, d)
	}
	return devices, nil
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
		SELECT device_id, machine_id, hostname, os, lan_ip, rdp_port, version, status, last_seen
		FROM devices WHERE device_id = ?
	`, deviceID).Scan(&d.DeviceID, &d.MachineID, &d.Hostname, &d.OS, &d.LanIP, &d.RDPPort, &d.Version, &d.Status, &d.LastSeen)
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
