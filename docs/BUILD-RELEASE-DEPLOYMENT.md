# EasyTierHost 编译、发布与部署说明书

> 适用版本：0.2 开发预览版（EasyTier 2.6.4 patched）
>
> 目标：用统一脚本完成源码编译、测试、发布包生成、完整性校验和节点部署。Internet Gateway 在完成 Seed + Gateway + 两个 Client 的多机实机验收前仍属于预览功能。

## 1. 组件与角色

EasyTierHost 使用四类角色：

| 角色 | Overlay 地址 | 说明 |
|---|---|---|
| Seed | 无业务 TUN 地址 | 只负责虚拟网发现/维护，不作为业务网关 |
| Gateway | `10.10.0.1/16` | DNS + Internet Gateway |
| Dedicated | `10.10.0.2–10.10.0.10/16` | 专用静态节点 |
| Client | `10.10.0.11–10.10.255.254/16` | 普通用户，DHCP 分配 |

整个 Overlay 为 `10.10.0.0/16`。`10.10.0.0` 是网络地址，`10.10.255.255` 是广播地址，均不得分配给节点。

Gateway 与 Dedicated 共用同一服务器发布包；运行角色由 `network.json` 决定，不需要单独的 gateway 二进制包。

## 2. 源码目录

关键目录：

```text
EasyTierHost/
├─ EasyTier-2.6.4/                 patched EasyTier 源码
├─ src/                            EasyTierHost .NET 源码
├─ config/templates/               Seed/Gateway/Dedicated/Client 模板
├─ scripts/build/                  一键 Release 构建
├─ scripts/publish/                打包、manifest、校验
├─ scripts/windows/                Windows SCM 安装/卸载
├─ scripts/linux/                  Linux systemd 与 Client 控制
├─ scripts/validation/             实机证据采集
├─ docs/                           设计、运维、验收文档
└─ publish/                        构建输出（不应手工编辑）
```

## 3. 编译环境

### 3.1 Windows

推荐 Windows 11 / Windows Server 2019+ x64。

必须具备：

- .NET SDK 8.0 或更高版本；
- Rust stable + Cargo（建议 rustup）；
- Visual Studio 2022 Build Tools / MSVC C++ linker；
- Protocol Buffers compiler：`protoc`；
- 7-Zip，且 `7z.exe` 在 PATH；
- PowerShell 7 或 Windows PowerShell；
- Git。

检查：

```powershell
dotnet --info
rustc --version
cargo --version
protoc --version
7z
```

### 3.2 Linux

推荐 Ubuntu 22.04/24.04 LTS。

必须具备：

```bash
sudo apt-get update
sudo apt-get install -y protobuf-compiler pkg-config build-essential curl tar
```

另外需要：

- .NET SDK 8.0+；
- Rust stable；
- PowerShell 7 (`pwsh`)；
- Git。

Linux ARM64 交叉编译还需要：

```bash
sudo apt-get install -y gcc-aarch64-linux-gnu
```

## 4. 自动化编译

### 4.1 Windows 一键 Release

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build/build-windows-release.ps1
```

默认行为：

1. 编译 Host/Deployment/Manager/Windows Client；
2. 运行 Host、Client、Diagnostics、Integration、Deployment 测试；
3. 运行 EasyTier DHCP/Underlay 针对性 Rust 测试；
4. Release 编译 patched `easytier-core.exe` 与 `easytier-cli.exe`；
5. 生成 Seed / Dedicated / Client / Manager 包；
6. 自动加入 `Packet.dll`、`wintun.dll`；
7. 生成 `version.txt`、`sha256.txt`、`artifact-manifest.json`；
8. 对每个包执行完整性校验；
9. 生成 ZIP 归档。

常用参数：

```powershell
# 指定输出目录
scripts/build/build-windows-release.ps1 -OutputRoot publish/rc

# ARM64（需要对应 MSVC/Rust target）
scripts/build/build-windows-release.ps1 -RuntimeIdentifier win-arm64

# 只做快速重新打包，不建议正式候选版使用
scripts/build/build-windows-release.ps1 -SkipTests
```

正式候选版本禁止使用 `-SkipTests`。

### 4.2 Linux 一键 Release

```bash
bash scripts/build/build-linux-release.sh
```

默认完成 .NET 编译/测试、EasyTier Rust 测试和 Release 编译、三个 Linux 角色包生成、manifest 校验与 tar.gz 归档。

常用参数：

```bash
bash scripts/build/build-linux-release.sh --output-root publish/rc
bash scripts/build/build-linux-release.sh --runtime linux-arm64
```

## 5. 标准发布目录

完成后默认输出：

```text
publish/release/
├─ manager/
├─ seed-windows/
├─ dedicated-windows/
├─ client-windows/
├─ seed-linux/
├─ dedicated-linux/
├─ client-linux/
└─ archives/
```

Windows Client 包额外包含：

```text
client-ui/EasyTierHost.Client.Windows.exe
```

每个节点包必须包含 manifest 中声明的所有文件。不要手工向包内加入 secret、`core.toml` 或其他未登记文件，否则部署完整性检查会拒绝该包。

## 6. GitHub 自动 Release Candidate

工作流：

```text
.github/workflows/release-candidate.yml
```

支持两种方式：

1. GitHub Actions 页面手工 `Run workflow`；
2. 推送 `rc-*` tag 自动触发，例如：

```bash
git tag rc-0.2.0-01
git push origin rc-0.2.0-01
```

Windows 与 Linux Runner 会分别调用仓库的一键构建脚本，使用真实 patched EasyTier Release Core 生成候选包并上传 Actions artifact。

候选包只表示“自动构建与自动测试通过”，不能替代四节点实机验收。

## 7. 配置文件

模板位于：

```text
config/templates/seed.json
config/templates/gateway.json
config/templates/dedicated.json
config/templates/client.json
```

基本原则：

- 所有节点使用相同 `networkName` 和 network secret；
- 非 Seed 节点的 `seedPhysicalIp` 必须填写 Seed 的真实物理 IP，不是 `10.10.0.0`；
- Gateway 固定 `10.10.0.1`；
- Dedicated index 仅允许 2–10；
- Client 地址由受限 DHCP 从 `.11` 开始分配；
- secret 使用独立 `network.secret` 文件，不把密码写进命令行或发布包。

部署前先验证：

```powershell
./easytier-host.exe validate C:\ProgramData\EasyTierHost\config\network.json
```

或 Linux：

```bash
./easytier-host validate /etc/easytier-host/network.json
```

## 8. 推荐部署顺序

严格按以下顺序：

```text
1. Seed
2. Gateway (10.10.0.1)
3. Dedicated (10.10.0.2–10)
4. Client A
5. Client B
6. 其他 Client
```

每个节点必须先达到 `ready` 再部署下一层，避免把网络故障误判成安装故障。

## 9. Windows 本地部署

以 Seed 为例。管理员 PowerShell：

```powershell
$pkg = 'D:\EasyTierHost\publish\release\seed-windows'
$install = 'C:\Program Files\EasyTierHost'
$config = 'C:\ProgramData\EasyTierHost\config'
$state = 'C:\ProgramData\EasyTierHost\state'

New-Item -ItemType Directory -Force $install,$config,$state | Out-Null
Copy-Item "$pkg\*" $install -Recurse -Force
Copy-Item '.\network.json' "$config\network.json" -Force
Copy-Item '.\network.secret' "$config\network.secret" -Force

& "$install\scripts\windows\install-service.ps1" `
  -InstallRoot $install `
  -ProfilePath "$config\network.json" `
  -StateDirectory $state
```

安装脚本会验证 profile、安装/更新 Windows SCM 服务、设为 LocalSystem + 自动启动，并配置失败自动重启。

检查：

```powershell
Get-Service EasyTierHost
& "$install\easytier-host.exe" ready "$config\network.json"
& "$install\easytier-host.exe" diagnostics "$config\network.json" $state
```

卸载：

```powershell
& "$install\scripts\windows\uninstall-service.ps1" -ServiceName EasyTierHost
```

## 10. Linux 本地部署

```bash
sudo mkdir -p /opt/easytier-host /etc/easytier-host /var/lib/easytier-host
sudo cp -a publish/release/seed-linux/. /opt/easytier-host/
sudo cp network.json /etc/easytier-host/network.json
sudo cp network.secret /etc/easytier-host/network.secret
sudo chmod 600 /etc/easytier-host/network.secret
sudo chmod +x /opt/easytier-host/easytier-host /opt/easytier-host/easytier-core /opt/easytier-host/easytier-cli
sudo chmod +x /opt/easytier-host/scripts/linux/*.sh

sudo /opt/easytier-host/scripts/linux/install-service.sh \
  /opt/easytier-host \
  /etc/easytier-host/network.json \
  /var/lib/easytier-host \
  easytier-host
```

检查：

```bash
systemctl status easytier-host --no-pager
sudo /opt/easytier-host/easytier-host ready /etc/easytier-host/network.json
sudo /opt/easytier-host/easytier-host diagnostics /etc/easytier-host/network.json /var/lib/easytier-host
```

## 11. Manager 远程自动部署

Windows 管理机运行：

```text
publish/release/manager/EasyTierHost.Manager.exe
```

Manager 当前支持 Seed、Gateway、Dedicated 的 Windows/Linux OpenSSH 部署。

远端要求：

- SSH Server 已启动；
- 推荐使用 SSH key 或 ssh-agent；
- Windows 远端用户具有管理员权限；
- Linux 远端为 root，或具有免交互 `sudo -n` 所需权限；
- 22 端口或自定义 SSH 端口已放行。

Manager 部署流程为事务式：

```text
校验本地 artifact manifest
→ SSH 连通性测试
→ 上传私有 staging
→ 备份旧版本/profile/secret
→ 安装新版本
→ 启动服务
→ 等待角色 readiness（最长约 90 秒）
→ Ready 后提交
```

如果安装或 readiness 失败，会尝试恢复旧程序、旧 profile、旧 secret 和旧服务。

## 12. Gateway 额外要求

Gateway 需要管理员/root 权限，因为必须配置：

- IPv4 forwarding；
- Windows WinNAT 或 Linux nftables NAT；
- DNS 53/UDP + 53/TCP；
- Gateway 自有状态 journal。

Linux 需要：

```bash
sudo apt-get install -y nftables
```

现有主机防火墙必须允许 Overlay 到物理网卡的 forwarding，以及来自 `10.10.0.0/16` 的 DNS 访问。EasyTierHost 不会清空用户已有 nftables/iptables ruleset。

## 13. Client Internet Gateway

Client `enableInternetGateway=true` 时，Host 的顺序为：

```text
捕获物理默认出口
→ Core 使用 --underlay-source-ipv4 绑定物理 IPv4
→ 等待 Client TUN/Core/Peer
→ 为 Seed/Peer endpoint 建立物理 /32 保护
→ Probe 1.1.1.1/32 与 8.8.8.8/32
→ DNS/HTTPS Probe
→ 应用 Client DNS
→ 提交 0.0.0.0/1 + 128.0.0.0/1 → 10.10.0.1
→ 删除临时 Probe /32
→ 周期 Reconcile
```

原物理 `0.0.0.0/0` 保留，不通过删除物理默认路由实现网关接管。

物理网卡/IP/网关改变、Core 身份异常、Owned route 丢失或健康检查失败时，先撤销 EasyTierHost 自有 DNS/路由，再重启 Core 并重新捕获物理出口。

## 14. 升级与回滚

推荐使用 Manager 执行远程升级。不要直接覆盖一个正在运行的 `easytier-host` 目录。

升级前至少保存：

```text
network.json
network.secret
当前版本号
当前 artifact manifest
```

Gateway/Client 若存在 recovery journal，不应通过手工删除 journal 强行启动；必须让 Host 执行 recovery，确认已恢复系统网络状态后再继续升级。

## 15. 发布前检查

正式候选版必须满足：

```text
Host CI                         PASS
Linux native CI                 PASS
Linux Privileged Network        PASS
DHCP Rust tests                 PASS
Underlay Rust tests             PASS
Windows package verification    PASS
Linux package verification      PASS
Release Candidate build         PASS
```

然后才进入 `docs/FOUR-NODE-VALIDATION.md` 的 Seed + Gateway + C1 + C2 实机验收。

实机验收重点：

- Client ↔ Client Overlay；
- Client → `10.10.0.1`；
- Client 经 Gateway 访问 Internet；
- DNS TCP/UDP；
- Seed/Peer/STUN Underlay 不递归进 Overlay；
- P2P 不无故退化；
- Gateway 服务停止/异常/断电恢复；
- Client 物理网卡切换；
- Windows/Linux 两平台；
- 与常见 VPN/代理共存。

## 16. 故障诊断

统一命令：

```text
easytier-host diagnostics <network.json> [state-directory]
```

诊断输出包括 Build/Core/Role、物理出口、Overlay、Peer、Gateway、DNS、路由、受保护 endpoint 和脱敏 recentErrors。

Windows 常用：

```powershell
Get-NetRoute -AddressFamily IPv4 | Sort-Object DestinationPrefix
Get-NetIPInterface -AddressFamily IPv4
Get-NetNat
Get-Service EasyTierHost
```

Linux 常用：

```bash
ip -4 route
ip -4 addr
sudo nft list ruleset
sysctl net.ipv4.ip_forward
systemctl status easytier-host --no-pager
```

## 17. 发布边界

当前代码、自动化构建、包完整性、Windows/Linux 服务化、Linux 真实内核 NAT/route/journal CI 已建立。但自动化 CI 无法等价模拟真实跨公网的 Seed + Gateway + Client 网络。

因此：

- 自动构建通过 = 可以生成实机候选包；
- 四节点实机验收通过 = 才可以考虑生产放行；
- 在完成实机验收前，Internet Gateway 保持预览状态。
