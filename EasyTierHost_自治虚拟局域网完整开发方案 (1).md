# EasyTierHost 自治虚拟局域网开发方案

> 目标版本：EasyTier 2.6.4  
> 虚拟网段：`10.10.0.0/16`  
> 设计目标：在尽量复用 EasyTier 原生能力的前提下，完成 Seed、专用服务器、普通用户三类节点的自动部署、自动寻址、统一网关/DNS、开机自启动、故障恢复和防递归路由保护。

---

## 1. 文档目标

本方案用于直接指导开发，不再停留在架构层描述。要求开发人员可按本文完成代码拆分、接口实现、部署脚本、配置、测试与发布。

核心原则：

1. EasyTier Core 继续负责 Overlay、P2P、Relay、TUN、Peer 路由、Exit Node、DHCP 冲突检测。
2. EasyTier Core 仅做必要小改，避免形成难维护的重度 Fork。
3. EasyTierHost 负责角色管理、地址策略、网关/DNS、路由保护、远程安装、服务守护、配置与健康检查。
4. 所有涉及系统路由的行为必须由单一控制器负责，禁止多个模块直接修改路由。
5. 必须避免“默认路由进入 Overlay 后，EasyTier 自身 Underlay/Seed/Peer 流量再次进入 Overlay”的递归。
6. 安装程序必须可重复执行，要求幂等。
7. 所有配置变更必须支持失败回滚。
8. 普通用户安装后不需要理解 EasyTier 参数。

---

# 2. 最终网络定义

## 2.1 地址规划

| 用途 | 地址 |
|---|---|
| Overlay 网络 | `10.10.0.0/16` |
| 网络地址 | `10.10.0.0` |
| 网关/DNS | `10.10.0.1` |
| 专用服务器 | `10.10.0.2 - 10.10.0.10` |
| 普通用户动态地址 | `10.10.0.11 - 10.10.255.254` |
| 广播地址 | `10.10.255.255` |

注意：

- Seed 不使用 `10.10.0.0` 作为主机地址。
- Seed 不创建业务 TUN IP。
- `10.10.0.0` 仅表示 `/16` 网络本身。
- `10.10.255.255` 不允许分配。

## 2.2 节点角色

```text
Seed
  └─ 无 TUN IP
  └─ 发现 / Peer 协调 / Relay / 网络维护
  └─ 不承担 Internet 出口

Gateway
  └─ 10.10.0.1/16
  └─ EasyTier Exit Node
  └─ DNS Forwarder
  └─ NAT / IP Forwarding
  └─ 虚拟网统一 Internet Gateway

Dedicated Server #2-#10
  └─ 10.10.0.2 - 10.10.0.10
  └─ 固定 IP
  └─ 功能等价普通 Overlay 节点
  └─ 不启用 Exit Node

Normal Client
  └─ Windows GUI / Linux CLI
  └─ 自动获取 10.10.0.11+
  └─ Gateway = 10.10.0.1
  └─ DNS = 10.10.0.1
```

---

# 3. 总体工程结构

建议在 `D:\EasytierHost` 下形成如下结构。

```text
D:\EasytierHost
│
├─ EasyTier-2.6.4\                 # 官方 EasyTier 源码，尽量少改
│
├─ src\
│  ├─ EasyTierHost.Abstractions\
│  ├─ EasyTierHost.Core\
│  ├─ EasyTierHost.Network\
│  ├─ EasyTierHost.Deployment\
│  ├─ EasyTierHost.Gateway\
│  ├─ EasyTierHost.Service\
│  ├─ EasyTierHost.Manager\
│  ├─ EasyTierHost.Client.Windows\
│  └─ EasyTierHost.Client.Linux\
│
├─ scripts\
│  ├─ windows\
│  ├─ linux\
│  └─ publish\
│
├─ config\
│  ├─ templates\
│  └─ schema\
│
├─ tests\
│  ├─ EasyTierHost.UnitTests\
│  ├─ EasyTierHost.IntegrationTests\
│  └─ EasyTierHost.NetworkTests\
│
├─ docs\
│  └─ EASYTIERHOST-DEVELOPMENT-PLAN.md
│
└─ publish\
   ├─ manager\
   ├─ seed-windows\
   ├─ seed-linux\
   ├─ dedicated-windows\
   ├─ dedicated-linux\
   ├─ client-windows\
   └─ client-linux\
```

---

# 4. EasyTier Core 修改范围

EasyTier Core 修改控制为两个方向：

1. DHCP 分配范围。
2. Underlay Socket/Route 防递归增强。

禁止把 GUI、角色管理、远程部署、DNS 管理、系统服务管理写进 EasyTier Core。

---

# 5. EasyTier Core：DHCP Range 改造

## 5.1 涉及源码文件

EasyTier 2.6.4 重点文件：

```text
EasyTier-2.6.4\easytier\src\core.rs
EasyTier-2.6.4\easytier\src\instance\instance.rs
EasyTier-2.6.4\easytier\src\common\config\*.rs
EasyTier-2.6.4\easytier-proto\proto\common.proto
```

具体路径以本地源码实际结构为准，但核心职责必须保持如下。

## 5.2 core.rs

新增 CLI / ENV 参数：

```rust
#[arg(long, env = "ET_DHCP_START")]
dhcp_start: Option<Ipv4Addr>,

#[arg(long, env = "ET_DHCP_END")]
dhcp_end: Option<Ipv4Addr>,
```

要求：

```text
未配置：
    保持 EasyTier 原始 DHCP 行为

配置：
    仅允许在 [dhcp_start, dhcp_end] 内选择地址
```

## 5.3 配置模型

建议增加：

```rust
pub struct DhcpRange {
    pub start: Option<Ipv4Addr>,
    pub end: Option<Ipv4Addr>,
}
```

建议位置：

```text
easytier/src/common/config/
```

新增文件：

```text
dhcp_range.rs
```

职责：

```rust
pub struct DhcpRangeValidator;
```

提供：

```rust
validate(network: Ipv4Inet, start: Ipv4Addr, end: Ipv4Addr)
contains(ip: Ipv4Addr) -> bool
```

校验：

```text
start <= end
start/end 必须属于当前 subnet
不能等于 network address
不能等于 broadcast address
```

## 5.4 instance.rs

当前核心方法：

```rust
check_dhcp_ip_conflict()
```

保留原有：

- Peer IP 收集
- IP 冲突检测
- 当前 IP 延用
- NIC 重建
- DhcpIpv4Changed 事件
- DhcpIpv4Conflicted 事件

只替换候选地址选择逻辑。

新增私有方法：

```rust
fn find_dhcp_candidate(
    network: Ipv4Inet,
    used_ipv4: &HashSet<Ipv4Inet>,
    range: Option<DhcpRange>,
) -> Option<Ipv4Inet>
```

默认：

```text
range == None
=> 原 EasyTier 逻辑
```

EasyTierHost：

```text
range.start = 10.10.0.11
range.end   = 10.10.255.254
```

## 5.5 单元测试

新增：

```text
tests/dhcp_range_tests.rs
```

至少覆盖：

```text
1. 10.10.0.1-10 已占用，不允许进入动态池
2. 首个动态节点必须得到 10.10.0.11
3. .11 已占用后分配 .12
4. 中间空洞优先回收最低可用地址
5. 255.255 不可分配
6. 越界配置拒绝启动
7. start > end 拒绝
8. DHCP 冲突后自动选择下一地址
```

---

# 6. EasyTier Core：Underlay 防递归增强

## 6.1 开发目标

当客户端默认路由指向 `10.10.0.1` 后，以下 EasyTier 自身流量不得经过 TUN：

```text
Seed
Manual Peer
Relay
STUN
NAT Probe
TCP Hole Punch
UDP Hole Punch
Peer endpoint
External node
```

## 6.2 原则

任何用于维持 Overlay 的 socket：

```text
必须绑定物理接口 / 物理源地址
不得服从 Overlay 默认路由
```

## 6.3 涉及源码目录

重点审计：

```text
easytier/src/socket/
easytier/src/connector/
easytier/src/tunnel/
easytier/src/peers/
easytier/src/instance/
```

重点文件至少包括：

```text
easytier/src/socket/tcp.rs
easytier/src/socket/udp.rs
easytier/src/tunnel/common.rs
easytier/src/connector/direct/*
easytier/src/connector/manual/*
easytier/src/connector/tcp_hole_punch/*
easytier/src/connector/udp_hole_punch/*
```

## 6.4 新增抽象

建议新增：

```text
easytier/src/socket/underlay_policy.rs
```

```rust
pub struct UnderlayBindingPolicy {
    pub bind_device: Option<String>,
    pub source_ipv4: Option<Ipv4Addr>,
    pub protect_from_tun: bool,
}
```

提供：

```rust
resolve_for_destination(dst: SocketAddr) -> UnderlayBindingPolicy
```

规则：

```text
Overlay 业务数据：
    正常走 EasyTier

EasyTier 自身 Underlay 连接：
    强制物理接口
```

## 6.5 不修改应用层业务数据路径

禁止：

```text
对所有 EasyTier 流量都绑物理接口
```

否则会破坏 Overlay 正常数据。

只有：

```text
socket/connect/listener underlay
```

使用该策略。

---

# 7. EasyTierHost.Abstractions

项目：

```text
src/EasyTierHost.Abstractions/
```

用于定义跨项目接口和数据模型。

## 7.1 文件清单

```text
Models/
  NodeRole.cs
  ServerOsType.cs
  OverlayAddressPlan.cs
  NetworkProfile.cs
  RemoteHostCredential.cs
  DeploymentRequest.cs
  DeploymentResult.cs
  GatewayState.cs
  HealthStatus.cs

Interfaces/
  IEasyTierProcessManager.cs
  IRouteController.cs
  IDnsController.cs
  IGatewayController.cs
  IRemoteExecutor.cs
  IServiceInstaller.cs
  IHealthChecker.cs
  IConfigurationStore.cs
```

## 7.2 NodeRole.cs

```csharp
public enum NodeRole
{
    Seed,
    Gateway,
    Dedicated,
    Client
}
```

## 7.3 ServerOsType.cs

```csharp
public enum ServerOsType
{
    Windows,
    Linux
}
```

## 7.4 OverlayAddressPlan.cs

```csharp
public sealed record OverlayAddressPlan
{
    public string NetworkCidr { get; init; } = "10.10.0.0/16";
    public string GatewayIp { get; init; } = "10.10.0.1";
    public string DnsIp { get; init; } = "10.10.0.1";
    public string DedicatedStart { get; init; } = "10.10.0.1";
    public string DedicatedEnd { get; init; } = "10.10.0.10";
    public string DhcpStart { get; init; } = "10.10.0.11";
    public string DhcpEnd { get; init; } = "10.10.255.254";
}
```

---

# 8. EasyTierHost.Core

项目：

```text
src/EasyTierHost.Core/
```

职责：

```text
角色判定
配置生成
启动参数生成
配置验证
状态聚合
```

## 8.1 文件

```text
Roles/
  SeedRolePolicy.cs
  GatewayRolePolicy.cs
  DedicatedRolePolicy.cs
  ClientRolePolicy.cs
  NodeRoleResolver.cs

Configuration/
  NetworkProfileBuilder.cs
  EasyTierConfigBuilder.cs
  EasyTierArgumentBuilder.cs
  NetworkProfileValidator.cs
  SecretProvider.cs
```

## 8.2 SeedRolePolicy

输出：

```text
NoTun = true
IPv4 = null
EnableExitNode = false
AcceptDns = false
```

Seed 只配置：

```text
network_name
network_secret
listeners
```

## 8.3 GatewayRolePolicy

固定：

```text
IPv4 = 10.10.0.1/16
EnableExitNode = true
GatewayEnabled = true
DnsEnabled = true
```

## 8.4 DedicatedRolePolicy

尾号范围：

```text
2-10
```

输出：

```text
EnableExitNode = false
Dhcp = false
IPv4 = fixed
```

## 8.5 ClientRolePolicy

输出：

```text
Dhcp = true
DhcpStart = 10.10.0.11
DhcpEnd = 10.10.255.254
ExitNode = 10.10.0.1
GatewayDns = 10.10.0.1
```

---

# 9. EasyTierHost.Network

项目：

```text
src/EasyTierHost.Network/
```

这是稳定性的核心项目。

## 9.1 文件结构

```text
Routes/
  GatewayRouteController.cs
  RouteSnapshot.cs
  RoutePlanner.cs
  RouteTransaction.cs
  RouteRollbackManager.cs
  UnderlayRouteProtector.cs
  LanRouteDetector.cs
  SeedRouteProtector.cs
  EndpointRouteProtector.cs

Windows/
  WindowsRouteApi.cs
  WindowsNetworkInterfaceResolver.cs
  WindowsDnsApi.cs

Linux/
  LinuxRouteApi.cs
  LinuxNetworkInterfaceResolver.cs
  LinuxDnsApi.cs
```

---

# 10. GatewayRouteController

文件：

```text
EasyTierHost.Network/Routes/GatewayRouteController.cs
```

这是系统唯一允许管理默认路由的类。

任何其它类禁止直接执行：

```text
route add
route delete
New-NetRoute
Remove-NetRoute
ip route add
ip route del
```

## 10.1 接口

```csharp
public interface IRouteController
{
    Task<RouteSnapshot> CaptureAsync();
    Task PrepareAsync(GatewayContext context);
    Task<bool> ProbeAsync(GatewayContext context);
    Task CommitAsync(GatewayContext context);
    Task RollbackAsync();
    Task ReconcileAsync();
}
```

## 10.2 状态机

```text
PhysicalOnly
    ↓
Capturing
    ↓
UnderlayProtected
    ↓
Probing
    ↓
GatewayActive

任一步失败
    ↓
RollingBack
    ↓
PhysicalOnly
```

枚举：

```csharp
public enum GatewayState
{
    PhysicalOnly,
    Capturing,
    UnderlayProtected,
    Probing,
    GatewayActive,
    RollingBack,
    Faulted
}
```

---

# 11. RouteSnapshot

保存启用网关前系统原始网络状态。

```csharp
public sealed record RouteSnapshot
{
    public required string PhysicalInterfaceName { get; init; }
    public required int PhysicalInterfaceIndex { get; init; }
    public required string PhysicalIpv4 { get; init; }
    public required string PhysicalGateway { get; init; }

    public required IReadOnlyList<RouteEntry> DefaultRoutes { get; init; }
    public required IReadOnlyList<RouteEntry> LocalLanRoutes { get; init; }
    public required IReadOnlyList<string> OriginalDnsServers { get; init; }
}
```

落盘：

```text
%ProgramData%\EasyTierHost\state\route-snapshot.json
```

Linux：

```text
/var/lib/easytier-host/state/route-snapshot.json
```

---

# 12. UnderlayRouteProtector

文件：

```text
Routes/UnderlayRouteProtector.cs
```

负责保护：

```text
Seed Physical IP
已知 Relay
已知 Peer Physical endpoint
STUN endpoint
当前 LAN
物理 Gateway
```

不能只保护 Seed。

## 12.1 动态 Endpoint

提供：

```csharp
Task UpdateProtectedEndpointsAsync(IEnumerable<IPAddress> endpoints);
```

EasyTierHost 定期从：

```text
EasyTier CLI / RPC / status
```

获取当前 peer physical endpoint。

变化时：

```text
新增 endpoint => 加 /32 物理路由
失效 endpoint => 延迟清理
```

不得频繁抖动。

建议设置：

```text
EndpointGracePeriod = 120s
```

---

# 13. Probe 流程

禁止直接上来修改默认路由。

步骤：

```text
1. Capture
2. Protect Seed / Peer / LAN
3. 确认 10.10.0.1 可达
4. 临时安装：
   1.1.1.1/32 -> Overlay
   8.8.8.8/32 -> Overlay
5. 测试：
   Ping/HTTPS/DNS
6. 成功 -> Commit
7. 失败 -> 删除 Probe Route + Rollback
```

Commit 后必须删除：

```text
1.1.1.1/32
8.8.8.8/32
```

---

# 14. 默认路由 Commit

Windows：

```text
0.0.0.0/0 -> Overlay interface
```

但在 Commit 前必须确保：

```text
Seed /32 -> physical
Known Peer /32 -> physical
LAN route -> physical
Physical gateway reachable
```

Windows 路由优先级计算必须使用：

```text
RouteMetric + InterfaceMetric
```

不能只调 InterfaceMetric。

---

# 15. EasyTierHost.Gateway

项目：

```text
src/EasyTierHost.Gateway/
```

负责 `.1` 节点。

## 15.1 文件

```text
Gateway/
  GatewayService.cs
  GatewayBootstrapper.cs
  IpForwardingManager.cs
  NatManager.cs
  GatewayHealthChecker.cs

Dns/
  DnsForwarderService.cs
  DnsUpstreamResolver.cs
  DnsHealthChecker.cs
```

---

# 16. GatewayBootstrapper

启动顺序：

```text
1. EasyTier ready
2. 确认 TUN = 10.10.0.1
3. 开启 IP forwarding
4. 建立 NAT
5. 启动 DNS Forwarder
6. health check
7. 标记 GatewayReady
```

禁止：

```text
EasyTier 未 ready
=> 先建默认路由/NAT
```

---

# 17. Windows NAT

建议封装：

```text
WindowsNatManager.cs
```

优先：

```text
WinNAT / New-NetNat
```

要求：

```text
SourcePrefix = 10.10.0.0/16
External = 当前物理网络
```

不把 NAT 写死到固定 Wi-Fi 名称。

必须自动解析当前真实 Internet 物理接口。

---

# 18. Linux NAT

文件：

```text
LinuxNatManager.cs
```

优先：

```text
nftables
```

兼容回退：

```text
iptables
```

规则核心：

```text
10.10.0.0/16 -> Physical WAN -> MASQUERADE
```

同时开启：

```text
net.ipv4.ip_forward=1
```

---

# 19. DNS Forwarder

不建议依赖 EasyTier Magic DNS 完成互联网 DNS。

网关 `.1` 单独运行 DNS Forwarder。

文件：

```text
Dns/DnsForwarderService.cs
```

监听：

```text
10.10.0.1:53 UDP
10.10.0.1:53 TCP
```

上游：

```text
从网关服务器物理网卡自动读取当前有效 DNS
```

禁止默认上游指向：

```text
10.10.0.1
```

否则自递归。

支持：

```text
UpstreamFallback:
  1.1.1.1
  8.8.8.8
```

是否启用公共 fallback 应配置化。

---

# 20. EasyTierHost.Deployment

项目：

```text
src/EasyTierHost.Deployment/
```

职责：

```text
远程连接
上传文件
安装服务
生成配置
启动
验证
失败回滚
```

## 20.1 目录

```text
Remote/
  RemoteExecutorFactory.cs
  SshRemoteExecutor.cs
  RemoteCommandResult.cs

Installers/
  SeedInstaller.cs
  DedicatedServerInstaller.cs
  GatewayServerInstaller.cs
  WindowsRemoteInstaller.cs
  LinuxRemoteInstaller.cs

Artifacts/
  ArtifactManifest.cs
  ArtifactResolver.cs
  ChecksumValidator.cs
```

---

# 21. IRemoteExecutor

统一 Windows/Linux 远程调用。

```csharp
public interface IRemoteExecutor
{
    Task<bool> TestConnectionAsync();
    Task UploadAsync(string localPath, string remotePath);
    Task<RemoteCommandResult> ExecuteAsync(string command);
}
```

Windows 默认：

```text
OpenSSH Server
PowerShell
```

Linux：

```text
OpenSSH
bash
```

这样管理程序不依赖 RDP。

---

# 22. SeedInstaller

文件：

```text
Installers/SeedInstaller.cs
```

步骤：

```text
Validate
  ↓
Test SSH
  ↓
Detect OS
  ↓
Upload Package
  ↓
Write Config
  ↓
Install Service
  ↓
Start
  ↓
Check Listener
  ↓
Check EasyTier status
  ↓
Success
```

Seed 配置禁止出现：

```text
ipv4 = 10.10.0.0
exit_node
dns
default gateway
```

---

# 23. GatewayServerInstaller

特殊服务器尾号为 1 时调用。

```text
Install EasyTier
Configure 10.10.0.1/16
Enable Exit Node
Install EasyTierHost.Gateway
Configure NAT
Configure DNS Forwarder
Install Service
Start
Health Check
```

失败必须回滚：

```text
NAT
IP forwarding
DNS service
EasyTierHost service
```

---

# 24. DedicatedServerInstaller

尾号 2-10。

要求：

```text
静态 Overlay IP
不安装 NAT
不启 DNS
不启 Exit Node
```

---

# 25. 管理端 GUI

项目：

```text
src/EasyTierHost.Manager/
```

推荐：

```text
.NET 8/9
WPF
MVVM
```

## 25.1 页面

```text
Views/
  MainWindow.xaml
  SeedInstallView.xaml
  DedicatedInstallView.xaml
  DeploymentLogView.xaml

ViewModels/
  MainViewModel.cs
  SeedInstallViewModel.cs
  DedicatedInstallViewModel.cs
  DeploymentLogViewModel.cs
```

---

# 26. Seed 管理页面字段

```text
OS Type
  Windows
  Linux

Remote Physical IP
SSH Port
Username
Password / Key

Network Name
Network Secret
Listener Port
```

按钮：

```text
测试连接
安装
重新部署
查看日志
卸载
```

---

# 27. 专用服务器页面字段

```text
OS Type
Physical IP
SSH Port
Username
Password
Seed Physical IP
Dedicated Index = 1-10
```

UI 自动展示：

```text
1 => 10.10.0.1 Gateway + DNS
2 => 10.10.0.2
...
10 => 10.10.0.10
```

当 index=1：

```text
Gateway/DNS Checkbox
```

必须锁定为开启，避免用户建立错误角色。

---

# 28. Windows 普通客户端

项目：

```text
src/EasyTierHost.Client.Windows/
```

推荐：

```text
WPF + Windows Service
```

UI 只需：

```text
Seed Physical IP
连接
断开
状态
```

高级设置可折叠。

## 28.1 类结构

```text
Services/
  ClientBootstrapper.cs
  EasyTierClientService.cs
  ClientGatewayCoordinator.cs
  ClientHealthMonitor.cs

ViewModels/
  MainViewModel.cs
```

---

# 29. ClientBootstrapper

启动顺序：

```text
Load Profile
  ↓
Start EasyTier
  ↓
Wait Peer Connected
  ↓
Wait DHCP IP
  ↓
Validate IP >= 10.10.0.11
  ↓
Ping 10.10.0.1
  ↓
RouteGuard.Prepare
  ↓
Gateway Probe
  ↓
Set DNS = 10.10.0.1
  ↓
Commit default route
  ↓
Health Check
```

任何失败：

```text
Rollback DNS
Rollback Route
Keep Overlay
```

原则：

> 网关失败不能导致整个虚拟局域网一起失效。

---

# 30. Linux 普通客户端

项目：

```text
src/EasyTierHost.Client.Linux/
```

命令：

```bash
sudo easytier-host install
```

首次交互：

```text
Seed physical IP:
>
```

其它配置自动生成。

安装后服务：

```text
easytier-host.service
```

支持：

```text
easytier-host status
easytier-host diagnostics
easytier-host reconnect
easytier-host uninstall
```

---

# 31. EasyTierHost.Service

项目：

```text
src/EasyTierHost.Service/
```

跨 Windows/Linux 主机守护。

## 31.1 文件

```text
Hosting/
  HostWorker.cs
  StartupCoordinator.cs
  ShutdownCoordinator.cs

Process/
  EasyTierProcessManager.cs
  ProcessSupervisor.cs

Health/
  OverlayHealthMonitor.cs
  GatewayHealthMonitor.cs
  DnsHealthMonitor.cs
  UnderlayHealthMonitor.cs
```

---

# 32. EasyTierProcessManager

职责：

```text
启动 easytier-core
停止
重启
读取 stdout/stderr
检测异常退出
控制配置路径
```

禁止多个模块直接 `Process.Start("easytier-core")`。

统一由：

```text
IEasyTierProcessManager
```

管理。

---

# 33. ProcessSupervisor

策略：

```text
EasyTier 异常退出
  ↓
5s
  ↓
restart

连续失败 > 5 次 / 2min
  ↓
进入 Degraded
  ↓
停止修改系统路由
  ↓
恢复物理默认路由
```

不能出现：

```text
EasyTier 已死
但系统 0/0 仍指向 Overlay
```

---

# 34. 健康状态模型

```csharp
public sealed record HealthStatus
{
    public bool UnderlayOk { get; init; }
    public bool SeedReachable { get; init; }
    public bool OverlayOk { get; init; }
    public bool GatewayReachable { get; init; }
    public bool DnsOk { get; init; }
    public bool InternetOk { get; init; }
}
```

---

# 35. 健康检查分层

## L1 Underlay

```text
Physical Gateway
Seed Physical IP
```

## L2 Overlay

```text
Local TUN
10.10.0.1
Peer list
```

## L3 DNS

```text
query via 10.10.0.1
```

## L4 Internet

```text
HTTPS test
```

不得使用单一 `ping 8.8.8.8` 判断全部健康。

---

# 36. 自动恢复策略

```text
Underlay down
=> 不删除 Overlay 配置
=> 等物理网络恢复

Seed down
=> 保留现有 Overlay
=> 重试 Seed / Peer

Gateway down
=> Rollback 默认路由和 DNS
=> Overlay 局域网继续工作

DNS down
=> 可回滚为物理 DNS
=> Overlay 保持

EasyTier down
=> Rollback 默认路由
```

---

# 37. 配置文件

统一配置：

Windows：

```text
%ProgramData%\EasyTierHost\config\network.json
```

Linux：

```text
/etc/easytier-host/network.json
```

建议内容：

```json
{
  "networkId": "company-overlay",
  "networkName": "company-overlay",
  "networkSecret": "ENCRYPTED_VALUE",
  "seed": {
    "physicalIp": "141.164.40.70",
    "port": 11010
  },
  "overlay": {
    "cidr": "10.10.0.0/16",
    "gateway": "10.10.0.1",
    "dns": "10.10.0.1",
    "dhcpStart": "10.10.0.11",
    "dhcpEnd": "10.10.255.254"
  },
  "role": "Client"
}
```

---

# 38. Secret 管理

禁止明文长期保存：

```text
SSH password
Network secret
```

Windows：

```text
DPAPI
```

Linux：

```text
root-only file + 0600
```

管理程序远程密码：

```text
仅会话使用
除非用户明确要求保存
```

---

# 39. 日志

统一目录：

Windows：

```text
%ProgramData%\EasyTierHost\logs\
```

Linux：

```text
/var/log/easytier-host/
```

日志分类：

```text
host.log
network.log
route.log
deployment.log
gateway.log
dns.log
easytier.log
```

禁止把密码和 network secret 写入日志。

---

# 40. 诊断命令

统一：

```text
easytier-host diagnostics
```

输出：

```text
BuildId
Role
Physical NIC
Physical IP
Physical Gateway
Overlay IP
Seed
Peer Count
Gateway State
DNS
Default Route
Protected Endpoints
EasyTier PID
Recent Errors
```

Windows 额外打印：

```text
RouteMetric
InterfaceMetric
TotalMetric
```

---

# 41. 发布结构

```text
publish/
├─ manager/
├─ seed-windows/
├─ seed-linux/
├─ dedicated-windows/
├─ dedicated-linux/
├─ client-windows/
└─ client-linux/
```

每个目录附带：

```text
manifest.json
version.txt
sha256.txt
```

---

# 42. BuildId

所有组件统一 BuildId：

```text
EasyTierHost BuildId
EasyTier Core SHA
Build Timestamp
Config Schema Version
```

诊断必须能看到。

示例：

```text
EasyTierHost: 1.0.0
EasyTierCore: 2.6.4+custom.3
EasyTierCommit: 8428a89
Schema: 1
```

---

# 43. Windows 服务

建议服务：

```text
EasyTierHost
```

由 Host 内部管理 EasyTier 子进程。

不要同时：

```text
EasyTierCore Windows Service
EasyTierHost Windows Service
```

各自互相抢状态。

推荐：

```text
SCM
  ↓
EasyTierHost.exe
  ↓
easytier-core.exe
```

启动：

```text
Automatic
DelayedAutoStart = true
```

---

# 44. Linux systemd

```ini
[Unit]
Description=EasyTierHost
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/local/bin/easytier-host run
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

EasyTier Core 由 Host 子进程管理。

---

# 45. Windows 网络 API

尽量不依赖 PowerShell 输出解析。

优先使用：

```text
IP Helper API
GetIpForwardTable2
CreateIpForwardEntry2
DeleteIpForwardEntry2
GetAdaptersAddresses
```

封装：

```text
WindowsRouteApi.cs
```

PowerShell 只用于：

```text
安装/诊断/兼容 fallback
```

---

# 46. Linux 网络 API

第一版可封装命令：

```text
ip route
ip addr
resolvectl
nft
sysctl
```

但所有调用只能通过：

```text
LinuxRouteApi
LinuxDnsApi
LinuxNatManager
```

禁止散落 shell 命令。

---

# 47. DNS 客户端策略

普通客户端：

```text
Overlay Active + Gateway Healthy
=> DNS = 10.10.0.1

Gateway Unhealthy
=> Restore Original DNS
```

需要保存原 DNS：

```text
RouteSnapshot.OriginalDnsServers
```

不能永久覆盖。

---

# 48. Gateway 服务器自己的 DNS

`.1`：

```text
系统 resolver
=> 继续使用物理网络 DNS
```

DNS Forwarder：

```text
10.10.0.1:53
=> physical upstream DNS
```

禁止：

```text
网关系统 DNS = 10.10.0.1
```

---

# 49. LAN 直连规则

在客户端启用 Overlay Gateway 后：

```text
本地局域网仍直连
```

必须保护系统当前所有 Connected Route。

例如：

```text
10.189.1.0/24 -> WLAN
192.168.1.0/24 -> Ethernet
```

不能只硬编码 RFC1918 全部直连，否则会影响用户可能希望经网关访问的私网。

优先保护：

```text
当前真实连接的 local subnet
```

---

# 50. 网络变更检测

用户可能：

```text
Wi-Fi -> 网线
热点 -> Wi-Fi
VPN 开/关
DHCP 更新
```

因此新增：

```text
NetworkChangeMonitor.cs
```

职责：

```text
监听系统网卡/路由变更
重新 Capture
重新保护 Underlay
必要时重新 Probe
```

禁止在网卡切换后继续沿用旧：

```text
PhysicalGateway
InterfaceIndex
```

---

# 51. 路由 Reconcile

每 10-30 秒执行轻量校验：

```text
默认路由是否仍正确
Seed route 是否存在
Peer endpoint route 是否存在
Overlay route 是否存在
```

只能：

```text
修正本系统拥有的路由
```

禁止删除用户其它 VPN/软件创建的未知路由。

因此 RouteEntry 必须带：

```text
OwnerTag
CreatedByEasyTierHost
```

Windows 无原生 tag 时：

```text
本地状态数据库记录 exact route identity
```

---

# 52. 本地状态存储

SQLite 不是必须。

第一版建议：

```text
JSON state files
```

因为状态量很小。

```text
state/
  route-snapshot.json
  managed-routes.json
  endpoint-cache.json
  deployment-state.json
```

后续复杂再引入 SQLite。

---

# 53. 远程部署幂等

所有 Installer：

```text
CheckCurrent
Diff
Apply
Verify
```

重复点击“安装”不得：

```text
重复添加服务
重复 NAT
重复 systemd unit
重复 route
```

---

# 54. 卸载

必须设计。

```text
Stop Host
Rollback Gateway
Restore DNS
Remove managed routes
Remove NAT
Disable forwarding if owned
Remove Service
Remove binaries
Keep logs optional
```

不能简单删除 EXE。

---

# 55. Manager 部署安全

安装前显示摘要：

```text
Target
Role
OS
Overlay IP
Seed
Gateway
DNS
Files
Service Name
```

用户确认后执行。

部署结果必须分阶段记录。

```text
Connected
Uploaded
Configured
ServiceInstalled
Started
Verified
```

---

# 56. 错误码

统一定义：

```text
ETH001 SSH_CONNECT_FAILED
ETH002 OS_UNSUPPORTED
ETH003 CONFIG_INVALID
ETH101 SEED_UNREACHABLE
ETH102 OVERLAY_IP_CONFLICT
ETH201 GATEWAY_UNREACHABLE
ETH202 DNS_FAILED
ETH203 INTERNET_PROBE_FAILED
ETH301 ROUTE_COMMIT_FAILED
ETH302 ROUTE_ROLLBACK_FAILED
```

GUI 和 CLI 均显示统一错误码。

---

# 57. 自动 IP 规则

动态客户端拿到 IP 后必须验证：

```text
10.10.0.11 <= IP <= 10.10.255.254
```

如果拿到：

```text
10.10.0.2 - 10
```

视为：

```text
DHCP RANGE 改造未生效 / 配置错误
```

立即停止 Gateway Commit。

---

# 58. 专用 IP 冲突检查

Manager 安装固定 `.1-.10` 前：

```text
查询 Overlay Peer list
```

如已存在相同 IP：

```text
拒绝安装
```

不允许靠 EasyTier 后续冲突处理碰运气。

---

# 59. Gateway 唯一性

第一版只允许：

```text
10.10.0.1
```

作为 Gateway。

检测到多个节点声明：

```text
Gateway Role
```

应警告并拒绝自动 Commit。

第一版不要做多网关选举。

---

# 60. 测试项目

## UnitTests

测试：

```text
IP range
角色策略
配置生成
RoutePlanner
状态机
回滚
```

## IntegrationTests

测试：

```text
Windows route
Linux route
DNS
NAT
service install
```

## NetworkTests

至少构建：

```text
1 Seed
1 Gateway
2 Client
```

---

# 61. 必须完成的网络验收

## Case A

不开 Gateway：

```text
Client A -> Client B
0% packet loss
```

## Case B

开启 Gateway：

```text
Client -> 10.10.0.1
Client -> Internet
```

## Case C

开启 Gateway 后 Seed：

```text
Seed physical RTT 不明显增加
```

## Case D

开启 Gateway 后：

```text
EasyTier Peer 仍保持 P2P
```

不能大面积退化 Relay。

## Case E

Gateway 宕机：

```text
<= 健康检测周期
恢复物理默认路由
```

## Case F

Seed 宕机：

```text
已建立 Peer 尽量继续
```

## Case G

切 Wi-Fi：

```text
Underlay 自动重新绑定
```

---

# 62. 性能验收

记录：

```text
Gateway Off:
  Seed RTT
  Overlay RTT
  P2P status

Gateway On:
  Seed RTT
  Overlay RTT
  P2P status
```

要求：

```text
开启 Gateway 后
Seed Underlay 不能从几十 ms 变成数百 ms
不能出现 25%-40% 丢包
```

这应作为硬性回归测试。

---

# 63. 开发顺序

## Phase 1 - EasyTier 最小修改

```text
DHCP Range
Underlay socket 审计
Build EasyTier Core
```

## Phase 2 - Host 核心

```text
ProcessManager
Config
Role
Health
```

## Phase 3 - RouteGuard

```text
Capture
Protect
Probe
Commit
Rollback
Reconcile
```

## Phase 4 - Gateway

```text
Exit Node
NAT
DNS
```

## Phase 5 - Windows Client

```text
GUI
Service
Auto DHCP
Gateway
```

## Phase 6 - Linux Client

```text
CLI
systemd
```

## Phase 7 - Manager

```text
Remote Seed
Remote Dedicated
Remote Gateway
```

## Phase 8 - Network Regression

```text
P2P
Gateway
DNS
Failover
Route recursion
```

---

# 64. 第一版禁止事项

第一版不要实现：

```text
多 Gateway
Gateway 负载均衡
复杂 ACL 管理台
Web Portal
集中式 DHCP Server
集中式租约数据库
Overlay 内 DNS 域名系统
复杂证书 PKI
自动多 Seed 选举
```

先把单 Seed + 单 Gateway 做稳定。

---

# 65. 关键代码所有权规则

必须遵守：

```text
EasyTier Core
  => Overlay 数据平面

GatewayRouteController
  => 系统路由唯一写入者

DnsController
  => 系统 DNS 唯一写入者

NatManager
  => NAT 唯一写入者

EasyTierProcessManager
  => easytier-core 进程唯一管理者

ServiceInstaller
  => 系统服务唯一安装者
```

任何绕过这些类的实现视为架构违规。

---

# 66. 建议新增 ADR

```text
docs/adr/
  ADR-0001-role-model.md
  ADR-0002-ip-plan.md
  ADR-0003-dhcp-range.md
  ADR-0004-single-route-owner.md
  ADR-0005-underlay-protection.md
  ADR-0006-gateway-dns.md
  ADR-0007-rollback.md
```

其中 `ADR-0005` 必须明确：

```text
Overlay 默认路由不得承载 Overlay 自身维持连接所依赖的 Underlay 流量。
```

---

# 67. 关键配置示例

## Seed

```text
role = Seed
ipv4 = none
dhcp = false
enable_exit_node = false
```

## Gateway

```text
role = Gateway
ipv4 = 10.10.0.1/16
dhcp = false
enable_exit_node = true
dns = true
```

## Dedicated Server #3

```text
role = Dedicated
ipv4 = 10.10.0.3/16
dhcp = false
enable_exit_node = false
```

## Client

```text
role = Client
dhcp = true
dhcp_start = 10.10.0.11
dhcp_end = 10.10.255.254
exit_node = 10.10.0.1
dns = 10.10.0.1
```

---

# 68. 最终核心调用链

普通客户端：

```text
EasyTierHost.Service
  ↓
StartupCoordinator
  ↓
EasyTierProcessManager
  ↓
EasyTier Core
  ↓
DHCP Range 获取 10.10.0.11+
  ↓
ClientGatewayCoordinator
  ↓
GatewayRouteController.Prepare
  ↓
UnderlayRouteProtector
  ↓
Probe
  ↓
DnsController
  ↓
Commit
  ↓
HealthMonitor
  ↓
Reconcile
```

Gateway：

```text
EasyTierHost.Service
  ↓
EasyTier Core
  ↓
10.10.0.1
  ↓
GatewayBootstrapper
  ├─ IpForwardingManager
  ├─ NatManager
  ├─ DnsForwarderService
  └─ GatewayHealthChecker
```

Manager：

```text
EasyTierHost.Manager
  ↓
DeploymentService
  ↓
RemoteExecutor
  ↓
SeedInstaller / GatewayInstaller / DedicatedInstaller
  ↓
Remote EasyTierHost Service
```

---

# 69. Definition of Done

项目第一版完成必须同时满足：

```text
[ ] Seed Windows 可远程安装
[ ] Seed Linux 可远程安装
[ ] Seed 开机自动运行
[ ] Dedicated Windows 可远程安装
[ ] Dedicated Linux 可远程安装
[ ] 10.10.0.1 自动启用 Gateway/DNS
[ ] 10.10.0.2-10 固定 IP
[ ] Windows Client 自动获取 .11+
[ ] Linux Client 自动获取 .11+
[ ] DHCP 不进入 .1-.10
[ ] 客户端可访问 Overlay
[ ] 客户端可通过 .1 上 Internet
[ ] DNS 通过 .1 工作
[ ] Gateway 失败可自动回滚
[ ] Seed/Peer Underlay 不递归
[ ] 开 Gateway 后 P2P 不明显退化
[ ] 所有服务开机自动启动
[ ] 重复安装幂等
[ ] 卸载能恢复网络
[ ] diagnostics 可定位网络问题
```

---

# 70. 开发执行原则

最终实施时坚持：

```text
EasyTier 核心少改
Host 外层自治
路由单一所有者
网关先 Probe 后 Commit
任何失败可回滚
Underlay 永远不进 Overlay
业务 Overlay 与 Internet Gateway 解耦
```

本方案中优先级最高的三个模块：

```text
1. DHCP Range
2. GatewayRouteController
3. UnderlayRouteProtector
```

GUI、远程安装和发布体系都应建立在这三个模块稳定之后。

