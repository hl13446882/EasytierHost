#!/usr/bin/env bash
set -euo pipefail

install_root="${1:-/opt/easytier-host}"
profile_path="${2:-/etc/easytier-host/network.json}"
state_dir="${3:-/var/lib/easytier-host}"
service_name="${4:-easytier-host}"

if [[ ${EUID} -ne 0 ]]; then
  echo "root privileges are required" >&2
  exit 1
fi
if [[ ! -x "$install_root/easytier-host" ]]; then
  echo "missing executable: $install_root/easytier-host" >&2
  exit 1
fi
if [[ ! -f "$profile_path" ]]; then
  echo "missing network profile: $profile_path" >&2
  exit 1
fi

mkdir -p "$state_dir"
chmod 700 "$state_dir"
"$install_root/easytier-host" validate "$profile_path"

unit_path="/etc/systemd/system/${service_name}.service"
tmp_unit="$(mktemp)"
trap 'rm -f "$tmp_unit"' EXIT
cat >"$tmp_unit" <<EOF
[Unit]
Description=EasyTierHost overlay service
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=120
StartLimitBurst=5

[Service]
Type=simple
ExecStart="$install_root/easytier-host" run "$profile_path" "$state_dir"
WorkingDirectory=$install_root
Restart=on-failure
RestartSec=5
UMask=0077
TimeoutStopSec=30
KillMode=control-group

[Install]
WantedBy=multi-user.target
EOF

install -m 0644 "$tmp_unit" "$unit_path"
systemctl daemon-reload
systemctl enable "$service_name"
systemctl restart "$service_name"
systemctl --no-pager --full status "$service_name" >/dev/null

echo "Installed and started $service_name"
