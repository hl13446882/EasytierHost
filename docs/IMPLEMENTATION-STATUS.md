# 实施状态（2026-09-18，0.2）

已实现 Overlay 基础、网关生命周期、平台适配组件，以及客户端 Internet 路由的实验性启动协调。尚未达到完整开发方案的交付标准；`enableInternetGateway=true` 现可进入受保护的 Probe → Commit → Reconcile 流程，但仍必须完成 Windows/Linux 多节点实机抓包与切网验收后才能视为正式可用。

| 模块 | 已实现 | 尚需完成 |
|---|---|---|
| DHCP / 角色 | /16 地址池、最低空洞回收、四角色配置、保留地址校验 | 多节点冲突回归、安装前专用地址查询 |
| Host | 单实例锁、进程守护、私有配置、机器级密钥保护、RPC 实例核对 | Windows SCM、安装卸载 |
| Gateway | .1 TUN 就绪检测、重复网关检查、forwarding/NAT/DNS 顺序启动、健康监测、日志恢复与失败撤销 | Windows/Linux 实机集成验收 |
| Windows NAT | 独占 WinNAT 检查、物理出口地址约束、按 GUID 恢复 forwarding、限定 TUN/子网/端口的 DNS 防火墙规则 | 实际 WinNAT 和防火墙写入验证 |
| Linux NAT | 独立 nftables 表、所有权标记、限定 10.10/16 与出口的 masquerade、sysctl 恢复 | Linux 实机；现有防火墙需允许转发和 TUN DNS，未提供 iptables 回退 |
| DNS 转发 | UDP/TCP、并发限制、上游回退、随机上游事务 ID、问题字段校验、真实 socket 健康探测 | 协议模糊测试与负载验收 |
| 客户端路由 | 写前日志、Probe 后提交两条 /1、原 /0 保留、启动协调器、动态 endpoint /32、接口变化撤销、崩溃恢复 | Windows/Linux 实机默认路由、切网、休眠/恢复和长期稳定性验收 |
| 客户端 DNS / Probe | Windows 自有 NRPT、Linux resolved 指定链路、DNS/HTTPS 源地址探测、激活后周期健康探测 | 两平台真实 DNS、代理/VPN 共存和故障恢复验收 |
| Underlay | TCP/UDP、STUN、打洞、发现 DNS 的显式物理 IPv4 绑定；Host 启动前捕获物理 IPv4 并传入 Core；保护模式拒绝 RPC `patch_config` 与 RPC 新建/覆盖实例 | 网卡切换、多节点抓包验证 Seed/Relay/STUN/新 endpoint/DNS |
| CI | Windows Host solution build、UnitTests、客户端路由集成 smoke；EasyTier DHCP/Underlay 针对性测试已接入 workflow | 发布包/manifest 验证接入 CI，Linux CI |
| GUI / 部署 | 尚未实现 | 两个 WPF 界面、SSH 部署、完整双平台安装包 |

## 使用边界

Gateway 角色现可由 `run` 启动，要求管理员/root、连接到同网络 Peer、独立物理接口和配置名称匹配的 .1/16 TUN。状态在专用状态目录的 `gateway-status.json`；崩溃恢复记录为 `gateway-journal.json`。清理失败保留日志并停止重启，不能手工删除日志来跳过恢复。

Client 设置 `enableInternetGateway=true` 时，Host 会在 Core 启动前捕获物理默认出口，将物理 IPv4 作为运行时参数传给 `--underlay-source-ipv4`，等待 Client TUN 与 Core 实例身份一致后执行：保护 Seed/Peer endpoint → 安装 Probe /32 → DNS/HTTPS Probe → 应用客户端 DNS → 安装 `0.0.0.0/1` 和 `128.0.0.0/1`。原物理 `/0` 保留。运行期间发现物理接口/IP/网关、Overlay 身份、路由所有权或健康状态变化，会先撤销自有路由/DNS，再由 Host 重启 Core 并重新捕获物理出口。

`UnderlayProtectionVerified` 仍不是用户可配置的豁免开关，只由 Host 根据本次 Core 启动实际绑定的物理 IPv4 生成。Core 保护模式仅支持单静态配置的 TCP/UDP Host 构建，强制关闭 UPnP；维护 DNS 使用受保护的物理 socket，避免系统 DNS stub 在默认路由切换后递归进入 TUN。保护模式下查询 RPC 保留，但运行时配置 patch 和 RPC 新建/覆盖实例被拒绝，配置变化必须由 Host 完成完整 rollback → restart。IPv6 默认路由不由 Host 接管。

目前自动测试仍不能替代实机验收：平台路由/NAT/DNS 写入的大部分测试使用替身命令执行器，真实网络测试只覆盖本机回环 socket。开发分支持续执行 Host build、UnitTests、Underlay/Client route smoke 以及 EasyTier DHCP/Underlay 针对性 Rust 测试；正式发布前仍需完成 Seed + Gateway + 两个 Client 的独立多机 Case、Windows/Linux 抓包与断电/切网恢复验证。
