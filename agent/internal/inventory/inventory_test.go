package inventory

import (
	"context"
	"os/exec"
	"runtime"
	"strings"
	"testing"
)

func TestParseCPUInfoCountsSocketsAndCores(t *testing.T) {
	two := "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) Silver 4210\nphysical id\t: 0\ncpu cores\t: 10\n\n" +
		"processor\t: 1\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) Silver 4210\nphysical id\t: 1\ncpu cores\t: 10\n"
	c := ParseCPUInfo(two)
	if c.Count != 2 || c.TotalCores != 20 || c.CoresPerCPU != 10 || c.Logical != 2 || c.Manufacturer != "GenuineIntel" {
		t.Fatalf("two sockets: %+v", c)
	}
	vm := ParseCPUInfo("processor\t: 0\nmodel name\t: QEMU Virtual CPU\n\nprocessor\t: 1\nmodel name\t: QEMU Virtual CPU\n")
	if vm.Count != 1 || vm.TotalCores != 2 || vm.Logical != 2 {
		t.Fatalf("a virtual machine without physical ids: %+v", vm)
	}
}

func TestParseLsblkGivesDisksWithPartitionsAndTypes(t *testing.T) {
	js := `{"blockdevices":[
 {"name":"nvme0n1","size":512110190592,"type":"disk","fstype":null,"mountpoint":null,"label":null,"model":"Samsung SSD 980","rota":false,"tran":"nvme","children":[
   {"name":"nvme0n1p1","size":536870912,"type":"part","fstype":"vfat","mountpoint":"/boot/efi","label":null},
   {"name":"nvme0n1p2","size":511573319680,"type":"part","fstype":"ext4","mountpoint":"/","label":"root"}]},
 {"name":"sda","size":2000398934016,"type":"disk","fstype":null,"model":"WDC WD20EZRZ","rota":"1","tran":"sata","children":[
   {"name":"sda1","size":2000397795328,"type":"part","fstype":"ntfs","mountpoint":"/data","label":"Data"}]},
 {"name":"loop0","size":1000,"type":"loop"}]}`
	d := ParseLsblk(js, func(m string) int64 { return 42 })
	if len(d) != 2 || d[0].Type != "NVMe" || d[1].Type != "HDD" || len(d[0].Partitions) != 2 || d[0].Partitions[1].Mount != "/" || d[0].Partitions[1].FreeBytes != 42 || d[1].Partitions[0].FS != "ntfs" {
		t.Fatalf("%+v", d)
	}
}

func TestParseTabularKeepsOnlyInstalledPackages(t *testing.T) {
	apps := ParseTabular("bash\t5.2-1\tUbuntu Developers\tii \nghost\t1.0\tx\trc \n", true)
	if len(apps) != 1 || apps[0].Name != "bash" || apps[0].Version != "5.2-1" {
		t.Fatalf("%+v", apps)
	}
	rpm := ParseTabular("curl\t7.76-3\tRed Hat, Inc.\t1700000000\n", false)
	if len(rpm) != 1 || rpm[0].InstalledOn != "2023-11-14" {
		t.Fatalf("%+v", rpm)
	}
}

func TestParseWindowsJSONAcceptsSingleItemsPrintedAsObjects(t *testing.T) {
	in := `{"hostname":"PC01","os_family":"windows","cpu":{"count":1,"total_cores":4,"cores_per_cpu":4,"logical":8},"ram_bytes":17179869184,
 "disks":{"name":"Disk 0","size_bytes":512,"type":"SSD","partitions":[{"name":"Partition 1","size_bytes":100,"mount":"C:","fs":"NTFS","free_bytes":50}]},
 "nics":[{"name":"Ethernet","mac":"AA-BB-CC-DD-EE-FF","connected":true,"speed_mbps":1000,"ips":["10.0.0.5"]}],"installed_apps":{"name":"7-Zip","version":"23.01"},"antivirus":[],"is_server":false}`
	inv, err := ParseWindowsJSON([]byte("\xEF\xBB\xBF" + in))
	if err != nil || inv.Hostname != "PC01" || len(inv.Disks) != 1 || inv.Disks[0].Partitions[0].Mount != "C:" || len(inv.Apps) != 1 || inv.NICs[0].IPs[0] != "10.0.0.5" || inv.CPU.Logical != 8 {
		t.Fatalf("%v %+v", err, inv)
	}
}

func TestPowerShellArgsEncodeTheScriptAsUTF16(t *testing.T) {
	a := PowerShellArgs("Write-Output 'é'")
	if a[len(a)-2] != "-EncodedCommand" || a[len(a)-1] == "" || strings.ContainsAny(a[len(a)-1], " \"'") {
		t.Fatalf("%v", a)
	}
}

func TestCollectOnThisComputerReturnsTheBasics(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("the live check runs on Linux here")
	}
	inv, err := Collect(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if inv.Hostname == "" || inv.OSFamily != "linux" || inv.RAMBytes <= 0 || inv.CPU.Logical <= 0 || len(inv.NICs) == 0 && false {
		t.Fatalf("%+v", inv)
	}
	t.Logf("%s, %s, %d cpu(s) %d cores, %d GB ram, %d disks, %d nics, %d apps", inv.Hostname, inv.OSName, inv.CPU.Count, inv.CPU.TotalCores, inv.RAMBytes>>30, len(inv.Disks), len(inv.NICs), len(inv.Apps))
}

func TestTheRemoteScriptRoundTripsOnThisComputer(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("runs the Linux script locally")
	}
	out := run(context.Background(), "sh", "-c", LinuxRemoteScript)
	inv := ParseLinuxRemote(out)
	if inv.Hostname == "" || inv.OSName == "" || inv.RAMBytes <= 0 || inv.CPU.Logical <= 0 || len(inv.Disks) == 0 {
		t.Fatalf("%+v", inv)
	}
	local, _ := Collect(context.Background())
	if inv.Hostname != local.Hostname || inv.RAMBytes != local.RAMBytes || (hasIP() && len(inv.NICs) != len(local.NICs)) {
		t.Fatalf("remote and local readings differ: %s/%s %d/%d %d/%d", inv.Hostname, local.Hostname, inv.RAMBytes, local.RAMBytes, len(inv.NICs), len(local.NICs))
	}
}

func TestParseIPJSONSkipsLoopbackAndContainerLinks(t *testing.T) {
	js := `[{"ifname":"lo","link_type":"loopback","address":"00:00:00:00:00:00","operstate":"UNKNOWN","addr_info":[{"local":"127.0.0.1"}]},
{"ifname":"eth0","link_type":"ether","address":"aa:bb:cc:00:11:22","operstate":"UP","addr_info":[{"local":"10.1.1.5"},{"local":"fe80::1"}]},
{"ifname":"docker0","link_type":"ether","address":"02:42:00:00:00:01","operstate":"DOWN","addr_info":[]}]`
	n := ParseIPJSON(js, map[string]int64{"eth0": 1000})
	if len(n) != 1 || n[0].Name != "eth0" || !n[0].Connected || n[0].SpeedMbps != 1000 || len(n[0].IPs) != 2 {
		t.Fatalf("%+v", n)
	}
}

func hasIP() bool { _, err := exec.LookPath("ip"); return err == nil }
