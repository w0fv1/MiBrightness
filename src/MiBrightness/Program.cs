using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

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

internal static class Program
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
    private static BridgeState State = new(false, "正在启动", 0, "", "");
    private static CancellationTokenSource BridgeCts = new();
    private static TcpClient? CurrentClient;

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Any(a => a.Equals("--config", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new SettingsForm());
            return;
        }

        using var mutex = new Mutex(true, @"Local\MiBrightness", out bool createdNew);
        if (!createdNew) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("FATAL " + (e.ExceptionObject?.ToString() ?? "unknown"));

        StartBridge();
        Application.Run(new TrayContext());

        StopBridge();
    }

    internal static AppConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            var d = new AppConfig();
            SaveConfig(d);
            return d;
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
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(
            ConfigPath,
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    internal static BridgeState GetState()
    {
        lock (StateLock)
        {
            if (State.Status != "正在启动") return State;
        }
        try
        {
            if (File.Exists(StatusPath))
            {
                var cached = JsonSerializer.Deserialize<BridgeState>(File.ReadAllText(StatusPath, Encoding.UTF8));
                if (cached is not null) return cached;
            }
        }
        catch { }
        lock (StateLock) return State;
    }

    private static void SetState(bool? online = null, string? status = null, int? brightness = null,
        string? endpoint = null, string? lastError = null)
    {
        lock (StateLock)
        {
            State = State with
            {
                Online = online ?? State.Online,
                Status = status ?? State.Status,
                Brightness = brightness ?? State.Brightness,
                Endpoint = endpoint ?? State.Endpoint,
                LastError = lastError ?? State.LastError
            };
            try
            {
                File.WriteAllText(StatusPath, JsonSerializer.Serialize(State), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    internal static void StartBridge()
    {
        if (BridgeCts.IsCancellationRequested) BridgeCts = new CancellationTokenSource();
        var token = BridgeCts.Token;
        Task.Run(() => BridgeLoop(token), token);
    }

    internal static void RestartConnection()
    {
        try { File.WriteAllText(RestartFlagPath, DateTime.UtcNow.Ticks.ToString(), Encoding.ASCII); } catch { }
        try { CurrentClient?.Close(); } catch { }
        SetState(online: false, status: "正在重新连接", lastError: "");
    }

    internal static void StopBridge()
    {
        try { BridgeCts.Cancel(); } catch { }
        try { CurrentClient?.Close(); } catch { }
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
                SetState(online: false, status: "离线，3 秒后重试", lastError: ex.Message);
                Log("ERR " + ex.Message);
            }

            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3))) return;
        }
    }

    private static string LoadSecret()
    {
        if (!File.Exists(SecretPath))
            TryMigrateLegacySecret();

        if (!File.Exists(SecretPath))
            throw new FileNotFoundException("缺少巴法私钥，请打开“配置”设置私钥。");

        byte[] enc = File.ReadAllBytes(SecretPath);
        byte[] plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain).Trim();
    }

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
            psi.ArgumentList.Add("$s=(Get-Content -LiteralPath '" + escaped +
                "' -Raw).Trim()|ConvertTo-SecureString; ([Net.NetworkCredential]::new('', $s)).Password");
            using var proc = Process.Start(psi);
            if (proc is null) return;
            string key = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(key))
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

    internal static void SaveSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("私钥不能为空。");

        Directory.CreateDirectory(DataDir);
        byte[] plain = Encoding.UTF8.GetBytes(key.Trim());
        byte[] enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SecretPath, enc);
        CryptographicOperations.ZeroMemory(plain);
    }

    private static void RunBridge(AppConfig cfg, string uid, CancellationToken token)
    {
        SetState(online: false, status: "正在连接巴法云", lastError: "");

        var localIp = GetInterfaceIPv4(cfg.InterfaceAlias);
        var serverIp = ResolveBemfaIp(cfg).GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();

        using var client = new TcpClient(new IPEndPoint(localIp, 0));
        CurrentClient = client;
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
        SetState(online: true, status: "在线", brightness: current, endpoint: endpoint, lastError: "");
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
                if (line is null) throw new IOException("巴法连接已关闭。");
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
            var json = await http.GetStringAsync(
                "https://dns.alidns.com/resolve?name=" + Uri.EscapeDataString(cfg.ServerHost) + "&type=A");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Answer", out var answers))
            {
                foreach (var item in answers.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetInt32() == 1 &&
                        item.TryGetProperty("data", out var data) &&
                        IPAddress.TryParse(data.GetString(), out var ip))
                        return ip;
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
            if (!string.Equals(nic.Name, alias, StringComparison.OrdinalIgnoreCase)) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !ua.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    return ua.Address;
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
            if (!part[..i].Equals("msg", StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return null;
    }

    private static void HandleMessage(string message, StreamWriter writer, string uid, AppConfig cfg)
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
            @"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
        foreach (ManagementObject obj in searcher.Get())
            return Convert.ToInt32(obj["CurrentBrightness"]);
        throw new InvalidOperationException("未找到可通过 WMI 控制的内置屏幕。");
    }

    internal static int SetBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject obj in searcher.Get())
        {
            using var input = obj.GetMethodParameters("WmiSetBrightness");
            input["Timeout"] = 0u;
            input["Brightness"] = (byte)value;
            obj.InvokeMethod("WmiSetBrightness", input, null);
            Thread.Sleep(120);
            int actual = GetBrightness();
            SetState(brightness: actual);
            return actual;
        }
        throw new InvalidOperationException("未找到 WMI 亮度设置接口。");
    }

    private static void PublishState(StreamWriter writer, string uid, string topic, int brightness)
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
                int.TryParse(File.ReadAllText(LastBrightnessPath).Trim(), out int v) &&
                v > 0) return v;
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
        catch { return false; }
    }

    internal static void SetAutoStart(bool enabled)
    {
        if (!enabled)
        {
            RunPowerShell("Unregister-ScheduledTask -TaskName 'MiBrightness' -Confirm:$false -ErrorAction SilentlyContinue");
            return;
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径。");
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
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell。");
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(error);
    }

    internal static void OpenLogFolder()
    {
        Directory.CreateDirectory(LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", LogDir) { UseShellExecute = true });
    }

    internal static void OpenConfigFile()
    {
        if (!File.Exists(ConfigPath)) SaveConfig(new AppConfig());
        Process.Start(new ProcessStartInfo("notepad.exe", ConfigPath) { UseShellExecute = true });
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem brightnessItem;
    private readonly ToolStripMenuItem autoStartItem;
    private readonly System.Windows.Forms.Timer timer;
    private System.Windows.Forms.Timer? firstRunTimer;

    public TrayContext()
    {
        statusItem = new ToolStripMenuItem("状态：正在启动") { Enabled = false };
        brightnessItem = new ToolStripMenuItem("亮度：--") { Enabled = false };
        autoStartItem = new ToolStripMenuItem("开机自启") { Checked = Program.IsAutoStartEnabled(), CheckOnClick = true };
        autoStartItem.CheckedChanged += (_, _) =>
        {
            try { Program.SetAutoStart(autoStartItem.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "MiBrightness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                autoStartItem.Checked = Program.IsAutoStartEnabled();
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(brightnessItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("配置...", null, (_, _) => OpenSettings());
        menu.Items.Add("重新连接", null, (_, _) => Program.RestartConnection());
        menu.Items.Add("打开配置文件", null, (_, _) => Program.OpenConfigFile());
        menu.Items.Add("打开日志目录", null, (_, _) => Program.OpenLogFolder());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MiBrightness",
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => OpenSettings();

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => RefreshTray();
        timer.Start();
        RefreshTray();

        if (!File.Exists(Program.SecretPath))
        {
            firstRunTimer = new System.Windows.Forms.Timer { Interval = 700 };
            firstRunTimer.Tick += (_, _) =>
            {
                firstRunTimer?.Stop();
                OpenSettings();
            };
            firstRunTimer.Start();
        }
    }

    private void RefreshTray()
    {
        var s = Program.GetState();
        statusItem.Text = "状态：" + s.Status;
        brightnessItem.Text = "亮度：" + (s.Brightness > 0 ? s.Brightness + "%" : s.Brightness == 0 ? "0%" : "--");
        tray.Text = s.Online
            ? $"MiBrightness - 在线 - {s.Brightness}%"
            : "MiBrightness - " + s.Status;
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm();
        form.ShowDialog();
        RefreshTray();
    }

    private void Exit()
    {
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        Program.StopBridge();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class UiCard : Panel
{
    public UiCard()
    {
        BackColor = Color.White;
        Padding = new Padding(22, 18, 22, 18);
        Margin = new Padding(0, 0, 0, 14);
        BorderStyle = BorderStyle.None;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(226, 230, 236));
        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        e.Graphics.DrawRectangle(pen, r);
    }
}

internal sealed class SettingsForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 120, 212);
    private static readonly Color AccentHover = Color.FromArgb(16, 110, 190);
    private static readonly Color TextPrimary = Color.FromArgb(32, 33, 36);
    private static readonly Color TextSecondary = Color.FromArgb(95, 99, 104);
    private static readonly Color Surface = Color.FromArgb(246, 248, 251);
    private static readonly Color Success = Color.FromArgb(16, 124, 65);
    private static readonly Color Danger = Color.FromArgb(196, 43, 28);

    private readonly TextBox topicBox = new();
    private readonly ComboBox interfaceBox = new();
    private readonly NumericUpDown heartbeatBox = new();
    private readonly NumericUpDown defaultBrightnessBox = new();
    private readonly Label statusBadge = new();
    private readonly Label statusDetail = new();
    private readonly Label brightnessValue = new();
    private readonly TrackBar brightnessSlider = new();
    private readonly CheckBox autoStartBox = new();
    private readonly Button saveButton;
    private readonly System.Windows.Forms.Timer timer = new();

    public SettingsForm()
    {
        Text = "MiBrightness";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(860, 720);
        MinimumSize = MaximumSize = new Size(876, 759);
        Font = new Font("Segoe UI", 10F);
        BackColor = Surface;
        AutoScaleMode = AutoScaleMode.Dpi;

        var cfg = Program.LoadConfig();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 20),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Surface
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildConnectionCard(cfg), 0, 1);
        root.Controls.Add(BuildBrightnessCard(cfg), 0, 2);
        root.Controls.Add(BuildAdvancedCard(cfg), 0, 3);

        var hint = new Label
        {
            Text = "配置和加密私钥保存在当前 Windows 用户目录。保存后后台连接会自动重启。",
            AutoSize = true,
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 9F),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 10, 0, 0)
        };
        root.Controls.Add(hint, 0, 4);

        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 9, 0, 0),
            WrapContents = false
        };

        saveButton = PrimaryButton("保存并重新连接", 150);
        saveButton.Click += (_, _) => Save();

        var closeButton = SecondaryButton("关闭", 92);
        closeButton.DialogResult = DialogResult.Cancel;

        footer.Controls.Add(saveButton);
        footer.Controls.Add(closeButton);
        root.Controls.Add(footer, 0, 5);

        Controls.Add(root);
        AcceptButton = saveButton;
        CancelButton = closeButton;

        timer.Interval = 1000;
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        RefreshStatus();
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface };

        var title = new Label
        {
            Text = "MiBrightness",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 21F),
            ForeColor = TextPrimary,
            Location = new Point(0, 2)
        };

        var subtitle = new Label
        {
            Text = "小爱同学 × 巴法云 × Windows 内屏亮度",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = TextSecondary,
            Location = new Point(2, 46)
        };

        statusBadge.AutoSize = true;
        statusBadge.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        statusBadge.Padding = new Padding(12, 7, 12, 7);
        statusBadge.TextAlign = ContentAlignment.MiddleCenter;
        statusBadge.Location = new Point(610, 12);

        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(statusBadge);
        return panel;
    }

    private Control BuildConnectionCard(AppConfig cfg)
    {
        var card = new UiCard { Dock = DockStyle.Fill };

        var title = SectionTitle("连接");
        title.Location = new Point(22, 17);
        card.Controls.Add(title);

        var topicLabel = FieldLabel("巴法 Topic");
        topicLabel.Location = new Point(24, 59);
        card.Controls.Add(topicLabel);

        topicBox.Text = cfg.Topic;
        topicBox.Location = new Point(142, 55);
        topicBox.Size = new Size(285, 28);
        topicBox.BorderStyle = BorderStyle.FixedSingle;
        card.Controls.Add(topicBox);

        var nicLabel = FieldLabel("直连网卡");
        nicLabel.Location = new Point(455, 59);
        card.Controls.Add(nicLabel);

        interfaceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        interfaceBox.FlatStyle = FlatStyle.System;
        interfaceBox.Items.AddRange(Program.GetUsableInterfaces());
        if (interfaceBox.Items.Contains(cfg.InterfaceAlias)) interfaceBox.SelectedItem = cfg.InterfaceAlias;
        else if (interfaceBox.Items.Count > 0) interfaceBox.SelectedIndex = 0;
        interfaceBox.Location = new Point(545, 55);
        interfaceBox.Size = new Size(205, 28);
        card.Controls.Add(interfaceBox);

        statusDetail.AutoEllipsis = true;
        statusDetail.ForeColor = TextSecondary;
        statusDetail.Font = new Font("Microsoft YaHei UI", 9F);
        statusDetail.Location = new Point(24, 101);
        statusDetail.Size = new Size(690, 36);
        card.Controls.Add(statusDetail);

        var reconnect = LinkButton("重新连接");
        reconnect.Location = new Point(698, 99);
        reconnect.Click += (_, _) =>
        {
            Program.RestartConnection();
            statusDetail.Text = "正在重新连接...";
        };
        card.Controls.Add(reconnect);

        return card;
    }

    private Control BuildBrightnessCard(AppConfig cfg)
    {
        var card = new UiCard { Dock = DockStyle.Fill };

        var title = SectionTitle("屏幕亮度");
        title.Location = new Point(22, 17);
        card.Controls.Add(title);

        brightnessValue.Text = "--%";
        brightnessValue.Font = new Font("Segoe UI Semibold", 18F);
        brightnessValue.ForeColor = Accent;
        brightnessValue.AutoSize = true;
        brightnessValue.Location = new Point(705, 16);
        card.Controls.Add(brightnessValue);

        brightnessSlider.Minimum = 0;
        brightnessSlider.Maximum = 100;
        brightnessSlider.TickFrequency = 10;
        brightnessSlider.SmallChange = 1;
        brightnessSlider.LargeChange = 10;
        brightnessSlider.AutoSize = false;
        brightnessSlider.Location = new Point(23, 58);
        brightnessSlider.Size = new Size(620, 44);
        try { brightnessSlider.Value = Program.GetBrightness(); } catch { brightnessSlider.Value = 50; }

        var liveValue = new Label
        {
            AutoSize = true,
            ForeColor = TextSecondary,
            Location = new Point(24, 108)
        };
        liveValue.Text = $"测试值：{brightnessSlider.Value}%";
        brightnessSlider.ValueChanged += (_, _) => liveValue.Text = $"测试值：{brightnessSlider.Value}%";
        card.Controls.Add(brightnessSlider);
        card.Controls.Add(liveValue);

        var testButton = SecondaryButton("应用测试亮度", 126);
        testButton.Location = new Point(650, 66);
        testButton.Click += (_, _) =>
        {
            try
            {
                int actual = Program.SetBrightness(brightnessSlider.Value);
                brightnessValue.Text = actual + "%";
                statusDetail.Text = $"亮度测试成功 · 当前 {actual}%";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        card.Controls.Add(testButton);

        return card;
    }

    private Control BuildAdvancedCard(AppConfig cfg)
    {
        var card = new UiCard { Dock = DockStyle.Fill };

        var title = SectionTitle("高级设置");
        title.Location = new Point(22, 17);
        card.Controls.Add(title);

        var hbLabel = FieldLabel("心跳间隔");
        hbLabel.Location = new Point(24, 61);
        card.Controls.Add(hbLabel);

        heartbeatBox.Minimum = 10;
        heartbeatBox.Maximum = 300;
        heartbeatBox.Value = Math.Clamp(cfg.HeartbeatSeconds, 10, 300);
        heartbeatBox.Location = new Point(119, 56);
        heartbeatBox.Size = new Size(76, 28);
        card.Controls.Add(heartbeatBox);

        var sec = new Label
        {
            Text = "秒",
            AutoSize = true,
            ForeColor = TextSecondary,
            Location = new Point(201, 61)
        };
        card.Controls.Add(sec);

        var defLabel = FieldLabel("开机默认亮度");
        defLabel.Location = new Point(270, 61);
        card.Controls.Add(defLabel);

        defaultBrightnessBox.Minimum = 1;
        defaultBrightnessBox.Maximum = 100;
        defaultBrightnessBox.Value = Math.Clamp(cfg.DefaultBrightness, 1, 100);
        defaultBrightnessBox.Location = new Point(385, 56);
        defaultBrightnessBox.Size = new Size(76, 28);
        card.Controls.Add(defaultBrightnessBox);

        var pct = new Label
        {
            Text = "%",
            AutoSize = true,
            ForeColor = TextSecondary,
            Location = new Point(467, 61)
        };
        card.Controls.Add(pct);

        autoStartBox.Text = "登录 Windows 后自动启动";
        autoStartBox.Checked = Program.IsAutoStartEnabled();
        autoStartBox.AutoSize = true;
        autoStartBox.Location = new Point(540, 59);
        card.Controls.Add(autoStartBox);

        var keyButton = SecondaryButton("设置巴法私钥", 126);
        keyButton.Location = new Point(24, 116);
        keyButton.Click += (_, _) => SetSecret();
        card.Controls.Add(keyButton);

        var configButton = SecondaryButton("打开 config.json", 132);
        configButton.Location = new Point(162, 116);
        configButton.Click += (_, _) => Program.OpenConfigFile();
        card.Controls.Add(configButton);

        var logButton = SecondaryButton("打开日志", 104);
        logButton.Location = new Point(306, 116);
        logButton.Click += (_, _) => Program.OpenLogFolder();
        card.Controls.Add(logButton);

        var privacy = new Label
        {
            Text = "DPAPI 加密，仅当前用户可解密",
            AutoSize = true,
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Location = new Point(455, 124)
        };
        card.Controls.Add(privacy);

        return card;
    }

    private void RefreshStatus()
    {
        var s = Program.GetState();

        if (s.Online)
        {
            statusBadge.Text = $"● 在线 {s.Brightness}%";
            statusBadge.ForeColor = Success;
            statusBadge.BackColor = Color.FromArgb(226, 246, 234);
        }
        else
        {
            statusBadge.Text = "●  " + s.Status;
            statusBadge.ForeColor = Danger;
            statusBadge.BackColor = Color.FromArgb(253, 235, 233);
        }

        brightnessValue.Text = s.Brightness >= 0 ? s.Brightness + "%" : "--%";

        string endpoint = string.IsNullOrWhiteSpace(s.Endpoint) ? "尚未建立连接" : s.Endpoint;
        statusDetail.Text = string.IsNullOrWhiteSpace(s.LastError)
            ? endpoint
            : endpoint + "    ·    最近错误：" + s.LastError;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(topicBox.Text))
        {
            MessageBox.Show("Topic 不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            topicBox.Focus();
            return;
        }

        if (interfaceBox.SelectedItem is null)
        {
            MessageBox.Show("请选择一个真实联网网卡。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            interfaceBox.Focus();
            return;
        }

        try
        {
            var cfg = Program.LoadConfig();
            cfg.Topic = topicBox.Text.Trim();
            cfg.InterfaceAlias = interfaceBox.SelectedItem.ToString()!;
            cfg.HeartbeatSeconds = (int)heartbeatBox.Value;
            cfg.DefaultBrightness = (int)defaultBrightnessBox.Value;
            Program.SaveConfig(cfg);
            Program.SetAutoStart(autoStartBox.Checked);
            Program.RestartConnection();

            saveButton.Text = "已保存 ✓";
            var reset = new System.Windows.Forms.Timer { Interval = 1400 };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                reset.Dispose();
                saveButton.Text = "保存并重新连接";
            };
            reset.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetSecret()
    {
        using var dialog = new Form
        {
            Text = "巴法私钥",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(480, 205),
            BackColor = Surface,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };

        var heading = new Label
        {
            Text = "设置巴法云私钥",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(24, 22)
        };

        var help = new Label
        {
            Text = "私钥只会使用 Windows DPAPI 加密保存在当前用户目录。",
            AutoSize = true,
            ForeColor = TextSecondary,
            Location = new Point(25, 56)
        };

        var box = new TextBox
        {
            Left = 26,
            Top = 88,
            Width = 428,
            Height = 28,
            UseSystemPasswordChar = true,
            BorderStyle = BorderStyle.FixedSingle
        };

        var ok = PrimaryButton("保存私钥", 100);
        ok.Location = new Point(354, 142);
        ok.DialogResult = DialogResult.OK;

        var cancel = SecondaryButton("取消", 82);
        cancel.Location = new Point(262, 142);
        cancel.DialogResult = DialogResult.Cancel;

        dialog.Controls.AddRange(new Control[] { heading, help, box, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Program.SaveSecret(box.Text);
            Program.RestartConnection();
            statusDetail.Text = "私钥已保存，正在重新连接...";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
        ForeColor = TextPrimary
    };

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9F),
        ForeColor = TextSecondary
    };

    private static Button PrimaryButton(string text, int width)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Margin = new Padding(8, 0, 0, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        button.MouseEnter += (_, _) => button.BackColor = AccentHover;
        button.MouseLeave += (_, _) => button.BackColor = Accent;
        return button;
    }

    private static Button SecondaryButton(string text, int width)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F),
            Margin = new Padding(8, 0, 0, 0)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(202, 207, 214);
        button.FlatAppearance.BorderSize = 1;
        button.MouseEnter += (_, _) => button.BackColor = Color.FromArgb(245, 247, 250);
        button.MouseLeave += (_, _) => button.BackColor = Color.White;
        return button;
    }

    private static Button LinkButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Width = 88,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Accent,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
