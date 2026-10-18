using BluetoothBattery.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BluetoothBattery;

/// <summary>
/// 设置页。工厂每次进页面都会新建一份，所以订阅在 Loaded / Unloaded 里成对处理
/// （别让插件一直握着这个页面）。值没变就不写设置 —— Settings.Set 会触发一次刷新通知。
///
/// 注意：设置窗口跟的是宿主主题，这里的文字**不要自己指定颜色**，靠主题画刷 + Opacity 分层次。
/// </summary>
internal sealed class BluetoothBatterySettingsPage : UserControl
{
    private readonly BluetoothBatteryPlugin _plugin;
    private readonly NumberBox _refreshBox;
    private readonly ToggleSwitch _notifyToggle;
    private readonly NumberBox _thresholdBox;
    private readonly TextBlock _devicesText;
    private readonly TextBlock _statusText;
    private bool _loading = true;

    public BluetoothBatterySettingsPage(BluetoothBatteryPlugin plugin)
    {
        _plugin = plugin;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = "在灵动岛上显示已连接蓝牙设备（耳机、鼠标、键盘等）的电量，以及 Windows 默认播放设备的系统音量。"
                 + "电量来自 Windows 蓝牙设备上报，与「设置 → 蓝牙」显示的是同一份数据；"
                 + "部分设备不提供电量时会显示「—」。点岛体可以看全部设备 / 音量并手动刷新。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        AddToggle(panel, "在灵动岛上显示", "关掉后插件照常后台读取电量，只是不占用岛。", "enabled", true);
        AddToggle(panel, "显示系统音量", "在岛上显示默认播放设备的主音量（静音时显示「静音」，每秒刷新）。", "showVolume", true);

        _refreshBox = new NumberBox
        {
            Header = "自动刷新间隔（秒）",
            Value = plugin.RefreshSeconds,
            Minimum = 3,
            Maximum = 120,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 240,
        };
        _refreshBox.ValueChanged += (_, _) => CommitRefreshSeconds();
        _refreshBox.LostFocus += (_, _) => CommitRefreshSeconds();
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    _refreshBox,
                    new TextBlock
                    {
                        Text = "蓝牙设备电量不会频繁变化，5 秒左右足够；调小只会增加后台枚举次数。",
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        });

        _notifyToggle = new ToggleSwitch
        {
            Header = "低电量提醒",
            IsOn = plugin.GetBool("notifyLowBattery", true),
        };
        _notifyToggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _plugin.SetBool("notifyLowBattery", _notifyToggle.IsOn);
        };

        _thresholdBox = new NumberBox
        {
            Header = "低电量阈值（%）",
            Value = plugin.GetInt("lowThreshold", 20),
            Minimum = 5,
            Maximum = 50,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 240,
        };
        _thresholdBox.ValueChanged += (_, _) => CommitThreshold();
        _thresholdBox.LostFocus += (_, _) => CommitThreshold();

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    _notifyToggle,
                    new TextBlock
                    {
                        Text = "设备电量掉到阈值以下时，在岛上弹一条提醒（每台设备只提醒一次，"
                             + "电量回升或设备断开后重新计数）。",
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _thresholdBox,
                },
            },
        });

        _statusText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        _devicesText = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        };

        var refreshButton = new Button { Content = "立即刷新" };
        refreshButton.Click += async (_, _) =>
        {
            // 动作之前先把没提交的输入框值写进设置：按钮点击早于 LostFocus（见 sdk-api §15.1）
            CommitRefreshSeconds();
            CommitThreshold();
            await _plugin.RefreshNowAsync("设置页手动刷新");
            await _plugin.RefreshVolumeNowAsync();
        };

        var testButton = new Button { Content = "发一条测试提醒" };
        testButton.Click += (_, _) => _plugin.PreviewLowBatteryMessage();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { refreshButton, testButton },
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "当前检测到的设备",
                        FontSize = 13,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    _statusText,
                    _devicesText,
                    actions,
                },
            },
        });

        Content = new ScrollViewer
        {
            Content = panel,
            Padding = new Thickness(0, 0, 12, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _loading = false;
        RefreshState();

        Loaded += (_, _) =>
        {
            RefreshState();
            _plugin.StateChanged += RefreshState;
        };
        Unloaded += (_, _) => _plugin.StateChanged -= RefreshState;
    }

    private void AddToggle(StackPanel panel, string header, string description, string key, bool fallback)
    {
        var toggle = new ToggleSwitch
        {
            Header = header,
            IsOn = _plugin.GetBool(key, fallback),
        };
        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;
            _plugin.SetBool(key, toggle.IsOn);
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    toggle,
                    new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                },
            },
        });
    }

    private void CommitRefreshSeconds()
    {
        if (_loading) return;

        var value = double.IsNaN(_refreshBox.Value) ? 5 : (int)Math.Round(_refreshBox.Value);
        value = Math.Clamp(value, 3, 120);
        if (value == _plugin.RefreshSeconds) return;   // 值没变就别写，避免白触发一次刷新通知

        _plugin.SetInt("refreshSeconds", value);
    }

    private void CommitThreshold()
    {
        if (_loading) return;

        var value = double.IsNaN(_thresholdBox.Value) ? 20 : (int)Math.Round(_thresholdBox.Value);
        value = Math.Clamp(value, 5, 50);
        if (value == _plugin.GetInt("lowThreshold", 20)) return;

        _plugin.SetInt("lowThreshold", value);
    }

    private void RefreshState()
    {
        var devices = _plugin.Devices;
        var connected = devices.Count(d => d.Connected);

        var volumeText = _plugin.Volume is { Available: true } volume
            ? (volume.Muted ? "系统音量 静音" : $"系统音量 {volume.Percent}%")
            : "系统音量 读取失败";

        _statusText.Text = _plugin.LastUpdated is { } updated
            ? $"最后更新 {updated:HH:mm:ss} · 已连接 {connected} 台 · {volumeText}"
            : $"正在读取…… · {volumeText}";

        if (devices.Count == 0)
        {
            _devicesText.Text = "暂无已连接的蓝牙设备 —— 打开蓝牙耳机 / 鼠标 / 键盘试试。";
            return;
        }

        var lines = new List<string>(devices.Count);
        foreach (var device in devices)
        {
            var battery = device.Percent is int percent ? $"{percent}%" : "—";
            lines.Add($"· {device.Name} · {battery}");
        }

        _devicesText.Text = string.Join(Environment.NewLine, lines);
    }
}
