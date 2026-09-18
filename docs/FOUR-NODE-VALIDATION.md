# 四节点实机验收方案

适用于 `EasyTierHost 0.2` 开发分支的最终网络验收。自动测试不能替代本方案，因为路由、WinNAT/nftables、DNS、网卡切换和 Underlay 防递归必须在独立机器上观察真实数据面。

## 1. 拓扑

至少使用四台相互独立的主机：

| 节点 | 角色 | Overlay 地址 | 要求 |
|---|---|---:|---|
| S | Seed | 无 TUN | 仅维护 EasyTier Overlay/Peer，使用真实物理 IP 提供 Seed |
| G | Gateway | `10.10.0.1/16` | 独立物理出口，可执行 NAT/forwarding/DNS |
| C1 | Client | DHCP `10.10.0.11–10.10.255.254` | `enableInternetGateway=true` |
| C2 | Client | DHCP `10.10.0.11–10.10.255.254` | 地址必须与 C1 不同 |

所有节点使用相同 `networkName` 与 network secret。Seed Physical IP 永远指物理网络地址，不是 `10.10.0.0` 或 `10.10.0.1`。

建议 C1、C2 至少有一台 Windows 10/11；Gateway 同时分别在 Windows 与 Linux 各完成一轮验收。Linux 节点应有 `python3`，Windows 使用管理员 PowerShell。

## 2. 采集工具

Windows：

```powershell
./scripts/validation/collect-windows.ps1 `
  -HostExecutable 'C:\Program Files\EasyTierHost\easytier-host.exe' `
  -ProfilePath 'C:\ProgramData\EasyTierHost\config\network.json' `
  -StateDirectory 'C:\ProgramData\EasyTierHost\state'
```

对采集出的 `diagnostics.json` 可执行：

```powershell
./scripts/validation/verify-windows-node.ps1 -DiagnosticsPath <path> -ExpectedRole Seed
./scripts/validation/verify-windows-node.ps1 -DiagnosticsPath <path> -ExpectedRole Gateway
./scripts/validation/verify-windows-node.ps1 -DiagnosticsPath <path> -ExpectedRole Client -RequireInternetGateway
```

Linux：

```bash
sudo EASYTIER_HOST_BIN=/opt/easytier-host/easytier-host \
  EASYTIER_HOST_PROFILE=/etc/easytier-host/network.json \
  EASYTIER_HOST_STATE=/var/lib/easytier-host \
  ./scripts/validation/collect-linux.sh
```

`diagnostics` 输出不包含 network secret；不要上传 `core.toml`、`network.secret` 或 SSH 凭据作为验收证据。

## 3. Case A：角色与地址唯一性

启动 S、G、C1、C2 后等待稳定 60 秒，然后四台机器分别采集诊断。

通过条件：Seed 无 `10.10.0.0/16` TUN 地址；Gateway 只有 `10.10.0.1/16`；C1/C2 均位于客户端 DHCP 池且地址不同；四节点 Core RPC 正常；Gateway 状态为 `GatewayReady`；客户端开启 Internet Gateway 时状态最终为 `GatewayActive`。

如果发现两个 Peer 使用同一 Overlay IP，应判定失败，不得继续测试 Internet Gateway。

## 4. Case B：客户端路由提交正确

在 C1/C2 检查诊断中的 `routeMetrics` 与系统路由表。

通过条件：原物理 `0.0.0.0/0` 仍存在；Host 新增 `0.0.0.0/1` 与 `128.0.0.0/1`，下一跳均为 `10.10.0.1`；Seed 与当前真实 Peer endpoint 的 `/32` 保护路由走物理接口；Probe 使用的 `1.1.1.1/32`、`8.8.8.8/32` 在 Commit 后不存在；本地 Connected Route 保持不变。

Windows 的 `routeMetrics` 同时记录 `RouteMetric`、`InterfaceMetric` 与 `TotalMetric`，用于复核路由选择，但 `/1` 对 `/0` 的优先级首先由最长前缀决定。

## 5. Case C：Underlay 防递归

在客户端 Gateway 已激活时检查到 Seed 的实际选路。Windows 采集文件中的 `SeedRouting.SelectedSourceAddress` 应为物理网卡地址；Linux `route-to-seed.txt` 中 `dev/src` 应为物理接口/物理 IP。

同时建议在 C1 做一次 2–5 分钟抓包：物理网卡应看到到 Seed/Peer/STUN 的 TCP/UDP 维护流量；TUN 上不应出现以 Seed Physical IP 为目的地址的维护连接。业务 Internet 流量进入 TUN 属于正常现象。

通过条件：Gateway 激活前后 Seed/Peer 维护连接不绕回 `10.10.0.1`，无持续高延迟/丢包递归现象。

## 6. Case D：Internet 与 DNS

在 C1/C2 执行 DNS 查询和 HTTPS 测试。DNS 客户端应通过 `10.10.0.1` 的 Gateway DNS 策略；Gateway 自身仍使用物理 DNS，上游不得指向 `10.10.0.1`。

通过条件：UDP 53、TCP 53 均可用；HTTPS 可访问公网；客户端到公网的路由首段进入 Overlay Gateway；Gateway 外网数据从其物理出口 NAT 发出；客户端本地 LAN Connected Route 仍直连。

不要只用 `ping 8.8.8.8` 作为健康结论，至少同时检查 DNS 与 HTTPS。

## 7. Case E：Gateway 故障恢复

保持 S/C1/C2 在线，停止 G 的 EasyTierHost 服务或断开 G 的物理 Internet。

通过条件：客户端健康探测失败后撤销自有 `/1` 和 DNS 接管，恢复原物理 Internet 路由/DNS；客户端物理网络不能被锁死；若 Overlay 的其它 P2P 路径仍存在，C1/C2 局域网通信应尽量保持。恢复 G 后，客户端重新经过 Underlay → Overlay → Probe → Commit 流程，而不是直接复用旧 `GatewayActive` 状态。

## 8. Case F：客户端切网

在 C1 进行 Wi-Fi → Ethernet、热点 → Wi-Fi 或 DHCP 地址变化。

通过条件：Host 检测 `PhysicalInterfaceIndex/PhysicalIpv4/PhysicalGateway` 变化；先回滚旧 DNS/路由；Core 重启并用新的 `--underlay-source-ipv4`；新的 Seed/Peer `/32` 安装到新物理接口；Probe 成功后才恢复 `/1` Gateway 路由。不得继续使用旧网卡索引或旧源 IP。

## 9. Case G：崩溃/断电恢复

分别在 GatewayReady 与 Client GatewayActive 状态下模拟进程强制终止或主机重启。

通过条件：启动时先处理 `gateway-journal.json` / `route-journal.json`；只删除 EasyTierHost 自己创建的 NAT、DNS、路由/forwarding 状态；未知 VPN/第三方路由不得被删除；恢复失败时保留 journal 并进入 Faulted/Degraded，而不是忽略日志继续接管网络。

## 10. Case H：冲突与共存

验证以下负面场景：第二台 Gateway 尝试使用 `10.10.0.1`；客户端存在其它 VPN 默认路由；客户端启动时 Seed 不可达；Gateway DNS 上游不可用。

通过条件：地址冲突被拒绝；Host 不收编或删除第三方路由；未通过 Probe 时不提交 `/1`；DNS/NAT 配置失败能够撤销；`recentErrors` 只记录安全错误码/类型，不出现 network secret、SSH 密码或完整远程命令输出。

## 11. 验收证据

每轮保留四台节点的 `diagnostics.json`、Windows `windows-network-snapshot.json` 或 Linux 采集目录、关键抓包摘要、测试时间、节点 OS/版本、EasyTierHost BuildId 与 HostCommit。失败 Case 必须记录失败前后两份诊断，确认回滚是否发生。

只有 Windows Gateway、Linux Gateway、至少两种客户端网络切换以及一次断电/崩溃恢复全部通过后，才应把 `enableInternetGateway=true` 从“实验性”升级为生产可用状态。
