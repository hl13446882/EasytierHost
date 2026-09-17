# Underlay 防递归审计

结论：已有 `bind_device=true` 不能证明所有 Underlay 流量都受保护。当前 Host 不启用 Internet 默认路由。

## 已有公共入口

`easytier/src/tunnel/common.rs` 的 `bind` / `setup_socket2_ext` 支持 Auto、Disabled、Custom。Windows 委托 `arch/windows.rs::setup_socket_for_win` 设置接口；Linux 使用 `bind_device`。绑定 `0.0.0.0` 等未指定地址时，Linux 会提前返回，不能假设已绑定设备。

## 需要逐条处理的生产路径

- `connector/udp_hole_punch/common.rs`、`cone.rs`、`sym_to_cone.rs` 中存在直接 `UdpSocket::bind`。
- `connector/direct.rs`、`connector/mod.rs` 存在未指定地址的 UDP 创建。
- `common/stun.rs` 有多个 UDP 入口及直接 socket2 创建；需要同时覆盖 STUN 与 TCP NAT 探测。
- `common/network.rs` 的 IPv4/IPv6 探测 socket 需要单独区分用途。
- `tunnel/tcp.rs`、`tunnel/udp.rs` 存在绕过公共绑定入口的连接路径，需要区分 RPC、本地桥接和公网隧道。
- `tunnel/websocket.rs` 和 `tunnel/fake_tcp/mod.rs` 存在直接 TCP socket；后者还有原始流量路径。
- UPnP 和依赖库内创建 socket 的情况需要按真实使用路径确认。

不能全局替换所有 `TcpStream/UdpSocket`：IP proxy、Exit Node 转发、DNS 和 Overlay 业务 socket 的路由语义不同。策略必须携带用途和所属网络实例，并在物理接口变化时重建相关连接。仅定时增加已知 peer `/32` 无法保护首次打洞的新目标。

## 保护完成的验证条件

抓包确认 Seed、Relay、STUN、NAT Probe、TCP/UDP 打洞及新出现 endpoint 的出站流量走物理接口；Overlay 业务仍经过 TUN。覆盖 TCP/UDP/QUIC/WebSocket/WireGuard 及实际启用的其他传输。比较 Gateway 开关前后 P2P、RTT 和丢包；切换物理接口后重复验证。
