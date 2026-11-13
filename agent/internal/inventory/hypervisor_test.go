package inventory

import "testing"

func TestHypervisorExtraReadsVirshAndProxmox(t *testing.T) {
	kvm := Sections("##VIRSH\n@@web 01\nId:             3\nName:           web 01\nState:          running\nCPU(s):         2\nMax memory:     4194304 KiB\nIP: 192.168.122.10/24\n@@db\nState:          shut off\nCPU(s):         4\nMax memory:     8388608 KiB\n##QM\n##PCT\n##PVE\n##VEND\n")
	x := HypervisorExtra(kvm, "kvm01")
	vms := x["vms"].([]map[string]any)
	if x["platform"] != "kvm" || len(vms) != 2 || vms[0]["name"] != "web 01" || vms[0]["power"] != "On" || vms[0]["cpus"] != 2 || vms[0]["memory_bytes"] != int64(4294967296) || vms[0]["ip"] != "192.168.122.10" || vms[1]["power"] != "Off" {
		t.Fatalf("kvm = %v", x)
	}
	pve := Sections("##VIRSH\n##QM\n      VMID NAME                 STATUS     MEM(MB)    BOOTDISK(GB) PID\n       100 win2022              running    8192             100.00 2214\n       101 ubuntu               stopped    2048              32.00 0\n##PCT\nVMID       Status     Lock         Name\n200        running                 pihole\n201        stopped    backup       nginx\n##PVE\npve-manager/8.2.4/faa83925c9641325 (running kernel: 6.8.8-2-pve)\n##VEND\n")
	x = HypervisorExtra(pve, "pve01")
	vms = x["vms"].([]map[string]any)
	if x["platform"] != "proxmox" || x["api"] != "pve-manager/8.2.4/faa83925c9641325 (running kernel: 6.8.8-2-pve)" || len(vms) != 4 {
		t.Fatalf("pve = %v", x)
	}
	if vms[0]["name"] != "win2022" || vms[0]["memory_bytes"] != int64(8192)<<20 || vms[0]["disk_bytes"] != int64(100*1073741824) || vms[1]["power"] != "Off" {
		t.Fatalf("qm = %v", vms[:2])
	}
	if vms[2]["name"] != "pihole" || vms[2]["kind"] != "Container" || vms[3]["name"] != "nginx" || vms[3]["power"] != "Off" {
		t.Fatalf("pct = %v", vms[2:])
	}
	if HypervisorExtra(Sections("##VIRSH\n##QM\n##PCT\n##PVE\n##VEND\n"), "x") != nil {
		t.Fatal("a plain Linux computer has no hypervisor table")
	}
}

func TestWindowsInventoryWithOneHyperVMachine(t *testing.T) {
	inv, err := ParseWindowsJSON([]byte(`{"hostname":"HV01","os_family":"windows","cpu":{"count":2},"disks":[],"nics":[],"installed_apps":[],"antivirus":[],
		"hardware_extra":{"platform":"hyperv","api":"Microsoft Hyper-V on Windows Server 2022","vms":{"name":"DC01","power":"On","cpus":2,"memory_bytes":4294967296}}}`))
	if err != nil {
		t.Fatal(err)
	}
	vms, ok := inv.Extra["vms"].([]any)
	if !ok || len(vms) != 1 || vms[0].(map[string]any)["name"] != "DC01" {
		t.Fatalf("vms = %#v", inv.Extra["vms"])
	}
}
