# MiBrightness

用小爱同学 / 米家通过巴法云控制 Windows 笔记本内屏亮度。

MiBrightness 会把巴法云灯类设备（主题后缀 `002`）收到的 `on#30`、`on`、`off` 等指令映射到 Windows WMI 亮度接口。程序常驻系统托盘，并提供 GUI 配置。

## 功能

- 小爱语音调节 Windows 笔记本内屏亮度
- 巴法云 TCP 长连接，自动心跳和断线重连
- 托盘状态：在线/离线、当前亮度
- GUI 配置：Topic、直连网卡、心跳、默认亮度、巴法私钥
- 一键测试亮度
- 登录 Windows 后自动启动
- Windows DPAPI 加密保存巴法私钥
- Clash / Mihomo TUN + Fake-IP 场景下可绑定真实物理网卡直连巴法
- 自包含 win-x64 单文件，不要求用户预装 .NET

## 安装

从 [Releases](../../releases) 下载：

- `MiBrightnessSetup.exe`：推荐，双击安装
- `MiBrightnessPortable-win-x64.zip`：便携包

安装后：

1. 从开始菜单打开 **MiBrightness 配置**，或双击托盘图标。
2. 填写巴法 Topic，例如 `pcbrightness002`。
3. 选择实际联网的物理网卡，例如 `WLAN` 或 `Ethernet`。
4. 点“设置 / 更换巴法私钥”，输入巴法云私钥。
5. 保存并重新连接。
6. 在巴法云中创建后缀为 `002` 的 TCP 设备，并同步到米家。
7. 给设备取一个不会和现有米家设备冲突的昵称，例如“电脑屏幕”。

之后可以尝试：

> 小爱同学，把电脑屏幕亮度调到 30%。

## 托盘和配置

托盘菜单提供：

- 当前在线状态
- 当前内屏亮度
- 配置
- 重新连接
- 打开 `config.json`
- 打开日志目录
- 开机自启
- 退出

即使找不到托盘图标，也可以：

```powershell
MiBrightness.exe --config
```

或从开始菜单打开 **MiBrightness 配置**。

## 文件位置

安装版默认位置：

| 内容 | 路径 |
|---|---|
| 程序 | `%LOCALAPPDATA%\Programs\MiBrightness\MiBrightness.exe` |
| 配置 | `%LOCALAPPDATA%\MiBrightness\config.json` |
| 加密私钥 | `%LOCALAPPDATA%\MiBrightness\secret.dat` |
| 日志 | `%LOCALAPPDATA%\MiBrightness\logs\MiBrightness.log` |
| 状态缓存 | `%LOCALAPPDATA%\MiBrightness\status.json` |
| 上次亮度 | `%LOCALAPPDATA%\MiBrightness\last-brightness.txt` |

`secret.dat` 使用 Windows DPAPI 的 CurrentUser 范围加密，不能直接复制到其他 Windows 用户账户使用。

## 配置示例

```json
{
  "Topic": "pcbrightness002",
  "ServerHost": "bemfa.com",
  "ServerIpFallback": "119.91.109.180",
  "Port": 8344,
  "InterfaceAlias": "WLAN",
  "HeartbeatSeconds": 30,
  "DefaultBrightness": 80
}
```

一般只需要修改 `Topic` 和 `InterfaceAlias`。

## Clash / TUN / Fake-IP

如果使用 Clash Verge、Mihomo 等，并开启 TUN + Fake-IP，`bemfa.com` 可能被解析成 `198.18.0.0/16` 的虚拟地址。普通 TCP 8344 长连接可能因此建立后立即断开，巴法云会显示设备离线。

MiBrightness 不要求关闭代理，而是将巴法 TCP 连接绑定到指定的真实物理网卡，并通过 DoH 获取巴法真实 IPv4；失败时使用配置中的备用 IP。

因此请在 GUI 中把“直连网卡”选成实际联网的 `WLAN` / `Ethernet`，不要选 TUN、Meta、VPN 等虚拟网卡。

## 支持的指令

当前主要兼容巴法灯类协议：

- `on#30` → 设置亮度 30%
- `on` → 恢复上次非零亮度；无历史记录时使用 `DefaultBrightness`
- `off` → 设置亮度 0%

## 硬件限制

程序使用 Windows WMI 的 `WmiMonitorBrightnessMethods`，主要用于笔记本内置屏。

外接显示器通常需要 DDC/CI，不在当前版本支持范围内。

## 构建

需要 .NET 10 SDK：

```powershell
dotnet publish .\src\MiBrightness\MiBrightness.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

也可以直接运行：

```powershell
.\scripts\build-release.ps1
```

生成的发布资产在 `artifacts\`。

## 隐私与安全

- 私钥不会写进 `config.json`。
- 私钥使用 Windows DPAPI 加密。
- 日志中的巴法 UID 会被替换为 `[uid]`。
- 仓库和 Release 不包含用户私钥、手机号、本机配置或运行日志。

## 卸载

安装目录中运行：

```powershell
powershell.exe -ExecutionPolicy Bypass -File Uninstall.ps1
```

默认保留配置与私钥。

完全删除用户数据：

```powershell
powershell.exe -ExecutionPolicy Bypass -File Uninstall.ps1 -RemoveData
```

## License

MIT

