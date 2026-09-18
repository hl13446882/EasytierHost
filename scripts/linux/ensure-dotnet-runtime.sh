#!/usr/bin/env bash
set -euo pipefail

package_root="${1:-}"
if [[ -z "$package_root" ]]; then
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  package_root="$(cd "$script_dir/../.." && pwd)"
fi

log() { echo "$*" >&2; }

rid=""
if [[ -f "$package_root/version.txt" ]]; then
  rid="$(awk -F= '/^RuntimeIdentifier=/{print $2; exit}' "$package_root/version.txt" | tr -d '\r')"
fi

arch=""
case "$rid" in
  linux-x64) arch=x64 ;;
  linux-arm64) arch=arm64 ;;
  '')
    case "$(uname -m)" in
      x86_64) arch=x64 ;;
      aarch64|arm64) arch=arm64 ;;
      *) log "Unsupported Linux architecture: $(uname -m)"; exit 1 ;;
    esac
    ;;
  *) log "Unsupported Linux runtime identifier: $rid"; exit 1 ;;
esac

has_net10() {
  local root="$1"
  compgen -G "$root/shared/Microsoft.NETCore.App/10.*" >/dev/null
}

find_dotnet_root() {
  if [[ -n "${DOTNET_ROOT:-}" && -d "$DOTNET_ROOT/shared/Microsoft.NETCore.App" ]]; then
    printf '%s\n' "$DOTNET_ROOT"
    return 0
  fi
  local dir
  for dir in /usr/share/dotnet /usr/lib/dotnet /usr/lib64/dotnet; do
    if [[ -d "$dir/shared/Microsoft.NETCore.App" ]]; then
      printf '%s\n' "$dir"
      return 0
    fi
  done
  if command -v dotnet >/dev/null 2>&1; then
    local bindir
    bindir="$(dirname "$(command -v dotnet)")"
    if [[ -d "$bindir/shared/Microsoft.NETCore.App" ]]; then
      printf '%s\n' "$bindir"
      return 0
    fi
  fi
  return 1
}

install_runtime() {
  if [[ ${EUID} -ne 0 ]]; then
    log "root privileges are required to install the .NET 10 runtime"
    exit 1
  fi

  local installer="/tmp/easytier-host-dotnet-install.sh"
  log "Downloading Microsoft .NET 10 runtime ($arch)..."
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$installer" https://dot.net/v1/dotnet-install.sh
  else
    log "curl or wget is required to download the .NET 10 runtime"
    exit 1
  fi
  bash "$installer" --channel 10.0 --runtime dotnet --architecture "$arch" --install-dir /usr/share/dotnet
  rm -f "$installer"
  ln -sfn /usr/share/dotnet/dotnet /usr/local/bin/dotnet
}

root=""
if root="$(find_dotnet_root)" && has_net10 "$root"; then
  printf '%s\n' "$root"
  exit 0
fi

install_runtime
root="$(find_dotnet_root || true)"
if [[ -z "$root" ]] || ! has_net10 "$root"; then
  log "The .NET 10 runtime was installed but is still not visible to EasyTierHost"
  exit 1
fi
printf '%s\n' "$root"
