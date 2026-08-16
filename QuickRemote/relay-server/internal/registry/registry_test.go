package registry

import (
	"testing"
	"time"
)

func NewTestRegistry(t *testing.T) *Registry {
	t.Helper()
	reg, err := New(t.TempDir() + "/test.db")
	if err != nil {
		t.Fatalf("failed to create test registry: %v", err)
	}
	return reg
}

func TestRegisterAndGetDevice(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	dev := &Device{
		MachineID: "machine-001",
		Hostname:  "PC-OFFICE",
		OS:        "Windows 11 Pro",
		RDPPort:   3389,
		Version:   "1.0.0",
	}

	deviceID, err := reg.Register(dev)
	if err != nil {
		t.Fatalf("Register failed: %v", err)
	}
	if deviceID == "" {
		t.Error("expected non-empty device ID")
	}

	// 同一台机器再次注册应该更新而非新增
	deviceID2, err := reg.Register(dev)
	if err != nil {
		t.Fatalf("Re-register failed: %v", err)
	}
	if deviceID2 != deviceID {
		t.Errorf("expected same device ID on re-register, got '%s' vs '%s'", deviceID2, deviceID)
	}
}

func TestListOnlineDevices(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	reg.Register(&Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	})
	reg.Register(&Device{
		MachineID: "m2", Hostname: "PC2", OS: "Win10", RDPPort: 3389, Version: "1.0.0",
	})

	devices, err := reg.ListOnline()
	if err != nil {
		t.Fatalf("ListOnline failed: %v", err)
	}
	if len(devices) != 2 {
		t.Errorf("expected 2 devices, got %d", len(devices))
	}
}

func TestMarkOffline(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	dev := &Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	}
	deviceID, _ := reg.Register(dev)

	err := reg.MarkOffline(deviceID)
	if err != nil {
		t.Fatalf("MarkOffline failed: %v", err)
	}

	devices, _ := reg.ListOnline()
	if len(devices) != 0 {
		t.Errorf("expected 0 online devices, got %d", len(devices))
	}
}

func TestUpdateHeartbeat(t *testing.T) {
	reg := NewTestRegistry(t)
	defer reg.Close()

	dev := &Device{
		MachineID: "m1", Hostname: "PC1", OS: "Win11", RDPPort: 3389, Version: "1.0.0",
	}
	deviceID, _ := reg.Register(dev)

	oldTime := time.Now()
	time.Sleep(10 * time.Millisecond)
	reg.UpdateHeartbeat(deviceID)

	devices, _ := reg.ListOnline()
	if len(devices) != 1 {
		t.Fatalf("expected 1 online device")
	}
	if !devices[0].LastSeen.After(oldTime) {
		t.Error("expected LastSeen to be updated")
	}
}
