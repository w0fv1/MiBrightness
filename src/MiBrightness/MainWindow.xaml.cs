using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MiBrightness;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _configOnly;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow(bool configOnly = false)
    {
        _configOnly = configOnly;
        InitializeComponent();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += (_, _) => RefreshState(BridgeRuntime.GetState());

        BridgeRuntime.StateChanged += BridgeRuntime_StateChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        RefreshState(BridgeRuntime.GetState());
        _statusTimer.Start();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        BridgeRuntime.StateChanged -= BridgeRuntime_StateChanged;
    }

    private void BridgeRuntime_StateChanged(object? sender, BridgeState state)
    {
        Dispatcher.BeginInvoke(() => RefreshState(state));
    }

    private void LoadSettings()
    {
        var cfg = BridgeRuntime.LoadConfig();

        TopicBox.Text = cfg.Topic;

        InterfaceBox.ItemsSource = BridgeRuntime.GetUsableInterfaces();
        InterfaceBox.SelectedItem = InterfaceBox.Items
            .Cast<string>()
            .FirstOrDefault(x => x.Equals(cfg.InterfaceAlias, StringComparison.OrdinalIgnoreCase));

        if (InterfaceBox.SelectedItem is null && InterfaceBox.Items.Count > 0)
            InterfaceBox.SelectedIndex = 0;

        HeartbeatBox.Value = Math.Clamp(cfg.HeartbeatSeconds, 10, 300);
        DefaultBrightnessBox.Value = Math.Clamp(cfg.DefaultBrightness, 1, 100);
        AutoStartToggle.IsChecked = BridgeRuntime.IsAutoStartEnabled();

        try
        {
            int brightness = BridgeRuntime.GetBrightness();
            BrightnessSlider.Value = brightness;
            BrightnessText.Text = brightness + "%";
            TestValueText.Text = $"测试值：{brightness}%";
        }
        catch
        {
            BrightnessSlider.Value = 50;
            TestValueText.Text = "测试值：50%";
        }
    }

    private void RefreshState(BridgeState state)
    {
        StatusText.Text = state.Online
            ? $"● 在线 {state.Brightness}%"
            : "● " + state.Status;

        StatusText.Foreground = state.Online
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 124, 65))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 43, 28));

        StatusBadge.Background = state.Online
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 246, 236))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 235, 233));

        if (state.Brightness >= 0)
            BrightnessText.Text = state.Brightness + "%";

        EndpointText.Text = string.IsNullOrWhiteSpace(state.Endpoint)
            ? "尚未建立连接"
            : state.Endpoint;

        if (string.IsNullOrWhiteSpace(state.LastError))
        {
            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = "";
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = "最近错误：" + state.LastError;
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TestValueText is null) return;
        TestValueText.Text = $"测试值：{Math.Round(e.NewValue)}%";
    }

    private void TestBrightness_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int actual = BridgeRuntime.SetBrightness((int)Math.Round(BrightnessSlider.Value));
            BrightnessSlider.Value = actual;
            BrightnessText.Text = actual + "%";
            SaveHintText.Text = $"亮度测试成功，当前 {actual}%";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "MiBrightness",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        BridgeRuntime.RestartConnection();
        SaveHintText.Text = "已请求重新连接。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TopicBox.Text))
        {
            System.Windows.MessageBox.Show(
                "巴法 Topic 不能为空。",
                "MiBrightness",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Warning);
            TopicBox.Focus();
            return;
        }

        if (InterfaceBox.SelectedItem is not string interfaceAlias)
        {
            System.Windows.MessageBox.Show(
                "请选择一个真实联网网卡。",
                "MiBrightness",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Warning);
            InterfaceBox.Focus();
            return;
        }

        try
        {
            var cfg = BridgeRuntime.LoadConfig();
            cfg.Topic = TopicBox.Text.Trim();
            cfg.InterfaceAlias = interfaceAlias;
            cfg.HeartbeatSeconds = Math.Clamp((int)Math.Round(HeartbeatBox.Value ?? 30), 10, 300);
            cfg.DefaultBrightness = Math.Clamp((int)Math.Round(DefaultBrightnessBox.Value ?? 80), 1, 100);

            BridgeRuntime.SaveConfig(cfg);
            BridgeRuntime.SetAutoStart(AutoStartToggle.IsChecked == true);
            BridgeRuntime.RestartConnection();

            SaveHintText.Text = "✓ 已保存，正在重新连接。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "MiBrightness",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetSecret_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "设置巴法私钥",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = System.Windows.SystemColors.WindowBrush
        };

        var root = new StackPanel
        {
            Width = 460,
            Margin = new Thickness(24)
        };

        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "巴法云私钥",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });

        root.Children.Add(new TextBlock
        {
            Text = "私钥会使用 Windows DPAPI 加密，仅当前 Windows 用户可解密。",
            Margin = new Thickness(0, 8, 0, 16),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128)),
            TextWrapping = TextWrapping.Wrap
        });

        var password = new System.Windows.Controls.PasswordBox
        {
            MinHeight = 38
        };
        root.Children.Add(password);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            MinWidth = 86,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;

        var save = new System.Windows.Controls.Button
        {
            Content = "保存私钥",
            MinWidth = 100,
            Padding = new Thickness(16, 8, 16, 8)
        };
        save.Click += (_, _) =>
        {
            try
            {
                BridgeRuntime.SaveSecret(password.Password);
                BridgeRuntime.RestartConnection();
                SaveHintText.Text = "✓ 私钥已保存，正在重新连接。";
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "MiBrightness",
                    System.Windows.MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        BridgeRuntime.OpenConfigFile();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        BridgeRuntime.OpenLogFolder();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
