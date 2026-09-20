# EasyTierHost 编译、发布与部署说明书

> 适用版本：0.2 开发预览版，基于 patched EasyTier 2.6.4。
>
> 本文给出标准编译、自动发布、安装、远程部署、升级、回滚和验收流程。Internet Gateway 在四节点实机验收完成前仍按预览功能管理。

## 1. 网络与角色基线

| 角色 | Overlay 地址 | 说明 |
|---|---|---|
| Seed | 无业务 TUN 地址 | 只维护虚拟网发现/连接，不承担业务网关 |
| Gateway | `10.10.0.1/16` | Internet Gateway + DNS |
| Dedicated | `10.10.0.2–10.10.0.10/16` | 专用静态节点 |
| Client | `10.10.0.11–10.10.255.254/16` | 普通用户，受限 DHCP 分配 |

Overlay 固定为 `10.10.0.0/16`。`10.10.0.0` 为网络地址，`10.10.255.255` 为广播地址，均不得分配。

Gateway 与 Dedicated 使用相同服务器发布包，角色由 `network.json` 决定，不维护第二套 Gateway 二进制目录。

## 2. 目录约定

```text
EasyTierHost/
├─ EasyTier-2.6.4/                 patched EasyTier 源码
├─ src/                            EasyTierHost .NET 源码
├─ config/templates/               四类角色配置模板
├─ scripts/build/                  一键 Release 编译
├─ scripts/publish/                打包、元数据、manifest、校验
├─ scripts/windows/                Windows SCM 安装/卸载
├─ scripts/linux/                  Linux systemd 与 Client 控制
├─ scripts/validation/             实机/隔离网络验收工具
├─ docs/                           运维与验收文档
└─ publish/                        构建输出，不应手工修改
```

## 3. Windows 编译环境

推荐 Windows 11 或 Windows Server 2019+ x64。

需要：

- .NET SDK 10.0；
- Rust stable / Cargo，建议通过 rustup 安装；
- Visual Studio 2022 Build Tools，含 MSVC C++ linker；
- `protoc`；
- 7-Zip，`7z.exe` 在 PATH；
- Git；
- PowerShell。

检查：

```powershell
dotnet --info
rustc --version
cargo --version
protoc --version
7z
```

## 4. Linux 编译环境

推荐 Ubuntu 22.04/24.04 LTS。

```bash
sudo apt-get update
sudo apt-get install -y \
  protobuf-compiler pkg-config build-essential mold curl tar
```

还需要：.NET SDK 10.0、Rust stable、Git、PowerShell 7 (`pwsh`)。

EasyTier 的 Linux Cargo 配置使用 `-fuse-ld=mold`，所以正式 Linux Release 构建必须存在 `mold`。

Linux ARM64 交叉编译另外需要：

```bash
sudo apt-get install -y gcc-aarch64-linux-gnu
```

## 5. Windows 一键编译与发布

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build/build-windows-release.ps1
```

脚本会依次完成：

1. 编译 Host、Deployment、Manager、Windows Client；
2. 运行 Host/Client/Diagnostics/Integration/Deployment 测试；
3. 运行 EasyTier DHCP 与 Underlay 针对性 Rust 测试；
4. Release 编译 patched `easytier-core.exe`、`easytier-cli.exe`；
5. 生成 Seed、Dedicated、Client、Manager 包（framework-dependent，不内嵌 .NET 运行时）；
6. Windows 节点包加入匹配架构的 `Packet.dll` 与 `wintun.dll`；
7. 生成 `version.txt`、`sha256.txt`、`artifact-manifest.json`；
8. 对全部发布包执行结构、长度和 SHA-256 校验；
9. 生成 ZIP 归档。

常用参数：

```powershell
# 指定输出根目录
scripts/build/build-windows-release.ps1 -OutputRoot publish/rc

# Windows ARM64
scripts/build/build-windows-release.ps1 -RuntimeIdentifier win-arm64

# 仅开发期间快速重打包；正式候选版禁止跳过测试
scripts/build/build-windows-release.ps1 -SkipTests
```

## 6. Linux 一键编译与发布

```bash
bash scripts/build/build-linux-release.sh
```

脚本会完成 .NET 编译/测试、EasyTier Rust 测试、真实 Release Core/CLI 编译、Seed/Dedicated/Client 三类 Linux 包生成、manifest 校验与 `.tar.gz` 归档。

常用参数：

```bash
bash scripts/build/build-linux-release.sh --output-root publish/rc
bash scripts/build/build-linux-release.sh --runtime linux-arm64
```

## 7. 标准发布目录

默认输出：

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

Windows Client 包包含 scripts/windows/install-client.ps1 与 scripts/windows/uninstall-client.ps1。每个角色包根目录都有 DEPLOY.txt 部署教程。普通用户不需要图形界面。远程管理使用 publish/release/manager。发布目录不含 Client WPF 与 PDB。

节点包内不得人工增加 `network.secret`、`core.toml` 或其他未进入 manifest 的文件。部署器会拒绝未登记、多余、缺失、长度不匹配或 SHA-256 不匹配的包。

## 8. GitHub 自动 Release Candidate

工作流：

```text
.github/workflows/release-candidate.yml
```

支持：

- GitHub Actions 页面手工 `Run workflow`；
- 推送 `rc-*` tag；
- 开发分支中修改 `scripts/build/**`、`scripts/publish/**` 或 Release Candidate workflow 时自动验证构建链。

正式候选标签示例：

```bash
git tag rc-0.2.0-01
git push origin rc-0.2.0-01
```

Windows/Linux Runner 均直接调用仓库的一键 Release 脚本，因此 CI 与本地发布使用同一入口。成功后上传 Windows/Linux candidate artifacts。

候选 artifact 只能说明“源码、自动测试、真实 Core Release 编译、发布包校验成功”，不能代替四台独立机器的网络验收。

## 9. 配置规则

模板：

```text
config/templates/seed.json
config/templates/gateway.json
config/templates/dedicated.json
config/templates/client.json
```

必须遵守：

- 所有节点使用相同 `networkName` 与 network secret；
- 非 Seed 节点的 `seedPhysicalIp` 填 Seed 的真实物理 IP，不得填写 `10.10.0.0`；
- Gateway 固定 `.1`；
- Dedicated index 仅 2–10；
- Client 由受限 DHCP 从 `.11` 开始；
- secret 独立存放在 `network.secret`，不进入命令行、Git 和发布包。

部署前验证：

```powershell
./easytier-host.exe validate C:\ProgramData\EasyTierHost\config\network.json
```

Linux：

```bash
./easytier-host validate /etc/easytier-host/network.json
```

## 10. 标准部署顺序

```text
Seed
 ↓
Gateway 10.10.0.1
 ↓
Dedicated 10.10.0.2–10
 ↓
Client A
 ↓
Client B
 ↓
其他 Client
```

每一层都应先通过 `ready` 再继续下一层。

## 11. Windows 本地安装与卸载（Seed / 网关 / 普通客户端）

三者共用固定路径：

- 程序：`C:\Program Files\EasyTierHost`
- 配置：`C:\ProgramData\EasyTierHost\config`
- 状态：`C:\ProgramData\EasyTierHost\state`（必须精确匹配，拒绝 `state2` 等近似路径）

| 角色 | 发布包 | 安装 | 卸载 | 默认删除范围 |
| --- | --- | --- | --- | --- |
| Seed | `seed-windows` | 手工 json + `install-service.ps1` | `uninstall-service.ps1` | 服务；可选 `-RemoveState` |
| 网关 | `dedicated-windows` | 同上，`role=Gateway` | 同上 | 同上，并恢复 NAT/forwarding |
| 普通客户端 | `client-windows` | `install-client.ps1` | `uninstall-client.ps1` | 服务、state、config、密钥、程序 |

### Seed / 网关

管理员 PowerShell，以 Seed 为例（网关把 `$pkg` 换成 `dedicated-windows`，并使用 `gateway.json` 模板）：

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

安装脚本会：确认/安装 .NET 10、先 `recover-network`、校验 profile、创建 `EasyTierHost`（LocalSystem、Delayed Start、失败重启、停服等待约 180 秒）。

检查：

```powershell
Get-Service EasyTierHost
& "$install\easytier-host.exe" ready "$config\network.json"
& "$install\easytier-host.exe" diagnostics "$config\network.json" $state
```

卸载（保留程序与 config；需要清 state 时加 `-RemoveState`）：

```powershell
& "$install\scripts\windows\uninstall-service.ps1" `
  -ServiceName EasyTierHost `
  -StateDirectory $state
```

流程：停服务 → 结束本实例 Core → `recover-network` → 残留检查 → 删服务。失败则保留安装。

### 普通客户端

不要手工写 profile，也不需要图形界面：

```powershell
$pkg = 'D:\EasyTierHost\publish\release\client-windows'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$pkg\scripts\windows\install-client.ps1" `
  -SeedPhysicalIp 141.164.40.70 `
  -NetworkName company-overlay `
  -SecretFromFile C:\secure\network.secret
```

`install-client.ps1` 会复制程序、写入 Client profile（`enableInternetGateway=true`）、`set-secret`、安装服务，并等待 DHCP `10.10.0.11+`；随后 Host 接管默认路由与 NRPT DNS 到 `10.10.0.1`。

卸载虚拟网（全量删除）：

```powershell
& "$env:ProgramFiles\EasyTierHost\scripts\windows\uninstall-client.ps1"
```

详细验收与残留检查见发布包内 `DEPLOY.txt` 第六节、第八节、第十四节。

## 12. Linux 本地安装

```bash
sudo mkdir -p /opt/easytier-host /etc/easytier-host /var/lib/easytier-host
sudo cp -a publish/release/seed-linux/. /opt/easytier-host/
sudo cp network.json /etc/easytier-host/network.json
sudo cp network.secret /etc/easytier-host/network.secret
sudo chmod 600 /etc/easytier-host/network.secret
sudo chmod +x /opt/easytier-host/easytier-host \
  /opt/easytier-host/easytier-core \
  /opt/easytier-host/easytier-cli \
  /opt/easytier-host/scripts/linux/*.sh

sudo /opt/easytier-host/scripts/linux/install-service.sh \
  /opt/easytier-host \
  /etc/easytier-host/network.json \
  /var/lib/easytier-host \
  easytier-host
```

systemd 使用 `Restart=on-failure`，并在正常停止时给 Host 180 秒清理路由/NAT/DNS 与 recovery journal。

普通 Linux 客户端解压 `client-linux` 到 `/opt/easytier-host` 后，不走图形界面：

```bash
printf '%s\n' "$SECRET" | sudo /opt/easytier-host/scripts/linux/client-control.sh install 141.164.40.70 company-overlay
```

会写入 Client profile、经 stdin 保存 secret、安装 systemd 服务，并由 DHCP 分配 Overlay IP；网关/DNS 为 `10.10.0.1`。

检查：

```bash
systemctl status easytier-host --no-pager
sudo /opt/easytier-host/easytier-host ready /etc/easytier-host/network.json
sudo /opt/easytier-host/easytier-host diagnostics \
  /etc/easytier-host/network.json /var/lib/easytier-host
```

## 13. Manager 远程自动部署

Windows 管理机运行：

```text
publish/release/manager/EasyTierHost.Manager.exe
```

Manager 支持 Seed、Gateway、Dedicated 的 Windows/Linux OpenSSH 部署。

远端要求：SSH Server 已启用；推荐 SSH key/ssh-agent；Windows 用户具有管理员权限；Linux 使用 root 或具有部署命令所需的免交互 `sudo -n`；SSH 端口已放行。

部署事务：

```text
校验本地 artifact manifest
→ SSH 测试
→ 上传私有 staging
→ 备份旧程序/profile/secret
→ 安装新版本
→ 启动服务
→ 最长约 90 秒等待角色 readiness
→ Ready 后提交
```

安装或 readiness 失败时，部署器恢复旧程序、旧 profile、旧 secret 和旧服务。

## 14. Gateway 平台要求

Gateway 需要管理员/root 权限，并会管理 IPv4 forwarding、NAT、DNS 53/TCP+UDP 和自有 journal。

Windows 使用 WinNAT。

Linux **优先 nftables**；如果 nft 不可用，会回退为一条带唯一 `EasyTierHost:<owner>` comment 的精确 iptables MASQUERADE 规则。删除/恢复时只删除自己拥有的对象，不清空用户 ruleset。

推荐 Linux Gateway 安装：

```bash
sudo apt-get install -y nftables iptables
```

现有主机防火墙仍必须允许 Overlay 与物理接口之间 forwarding，以及来自 `10.10.0.0/16` 的 DNS 53/TCP、53/UDP。EasyTierHost 不负责清空或接管用户已有防火墙策略。

## 15. Client Internet Gateway 流程

`enableInternetGateway=true` 时：

```text
捕获物理默认出口
→ Core --underlay-source-ipv4 绑定物理 IPv4
→ 等待 Client TUN / Core / Peer
→ Seed/Peer endpoint 安装物理 /32 保护
→ 1.1.1.1/32、8.8.8.8/32 临时 Probe
→ DNS/HTTPS Probe
→ 应用 Client DNS
→ 0.0.0.0/1 + 128.0.0.0/1 → 10.10.0.1
→ 删除临时 Probe /32
→ 周期 Reconcile
```

原物理 `0.0.0.0/0` 始终保留。物理网卡/IP/网关变化、Core 身份异常、Owned route 丢失或健康失败时，Host 先撤销自有 DNS/路由，再重启 Core 并重新捕获物理出口。

## 16. 升级与回滚

推荐通过 Manager 执行远程升级，不要直接覆盖正在运行的安装目录。

至少保留：

```text
network.json
network.secret
当前 version.txt
当前 artifact-manifest.json
```

Gateway/Client 存在 recovery journal 时，不得删除 journal 强行绕过恢复。应让 Host 完成 recovery 并确认物理网络状态恢复后再升级。

## 17. 发布前门禁

正式候选包至少满足：

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

Linux Privileged Network 会在隔离 network namespace 中实际测试 nftables 和强制 iptables fallback 两条路径，包括 route、`ip_forward`、NAT、Client packet forwarding、正常 rollback 与 crash journal recovery。

自动门禁完成后，再执行 `docs/FOUR-NODE-VALIDATION.md` 的真实 Seed + Gateway + C1 + C2 验收。

## 18. 实机验收重点

- Client ↔ Client Overlay；
- Client → `10.10.0.1`；
- Client 经 Gateway 上 Internet；
- DNS TCP/UDP；
- Seed/Peer/STUN/维护 DNS 的 Underlay 不递归进入 Overlay；
- P2P 不无故退化 Relay；
- Gateway 正常停止、异常退出和断电后的 journal recovery；
- Client Wi-Fi/有线切换后的 Underlay 重绑定和路由恢复；
- Windows/Linux Gateway；
- 常见 VPN/代理共存；
- 长时间运行和重连。

## 19. 诊断

统一入口：

```text
easytier-host diagnostics <network.json> [state-directory]
```

输出包括 Build/Core/Role、物理出口、Overlay、Peer、Gateway、DNS、路由、受保护 endpoint 和脱敏 recentErrors。

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
sudo iptables -t nat -S POSTROUTING
sysctl net.ipv4.ip_forward
systemctl status easytier-host --no-pager
```

## 20. 发布边界

代码侧已经建立完整的第一版主链路、自动构建、发布包完整性、Windows/Linux 服务化、事务式远程部署以及真实 Linux 内核网络 CI。自动 CI 仍不能等价模拟真实跨公网的四台机器。

因此：

- Release Candidate 自动构建通过：可以进入实机候选阶段；
- 四节点实机验收通过：才可考虑生产放行；
- 实机验收完成前，Internet Gateway 继续标记为预览功能。
