#!/usr/bin/env bash
set -euo pipefail

service_name="${1:-easytier-host}"
state_dir="${2:-/var/lib/easytier-host}"
remove_state="${3:-false}"

if [[ ${EUID} -ne 0 ]]; then
  echo "root privileges are required" >&2
  exit 1
fi

if systemctl list-unit-files --type=service --no-legend "${service_name}.service" 2>/dev/null | grep -q "${service_name}.service"; then
  systemctl stop "$service_name" || true
  systemctl disable "$service_name" || true
fi
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
