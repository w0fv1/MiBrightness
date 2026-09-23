using System.IO;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiBrightness;

internal static class BridgeRuntime
{
    internal static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiBrightness");
    internal static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
    internal static readonly string SecretPath = Path.Combine(DataDir, "secret.dat");
    internal static readonly string LegacySecretPath = Path.Combine(DataDir, "bemfa.uid.dpapi");
    internal static readonly string LastBrightnessPath = Path.Combine(DataDir, "last-brightness.txt");
    internal static readonly string LogDir = Path.Combine(DataDir, "logs");
    internal static readonly string LogPath = Path.Combine(LogDir, "MiBrightness.log");
    internal static readonly string RestartFlagPath = Path.Combine(DataDir, "restart.flag");
    internal static readonly string StatusPath = Path.Combine(DataDir, "status.json");

    private static readonly object StateLock = new();
    private static BridgeState _state = new(false, "正在启动", 0, "", "");
    private static CancellationTokenSource _bridgeCts = new();
    private static TcpClient? _currentClient;
    private static bool _isBridgeHost;

    internal static event EventHandler<BridgeState>? StateChanged;

    internal static void InitializeStorage()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }

    internal static AppConfig LoadConfig()
    {
        InitializeStorage();
        if (!File.Exists(ConfigPath))
        {
            var cfg = new AppConfig();
            SaveConfig(cfg);
            return cfg;
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8))
                   ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log("CONFIG ERR " + ex.Message);
            return new AppConfig();
        }
    }

    internal static void SaveConfig(AppConfig cfg)
    {
        InitializeStorage();
        File.WriteAllText(
            ConfigPath,
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    internal static BridgeState GetState()
    {
        if (!_isBridgeHost)
        {
            try
            {
                if (File.Exists(StatusPath))
                {
                    var cached = JsonSerializer.Deserialize<BridgeState>(
                        File.ReadAllText(StatusPath, Encoding.UTF8));
                    if (cached is not null) return cached;
                }
            }
            catch { }
        }

        lock (StateLock)
        {
            if (_state.Status != "正在启动") return _state;
        }

        try
        {
            if (File.Exists(StatusPath))
            {
                var cached = JsonSerializer.Deserialize<BridgeState>(
                    File.ReadAllText(StatusPath, Encoding.UTF8));
                if (cached is not null) return cached;
            }
        }
        catch { }

        lock (StateLock) return _state;
    }

    private static void SetState(
        bool? online = null,
        string? status = null,
        int? brightness = null,
        string? endpoint = null,
        string? lastError = null)
    {
        BridgeState snapshot;
        lock (StateLock)
        {
            _state = _state with
            {
                Online = online ?? _state.Online,
                Status = status ?? _state.Status,
                Brightness = brightness ?? _state.Brightness,
                Endpoint = endpoint ?? _state.Endpoint,
                LastError = lastError ?? _state.LastError
            };
            snapshot = _state;

            if (_isBridgeHost)
            {
                try
                {
                    File.WriteAllText(
                        StatusPath,
                        JsonSerializer.Serialize(_state),
                        new UTF8Encoding(false));
                }
                catch { }
            }
        }

        StateChanged?.Invoke(null, snapshot);
    }

    internal static void StartBridge()
    {
        _isBridgeHost = true;

        if (_bridgeCts.IsCancellationRequested)
            _bridgeCts = new CancellationTokenSource();

        var token = _bridgeCts.Token;
        Task.Run(() => BridgeLoop(token), token);
    }

    internal static void StopBridge()
    {
        _isBridgeHost = false;
        try { _bridgeCts.Cancel(); } catch { }
        try { _currentClient?.Close(); } catch { }
    }

    internal static void RestartConnection()
    {
        try
        {
            File.WriteAllText(RestartFlagPath, DateTime.UtcNow.Ticks.ToString(), Encoding.ASCII);
        }
        catch { }

        try { _currentClient?.Close(); } catch { }
        SetState(online: false, status: "正在重新连接", lastError: "");
    }

    private static void BridgeLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var cfg = LoadConfig();
                var uid = LoadSecret();
                RunBridge(cfg, uid, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetState(false, "离线，3 秒后重试", lastError: ex.Message);
                Log("ERR " + ex.Message);
            }

            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
                return;
        }
    }

    private static string LoadSecret()
    {
        if (!File.Exists(SecretPath))
            TryMigrateLegacySecret();

        if (!File.Exists(SecretPath))
            throw new FileNotFoundException("缺少巴法私钥，请打开设置填写私钥。");

        var encrypted = File.ReadAllBytes(SecretPath);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain).Trim();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static void SaveSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("私钥不能为空。");

        InitializeStorage();
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(SecretPath, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static bool HasSecret() => File.Exists(SecretPath);

    private static void TryMigrateLegacySecret()
    {
        if (!File.Exists(LegacySecretPath)) return;

        try
        {
            string escaped = LegacySecretPath.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "$s=(Get-Content -LiteralPath '" + escaped +
                "' -Raw).Trim()|ConvertTo-SecureString; ([Net.NetworkCredential]::new('', $s)).Password");

            using var process = Process.Start(psi);
            if (process is null) return;

            string key = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(key))
            {
                SaveSecret(key);
                Log("Migrated legacy encrypted key to DPAPI secret.dat");
            }
        }
        catch (Exception ex)
        {
            Log("Legacy key migration failed: " + ex.Message);
        }
    }

    private static void RunBridge(AppConfig cfg, string uid, CancellationToken token)
    {
        SetState(false, "正在连接巴法云", lastError: "");

        var localIp = GetInterfaceIPv4(cfg.InterfaceAlias);
        var serverIp = ResolveBemfaIp(cfg).GetAwaiter().GetResult();

        token.ThrowIfCancellationRequested();

        using var client = new TcpClient(new IPEndPoint(localIp, 0));
        _currentClient = client;
        client.ReceiveTimeout = 7000;
        client.SendTimeout = 7000;
        client.Connect(serverIp, cfg.Port);

        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };

        SendLine(writer, $"cmd=1&uid={uid}&topic={cfg.Topic}");
        string? ack = reader.ReadLine();

        if (!string.Equals(ack, "cmd=1&res=1", StringComparison.Ordinal))
            throw new IOException("巴法订阅失败: " + (ack ?? "<null>"));

        int current = GetBrightness();
        if (current > 0) SaveLastBrightness(current);

        string endpoint = $"{localIp} → {serverIp}:{cfg.Port}";
        SetState(true, "在线", current, endpoint, "");
        Log($"CONNECTED local={localIp} server={serverIp}:{cfg.Port} topic={cfg.Topic}");

        PublishState(writer, uid, cfg.Topic, current);
        DateTime lastHeartbeat = DateTime.UtcNow;

        while (client.Connected && !token.IsCancellationRequested)
        {
            if (File.Exists(RestartFlagPath))
            {
                try { File.Delete(RestartFlagPath); } catch { }
                throw new IOException("配置已更新，重新连接。");
            }

            if (stream.DataAvailable)
            {
                string? line = reader.ReadLine();
                if (line is null)
                    throw new IOException("巴法连接已关闭。");

                Log("RAW " + line.Replace(uid, "[uid]"));

                string? msg = ParseMsg(line);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    HandleMessage(msg, writer, uid, cfg);
                    SetState(brightness: GetBrightness());
                }
            }
            else
            {
                if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= Math.Max(10, cfg.HeartbeatSeconds))
                {
                    SendLine(writer, "ping");
                    lastHeartbeat = DateTime.UtcNow;
                }

                token.WaitHandle.WaitOne(120);
            }
        }

        token.ThrowIfCancellationRequested();
        throw new IOException("巴法 TCP 连接结束。");
    }

    private static async Task<IPAddress> ResolveBemfaIp(AppConfig cfg)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string url =
                "https://dns.alidns.com/resolve?name=" +
                Uri.EscapeDataString(cfg.ServerHost) +
                "&type=A";

            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Answer", out var answers))
            {
                foreach (var item in answers.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) &&
                        type.GetInt32() == 1 &&
                        item.TryGetProperty("data", out var data) &&
                        IPAddress.TryParse(data.GetString(), out var ip))
                    {
                        return ip;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("DNS fallback: " + ex.Message);
        }

        if (IPAddress.TryParse(cfg.ServerIpFallback, out var fallback))
            return fallback;

        throw new InvalidOperationException("没有可用的巴法服务器 IP。");
    }

    internal static string[] GetUsableInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.GetIPProperties().UnicastAddresses.Any(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork &&
                !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
            .Select(n => n.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToArray();
    }

    private static IPAddress GetInterfaceIPv4(string alias)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, alias, StringComparison.OrdinalIgnoreCase))
                continue;

            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !ua.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    return ua.Address;
                }
            }
        }

        throw new InvalidOperationException($"网卡“{alias}”没有可用 IPv4 地址。");
    }

    private static string? ParseMsg(string line)
    {
        foreach (var part in line.Split('&'))
        {
            int i = part.IndexOf('=');
            if (i <= 0) continue;
            if (!part[..i].Equals("msg", StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(part[(i + 1)..]);
        }

        return null;
    }

    private static void HandleMessage(
        string message,
        StreamWriter writer,
        string uid,
        AppConfig cfg)
    {
        string m = message.Trim();
        Log("RX " + m);
        int current = GetBrightness();

        if (m.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (current > 0) SaveLastBrightness(current);
            int actual = SetBrightness(0);
            PublishState(writer, uid, cfg.Topic, actual);
            Log("SET " + actual);
            return;
        }

        if (m.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            int target = LoadLastBrightness(cfg.DefaultBrightness);
            int actual = SetBrightness(Math.Clamp(target, 1, 100));
            SaveLastBrightness(actual);
            PublishState(writer, uid, cfg.Topic, actual);
            Log("SET " + actual);
            return;
        }

        if (m.StartsWith("on#", StringComparison.OrdinalIgnoreCase))
        {
            var token = m.Split('#', StringSplitOptions.None).ElementAtOrDefault(1);
            if (int.TryParse(token, out int requested))
            {
                int target = Math.Clamp(requested, 1, 100);
                int actual = SetBrightness(target);
                SaveLastBrightness(actual);
                PublishState(writer, uid, cfg.Topic, actual);
                Log("SET " + actual);
            }
        }
    }

    internal static int GetBrightness()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT CurrentBrightness FROM WmiMonitorBrightness");

        foreach (ManagementObject obj in searcher.Get())
            return Convert.ToInt32(obj["CurrentBrightness"]);

        throw new InvalidOperationException("未找到可通过 WMI 控制的内置屏幕。");
    }

    internal static int SetBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);

        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods");

        foreach (ManagementObject obj in searcher.Get())
        {
            using var input = obj.GetMethodParameters("WmiSetBrightness");
            input["Timeout"] = 0u;
            input["Brightness"] = (byte)value;
            obj.InvokeMethod("WmiSetBrightness", input, null);

            Thread.Sleep(120);
            int actual = GetBrightness();
            if (_isBridgeHost)
                SetState(brightness: actual);
            return actual;
        }

        throw new InvalidOperationException("未找到 WMI 亮度设置接口。");
    }

    private static void PublishState(
        StreamWriter writer,
        string uid,
        string topic,
        int brightness)
    {
        string msg = brightness <= 0 ? "off" : "on#" + brightness;
        SendLine(writer, $"cmd=2&uid={uid}&topic={topic}/up&msg={msg}");
    }

    private static void SendLine(StreamWriter writer, string line)
    {
        writer.Write(line);
        writer.Write("\r\n");
        writer.Flush();
    }

    private static void SaveLastBrightness(int value)
    {
        File.WriteAllText(LastBrightnessPath, value.ToString(), Encoding.ASCII);
    }

    private static int LoadLastBrightness(int fallback)
    {
        try
        {
            if (File.Exists(LastBrightnessPath) &&
                int.TryParse(File.ReadAllText(LastBrightnessPath).Trim(), out int value) &&
                value > 0)
            {
                return value;
            }
        }
        catch { }

        return fallback;
    }

    internal static bool IsAutoStartEnabled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN MiBrightness",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void SetAutoStart(bool enabled)
    {
        if (!enabled)
        {
            RunPowerShell(
                "Unregister-ScheduledTask -TaskName 'MiBrightness' -Confirm:$false -ErrorAction SilentlyContinue");
            return;
        }

        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取程序路径。");

        string script =
            "$a=New-ScheduledTaskAction -Execute '" + exe.Replace("'", "''") + "';" +
            "$t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME;" +
            "$p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited;" +
            "Register-ScheduledTask -TaskName 'MiBrightness' -Action $a -Trigger $t -Principal $p -Force|Out-Null";

        RunPowerShell(script);
    }

    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 PowerShell。");

        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(error);
    }

    internal static void OpenLogFolder()
    {
        Directory.CreateDirectory(LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", LogDir)
        {
            UseShellExecute = true
        });
    }

    internal static void OpenConfigFile()
    {
        if (!File.Exists(ConfigPath))
            SaveConfig(new AppConfig());

        Process.Start(new ProcessStartInfo("notepad.exe", ConfigPath)
        {
            UseShellExecute = true
        });
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }
}
