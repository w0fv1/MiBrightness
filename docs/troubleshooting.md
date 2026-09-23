# 故障排查

## 米家 / 小爱提示设备不在线

先确认托盘显示“在线”。

如果离线：

1. 打开 MiBrightness 配置。
2. 检查 Topic 是否与巴法完全一致。
3. 检查私钥。
4. 检查“直连网卡”是否选择真实 WLAN/Ethernet。
5. 打开日志查看最近错误。

## Clash / Mihomo 开启后离线

如果 `Resolve-DnsName bemfa.com` 返回 `198.18.x.x`，通常是 Fake-IP。

MiBrightness 会绕过这个地址，使用 DoH 取得真实 IPv4，并把 TCP 连接绑定到你选择的物理网卡。

不要把 `InterfaceAlias` 设置为 Meta、TUN、VPN 等虚拟网卡。

## 小爱回复“好的”，但亮度没变化

先在巴法网页控制台手工发送：

`on#37`

如果电脑能变成 37%，说明“巴法 → 电脑”链路正常，问题通常在“米家/小爱 → 巴法”的设备映射。

避免把巴法设备昵称取成“电脑”等可能和已有原生米家电脑设备冲突的名称。可以使用：

`电脑屏幕`

或：

`电脑屏幕灯`

## 外接显示器不变化

当前版本使用 WMI，只控制支持 WMI 亮度接口的内置屏幕。

外接显示器通常需要 DDC/CI，当前版本不支持。

## 查看日志

`%LOCALAPPDATA%\MiBrightness\logs\MiBrightness.log`

日志不会记录明文私钥，巴法 UID 也会被打码。
