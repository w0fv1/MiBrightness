using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace MiBrightness;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _settingsWindow;
    private bool _isConfigOnly;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        BridgeRuntime.InitializeStorage();

        _isConfigOnly = e.Args.Any(a =>
            a.Equals("--config", StringComparison.OrdinalIgnoreCase));

        if (_isConfigOnly)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var configWindow = new MainWindow(configOnly: true);
            MainWindow = configWindow;
            configWindow.Show();
            return;
        }

        _mutex = new Mutex(true, @"Local\MiBrightness", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        BridgeRuntime.StartBridge();
        CreateTray();

        if (!BridgeRuntime.HasSecret())
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(ShowSettings));
        }
    }

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        var statusItem = new WinForms.ToolStripMenuItem("状态：正在启动") { Enabled = false };
        var brightnessItem = new WinForms.ToolStripMenuItem("亮度：--") { Enabled = false };
        var autoStartItem = new WinForms.ToolStripMenuItem("开机自启")
        {
            Checked = BridgeRuntime.IsAutoStartEnabled(),
            CheckOnClick = true
        };

        menu.Items.Add(statusItem);
        menu.Items.Add(brightnessItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("重新连接", null, (_, _) => BridgeRuntime.RestartConnection());
        menu.Items.Add("打开配置文件", null, (_, _) => BridgeRuntime.OpenConfigFile());
        menu.Items.Add("打开日志目录", null, (_, _) => BridgeRuntime.OpenLogFolder());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        autoStartItem.CheckedChanged += (_, _) =>
        {
            try
            {
                BridgeRuntime.SetAutoStart(autoStartItem.Checked);
            }
            catch
            {
                autoStartItem.Checked = BridgeRuntime.IsAutoStartEnabled();
            }
        };

        _tray = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "MiBrightness",
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowSettings);

        BridgeRuntime.StateChanged += (_, state) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_tray is null) return;

                statusItem.Text = "状态：" + state.Status;
                brightnessItem.Text = "亮度：" + state.Brightness + "%";

                string text = state.Online
                    ? $"MiBrightness - 在线 - {state.Brightness}%"
                    : "MiBrightness - " + state.Status;

                _tray.Text = text.Length > 63 ? text[..63] : text;
            }));
        };

        var current = BridgeRuntime.GetState();
        statusItem.Text = "状态：" + current.Status;
        brightnessItem.Text = "亮度：" + current.Brightness + "%";
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;

            _settingsWindow.Activate();
            _settingsWindow.Topmost = true;
            _settingsWindow.Topmost = false;
            return;
        }

        _settingsWindow = new MainWindow(configOnly: false);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        _tray = null;
        BridgeRuntime.StopBridge();
        _mutex?.Dispose();
        _mutex = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_isConfigOnly)
        {
            _tray?.Dispose();
            BridgeRuntime.StopBridge();
            _mutex?.Dispose();
        }

        base.OnExit(e);
    }
}
