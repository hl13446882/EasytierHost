# EasyTierHost

基于 EasyTier 2.6.4 源码实现的 **0.2**。虚拟网主功能（四角色、DHCP、Gateway/DNS `10.10.0.1`、普通客户端自动入网）已冻结，后续只修缺陷与发布物，不再扩展 Overlay 行为。Internet Gateway 多机抓包验收仍未完成，不能视为生产放行。具体边界见 [实施状态](docs/IMPLEMENTATION-STATUS.md)。

完整的编译、发布与部署流程见：[编译、发布与部署说明书](docs/BUILD-RELEASE-DEPLOYMENT.md)。仓库已提供 Windows/Linux 一键 Release 构建脚本，并支持 `rc-*` tag 自动触发 GitHub Release Candidate 编译。

## 构建与测试

需要 .NET 10 SDK、Rust stable、Windows C++ 链接工具；Windows 构建 Core 时需要 7-Zip 在 PATH 中。依赖恢复需要联网。发布包不内嵌 .NET 运行时；目标机器在安装时若缺少 .NET 10 会自动下载安装。

Windows 一键 Release：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build/build-windows-release.ps1
```

Linux 一键 Release：

```bash
bash scripts/build/build-linux-release.sh
```

手工开发测试：

```powershell
dotnet build EasyTierHost.sln
dotnet build src/EasyTierHost.Manager/EasyTierHost.Manager.csproj
dotnet build src/EasyTierHost.Client.Windows/EasyTierHost.Client.Windows.csproj
dotnet run --project tests/EasyTierHost.UnitTests
dotnet run --project tests/EasyTierHost.ClientTests
dotnet run --project tests/EasyTierHost.DiagnosticsTests
dotnet run --project tests/EasyTierHost.IntegrationTests
dotnet run --project tests/EasyTierHost.DeploymentTests

cd EasyTier-2.6.4
$env:PATH = "$PWD/easytier/third_party/x86_64;C:/Program Files/7-Zip;" + $env:PATH
cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features --features tun
cargo +stable test -p easytier --lib underlay_ --no-default-features --features tun
cargo +stable build -p easytier --no-default-features --features tun --bin easytier-core --bin easytier-cli
```

`.github/workflows/host-ci.yml` 同时运行 Windows 与 Ubuntu 原生 job。Windows job 执行 Host/Deployment/Manager/Windows Client 编译、PowerShell/Bash 语法检查、Host/Client/Diagnostics/Integration/Deployment 测试、Windows/Linux 发布结构与 manifest smoke，以及 EasyTier DHCP/Underlay 针对性 Rust 测试；Ubuntu job 原生编译 Host/Deployment 并执行 Bash、Host/Client/Diagnostics/Integration/Deployment 测试。`linux-privileged-network.yml` 另外在隔离 Linux network namespace 内实际验证路由、`ip_forward`、nftables NAT、数据包转发和 gateway journal 恢复。`tun` 构建支持预览版使用的 TCP/UDP Overlay；完整传输集合可用上游默认 features 构建。Windows 运行需要 EasyTier 的 `Packet.dll` 与 `wintun.dll`，Windows 发布脚本会按 `win-x64`/`win-arm64` 自动复制匹配架构的原生运行时并把它们纳入 manifest 校验。

## 地址与角色

固定 Overlay 网络为 `10.10.0.0/16`：Gateway/DNS 为 `10.10.0.1`；Dedicated 为 `10.10.0.2–10.10.0.10`；普通 Client 由受限 DHCP 从 `10.10.0.11–10.10.255.254` 分配；Seed 不创建业务 TUN。所有节点使用相同 `networkName` 与 network secret。`SeedPhysicalIp` 始终是 Seed 的公网/物理 IPv4，不能填写 Overlay 地址。

## Host 命令

将 `config/templates` 下相应角色配置复制到专用配置目录，并把示例 Seed 地址替换为真实物理地址。Core/CLI 必须使用本仓库带 DHCP/Underlay 补丁的构建，不应换成未打补丁的官方二进制。

```powershell
easytier-host set-secret C:/your-private-config/network.secret
easytier-host validate C:/your-private-config/network.json
easytier-host run C:/your-private-config/network.json C:/your-private-state

easytier-host status C:/your-private-config/network.json
easytier-host ready C:/your-private-config/network.json
easytier-host diagnostics C:/your-private-config/network.json C:/your-private-state
```

`set-secret` 从 stdin/交互终端读取，不把 secret 放入命令行参数。Windows 使用 DPAPI machine scope 并依赖私有目录 ACL；Linux secret 必须为 0600。`secretFile` 相对路径以 profile 所在目录为基准。`configure` 生成的 Core TOML 含明文 network secret，只能写入受保护目录并在运行后清理。

`diagnostics` 的 state directory 为可选参数，但实机运维建议传入。输出为固定 JSON 合同，包括 BuildId/CoreBase、角色、Core PID/运行态、物理网卡/IP/网关/DNS、Overlay IP、Peer 数、Gateway 状态、受保护 endpoint、最近脱敏错误，以及 Windows `/0`/`/1` 的 RouteMetric、InterfaceMetric、TotalMetric。Gateway/Client 已进入活动状态时，诊断会把启动中的旧 `status.json` 状态规范化为当前活动态；Seed 永远不会把机器上无关的 `10.10/16` 网卡误报成自己的 Overlay。诊断不读取 network secret；未知异常只持久化异常类型，不保存原始 Message。

## Client Internet Gateway

Client 的 `enableInternetGateway=false` 时只加入虚拟局域网；设为 `true` 后，Host 在 Core 启动前捕获物理默认出口，把物理 IPv4 传给 Core 的 `--underlay-source-ipv4`，等待 Client TUN/Core/Peer 一致后保护 Seed/Peer endpoint，再执行 `.1` DNS/HTTPS Probe。Probe 成功后才提交客户端 DNS 与 `0.0.0.0/1`、`128.0.0.0/1 → 10.10.0.1`，原物理 `/0` 始终保留。

运行期间 Host 持续刷新动态 endpoint `/32`，监测物理接口/IP/网关、Overlay 身份、路由所有权与 Internet 健康。异常时按 journal 撤销自有 DNS/路由并重新捕获物理出口，不通过删除系统默认路由强行接管。IPv6 默认路由目前不接管。详见 [Underlay 审计](docs/UNDERLAY-AUDIT.md) 与 [Gateway 运行说明](docs/GATEWAY-OPERATIONS.md)。

## Windows 普通客户端

普通用户**不需要图形界面**。解压 `publish/client-windows` 后，以管理员身份静默安装即可：服务自动启动、DHCP 从 `10.10.0.11` 起分配 Overlay IP，Host 在虚拟网就绪后把网关和 DNS 指定为 `10.10.0.1`。

```powershell
$pkg = 'D:\EasyTierHost\publish\release\client-windows'
# secret 走 stdin / 文件，不要放进命令行参数
Get-Content -LiteralPath C:\secure\network.secret -TotalCount 1 |
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$pkg\scripts\windows\install-client.ps1" `
    -SeedPhysicalIp 141.164.40.70 `
    -NetworkName company-overlay

# 或
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$pkg\scripts\windows\install-client.ps1" `
  -SeedPhysicalIp 141.164.40.70 `
  -NetworkName company-overlay `
  -SecretFromFile C:\secure\network.secret
```

安装脚本会把程序复制到 `C:\Program Files\EasyTierHost`，把 profile/secret 写入 `C:\ProgramData\EasyTierHost\config`，创建并启动 `EasyTierHost` 服务。关闭安装窗口不影响虚拟网。卸载虚拟网（停止服务并删除程序与配置）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:ProgramFiles\EasyTierHost\scripts\windows\uninstall-client.ps1"
```

也可从发布包运行同一脚本。输入 `Y` 确认；远程/无人值守加 `-Force`。

## Linux 普通客户端

Linux 发布包提供 `scripts/linux/client-control.sh`。安装目录为 `/opt/easytier-host` 时可执行：

```bash
printf '%s\n' "$SECRET" | sudo /opt/easytier-host/scripts/linux/client-control.sh install 141.164.40.70 company-overlay
sudo /opt/easytier-host/scripts/linux/client-control.sh status
sudo /opt/easytier-host/scripts/linux/client-control.sh diagnostics
sudo /opt/easytier-host/scripts/linux/client-control.sh reconnect
sudo /opt/easytier-host/scripts/linux/client-control.sh uninstall
```

首次 `install` 必须带上 Seed 物理 IP；新机器的 network secret 从 stdin 读入（不出现在进程参数中）。TTY 下仍可交互输入。服务由 systemd 管理。远程 Linux 安装器会在上传完成后只对已知 Host/Core/CLI 与自有 `.sh` 恢复 execute bit，以兼容从 Windows 生成/上传发布包的场景。

```bash
printf '%s\n' "$SECRET" | sudo /opt/easytier-host/scripts/linux/client-control.sh install 141.164.40.70 company-overlay
```

## 管理端与远程部署

`EasyTierHost.Manager` 用于配置和部署 Seed、Gateway `10.10.0.1`、Dedicated `10.10.0.2–10.10.0.10`，以及普通客户端虚拟网。远程部署走 OpenSSH，先上传到私有 staging，校验 artifact manifest/SHA-256，再事务式替换程序、profile 与 secret；新节点在规定时间内未达到角色 readiness 时自动尝试恢复旧版本。普通客户端页提供 **安装虚拟网** / **卸载虚拟网**（卸载会停止远端服务并删除程序与配置）。

Windows 使用 SCM；Linux 使用 systemd。Gateway 与 Dedicated 共用 dedicated 二进制发布布局，通过 profile 的 Role 决定 `.1` Gateway 或 `.2–.10` Dedicated 行为，避免维护两套二进制。

## 打包

规范打包入口：

```powershell
scripts/publish/publish-all.ps1 `
  -WindowsCoreDirectory <windows-core-dir> `
  -LinuxCoreDirectory <linux-core-dir>
```

生成：

```text
publish/release/manager
publish/release/seed-windows
publish/release/seed-linux
publish/release/dedicated-windows
publish/release/dedicated-linux
publish/release/client-windows
publish/release/client-linux
publish/release/archives
```

`publish/` 只保留上述发布物：二进制、原生运行时、安装/卸载脚本、配置模板与校验元数据，不含 Client WPF、PDB 或开发中间目录。`publish/release/client-windows` 含 Host/Core/CLI、`install-client.ps1` 与 `uninstall-client.ps1`；`publish/release/manager` 为远程部署 UI（含卸载虚拟网）。所有 Windows 节点包同时包含匹配架构的 `Packet.dll` 与 `wintun.dll`。每个发布目录统一生成 `version.txt`、`sha256.txt` 和 `artifact-manifest.json`。可用：

```powershell
scripts/publish/verify-package.ps1 -PackageDirectory publish/release/client-windows -ExpectedPackageKind client-windows
```

校验 manifest 路径、文件数量、长度、SHA-256、必需组件和 Windows TUN 原生运行时，并拒绝把 `.secret` 或 `core.toml` 打进发布包。日常 CI 中的发布 smoke 使用 sentinel Core/CLI 只验证发布管线和完整性算法；正式候选包必须使用真实 Release Core/CLI 构建。

`.github/workflows/release-candidate.yml` 可手工触发，也可通过推送 `rc-*` tag 自动触发。Windows 与 Linux 会分别从当前 patched EasyTier 源码执行测试、Release 编译 Core/CLI、调用仓库一键构建脚本生成规范角色包、执行 manifest 校验，再上传短期候选 artifact。该工作流生成的是**实机验收候选物**，仍不能替代安装、路由、NAT、DNS 与抓包验收。

## 当前验收边界

自动测试不能替代真实网络验收。正式放行前按照 [四节点实机验收方案](docs/FOUR-NODE-VALIDATION.md)，使用独立 Seed + Gateway + Client A + Client B 验证：Client↔Client、Client 经 `.1` 上网、DNS、P2P/Relay 路径、Seed RTT、Gateway/Seed 故障、物理网卡切换、休眠恢复、断电 journal 恢复，以及 Windows/Linux 抓包确认 Underlay 的 Seed/Peer/STUN/DNS/打洞流量始终从物理网发出。仓库已提供 `scripts/validation` 下的 Windows/Linux 证据采集脚本和 Windows 节点契约断言脚本。
