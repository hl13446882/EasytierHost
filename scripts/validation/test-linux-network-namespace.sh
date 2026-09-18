#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "root privileges are required" >&2
  exit 1
fi

for command in ip nft iptables sysctl ping dotnet; do
  command -v "$command" >/dev/null 2>&1 || { echo "missing required command: $command" >&2; exit 2; }
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd -- "$script_dir/../.." && pwd)"
configuration="${CONFIGURATION:-Release}"
dll="$repo/tests/EasyTierHost.LinuxPrivilegedTests/bin/$configuration/net8.0/EasyTierHost.LinuxPrivilegedTests.dll"
if [[ ! -f "$dll" ]]; then
  echo "missing test binary: $dll" >&2
  echo "build tests/EasyTierHost.LinuxPrivilegedTests first" >&2
  exit 2
fi

GW_NS="ethgw-ci"
CLIENT_NS="ethcl-ci"
WAN_HOST="ethgw-host"
WAN_GW="ethwan"
OVERLAY_GW="easytierhost"
OVERLAY_CLIENT="ethclient"
FAKE_BIN=""

cleanup() {
  ip netns del "$CLIENT_NS" >/dev/null 2>&1 || true
  ip netns del "$GW_NS" >/dev/null 2>&1 || true
  ip link del "$WAN_HOST" >/dev/null 2>&1 || true
  ip link del "$OVERLAY_GW" >/dev/null 2>&1 || true
  [[ -z "$FAKE_BIN" ]] || rm -rf "$FAKE_BIN"
}
cleanup
trap cleanup EXIT

ip netns add "$GW_NS"
ip netns add "$CLIENT_NS"

ip link add "$WAN_HOST" type veth peer name "$WAN_GW"
ip link set "$WAN_GW" netns "$GW_NS"
ip addr add 192.0.2.1/24 dev "$WAN_HOST"
ip link set "$WAN_HOST" up
ip -n "$GW_NS" link set lo up
ip -n "$GW_NS" addr add 192.0.2.2/24 dev "$WAN_GW"
ip -n "$GW_NS" link set "$WAN_GW" up
ip -n "$GW_NS" route add default via 192.0.2.1 dev "$WAN_GW" metric 25

ip link add "$OVERLAY_GW" type veth peer name "$OVERLAY_CLIENT"
ip link set "$OVERLAY_GW" netns "$GW_NS"
ip link set "$OVERLAY_CLIENT" netns "$CLIENT_NS"
ip -n "$GW_NS" addr add 10.10.0.1/16 dev "$OVERLAY_GW"
ip -n "$GW_NS" link set "$OVERLAY_GW" up
ip -n "$CLIENT_NS" link set lo up
ip -n "$CLIENT_NS" addr add 10.10.0.11/16 dev "$OVERLAY_CLIENT"
ip -n "$CLIENT_NS" link set "$OVERLAY_CLIENT" up
ip -n "$CLIENT_NS" route add default via 10.10.0.1 dev "$OVERLAY_CLIENT"

# The .NET test runs inside the gateway namespace. All sysctl/NAT/route writes are isolated
# from the runner's real network namespace. First verify the preferred nftables backend.
echo '== privileged gateway test: nftables =='
ip netns exec "$GW_NS" env "PATH=$PATH" dotnet "$dll" "$WAN_GW" "$OVERLAY_GW" "$CLIENT_NS" 192.0.2.1

# Run the same lifecycle again while deliberately making nft unavailable. LinuxNatManager must
# fall back to one exact owned iptables MASQUERADE rule and remove it during normal/crash recovery.
FAKE_BIN="$(mktemp -d)"
cat >"$FAKE_BIN/nft" <<'EOF'
#!/usr/bin/env sh
exit 127
EOF
chmod +x "$FAKE_BIN/nft"

echo '== privileged gateway test: iptables fallback =='
ip netns exec "$GW_NS" env "PATH=$FAKE_BIN:$PATH" dotnet "$dll" "$WAN_GW" "$OVERLAY_GW" "$CLIENT_NS" 192.0.2.1
