namespace MiBrightness;

internal sealed class AppConfig
{
    public string Topic { get; set; } = "pcbrightness002";
    public string ServerHost { get; set; } = "bemfa.com";
    public string ServerIpFallback { get; set; } = "119.91.109.180";
    public int Port { get; set; } = 8344;
    public string InterfaceAlias { get; set; } = "WLAN";
    public int HeartbeatSeconds { get; set; } = 30;
    public int DefaultBrightness { get; set; } = 80;
}

internal sealed record BridgeState(
    bool Online,
    string Status,
    int Brightness,
    string Endpoint,
    string LastError);
