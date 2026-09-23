# 配置说明

MiBrightness 的主配置位于：

`%LOCALAPPDATA%\MiBrightness\config.json`

## 字段

### Topic

巴法 TCP 设备主题。

如果希望作为灯设备同步给米家，主题应按巴法规则使用 `002` 后缀，例如：

`pcbrightness002`

### ServerHost

巴法服务器域名。默认 `bemfa.com`。

### ServerIpFallback

当 DoH 查询失败时使用的备用 IPv4。

### Port

巴法 TCP 端口，默认 `8344`。

### InterfaceAlias

巴法 TCP 长连接绑定的 Windows 网络适配器名称。

常见值：

- `WLAN`
- `Ethernet`

如果使用 Clash/Mihomo TUN，请选择真实物理网卡，不要选择 Meta/TUN/VPN 虚拟接口。

### HeartbeatSeconds

心跳间隔。默认 30 秒。

不建议设置超过 60 秒。

### DefaultBrightness

收到 `on` 且没有保存的上次亮度时使用的亮度值。

## 巴法私钥

私钥不存储在 `config.json`。

GUI 会把它加密到：

`%LOCALAPPDATA%\MiBrightness\secret.dat`

加密范围为当前 Windows 用户。
