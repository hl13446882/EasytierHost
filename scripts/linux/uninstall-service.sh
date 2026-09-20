#!/usr/bin/env bash
set -euo pipefail

service_name="${1:-easytier-host}"
state_dir="${2:-/var/lib/easytier-host}"
remove_state="${3:-false}"
host="${4:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)/easytier-host}"
[[ "$service_name" =~ ^[a-zA-Z0-9_-]+$ ]] || exit 1
state_dir="$(realpath -m -- "$state_dir")"

if [[ ${EUID} -ne 0 ]]; then
  echo "root privileges are required" >&2
  exit 1
fi

if systemctl list-unit-files --type=service --no-legend "${service_name}.service" 2>/dev/null | grep -q "${service_name}.service"; then
  systemctl stop "$service_name"
fi
"$host" recover-network "$state_dir"
for journal in route-journal.json gateway-journal.json; do
  [[ ! -f "$state_dir/$journal" ]] || { echo 'Recovery incomplete; preserving installation' >&2; exit 1; }
done
if command -v nft >/dev/null 2>&1; then
  tables="$(nft list tables)"
  if grep -Eq 'table ip nft_[a-f0-9]{32}' <<< "$tables"; then echo 'Residual Host NAT; recovery required' >&2; exit 1; fi
fi
if command -v iptables >/dev/null 2>&1; then
  nat_rules="$(iptables -t nat -S POSTROUTING)"
  if grep -q 'EasyTierHost:' <<< "$nat_rules"; then echo 'Residual Host NAT; recovery required' >&2; exit 1; fi
fi
routes="$(ip -4 route show)"
if grep -Eq '^(default|0.0.0.0/1|128.0.0.0/1) via 10\.10\.0\.1 ' <<< "$routes"; then
  echo 'Residual virtual default route; preserving installation' >&2; exit 1
fi
if systemctl cat "$service_name" >/dev/null 2>&1; then systemctl disable "$service_name"; fi
rm -f "/etc/systemd/system/${service_name}.service"
systemctl daemon-reload
systemctl reset-failed "$service_name" 2>/dev/null || true

if [[ "$remove_state" == "true" ]]; then
  case "$state_dir" in
    /var/lib/easytier-host|/var/lib/easytier-host/*) rm -rf -- "$state_dir" ;;
    *) echo "refusing to remove state outside /var/lib/easytier-host: $state_dir" >&2; exit 1 ;;
  esac
fi

echo "Uninstalled $service_name. Network profile and secret files were not deleted."
