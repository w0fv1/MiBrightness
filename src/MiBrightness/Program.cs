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

internal sealed class SettingsForm : Form
{
    private readonly TextBox topicBox = new();
    private readonly ComboBox interfaceBox = new();
    private readonly NumericUpDown heartbeatBox = new();
    private readonly NumericUpDown defaultBrightnessBox = new();
    private readonly Label statusLabel = new();
    private readonly Label detailLabel = new();
    private readonly NumericUpDown testBrightnessBox = new();
    private readonly System.Windows.Forms.Timer timer = new();

    public SettingsForm()
    {
        Text = "MiBrightness 配置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 420);
        Font = new Font("Microsoft YaHei UI", 9F);

        var cfg = Program.LoadConfig();

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 9
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        topicBox.Text = cfg.Topic;
        topicBox.Dock = DockStyle.Fill;

        interfaceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        interfaceBox.Items.AddRange(Program.GetUsableInterfaces());
        if (interfaceBox.Items.Contains(cfg.InterfaceAlias)) interfaceBox.SelectedItem = cfg.InterfaceAlias;
        else if (interfaceBox.Items.Count > 0) interfaceBox.SelectedIndex = 0;
        interfaceBox.Dock = DockStyle.Fill;

        heartbeatBox.Minimum = 10;
        heartbeatBox.Maximum = 300;
        heartbeatBox.Value = Math.Clamp(cfg.HeartbeatSeconds, 10, 300);
        heartbeatBox.Dock = DockStyle.Left;
        heartbeatBox.Width = 120;

        defaultBrightnessBox.Minimum = 1;
        defaultBrightnessBox.Maximum = 100;
        defaultBrightnessBox.Value = Math.Clamp(cfg.DefaultBrightness, 1, 100);
        defaultBrightnessBox.Dock = DockStyle.Left;
        defaultBrightnessBox.Width = 120;

        testBrightnessBox.Minimum = 0;
        testBrightnessBox.Maximum = 100;
        try { testBrightnessBox.Value = Program.GetBrightness(); } catch { testBrightnessBox.Value = 50; }
        testBrightnessBox.Width = 90;

        AddRow(table, 0, "巴法 Topic", topicBox);
        AddRow(table, 1, "直连网卡", interfaceBox);
        AddRow(table, 2, "心跳间隔（秒）", heartbeatBox);
        AddRow(table, 3, "默认亮度", defaultBrightnessBox);

        var testPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        testPanel.Controls.Add(testBrightnessBox);
        var testBtn = new Button { Text = "测试亮度", AutoSize = true };
        testBtn.Click += (_, _) =>
        {
            try
            {
                int actual = Program.SetBrightness((int)testBrightnessBox.Value);
                statusLabel.Text = $"测试成功，当前亮度 {actual}%";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        testPanel.Controls.Add(testBtn);
        AddRow(table, 4, "屏幕测试", testPanel);

        statusLabel.AutoSize = true;
        statusLabel.Font = new Font(Font, FontStyle.Bold);
        detailLabel.AutoSize = true;
        detailLabel.ForeColor = SystemColors.GrayText;
        var statePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoSize = true };
        statePanel.Controls.Add(statusLabel);
        statePanel.Controls.Add(detailLabel);
        AddRow(table, 5, "运行状态", statePanel);

        var keyBtn = new Button { Text = "设置 / 更换巴法私钥...", AutoSize = true };
        keyBtn.Click += (_, _) => SetSecret();
        AddRow(table, 6, "凭据", keyBtn);

        var toolsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var configBtn = new Button { Text = "打开 config.json", AutoSize = true };
        configBtn.Click += (_, _) => Program.OpenConfigFile();
        var logBtn = new Button { Text = "打开日志目录", AutoSize = true };
        logBtn.Click += (_, _) => Program.OpenLogFolder();
        toolsPanel.Controls.Add(configBtn);
        toolsPanel.Controls.Add(logBtn);
        AddRow(table, 7, "高级", toolsPanel);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var cancel = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "保存并重新连接", AutoSize = true };
        save.Click += (_, _) => Save();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        table.Controls.Add(buttons, 0, 8);
        table.SetColumnSpan(buttons, 2);

        Controls.Add(table);
        AcceptButton = save;
        CancelButton = cancel;

        timer.Interval = 1000;
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        RefreshStatus();
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 0, 0) };
        table.Controls.Add(l, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void RefreshStatus()
    {
        var s = Program.GetState();
        statusLabel.Text = s.Online ? $"在线 · {s.Brightness}%" : s.Status;
        detailLabel.Text = string.IsNullOrWhiteSpace(s.LastError)
            ? s.Endpoint
            : s.Endpoint + Environment.NewLine + "最近错误：" + s.LastError;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(topicBox.Text))
        {
            MessageBox.Show("Topic 不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (interfaceBox.SelectedItem is null)
        {
            MessageBox.Show("请选择一个直连网卡。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cfg = Program.LoadConfig();
        cfg.Topic = topicBox.Text.Trim();
        cfg.InterfaceAlias = interfaceBox.SelectedItem.ToString()!;
        cfg.HeartbeatSeconds = (int)heartbeatBox.Value;
        cfg.DefaultBrightness = (int)defaultBrightnessBox.Value;
        Program.SaveConfig(cfg);
        Program.RestartConnection();
        statusLabel.Text = "配置已保存，正在重新连接...";
    }

    private void SetSecret()
    {
        using var dialog = new Form
        {
            Text = "设置巴法私钥",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(430, 145),
            Font = Font
        };
        var label = new Label { Text = "巴法私钥", Left = 18, Top = 24, AutoSize = true };
        var box = new TextBox { Left = 95, Top = 20, Width = 315, UseSystemPasswordChar = true };
        var ok = new Button { Text = "保存", Left = 245, Top = 82, Width = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 330, Top = 82, Width = 80, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Program.SaveSecret(box.Text);
            Program.RestartConnection();
            statusLabel.Text = "私钥已保存，正在重新连接...";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

