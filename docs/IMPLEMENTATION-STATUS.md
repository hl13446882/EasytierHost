# 实施状态（2026-09-17）

本次产物是可编译的 Overlay 基础实现与路由事务组件，**尚未达到原方案第 69 节的完整交付标准**。不可作为完整自治网关发布。

| 模块 | 实现与验证范围 | 待完成 |
|---|---|---|
| DHCP | 显式子网、地址池、最低空洞回收、按 IP 判断冲突、旧配置兼容；Rust 单元测试 | 多节点 TUN 环境冲突回归 |
| 角色配置 | Seed / Gateway / Dedicated / Client 校验和 TOML 生成 | 专用地址安装前在线冲突查询、网关唯一性检测 |
| 进程管理 | 单入口启动/停止、输出排空、异常重启、2 分钟失败阈值、实例锁 | Windows SCM 生命周期集成、结构化健康监测 |
| Secret | Windows 机器级 DPAPI + 私有 ACL；Linux 0600；临时 Core 配置私有化 | 远程凭据和密钥轮换 |
| 路由事务 | 独占控制器、写前日志、探测/提交/回滚、重启恢复、原路由保留、并发串行、网卡改变回滚 | 生产 DNS 适配器、生产探测器、动态 endpoint/RPC 接入、OS 实机集成 |
| 系统路由适配 | Windows 结构化 PowerShell JSON 回退、Linux ip JSON | Windows IP Helper 原生实现；Linux 多路由表/虚拟化物理接口选择；实机写入验证 |
| DNS 转发 | UDP/TCP、128 并发上限、超时、上游回退、禁止自指；真实回环上游 socket 测试 | 从网关生命周期统一启动、完整响应问题字段校验、协议模糊测试 |
| Gateway | 配置生成与 DNS 独立命令 | NAT、forwarding、健康检查、失败撤销未实现，Host 拒绝启动 Gateway 角色 |
| GUI / 部署 | 尚未实现 | WPF Manager、Windows Client、SSH 远程安装/回滚/卸载、Windows 服务 |
| 发布 | Windows x64 自包含预览包（带 TUN 的 debug Core/CLI）、manifest / SHA256、Linux unit 模板 | 双平台完整安装包、Linux 构建、Release Core、多节点验收 |

## 与方案的明确差异

1. 第一版代码使用 `[dhcp_range]` TOML 表，字段为 `network/start/end`。CLI 为 `--dhcp-network/--dhcp-start/--dhcp-end`，三者必须一起提供。显式子网解决无 TUN Seed 无法提供地址前缀的问题。
2. 路由提交采用两条 `/1` 路由，不删除或改写物理 `/0`。这样无需通过降低物理网卡 metric 获胜；快照仍记录 Windows `RouteMetric + InterfaceMetric` 所需数据。原本的 connected routes 保留。探测成功后才提交，提交后删除探测 `/32`。
3. 对已有同目标路由采取拒绝策略，不接管已有路由。状态日志记录 exact identity；删除前再次核对。
4. Host 服务目前只允许 Seed、Dedicated、Client 的 Overlay-only 模式。`enableInternetGateway=true` 明确报 ETH301；不存在自动绕过开关。库中的 `UnderlayProtectionVerified` 是将来由经过审计的启动协调器提供的前置条件，不能把用户手动布尔值视为保护证明。
5. Windows RPC portal 是本地源码的进程级 CLI 参数，不是 TOML 的配置字段，因此由 ProcessManager 传入。
6. 不修改机器默认路由、DNS、NAT、系统服务来冒充完成验收。路由故障测试使用内存适配器；DNS 上游测试只使用本机回环。

## 后续实施顺序

先完成 `UNDERLAY-AUDIT.md` 中所有生产 socket 出口的防递归保护和物理网卡重绑定，再接通平台 DNS/Probe 和启动协调器；然后实现 NAT/forwarding 与 Gateway 生命周期、SCM/systemd 安装卸载、SSH 部署和两个 WPF 界面。最后在 Windows/Linux 的 Seed + Gateway + 两个 Client 环境执行原方案 Case A–G，记录延迟、丢包及 P2P 状态。
