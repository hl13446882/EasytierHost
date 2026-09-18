# 实施状态（2026-09-18，0.2）

已实现 Overlay 基础、网关生命周期、平台适配组件、客户端 Internet 路由事务、Windows/Linux 服务化、SSH 事务式远程部署、管理端 WPF、普通客户端控制入口、结构化诊断与发布包完整性校验。尚未达到完整开发方案的最终交付标准；`enableInternetGateway=true` 已进入受保护的 Probe → Commit → Reconcile 流程，但仍必须完成 Windows/Linux 多节点实机抓包、切网、断电恢复验收后才能视为正式可用。

| 模块 | 已实现 | 尚需完成 |
|---|---|---|
| DHCP / 角色 | /16 地址池、最低空洞回收、四角色配置、保留地址校验；Client `.11–.255.254`、Gateway `.1`、Dedicated `.2–.10` | 多节点冲突回归、真实大规模地址租约/重连验收 |
| Host | 单实例锁、进程守护、私有配置、机器级密钥保护、RPC 实例核对、Windows SCM、Linux systemd、正常停止与自动重启 | 双平台长期服务稳定性、升级/断电实机验收 |
| Gateway | `.1` TUN 就绪检测、重复网关检查、forwarding/NAT/DNS 顺序启动、健康监测、日志恢复与失败撤销 | Windows/Linux 实机集成验收 |
| Windows NAT | 独占 WinNAT 检查、物理出口地址约束、按 GUID 恢复 forwarding、限定 TUN/子网/端口的 DNS 防火墙规则 | 实际 WinNAT 和防火墙写入验证 |
| Linux NAT | 独立 nftables 表、所有权标记、限定 10.10/16 与出口的 masquerade、sysctl 恢复 | Linux 实机；现有防火墙需允许转发和 TUN DNS，未提供 iptables 回退 |
| DNS 转发 | UDP/TCP、并发限制、上游回退、随机上游事务 ID、问题字段校验、真实 socket 健康探测 | 协议模糊测试与负载验收 |
| 客户端路由 | 写前日志、Probe 后提交两条 `/1`、原 `/0` 保留、启动协调器、动态 endpoint `/32`、接口变化撤销、崩溃恢复 | Windows/Linux 实机默认路由、切网、休眠/恢复和长期稳定性验收 |
| 客户端 DNS / Probe | Windows 自有 NRPT、Linux resolved 指定链路、DNS/HTTPS 源地址探测、激活后周期健康探测 | 两平台真实 DNS、代理/VPN 共存和故障恢复验收 |
| Underlay | TCP/UDP、STUN、打洞、发现 DNS 的显式物理 IPv4 绑定；Host 启动前捕获物理 IPv4 并传入 Core；保护模式拒绝 RPC `patch_config` 与 RPC 新建/覆盖实例 | 网卡切换、多节点抓包验证 Seed/Relay/STUN/新 endpoint/DNS |
| Diagnostics | 稳定 JSON 合同；Build/Core/Role/Runtime、物理出口、Overlay、Peer、Gateway、DNS、Windows Route/Interface/Total Metric、受保护 endpoint、脱敏 recentErrors；活动 Gateway/Client 运行态规范化；Seed 不采纳无关 10.10/16 网卡 | 实机诊断字段校准、长期错误历史/日志联动 |
| Windows Client | WPF 普通客户端入口、Seed IP/Network/Secret 配置、DPAPI secret、连接/断开/重连/状态；诊断已格式化物理网络/Overlay/Peer/Gateway/路由/错误摘要；UI 只控制 Host Service，不拥有路由 | 实机安装、UAC、开关网关、休眠与升级体验验收 |
| Linux Client | `client-control.sh` install/status/diagnostics/reconnect/uninstall；secret stdin；systemd | Linux 实机首次安装、切网、重启与发行版兼容验证 |
| 远程部署 | OpenSSH、SHA-256 manifest、私有 staging、profile/secret 事务替换、readiness、失败恢复；Linux 上传后恢复已知执行文件 execute bit | 多台真实 Windows Server/Linux 远程升级/回滚验收 |
| Manager | WPF 管理端；Seed、Gateway、Dedicated 配置与远程安装/卸载/诊断 | UI 完整易用性、批量部署/状态刷新与真实环境验收 |
| 发布 | Windows/Linux/Manager 统一 `version.txt`、`sha256.txt`、`artifact-manifest.json`；manifest 校验长度/哈希/路径/必需文件并拒绝 secret/core.toml；Windows 包强制携带匹配架构 `Packet.dll`/`wintun.dll`；提供手工 Release Candidate workflow 从真实 patched Core Release 编译并上传候选 artifact | 在目标 Windows/Linux 上安装候选包并完成真实 Core、驱动、服务、升级验收 |
| 实机验收工具 | Windows/Linux 证据采集脚本、Windows 角色/路由契约断言、`FOUR-NODE-VALIDATION.md` 四节点 Case A–H | 实际执行 Seed + Gateway + C1 + C2 两平台矩阵并归档证据 |
| CI | Windows Host/Deployment/Manager/Client 编译与全套测试；Ubuntu 原生 Host/Deployment 编译及 Host/Client/Diagnostics/Integration/Deployment 测试；PowerShell/Bash 语法检查；Windows/Linux 发布结构与 manifest smoke；EasyTier DHCP/Underlay Rust 测试 | 实际安装级 CI、需要管理员/root 的真实路由/NAT/DNS 集成环境 |

## 使用边界

Gateway 角色可由 `run`/服务启动，要求管理员/root、连接到同网络 Peer、独立物理接口和配置名称匹配的 `.1/16` TUN。状态在专用状态目录的 `gateway-status.json`；崩溃恢复记录为 `gateway-journal.json`。清理失败保留日志并停止重启，不能手工删除日志来跳过恢复。

Client 设置 `enableInternetGateway=true` 时，Host 会在 Core 启动前捕获物理默认出口，将物理 IPv4 作为运行时参数传给 `--underlay-source-ipv4`，等待 Client TUN 与 Core 实例身份一致后执行：保护 Seed/Peer endpoint → 安装 Probe `/32` → DNS/HTTPS Probe → 应用客户端 DNS → 安装 `0.0.0.0/1` 和 `128.0.0.0/1`。原物理 `/0` 保留。运行期间发现物理接口/IP/网关、Overlay 身份、路由所有权或健康状态变化，会先撤销自有路由/DNS，再由 Host 重启 Core 并重新捕获物理出口。启动/恢复会重置 `client-gateway-status.json`，避免 UI 把上一次运行遗留的 `GatewayActive` 误判为当前状态。

`UnderlayProtectionVerified` 不是用户可配置的豁免开关，只由 Host 根据本次 Core 启动实际绑定的物理 IPv4 生成。Core 保护模式仅支持单静态配置的 TCP/UDP Host 构建，强制关闭 UPnP；维护 DNS 使用受保护的物理 socket，避免系统 DNS stub 在默认路由切换后递归进入 TUN。保护模式下查询 RPC 保留，但运行时配置 patch 和 RPC 新建/覆盖实例被拒绝，配置变化必须由 Host 完成完整 rollback → restart。IPv6 默认路由不由 Host 接管。

`diagnostics <network.json> [state-directory]` 输出固定结构。提供 state directory 时会读取当前 `status.json`、Gateway 状态和最近脱敏错误；Windows 额外计算 `/0`、`/1` 的 `RouteMetric + InterfaceMetric`。若角色协调器已经进入 `GatewayReady`/`GatewayActive`，诊断会把旧的 Starting 状态规范化为当前活动态。Seed 明确视为 TUN-less，即使主机上其它软件恰好拥有 `10.10/16` 地址也不会作为 Seed Overlay 输出。诊断不会读取或输出 network secret，也不会持久化未知异常的原始 Message；未知异常只保存类型名称。

Windows 普通客户端使用提升权限的 WPF 程序配置 ProgramData 私有 profile/secret，再通过 SCM 启停 `EasyTierHost`。UI 不直接运行 Core、不写系统路由、不写 DNS；真正的 Underlay、Probe、Commit、Reconcile 和 rollback 仍在 Host 服务内。Internet Gateway 未及时激活时，Overlay 本身不应因此被 UI 强制断开，Host 保持物理默认出口并继续按自身故障逻辑处理。诊断 UI 已改为消费结构化合同，而不是直接显示原始 JSON。

Linux 普通客户端通过 `scripts/linux/client-control.sh` 提供同类入口。远程 Linux 部署考虑发布包可能从 Windows 产生/上传、Unix execute bit 不可依赖，因此事务安装阶段会只对 `easytier-host`、`easytier-core`、`easytier-cli` 及自有 Linux shell 脚本恢复执行权限，不递归放宽整个安装目录。

发布脚本在业务文件完成后统一生成 `version.txt`、`sha256.txt` 和 `artifact-manifest.json`；manifest 最后生成并覆盖 SHA-256/长度信息。Windows 打包根据 RID 从 EasyTier `third_party/x86_64` 或 `third_party/arm64` 复制 `Packet.dll` 与 `wintun.dll`，package verifier 将两者视为 Windows 节点包必需文件。日常 CI 使用 sentinel Core/CLI 验证发布管线与完整性算法；`.github/workflows/release-candidate.yml` 则手工从当前 patched EasyTier 源码执行测试、Release 编译 Core/CLI、生成并校验 Windows/Linux 候选包。候选 artifact 仍必须经过目标系统实机安装验收。

目前自动测试仍不能替代实机验收：平台路由/NAT/DNS 写入的大部分测试使用替身命令执行器，真实网络测试只覆盖本机回环 socket。正式发布前按 [四节点实机验收方案](FOUR-NODE-VALIDATION.md) 完成 Seed + Gateway + 两个 Client 的独立多机 Case、Windows/Linux 抓包、P2P/Relay 路径、断电 journal 恢复、物理网卡切换、休眠恢复以及代理/VPN 共存验证。
