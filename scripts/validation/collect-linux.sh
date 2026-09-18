#!/usr/bin/env bash
set -euo pipefail

host="${EASYTIER_HOST_BIN:-/opt/easytier-host/easytier-host}"
profile="${EASYTIER_HOST_PROFILE:-/etc/easytier-host/network.json}"
state="${EASYTIER_HOST_STATE:-/var/lib/easytier-host}"
service="${EASYTIER_HOST_SERVICE:-easytier-host}"
out="${1:-validation-$(date -u +%Y%m%d-%H%M%S)}"

[[ -x "$host" ]] || { echo "Host executable not found: $host" >&2; exit 2; }
[[ -f "$profile" ]] || { echo "Profile not found: $profile" >&2; exit 2; }
mkdir -p "$out"
chmod 700 "$out"

"$host" diagnostics "$profile" "$state" >"$out/diagnostics.json"
ip -j -4 addr show >"$out/ip-addresses.json"
ip -j -4 route show table main >"$out/ip-routes.json"
ip -j link show >"$out/ip-links.json"
systemctl show "$service" --no-pager \
  --property=Id,ActiveState,SubState,MainPID,ExecMainStatus,Restart,FragmentPath \
  >"$out/service.txt" 2>&1 || true

if command -v resolvectl >/dev/null 2>&1; then
  resolvectl status >"$out/resolved.txt" 2>&1 || true
fi

seed="$(python3 - "$profile" <<'PY'
import json,sys
with open(sys.argv[1], encoding='utf-8') as f:
    p=json.load(f)
print(p.get('seedPhysicalIp') or '')
PY
)"
if [[ -n "$seed" ]]; then
  ip -4 route get "$seed" >"$out/route-to-seed.txt" 2>&1 || true
fi
ip -4 route get 1.1.1.1 >"$out/route-to-internet.txt" 2>&1 || true
ip -4 route get 10.10.0.1 >"$out/route-to-overlay-gateway.txt" 2>&1 || true

python3 - "$out/diagnostics.json" <<'PY'
import json,sys
with open(sys.argv[1], encoding='utf-8-sig') as f:
    d=json.load(f)
print(f"Role={d.get('role')} Overlay={d.get('overlayIp')} GatewayState={d.get('gatewayState')} PeerCount={d.get('peerCount')}")
PY

echo "Validation evidence written: $out"
