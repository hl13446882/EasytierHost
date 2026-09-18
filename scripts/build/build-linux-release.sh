#!/usr/bin/env bash
set -euo pipefail

RUNTIME_IDENTIFIER="linux-x64"
CONFIGURATION="Release"
OUTPUT_ROOT="publish/release"
SKIP_TESTS=0
SKIP_ARCHIVES=0

usage() {
  cat <<'EOF'
Usage: build-linux-release.sh [options]
  --runtime linux-x64|linux-arm64
  --configuration Debug|Release
  --output-root PATH          repository-relative output path
  --skip-tests
  --skip-archives
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --runtime) RUNTIME_IDENTIFIER="$2"; shift 2 ;;
    --configuration) CONFIGURATION="$2"; shift 2 ;;
    --output-root) OUTPUT_ROOT="$2"; shift 2 ;;
    --skip-tests) SKIP_TESTS=1; shift ;;
    --skip-archives) SKIP_ARCHIVES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$RUNTIME_IDENTIFIER" in linux-x64|linux-arm64) ;; *) echo "Unsupported runtime: $RUNTIME_IDENTIFIER" >&2; exit 2 ;; esac
case "$CONFIGURATION" in Debug|Release) ;; *) echo "Unsupported configuration: $CONFIGURATION" >&2; exit 2 ;; esac
if [[ "$OUTPUT_ROOT" == /* || "/$OUTPUT_ROOT/" == *"/../"* ]]; then
  echo "Output root must be repository-relative and must not contain .." >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "Required command '$1' not found. $2" >&2; exit 1; }
}
need dotnet "Install .NET 8 SDK or newer."
need cargo "Install Rust stable with rustup."
need rustup "Install Rust with rustup."
need protoc "Install protobuf-compiler."
need pwsh "Install PowerShell 7; publishing uses the shared PowerShell packaging scripts."
need tar "Install tar."

echo "== EasyTierHost Linux release build =="
echo "RID:           $RUNTIME_IDENTIFIER"
echo "Configuration: $CONFIGURATION"
echo "Output:        $OUTPUT_ROOT"

dotnet build EasyTierHost.sln -c "$CONFIGURATION" --nologo
dotnet build src/EasyTierHost.Deployment/EasyTierHost.Deployment.csproj -c "$CONFIGURATION" --nologo

if [[ $SKIP_TESTS -eq 0 ]]; then
  for project in \
    tests/EasyTierHost.UnitTests \
    tests/EasyTierHost.ClientTests \
    tests/EasyTierHost.DiagnosticsTests \
    tests/EasyTierHost.IntegrationTests \
    tests/EasyTierHost.DeploymentTests; do
    dotnet run --project "$project" -c "$CONFIGURATION"
  done
fi

pushd EasyTier-2.6.4 >/dev/null
if [[ $SKIP_TESTS -eq 0 ]]; then
  cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features --features tun
  cargo +stable test -p easytier --lib underlay_ --no-default-features --features tun
fi

if [[ "$RUNTIME_IDENTIFIER" == "linux-x64" ]]; then
  cargo +stable build -p easytier --release --no-default-features --features tun --bin easytier-core --bin easytier-cli
  CORE_DIR="$ROOT/EasyTier-2.6.4/target/release"
else
  TARGET="aarch64-unknown-linux-gnu"
  rustup target add "$TARGET"
  if ! command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
    echo "linux-arm64 requires aarch64-linux-gnu-gcc (for Ubuntu: apt install gcc-aarch64-linux-gnu)." >&2
    exit 1
  fi
  export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
  cargo +stable build -p easytier --release --target "$TARGET" --no-default-features --features tun --bin easytier-core --bin easytier-cli
  CORE_DIR="$ROOT/EasyTier-2.6.4/target/$TARGET/release"
fi
popd >/dev/null

for role in seed-linux dedicated-linux client-linux; do
  pwsh -NoProfile -File scripts/publish/publish-linux.ps1 \
    -CoreDirectory "$CORE_DIR" \
    -OutputDirectory "$OUTPUT_ROOT/$role" \
    -RuntimeIdentifier "$RUNTIME_IDENTIFIER" \
    -Configuration "$CONFIGURATION"
  pwsh -NoProfile -File scripts/publish/verify-package.ps1 \
    -PackageDirectory "$OUTPUT_ROOT/$role" \
    -ExpectedPackageKind node-linux
done

if [[ $SKIP_ARCHIVES -eq 0 ]]; then
  ARCHIVE_ROOT="$ROOT/$OUTPUT_ROOT/archives"
  rm -rf "$ARCHIVE_ROOT"
  mkdir -p "$ARCHIVE_ROOT"
  for role in seed-linux dedicated-linux client-linux; do
    tar -C "$ROOT/$OUTPUT_ROOT/$role" -czf "$ARCHIVE_ROOT/EasyTierHost-$role-$RUNTIME_IDENTIFIER.tar.gz" .
  done
  echo "Archives: $ARCHIVE_ROOT"
fi

echo "Linux release build completed successfully."
echo "Packages: $ROOT/$OUTPUT_ROOT"
