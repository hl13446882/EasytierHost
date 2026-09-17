# Gateway 开发预览运行说明

本组件会修改操作系统 forwarding、NAT，以及 Windows 上的 DNS 入站防火墙规则。请在用于集成测试的独立 Windows/Linux 主机上运行。尚未完成多节点验收，客户端默认路由接管保持禁用。

## 前置条件

- 已部署同版本补丁 Core/CLI；Seed 可连接，所有节点的 networkName 与 secret 一致。
- 以管理员/root 运行；物理接口拥有 IPv4 默认网关，TUN 名称与 profile 的 deviceName 一致。
- Windows 必须有 NetNat 模块，且不能已有其他 WinNAT 网络；Host 会拒绝接管现有 NAT。
- Linux 必须有 ip、sysctl、nft；防火墙策略需允许 TUN 与物理接口间转发及来自 10.10.0.0/16 的 TCP/UDP 53。Host 不清空已有 ruleset，也不会覆盖已有 drop 策略。
- DNS 上游优先来自 dnsUpstreams；为空时读取物理接口 DNS。Linux 自动读取依赖 systemd-resolved/busctl；其他 resolver 请显式填写物理 DNS。只有 allowPublicDnsFallback=true 才追加 1.1.1.1 和 8.8.8.8。

## 启动

复制 templates/gateway.json，替换 seedPhysicalIp、corePath、cliPath 与 secretFile。使用 set-secret 保存密钥，然后执行：

```powershell
./easytier-host.exe validate C:/EasyTierHost/private/gateway.json
./easytier-host.exe run C:/EasyTierHost/private/gateway.json C:/EasyTierHost/private/state
```

Linux 使用对应路径和 `./easytier-host`。每个实例使用独立、持久的状态目录。不要把状态放在临时目录。

Host 等待 .1/16 TUN、核对 Core 实例和 Peer，依次启用 forwarding、NAT、DNS，健康检查成功后写入 gateway-status.json 的 GatewayReady。就绪仅证明平台配置和本机 DNS 探测通过，仍需外部客户端验证真实转发。重复 .1、物理网络改变或健康检查失败会撤销自有配置并按限制重启 Core。

## 退出与恢复

Ctrl+C / SIGTERM 会先清理网关配置，再停止 Core。异常断电后，以同一状态目录重新启动，Host 在启动新 Core 前恢复 gateway-journal.json 记录的改动。Windows 按接口 GUID 恢复 forwarding，Linux 按快照恢复原 forwarding 值；只删除带本次所有权标识的 NAT/防火墙对象。

清理失败会保留日志并报错。保留日志与状态文件，检查权限和平台命令；解决问题后用同一目录重试。不要删除日志或改用新状态目录来绕过清理。没有日志时，Host 无法安全判断遗留配置的归属。

## 后续验收

需要在 Seed + Gateway + 两个 Client 的独立环境验证 P2P、.1 DNS、NAT 外网访问、重复地址、网卡切换与断电恢复。默认路由自动切换尚未接通，不能把本预览版视作完整客户端上网交付。
