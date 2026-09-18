#!/usr/bin/env bash
set -euo pipefail

install_root="${EASYTIER_HOST_ROOT:-/opt/easytier-host}"
profile_path="${EASYTIER_HOST_PROFILE:-/etc/easytier-host/network.json}"
secret_path="${EASYTIER_HOST_SECRET:-/etc/easytier-host/network.secret}"
state_dir="${EASYTIER_HOST_STATE:-/var/lib/easytier-host}"
service_name="${EASYTIER_HOST_SERVICE:-easytier-host}"
host="$install_root/easytier-host"

usage() {
  cat <<'EOF'
Usage: client-control.sh <command> [args]
  install [seed-ip] [network-name]   Unattended Client install (secret on stdin if new)
  status                              Show systemd and Overlay readiness
  diagnostics                         Print EasyTierHost diagnostics
  reconnect                           Restart the Client service safely
  uninstall                           Stop the Client, remove overlay config/state/program files
EOF
}

require_root() {
  if [[ ${EUID} -ne 0 ]]; then
    echo "root privileges are required" >&2
    exit 1
  fi
}

require_host() {
  local ensure="$install_root/scripts/linux/ensure-dotnet-runtime.sh"
  if [[ -f "$ensure" ]]; then
    local root
    root="$("$ensure" "$install_root")"
    export DOTNET_ROOT="$root"
    export PATH="$root:${PATH:-}"
  fi
  if [[ ! -x "$host" ]]; then
    echo "missing executable: $host" >&2
    exit 1
  fi
}

install_client() {
  require_root
  require_host
  local seed="${1:-}"
  local network_name="${2:-company-overlay}"
  if [[ -z "$seed" ]]; then
    if [[ -t 0 ]]; then
      read -r -p 'Seed physical IP: ' seed
    else
      echo "Seed physical IP is required for unattended install" >&2
      exit 2
    fi
  fi
  if [[ ! "$seed" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
    echo "Seed must be an IPv4 address" >&2
    exit 2
  fi
  if [[ ! "$network_name" =~ ^[A-Za-z0-9_-]{1,64}$ ]]; then
    echo "network name must contain only letters, digits, '_' or '-'" >&2
    exit 2
  fi

  local config_dir
  config_dir="$(dirname "$profile_path")"
  install -d -m 0700 "$config_dir" "$state_dir"

  if [[ ! -f "$secret_path" ]]; then
    local secret confirm
    if [[ -t 0 ]]; then
      read -r -s -p 'Network secret: ' secret; echo
      read -r -s -p 'Confirm secret: ' confirm; echo
      if [[ -z "$secret" || "$secret" != "$confirm" ]]; then
        unset secret confirm
        echo "network secret is empty or does not match" >&2
        exit 2
      fi
    else
      IFS= read -r secret || true
      secret="${secret%$'\r'}"
      if [[ -z "$secret" ]]; then
        unset secret
        echo "unattended install requires the network secret on stdin" >&2
        exit 2
      fi
    fi
    printf '%s\n' "$secret" | "$host" set-secret "$secret_path"
    unset secret confirm
  fi

  local tmp
  tmp="$(mktemp "$config_dir/network.json.XXXXXX")"
  trap 'rm -f "$tmp"' RETURN
  chmod 0600 "$tmp"
  cat >"$tmp" <<EOF
{
  "schemaVersion": 1,
  "networkName": "$network_name",
  "secretFile": "network.secret",
  "role": "Client",
  "seedPhysicalIp": "$seed",
  "port": 11010,
  "corePath": "easytier-core",
  "cliPath": "easytier-cli",
  "rpcPort": 15888,
  "deviceName": "easytierhost",
  "enableInternetGateway": true
}
EOF
  "$host" validate "$tmp"
  mv -f "$tmp" "$profile_path"
  chmod 0600 "$profile_path"
  trap - RETURN

  "$install_root/scripts/linux/install-service.sh" "$install_root" "$profile_path" "$state_dir" "$service_name"
  echo "Waiting for Overlay readiness..."
  local i
  for i in {1..60}; do
    if "$host" ready "$profile_path" >/dev/null 2>&1; then
      "$host" ready "$profile_path"
      echo "Client connected. Overlay IP assigned by DHCP; gateway/DNS is 10.10.0.1."
      return 0
    fi
    sleep 1
  done
  echo "Client Overlay did not become ready within 60 seconds; stopping service." >&2
  systemctl stop "$service_name" || true
  exit 3
}

status_client() {
  require_host
  systemctl --no-pager --full status "$service_name" || true
  if [[ -f "$profile_path" ]]; then
    "$host" ready "$profile_path"
  else
    echo "profile not configured: $profile_path" >&2
    exit 2
  fi
}

diagnostics_client() {
  require_host
  [[ -f "$profile_path" ]] || { echo "profile not configured: $profile_path" >&2; exit 2; }
  "$host" diagnostics "$profile_path" "$state_dir"
}

reconnect_client() {
  require_root
  [[ -f "$profile_path" ]] || { echo "profile not configured: $profile_path" >&2; exit 2; }
  systemctl restart "$service_name"
  echo "Restarted $service_name; Host will re-establish Underlay/Overlay and commit gateway routes only after probes pass."
}

uninstall_client() {
  require_root
  local uninstaller="$install_root/scripts/linux/uninstall-service.sh"
  if [[ -f "$uninstaller" ]]; then
    bash "$uninstaller" "$service_name" "$state_dir" true
  fi
  rm -f -- "$profile_path" "$secret_path"
  local config_dir
  config_dir="$(dirname "$profile_path")"
  case "$config_dir" in
    /etc/easytier-host|/etc/easytier-host/*) rm -rf -- "$config_dir" ;;
  esac
  case "$install_root" in
    /opt/easytier-host) rm -rf -- "$install_root" ;;
    *) echo "left program files at $install_root" ;;
  esac
  echo "Virtual network uninstalled. Service, profile, secret, state and program files were removed."
}

case "${1:-}" in
  install) shift; install_client "${1:-}" "${2:-}" ;;
  status) status_client ;;
  diagnostics) diagnostics_client ;;
  reconnect) reconnect_client ;;
  uninstall) uninstall_client ;;
  -h|--help|help|'') usage ;;
  *) usage >&2; exit 2 ;;
esac
