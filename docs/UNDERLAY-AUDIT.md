# Underlay 防递归审计（2026-09-18）

当前 Host 仍不启用 Internet 默认路由。仅设置 `bind_device=true` 不构成保护证明。

## 已实现的实验性保护

Core 参数 `--underlay-source-ipv4` 安装不可变的进程级 IPv4 策略，仅允许一个静态配置，拒绝包含 QUIC/WireGuard/WebSocket/FakeTCP 的构建。未指定源地址的维护 socket 改为物理 IPv4，并使用 Windows 接口绑定或 Linux SO_BINDTODEVICE；请求其他 IPv4 源时失败。物理地址改变必须重启 Core。

显式标记的维护路径包括 TCP/UDP 隧道监听和连接、UDP 打洞与地址发现、STUN UDP/TCP、网络地址探测，以及 Hickory TCP/UDP DNS。保护模式禁用系统 DNS 查询，使用原有公共 DNS 上游通过受保护 socket 查询；STUN 主机解析也统一进入该入口。UPnP 依赖库未受控，因此保护模式强制关闭 UPnP。

环回和 IPv6 保留原路由语义；维护 IPv6 socket 强制 v6-only，防止 IPv4-mapped 绕过绑定。Host 只计划接管 IPv4。业务 IP proxy、Exit Node 转发、虚拟网卡广播没有全局替换；公共 bind 默认仍不启用 Underlay 策略。

## 尚未满足的放行条件

- Host 启动前捕获物理接口，将策略传给 Core，并验证启动能力和实例身份。
- 物理地址/接口变化时先撤销路由/DNS，再停止并重建 Core。
- 约束运行中的 RPC 新建/修改实例，避免重新启用未审计的 UPnP 或其他路径。
- 检查所有实际启用维护出口，Windows/Linux 抓包验证 Seed、Relay、STUN、新打洞 endpoint 和 DNS 均走物理接口。
- 多节点验证默认路由前后业务、P2P、RTT、丢包和网卡切换恢复。

因此不能仅凭单元测试或已知 Peer 的 /32 保护路由释放默认路由开关。
