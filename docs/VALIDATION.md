# 验证记录

环境：Windows x64，.NET SDK 10.0.301（Host 目标 net8.0），Rust stable 1.97.1。

- `dotnet build EasyTierHost.sln`：通过，0 warning / 0 error。
- `dotnet run --project tests/EasyTierHost.UnitTests`：17/17 通过。
- `tests/Test-Configuration.ps1`：四角色 CLI 配置生成及 Python `tomllib` 独立解析通过。
- `cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features`：4/4 通过。原仓库在关闭默认 features 时有 3 个 unused 警告。
- `diagnostics config/templates/client.json`：真实 Windows 网卡、路由与 DNS 读取成功；只读，未修改网络。
- `dotnet publish src/EasyTierHost.Service -c Release --no-self-contained`：通过。
- `cargo +stable build -p easytier --no-default-features --features tun --bin easytier-core --bin easytier-cli`：通过，生成 Windows x64 Core / CLI。
- `scripts/publish/Publish.ps1`：生成 `publish/overlay-win-x64` 自包含预览包；Host help/validate、Core/CLI version 均运行成功，210 项包文件 SHA256 校验通过。Core 当前为 debug 构建，不能用于性能验收。

Windows Rust 测试依赖 `Packet.dll`。使用仓库 `easytier/third_party/x86_64` 加入进程 PATH；没有安装系统抓包驱动。Core 构建依赖 7-Zip，使用本机已存在安装。

## 尚未验证

路由写入测试使用内存适配器，不等价于 OS 网络栈集成。Linux 平台 API 没有实机执行。DNS 测试使用回环 UDP/TCP 上游，未验证公网递归解析或多客户端负载。没有执行方案中的四节点网络 Case A–G，也没有实际安装系统服务、NAT、远程主机或 GUI。

测试不是全量 EasyTier 回归：Rust 仅执行新增 DHCP 模块，其他 673 项测试被过滤。完整可用性以多节点验收为准。
