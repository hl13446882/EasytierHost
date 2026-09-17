# Underlay 防递归审计（2026-09-18）

客户端 Internet 接管已进入实验性联调阶段。仅设置 `bind_device=true` 仍不构成保护证明；Host 现在会在启动 Core 前捕获物理出口，并把物理 IPv4 通过 `--underlay-source-ipv4` 传给 Core，再由 ClientInternetCoordinator 完成 Probe、默认路由提交、动态 endpoint 保护与失败回滚。

## 已实现的保护

Core 参数 `--underlay-source-ipv4` 安装不可变的进程级 IPv4 策略，仅允许一个静态配置，拒绝包含 QUIC/WireGuard/WebSocket/FakeTCP 的构建。未指定源地址的维护 socket 改为物理 IPv4，并使用 Windows 接口绑定或 Linux SO_BINDTODEVICE；请求其他 IPv4 源时失败。物理地址改变必须重启 Core。

显式标记的维护路径包括 TCP/UDP 隧道监听和连接、UDP 打洞与地址发现、STUN UDP/TCP、网络地址探测，以及 Hickory TCP/UDP DNS。保护模式禁用系统 DNS 查询，使用原有公共 DNS 上游通过受保护 socket 查询；STUN 主机解析也统一进入该入口。UPnP 依赖库未受控，因此保护模式强制关闭 UPnP。

Host 在 `enableInternetGateway=true` 的 Client 上执行以下顺序：

```text
Capture physical /0 + physical IPv4
        ↓
start Core --underlay-source-ipv4 <physical IPv4>
        ↓
verify Core instance + Client TUN + Peer
        ↓
protect Seed/current Peer endpoints with physical /32
        ↓
install 1.1.1.1/32 + 8.8.8.8/32 through 10.10.0.1
        ↓
DNS/HTTPS probe using Client overlay source
        ↓
apply client DNS
        ↓
commit 0.0.0.0/1 + 128.0.0.0/1 through 10.10.0.1
        ↓
remove probe /32
        ↓
periodic endpoint refresh / physical-route reconcile / health probe
```

原有物理 `0.0.0.0/0` 不删除。新发现的 Peer endpoint 先增加物理 `/32`，过期 endpoint 由 120 秒 grace period 后移除。任何所有权冲突、物理接口/IP/网关变化、Overlay 身份变化或 Probe 失败都会触发回滚；恢复日志仍优先于重新接管网络。

环回和 IPv6 保留原路由语义；维护 IPv6 socket 强制 v6-only，防止 IPv4-mapped 绕过绑定。Host 只接管 IPv4。业务 IP proxy、Exit Node 转发、虚拟网卡广播没有全局替换；公共 bind 默认仍不启用 Underlay 策略。

## 尚未满足的正式放行条件

- 约束运行中的 RPC 新建/修改实例，避免重新启用未审计的 UPnP 或新 transport。
- Windows/Linux 抓包确认 Seed、Relay、STUN、新打洞 endpoint 和维护 DNS 在默认路由提交前后都使用物理接口。
- Seed + Gateway + 两个 Client 多节点验证 P2P、RTT、丢包、DNS、Internet 出口、重复地址与 endpoint 漂移。
- Wi-Fi/有线切换、DHCP 换地址、休眠恢复、Gateway/Seed 重启和异常断电后的自动恢复。
- 与现有 VPN/NRPT/resolved/firewall 策略共存验证。

因此当前 `enableInternetGateway=true` 仅用于开发与集成测试，不应视为生产放行。正式版本必须以上述多机与抓包验收结果为依据，而不是仅凭单元测试或已知 Peer 的 `/32` 保护路由。
