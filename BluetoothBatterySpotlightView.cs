using System.Diagnostics;
using BluetoothBattery.Devices;
using BluetoothBattery.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace BluetoothBattery;

/// <summary>
/// 「超级展开」聚光卡：小岛/大岛放不下全部设备时，点岛体在这里看完整列表。
/// 顶部显示系统音量（默认播放设备），下面是蓝牙设备电量列表。
/// 飞入飞回、遮罩、圆角、层级全由宿主负责，这里只给内容树。
///
/// 必须是**独立于岛视图的另一棵树**（每个窗口一棵树），所以实例单独创建。
/// </summary>
internal sealed class BluetoothBatterySpotlightView : UserControl
{
    private const double VolumeBarWidth = 160;
    private const double VolumeBarHeight = 12;

    private readonly IslandPalette _palette;
    private readonly IDisposable _themeSubscription;
    private readonly Func<Task> _onRefresh;
    private readonly TextBlock _summary;
    private readonly StackPanel _list;
    private readonly Border _volumePanel;
    private readonly FontIcon _volumeIcon;
    private readonly Border _volumeFill;
    private readonly TextBlock _volumeText;
    private readonly Button _refreshButton;
    private readonly TextBlock _footer;

    private DateTimeOffset? _lastUpdated;
    private int _refreshSeconds = 5;
    private bool _refreshing;

    public BluetoothBatterySpotlightView(PluginManifest manifest, IIslandTheme theme, Func<Task> onRefresh)
    {
        _palette = new IslandPalette(theme);
        _themeSubscription = _palette.Attach();
        _onRefresh = onRefresh;

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _palette.Text,
        };

        _summary = new TextBlock
        {
            FontSize = 13,
            Foreground = _palette.Muted,
            Text = "正在读取设备…",
        };

        // ---- 系统音量卡片 ----
        _volumeIcon = new FontIcon
        {
            Glyph = "\uE767",
            FontSize = 20,
            Foreground = _palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 26,
        };

        _volumeFill = new Border
        {
            Height = VolumeBarHeight - 4,
            CornerRadius = new CornerRadius(1.5),
            Background = _palette.Muted,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };

        var volumeTrack = new Grid
        {
            Width = VolumeBarWidth,
            Height = VolumeBarHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = _palette.Outline,
                },
                _volumeFill,
            },
        };

        _volumeText = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 56,
            TextAlignment = TextAlignment.Right,
            Text = string.Empty,
        };

        var volumeGrid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        volumeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volumeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeGrid.Children.Add(_volumeIcon);
        var volumeLabel = new TextBlock
        {
            Text = "系统音量",
            FontSize = 14,
            Foreground = _palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(volumeLabel, 1);
        volumeGrid.Children.Add(volumeLabel);
        Grid.SetColumn(volumeTrack, 2);
        volumeGrid.Children.Add(volumeTrack);
        Grid.SetColumn(_volumeText, 3);
        volumeGrid.Children.Add(_volumeText);

        _volumePanel = new Border
        {
            Background = _palette.CardFill,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = volumeGrid,
            Visibility = Visibility.Collapsed,
        };

        _list = new StackPanel { Spacing = 2 };

        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _refreshButton = new Button { Content = "立即刷新" };
        _refreshButton.Click += async (_, _) => await RefreshAsync();

        var settingsButton = new Button { Content = "打开声音设置" };
        settingsButton.Click += (_, _) => OpenSoundSettings();

        var bluetoothButton = new Button { Content = "打开蓝牙设置" };
        bluetoothButton.Click += (_, _) => OpenBluetoothSettings();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _refreshButton, settingsButton, bluetoothButton },
        };

        _footer = new TextBlock
        {
            FontSize = 11,
            Foreground = _palette.Faint,
            TextWrapping = TextWrapping.Wrap,
            Text = "系统音量来自 Windows 默认播放设备；设备电量来自蓝牙设备上报（与「设置 → 蓝牙」同一份数据），"
                 + "部分设备不提供电量时显示「—」。",
        };

        var divider = new Border { Height = 1, Background = _palette.Divider };

        var root = new Grid
        {
            Padding = new Thickness(28, 24, 28, 22),
            RowSpacing = 14,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 4, Children = { title, _summary } };
        root.Children.Add(header);

        Grid.SetRow(_volumePanel, 1);
        root.Children.Add(_volumePanel);

        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        Grid.SetRow(divider, 3);
        root.Children.Add(divider);

        var bottom = new StackPanel { Spacing = 10, Children = { actions, _footer } };
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);

        Content = root;
    }

    /// <summary>刷新卡片内容（UI 线程调用）。卡片收起后仍然可以继续调用，没有副作用。</summary>
    internal void Apply(IReadOnlyList<BatteryDevice> devices, SystemVolume? volume, DateTimeOffset updatedAt, int refreshSeconds)
    {
        _lastUpdated = updatedAt;
        _refreshSeconds = Math.Max(3, refreshSeconds);
        UpdateVolume(volume);

        _list.Children.Clear();
        if (devices.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "当前没有已连接的蓝牙设备。",
                FontSize = 14,
                Foreground = _palette.Muted,
                Margin = new Thickness(0, 8, 0, 0),
            });
            _list.Children.Add(new TextBlock
            {
                Text = "打开蓝牙耳机 / 鼠标 / 键盘后，这里会自动列出它们的电量。",
                FontSize = 12,
                Foreground = _palette.Faint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        else
        {
            foreach (var device in devices)
            {
                _list.Children.Add(BatteryRowFor(device));
            }
        }

        UpdateSummary(devices.Count);
        _refreshButton.Content = "立即刷新";
        _refreshButton.IsEnabled = true;
        _refreshing = false;
    }

    /// <summary>只更新音量卡片（音量定时器每秒调用）。</summary>
    internal void ApplyVolume(SystemVolume? volume) => UpdateVolume(volume);

    /// <summary>宿主收起卡片时的收尾（这里没有定时器 / 请求要取消，留作扩展点）。</summary>
    internal void OnHostClosed()
    {
    }

    /// <summary>插件停用 / 视图销毁时退订主题事件。</summary>
    internal void Detach() => _themeSubscription.Dispose();

    private void UpdateVolume(SystemVolume? volume)
    {
        if (volume is not { Available: true } value)
        {
            _volumePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _volumePanel.Visibility = Visibility.Visible;
        _volumeIcon.Glyph = value.Muted ? "\uE74F" : "\uE767";
        _volumeText.Text = value.Muted ? "静音" : $"{value.Percent}%";
        _volumeText.Foreground = value.Muted ? _palette.Muted : _palette.Text;
        _volumeFill.Background = value.Muted ? _palette.Faint : _palette.Muted;
        _volumeFill.Width = value.Muted
            ? 2
            : Math.Max(2, (VolumeBarWidth - 4) * Math.Clamp(value.Percent, 0, 100) / 100d);
    }

    private UIElement BatteryRowFor(BatteryDevice device)
    {
        var row = new BatteryRow(device, _palette, dense: false);
        var detail = new TextBlock
        {
            Text = device.Detail ?? string.Empty,
            FontSize = 10.5,
            Foreground = _palette.Faint,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        // 只在聚光卡里显示地址 / 接收器信息，方便区分同型号设备
        var wrapper = new Grid { ColumnSpacing = 8 };
        wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrapper.Children.Add(row);
        Grid.SetColumn(detail, 1);
        wrapper.Children.Add(detail);
        return wrapper;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;

        _refreshing = true;
        _refreshButton.IsEnabled = false;
        _refreshButton.Content = "刷新中…";
        _summary.Text = "正在读取设备…";

        try
        {
            await _onRefresh();
        }
        catch
        {
            _summary.Text = "刷新失败，稍后再试";
        }
        finally
        {
            _refreshing = false;
            _refreshButton.IsEnabled = true;
            _refreshButton.Content = "立即刷新";
        }
    }

    private void UpdateSummary(int deviceCount)
    {
        var updated = _lastUpdated is { } time
            ? $"最后更新 {time:HH:mm:ss} · 每 {_refreshSeconds} 秒自动刷新"
            : $"每 {_refreshSeconds} 秒自动刷新";

        _summary.Text = deviceCount > 0
            ? $"当前 {deviceCount} 台无线设备 · {updated}"
            : $"没有已连接的无线设备 · {updated}";
    }

    private static void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch
        {
            // 打不开系统设置不算插件错误（被系统策略拦掉之类），静默忽略
        }
    }

    private static void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
