#!/usr/bin/env bash
# DeskAsk inventory collector for Linux (RHEL/CentOS/Rocky/Alma 7+, Ubuntu/Debian, SUSE).
# Used by the DeskAsk agent locally and by the portal remotely over SSH.
# Output: one line of JSON on stdout. Read-only. Run as root for serial number / socket details.
export LC_ALL=C PATH="$PATH:/usr/sbin:/sbin:/usr/local/sbin"

j() { # JSON string or null
  local s="$1"
  [ -z "$s" ] && { printf 'null'; return; }
  s=${s//\\/\\\\}; s=${s//\"/\\\"}; s=${s//$'\t'/ }; s=${s//$'\n'/ }; s=${s//$'\r'/}
  s=${s//[$'\001'-$'\037']/}
  printf '"%s"' "$s"
}
n() { [[ "$1" =~ ^[0-9]+$ ]] && printf '%s' "$1" || printf 'null'; }
rd() { [ -r "$1" ] && tr -d '\0' < "$1" 2>/dev/null | head -c 256 | sed 's/[[:space:]]*$//'; }
has() { command -v "$1" >/dev/null 2>&1; }
dmi() { has dmidecode && [ "$(id -u)" = 0 ] && dmidecode -s "$1" 2>/dev/null | grep -v '^#' | head -1 | sed 's/[[:space:]]*$//'; }

# ---- system ----
make=$(rd /sys/class/dmi/id/sys_vendor); [ -z "$make" ] && make=$(dmi system-manufacturer)
model=$(rd /sys/class/dmi/id/product_name); [ -z "$model" ] && model=$(dmi system-product-name)
serial=$(rd /sys/class/dmi/id/product_serial); [ -z "$serial" ] && serial=$(dmi system-serial-number)
bios=$(rd /sys/class/dmi/id/bios_version); [ -z "$bios" ] && bios=$(dmi bios-version)
if [ -z "$make" ] && [ -r /proc/device-tree/model ]; then model=$(rd /proc/device-tree/model); fi

os_name=""; os_ver=""
if [ -r /etc/os-release ]; then . /etc/os-release; os_name="$PRETTY_NAME"; os_ver="$VERSION_ID"; fi
os_ver="$os_ver (kernel $(uname -r))"
host=$(hostname -s 2>/dev/null || hostname)
fqdn=$(hostname -f 2>/dev/null); [ -z "$fqdn" ] && fqdn="$host"

# ---- domain / workgroup ----
dtype="workgroup"; domain=""
if has realm; then domain=$(realm list --name-only 2>/dev/null | head -1); fi
if [ -z "$domain" ] && has wbinfo; then domain=$(wbinfo --own-domain 2>/dev/null); fi
if [ -n "$domain" ]; then dtype="domain"; else
  domain=$(testparm -s --parameter-name=workgroup 2>/dev/null)
  [ -z "$domain" ] && domain=$(dnsdomainname 2>/dev/null)
  [ -z "$domain" ] && domain="WORKGROUP"
fi

# ---- CPU ----
cpu_model=""; cpu_vendor=""; sockets=""; cps=""; logical=""
if has lscpu; then
  while IFS=: read -r k v; do
    v="${v#"${v%%[![:space:]]*}"}"
    case "$k" in
      "Model name") [ -z "$cpu_model" ] && cpu_model="$v" ;;
      "Vendor ID") cpu_vendor="$v" ;;
      "Socket(s)") sockets="$v" ;;
      "Core(s) per socket") cps="$v" ;;
      "CPU(s)") logical="$v" ;;
    esac
  done < <(lscpu 2>/dev/null)
fi
[ -z "$cpu_model" ] && cpu_model=$(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | sed 's/^ *//')
[ -z "$logical" ] && logical=$(grep -c ^processor /proc/cpuinfo)
[ -z "$sockets" ] && sockets=$(grep 'physical id' /proc/cpuinfo | sort -u | wc -l)
[ "$sockets" = 0 ] && sockets=1
socket_name=""
if has dmidecode && [ "$(id -u)" = 0 ]; then socket_name=$(dmidecode -t processor 2>/dev/null | awk -F': ' '/Socket Designation/{print $2; exit}'); fi
cores=""; [[ "$cps" =~ ^[0-9]+$ && "$sockets" =~ ^[0-9]+$ ]] && cores=$((cps * sockets))

# ---- memory ----
ram=""
if has dmidecode && [ "$(id -u)" = 0 ]; then
  ram=$(dmidecode -t 17 2>/dev/null | awk '/^\s*Size: [0-9]+/{ v=$2; u=$3; if(u=="GB") v*=1024; else if(u=="TB") v*=1048576; else if(u=="kB") v/=1024; s+=v } END{ if(s>0) printf "%.0f", s*1048576 }')
fi
[ -z "$ram" ] && ram=$(awk '/MemTotal/{printf "%.0f", $2*1024}' /proc/meminfo)

# ---- disks & partitions ----
declare -A PARENT TYPE SIZE MODEL FS MNT LBL
names=()
if has lsblk; then
  while read -r line; do
    nm=""; [[ $line =~ NAME=\"([^\"]*)\" ]] && nm="${BASH_REMATCH[1]}"; [ -z "$nm" ] && continue
    names+=("$nm")
    [[ $line =~ TYPE=\"([^\"]*)\" ]] && TYPE[$nm]="${BASH_REMATCH[1]}"
    [[ $line =~ SIZE=\"([^\"]*)\" ]] && SIZE[$nm]="${BASH_REMATCH[1]}"
    [[ $line =~ MODEL=\"([^\"]*)\" ]] && MODEL[$nm]="${BASH_REMATCH[1]}"
    [[ $line =~ FSTYPE=\"([^\"]*)\" ]] && FS[$nm]="${BASH_REMATCH[1]}"
    [[ $line =~ MOUNTPOINT=\"([^\"]*)\" ]] && MNT[$nm]="${BASH_REMATCH[1]}"
    [[ $line =~ LABEL=\"([^\"]*)\" ]] && LBL[$nm]="${BASH_REMATCH[1]}"
    [[ $line =~ PKNAME=\"([^\"]*)\" ]] && PARENT[$nm]="${BASH_REMATCH[1]}"
  done < <(lsblk -b -P -o NAME,TYPE,SIZE,MODEL,FSTYPE,MOUNTPOINT,PKNAME,LABEL 2>/dev/null)
fi
rootdisk() { local x="$1" i=0; while [ -n "${PARENT[$x]}" ] && [ $i -lt 8 ]; do x="${PARENT[$x]}"; i=$((i+1)); done; printf '%s' "$x"; }
total_storage=0; disks_json=""
for d in "${names[@]}"; do
  [ "${TYPE[$d]}" = "disk" ] || continue
  case "$d" in loop*|ram*|zram*|sr*|fd*) continue ;; esac
  total_storage=$((total_storage + ${SIZE[$d]:-0}))
  rot=$(rd "/sys/block/$d/queue/rotational"); media="Disk"; [ "$rot" = 0 ] && media="SSD"; [ "$rot" = 1 ] && media="HDD"
  case "$d" in nvme*) media="NVMe SSD" ;; esac
  parts=""; declare -A done_p=()
  for p in "${names[@]}"; do
    [ "$p" = "$d" ] && continue
    [ -n "${done_p[$p]}" ] && continue
    case "${TYPE[$p]}" in part|lvm|crypt|raid*|md) ;; *) continue ;; esac
    [ "$(rootdisk "$p")" = "$d" ] || continue
    done_p[$p]=1
    free="null"
    if [ -n "${MNT[$p]}" ] && [ "${MNT[$p]}" != "[SWAP]" ]; then free=$(df -B1 --output=avail "${MNT[$p]}" 2>/dev/null | tail -1 | tr -d ' '); fi
    parts+="${parts:+,}{\"name\":$(j "/dev/$p"),\"type\":$(j "${TYPE[$p]}"),\"size_bytes\":$(n "${SIZE[$p]}"),\"fs\":$(j "${FS[$p]}"),\"mount\":$(j "${MNT[$p]}"),\"label\":$(j "${LBL[$p]}"),\"free_bytes\":$(n "$free")}"
  done
  unset done_p
  disks_json+="${disks_json:+,}{\"name\":$(j "/dev/$d"),\"model\":$(j "${MODEL[$d]}"),\"type\":$(j "$media"),\"size_bytes\":$(n "${SIZE[$d]}"),\"partitions\":[${parts}]}"
done

# ---- network ----
nics_json=""
for path in /sys/class/net/*; do
  nic=$(basename "$path")
  [ "$nic" = lo ] && continue
  [ -e "$path/device" ] || continue          # physical adapters only (skip bridges, docker, veth, bonds)
  mac=$(rd "$path/address" | tr 'a-f' 'A-F')
  state=$(rd "$path/operstate"); carrier=$(rd "$path/carrier")
  conn=false; { [ "$state" = up ] || [ "$carrier" = 1 ]; } && conn=true
  master=""; [ -L "$path/master" ] && master=$(basename "$(readlink "$path/master")")
  speed=$(rd "$path/speed"); [[ "$speed" =~ ^[0-9]+$ ]] || speed=""
  ips=""
  for dev in "$nic" $master; do
    if has ip; then
      while read -r ipa; do [ -n "$ipa" ] && ips+="${ips:+,}$(j "${ipa%/*}")"; done < <(ip -o -4 addr show dev "$dev" 2>/dev/null | awk '{print $4}')
    elif has ifconfig; then
      while read -r ipa; do [ -n "$ipa" ] && ips+="${ips:+,}$(j "$ipa")"; done < <(ifconfig "$dev" 2>/dev/null | awk '/inet /{sub("addr:","",$2); print $2}')
    elif [ "$dev" = "$(awk '$2=="00000000"{print $1; exit}' /proc/net/route 2>/dev/null)" ]; then
      for ipa in $(hostname -I 2>/dev/null | tr ' ' '\n' | grep -E '^[0-9]+\.' | head -1); do ips+="${ips:+,}$(j "$ipa")"; done
    fi
  done
  nics_json+="${nics_json:+,}{\"name\":$(j "$nic"),\"description\":$(j "$(rd "$path/device/uevent" | grep -m1 DRIVER | cut -d= -f2)"),\"mac\":$(j "$mac"),\"connected\":$conn,\"speed_mbps\":$(n "$speed"),\"ips\":[${ips}]}"
done

# ---- installed applications ----
apps_json=""
if has dpkg-query; then
  while IFS=$'\t' read -r name ver; do apps_json+="${apps_json:+,}{\"name\":$(j "$name"),\"version\":$(j "$ver"),\"publisher\":null}"; done \
    < <(dpkg-query -W -f='${Package}\t${Version}\t${db:Status-Abbrev}\n' 2>/dev/null | awk -F'\t' '$3 ~ /^ii/{print $1"\t"$2}')
elif has rpm; then
  while IFS=$'\t' read -r name ver vendor; do apps_json+="${apps_json:+,}{\"name\":$(j "$name"),\"version\":$(j "$ver"),\"publisher\":$(j "$vendor")}"; done \
    < <(rpm -qa --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\n' 2>/dev/null | grep -v '^gpg-pubkey')
fi
if has snap; then
  while read -r name ver _; do apps_json+="${apps_json:+,}{\"name\":$(j "$name (snap)"),\"version\":$(j "$ver"),\"publisher\":null}"; done < <(snap list 2>/dev/null | tail -n +2)
fi

# ---- antivirus / EDR ----
av_json=""
check_av() { # service-name display-name
  local st
  if has systemctl; then st=$(systemctl is-active "$1" 2>/dev/null); else st=$(service "$1" status >/dev/null 2>&1 && echo active || echo inactive); fi
  if [ "$st" = active ] || [ "$st" = activating ] || systemctl list-unit-files "$1.service" 2>/dev/null | grep -q "^$1"; then
    local en=false; [ "$st" = active ] && en=true
    av_json+="${av_json:+,}{\"name\":$(j "$2"),\"enabled\":$en,\"up_to_date\":null,\"state\":$(j "$st")}"
  fi
}
check_av clamav-daemon "ClamAV"; check_av clamd@scan "ClamAV"; check_av falcon-sensor "CrowdStrike Falcon"; check_av sentinelone "SentinelOne"
check_av mdatp "Microsoft Defender for Endpoint"; check_av ds_agent "Trend Micro Deep Security"; check_av sophos-spl "Sophos"
check_av esets "ESET"; check_av cbagentd "VMware Carbon Black"; check_av kesl "Kaspersky"; check_av ma "Trellix / McAfee"; check_av wazuh-agent "Wazuh"
if has mdatp; then
  dh=$(mdatp health --field definitions_status 2>/dev/null | tr -d '"')
  [ -n "$dh" ] && av_json=$(printf '%s' "$av_json" | sed "s/\"name\":\"Microsoft Defender for Endpoint\",\"enabled\":\([a-z]*\),\"up_to_date\":null/\"name\":\"Microsoft Defender for Endpoint\",\"enabled\":\1,\"up_to_date\":$([ "$dh" = up_to_date ] && echo true || echo false)/")
fi

# ---- last patch date ----
lp=""
if [ -d /var/log/apt ]; then
  lp=$( (cat /var/log/apt/history.log 2>/dev/null; zcat /var/log/apt/history.log.*.gz 2>/dev/null) | awk '/^Start-Date:/{d=$2} /^(Upgrade|Install):/{if(d>m)m=d} END{print m}')
  [ -z "$lp" ] && lp=$(ls -l --time-style=+%Y-%m-%d /var/lib/dpkg/status 2>/dev/null | awk '{print $6}')
elif has rpm; then
  ts=$(rpm -qa --qf '%{INSTALLTIME}\n' 2>/dev/null | sort -n | tail -1)
  [ -n "$ts" ] && lp=$(date -d "@$ts" +%Y-%m-%d 2>/dev/null)
fi
[ -z "$lp" ] && [ -f /var/log/zypp/history ] && lp=$(awk -F'|' '$2 ~ /install/{d=substr($1,1,10)} END{print d}' /var/log/zypp/history)

is_server=true
if ls /usr/share/xsessions/*.desktop >/dev/null 2>&1 || systemctl get-default 2>/dev/null | grep -q graphical; then is_server=false; fi

printf '{"schema":1,"collector":"linux.sh","collected_at":%s,"os_family":"linux","os_name":%s,"os_version":%s,"is_server":%s,' \
  "$(j "$(date -u +%Y-%m-%dT%H:%M:%SZ)")" "$(j "$os_name")" "$(j "$os_ver")" "$is_server"
printf '"hostname":%s,"dns_name":%s,"domain_type":%s,"domain":%s,"make":%s,"model":%s,"serial_number":%s,"bios_version":%s,' \
  "$(j "$host")" "$(j "$fqdn")" "$(j "$dtype")" "$(j "$domain")" "$(j "$make")" "$(j "$model")" "$(j "$serial")" "$(j "$bios")"
printf '"cpu":{"model":%s,"manufacturer":%s,"socket":%s,"count":%s,"total_cores":%s,"cores_per_cpu":%s,"logical":%s},' \
  "$(j "$cpu_model")" "$(j "$cpu_vendor")" "$(j "$socket_name")" "$(n "$sockets")" "$(n "$cores")" "$(n "$cps")" "$(n "$logical")"
printf '"ram_bytes":%s,"disks":[%s],"total_storage_bytes":%s,"nics":[%s],"installed_apps":[%s],"antivirus":[%s],"last_patch_date":%s}\n' \
  "$(n "$ram")" "$disks_json" "$(n "$total_storage")" "$nics_json" "$apps_json" "$av_json" "$(j "$lp")"
