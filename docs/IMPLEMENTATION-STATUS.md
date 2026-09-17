# 实施状态（2026-09-18，0.2）

已实现 Overlay 基础、网关生命周期与平台适配组件。尚未达到完整开发方案的交付标准；客户端默认路由接管仍被 Host 明确禁用。

| 模块 | 已实现 | 尚需完成 |
|---|---|---|
| DHCP / 角色 | /16 地址池、最低空洞回收、四角色配置、保留地址校验 | 多节点冲突回归、安装前专用地址查询 |
| Host | 单实例锁、进程守护、私有配置、机器级密钥保护、RPC 实例核对 | Windows SCM、安装卸载 |
| Gateway | .1 TUN 就绪检测、重复网关检查、forwarding/NAT/DNS 顺序启动、健康监测、日志恢复与失败撤销 | Windows/Linux 实机集成验收 |
| Windows NAT | 独占 WinNAT 检查、物理出口地址约束、按 GUID 恢复 forwarding、限定 TUN/子网/端口的 DNS 防火墙规则 | 实际 WinNAT 和防火墙写入验证 |
| Linux NAT | 独立 nftables 表、所有权标记、限定 10.10/16 与出口的 masquerade、sysctl 恢复 | Linux 实机；现有防火墙需允许转发和 TUN DNS，未提供 iptables 回退 |
| DNS 转发 | UDP/TCP、并发限制、上游回退、随机上游事务 ID、问题字段校验、真实 socket 健康探测 | 协议模糊测试与负载验收 |
| 客户端路由 | 写前日志、探测后提交两条 /1、原 /0 保留、撤销和恢复、接口变化检查 | 启动协调器、动态 endpoint 与物理网络重绑定接线 |
| 客户端 DNS / Probe | Windows 自有 NRPT 规则、Linux resolved 指定链路配置及恢复、源地址约束的 DNS/HTTPS 探测 | 实机验证；默认路由功能尚未接通 |
| Underlay | TCP/UDP、STUN、打洞、发现 DNS 的显式物理 IPv4 绑定补丁 | Host 启动接线、RPC 配置变更约束、网卡切换、多节点抓包验收 |
| GUI / 部署 | 尚未实现 | 两个 WPF 界面、SSH 部署、完整双平台安装包 |

## 使用边界

Gateway 角色现可由 `run` 启动，要求管理员/root、连接到同网络 Peer、独立物理接口和配置名称匹配的 .1/16 TUN。状态在专用状态目录的 `gateway-status.json`；崩溃恢复记录为 `gateway-journal.json`。清理失败保留日志并停止重启，不能手工删除日志来跳过恢复。

`enableInternetGateway=true` 仍报错 ETH301。`UnderlayProtectionVerified` 不是可由用户配置的豁免开关。新 Core 参数 `--underlay-source-ipv4` 是实验性基础组件，仅支持单静态配置的 TCP/UDP 构建；Host 尚未传入该参数。启用时关闭 UPnP，并使维护 DNS 使用原 Core 的公共上游经物理接口查询，避免系统 DNS stub 递归。IPv6 默认路由不由 Host 接管。

平台写入测试使用替身命令执行器；真实网络测试只涉及本机回环 socket。没有修改开发机路由、DNS、NAT 或防火墙来代替多机验收。旧发布目录可能仍为 0.1，须以包内 version.txt 和 manifest 为准。
