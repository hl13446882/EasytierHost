# 验证记录（2026-09-18）

环境：Windows x64，.NET SDK 10.0.301（Host 目标 net8.0），Rust stable 1.97.1。

## 本轮源码验证

- `dotnet build EasyTierHost.sln --nologo`：通过，0 warning / 0 error。
- `dotnet run --project tests/EasyTierHost.UnitTests`：38/38 通过。包含路由事务、NAT/forwarding 启停失败恢复、DNS 所有权、Core RPC 解析及真实回环 UDP/TCP DNS。
- `tests/Test-Configuration.ps1`：四角色 CLI 生成配置，Python tomllib 独立解析通过。
- `cargo +stable test -p easytier --lib underlay_ --no-default-features --features tun`：6/6 通过。验证源地址策略、拒绝其他接口源、环回/IPv6 语义、未启用策略时 DNS TCP/UDP 兼容性。
- `cargo +stable test -p easytier --lib common::config::dhcp_range --no-default-features --features tun`：4/4 通过。

Rust 精简 feature 构建仍有原仓库的 3 个 unused 警告。以上仅为针对性测试，并非全量 EasyTier 回归。Windows Rust 测试通过 PATH 使用已有 Packet.dll；未安装系统驱动。

## 尚未验证

平台路由/NAT/DNS 配置命令使用替身执行器，未在开发机写入系统网络配置。Linux API 没有实机执行。真实 DNS 测试使用回环上游，未验证公网递归、转发吞吐或多客户端负载。Underlay 策略尚未完成实际物理网卡绑定抓包与切换恢复验收。

未执行方案的 Seed + Gateway + 两个 Client Case A–G；未实际安装系统服务、远程主机或 GUI。客户端默认路由功能仍被禁用。

## 0.2 发布验证

- TCP/UDP + TUN 的 Windows Core/CLI 编译通过；当前为 debug Core，不用于性能验收。
- 四角色生成的 TOML 均通过新 Core 的 `--check-config` 原生校验。
- `scripts/publish/Publish.ps1` 生成 `publish/preview-0.2-win-x64`，自包含 Release Host + debug Core/CLI。
- `tests/Test-Package.ps1`：Host help、四角色 validate、Core DHCP/Underlay 参数、CLI version 通过，211 个 manifest 文件 SHA256 全部匹配。
