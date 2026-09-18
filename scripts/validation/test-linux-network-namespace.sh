#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "root privileges are required" >&2
  exit 1
fi

for command in ip nft sysctl ping dotnet; do
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

cleanup() {
  ip netns del "$CLIENT_NS" >/dev/null 2>&1 || true
  ip netns del "$GW_NS" >/dev/null 2>&1 || true
  ip link del "$WAN_HOST" >/dev/null 2>&1 || true
  ip link del "$OVERLAY_GW" >/dev/null 2>&1 || true
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

# The .NET test runs inside the gateway namespace. All sysctl/nft/route writes are therefore
# isolated from the GitHub runner's real network namespace. It also launches a ping from the
# client namespace to prove the NAT rule is needed for the return path.
ip netns exec "$GW_NS" dotnet "$dll" "$WAN_GW" "$OVERLAY_GW" "$CLIENT_NS" 192.0.2.1
