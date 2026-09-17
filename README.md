# EasyTierHost

基于所提供 EasyTier 2.6.4 源码实现的 **0.2 开发预览版**。已提供受限 DHCP、角色配置、Host 进程守护、网关 NAT/forwarding/DNS 生命周期、Underlay 防递归补丁，以及客户端 Internet 路由的实验性协调流程。**完整自治网关、远程部署、Windows GUI 和多节点实机验收尚未完成**，具体边界见 [实施状态](docs/IMPLEMENTATION-STATUS.md)。

## 构建与测试

需要 .NET 8+ SDK、Rust stable、Windows C++ 链接工具；Windows 构建 Core 时需要 7-Zip 在 PATH 中。依赖恢复需要联网。

```powershell
dotnet build EasyTierHost.sln
dotnet run --project tests/EasyTierHost.UnitTests
cd EasyTier-2.6.4
$env:PATH = "$PWD/easytier/third_party/x86_64;C:/Program Files/7-Zip;" + $env:PATH
cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features --features tun
cargo +stable test -p easytier --lib underlay_ --no-default-features --features tun
cargo +stable build -p easytier --no-default-features --features tun --bin easytier-core --bin easytier-cli
```

开发分支包含根目录 `.github/workflows/host-ci.yml`，在 Windows runner 上自动执行 Host solution build 和 UnitTests。`tun` 构建支持此预览版使用的 TCP/UDP Overlay；完整传输集合可用原仓库默认 features 构建。Windows 运行需要仓库 `easytier/third_party/x86_64` 中的 Packet.dll 在 DLL 搜索路径，TUN 需要 wintun.dll；打包脚本会复制已有原生文件，不执行系统驱动安装。

## 使用

将 `config/templates` 下相应角色配置复制到自己的专用配置目录。修改示例 Seed `192.0.2.10` 为实际地址；给所有节点配置相同的 networkName 和 network secret。CorePath/CliPath 推荐填写新编译二进制的绝对路径，不能使用没有 DHCP/Underlay 补丁的官方 Core。

```powershell
dotnet run --project src/EasyTierHost.Service -- set-secret C:/your-private-config/network.secret
dotnet run --project src/EasyTierHost.Service -- validate C:/your-private-config/network.json
dotnet run --project src/EasyTierHost.Service -- run C:/your-private-config/network.json C:/your-private-state
```

`set-secret` 交互输入不回显，文件必须不存在。Windows 使用 DPAPI 与 ACL；Linux 文件必须为 0600。`secretFile` 相对路径以 profile 所在目录为基准。不要把密码放到命令行参数中。

Seed 不创建 TUN；Dedicated 仅允许尾号 2–10；Client 使用 `10.10.0.11–10.10.255.254/16`。Host 启动前检查 Core 的 DHCP 参数能力；当 Client 启用 Internet Gateway 时还会检查 `--underlay-source-ipv4` 能力。首次分配仍沿用 EasyTier 的“至少已连接一个 Peer”条件。实际创建 TUN 和修改路由/DNS 需管理员/root 权限。Ctrl+C 或 Linux SIGTERM 会先撤销 Host 自有网络状态，再停止 Core，并删除临时明文 Core 配置。

```powershell
dotnet run --project src/EasyTierHost.Service -- status C:/your-private-config/network.json
dotnet run --project src/EasyTierHost.Service -- diagnostics C:/your-private-config/network.json
```

`configure <profile> <output.toml>` 可单独生成私有 Core 配置；该文件含明文 network secret。`dns <gateway-profile>` 是独立 DNS 转发命令，要求本机已具有 `10.10.0.1`，不会配置 NAT 或系统 DNS。

Gateway 角色可由 Host 启动，配置 NAT、forwarding 和 UDP/TCP DNS，并在退出或失败时撤销自有状态。Windows 使用 WinNAT，Linux 要求 nftables 且现有防火墙允许转发与 TUN DNS。运行状态见专用状态目录的 `gateway-status.json`。

Client 的 `enableInternetGateway` 默认仍为 `false`。显式设为 `true` 时进入**实验性集成模式**：Host 先捕获物理默认出口，把物理 IPv4 传给 Core 的 `--underlay-source-ipv4`，等待 Client TUN 与 Core 实例一致后保护 Seed/Peer endpoint，执行 `.1` DNS/公网 Probe，再提交两条 IPv4 `/1` 默认路由到 `10.10.0.1`；原物理 `/0` 保留。运行期间会刷新动态 endpoint `/32`，监测物理接口、Overlay 身份、路由所有权与 Internet 健康；异常时按 journal 回滚 DNS/路由并重启 Core。该模式仍需 Windows/Linux 多机抓包、切网、休眠和断电恢复验收，当前不要视为生产放行。详见 [Underlay 审计](docs/UNDERLAY-AUDIT.md)。

## 打包

`scripts/publish/Publish.ps1 -CoreDirectory <编译输出目录>` 生成自包含 Host 加 Core/CLI 的 Overlay 预览包、文件校验和及 manifest。Linux 发布使用 `-Runtime linux-x64`，必须提供 Linux Core/CLI；此脚本不替代跨平台编译。Linux systemd unit 模板位于 `scripts/linux`，不是自动安装程序。当前没有 Windows 服务安装程序。

在 Windows 预览包目录中，可用 `./easytier-host.exe` 替代上文的 `dotnet run --project src/EasyTierHost.Service --` 前缀；Linux 使用 `./easytier-host`。先复制并编辑 templates 中的配置，再设置 secret；不要直接连接文档示例 IP。

网关前置条件、启动与恢复操作见 [Gateway 运行说明](docs/GATEWAY-OPERATIONS.md)。0.2 打包默认输出到 `publish/preview-0.2-win-x64`，保留旧版发布目录。
