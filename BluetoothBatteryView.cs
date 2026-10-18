using BluetoothBattery.Devices;
using BluetoothBattery.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace BluetoothBattery;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载「小岛（型号 + 电量 + 系统音量）」和
/// 「大岛（逐台列出电量，最多 3 台）」。
///
/// 小岛只显示型号（不带品牌，任务栏空间小）；展开后的明细里显示完整设备名。
/// 系统音量常驻第二行（喇叭图标 + 音量条 + 百分比；静音时显示「静音」）。
///
/// 几条硬件规则（宿主 SDK 2.x）：
///   1. 所有元素（含只在展开态出现的）一开始就常驻可视树；
///   2. 隐藏用 Height = 0 + Opacity = 0，不要用 Visibility.Collapsed（无法过渡）；
///   3. 形态动画逐帧直接赋值，**不要用 Storyboard**（原因见 StartMorph 注释）；
///   4. 缓动与宿主一致：BackEase(EaseOut, Amplitude: 0.45)；
///   5. 配色跟着岛体主题走：颜色全在 <see cref="IslandPalette"/> 的共享画刷里。
/// </summary>
internal sealed class BluetoothBatteryView : UserControl, IMorphView
{
    private const double CompactIconSize = 16;
    private const double ExpandedIconSize = 22;
    private const double CompactTitleSize = 13;
    private const double ExpandedTitleSize = 15;
    private const double CompactPercentSize = 13;
    private const double ExpandedPercentSize = 15;
    private const double ExpandedDetailHeight = 104;
    private const double VolumeBarWidth = 38;
    private const double VolumeBarHeight = 10;
    private const int ExpandedRows = 3;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度，手感保持一致。</summary>
    private const double BackAmplitude = 0.45;

    private readonly IslandPalette _palette;
    private readonly IDisposable _themeSubscription;
    private readonly Grid _header;
    private readonly Grid _volumeRow;
    private readonly FontIcon _icon;
    private readonly TextBlock _headline;
    private readonly TextBlock _percent;
    private readonly FontIcon _volumeIcon;
    private readonly Border _volumeFill;
    private readonly TextBlock _volumeText;
    private readonly StackPanel _rows;
    private readonly TextBlock _hint;
    private readonly StackPanel _detail;
    private readonly RectangleGeometry _detailClip = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;                        // 0 = 紧凑，1 = 展开
    private bool _expanded;

    /// <summary>当前主设备：紧凑态显示型号（不带品牌），展开明细显示完整名称。</summary>
    private string _primaryFullName = string.Empty;
    private string _primaryModelName = string.Empty;
    private bool _hasPrimary;

    public BluetoothBatteryView(PluginManifest manifest, IIslandTheme theme)
    {
        _palette = new IslandPalette(theme);
        _themeSubscription = _palette.Attach();

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE702",
            FontSize = CompactIconSize,
            Foreground = _palette.BluetoothBlue,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _headline = new TextBlock
        {
            Text = manifest.Name,
            FontSize = CompactTitleSize,
            Foreground = _palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _percent = new TextBlock
        {
            FontSize = CompactPercentSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _palette.Faint,
            VerticalAlignment = VerticalAlignment.Center,
            Text = string.Empty,
        };

        _header = new Grid { ColumnSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.Children.Add(_icon);
        Grid.SetColumn(_headline, 1);
        _header.Children.Add(_headline);
        Grid.SetColumn(_percent, 2);
        _header.Children.Add(_percent);

        // ---- 系统音量行：喇叭图标 + 音量条 + 百分比 ----
        _volumeIcon = new FontIcon
        {
            Glyph = "\uE767",                        // Segoe Fluent Icons: Volume
            FontSize = 13,
            Foreground = _palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
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
            FontSize = 11.5,
            Foreground = _palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 30,
            TextAlignment = TextAlignment.Right,
            Text = string.Empty,
        };

        _volumeRow = new Grid { ColumnSpacing = 5, VerticalAlignment = VerticalAlignment.Center };
        _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _volumeRow.Children.Add(_volumeIcon);
        Grid.SetColumn(volumeTrack, 1);
        _volumeRow.Children.Add(volumeTrack);
        Grid.SetColumn(_volumeText, 2);
        _volumeRow.Children.Add(_volumeText);
        HideVolumeRow();

        _rows = new StackPanel { Spacing = 2 };

        _hint = new TextBlock
        {
            FontSize = 11,
            Foreground = _palette.Faint,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 展开态才显示的内容：Height/Opacity 归零藏起来（元素仍常驻可视树）
        _detail = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = 4,
            Padding = new Thickness(0, 4, 0, 0),
            Children = { _rows, _hint },
            Clip = _detailClip,
        };
        _detail.SizeChanged += (_, _) => UpdateDetailClip();

        var root = new StackPanel
        {
            Spacing = 3,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _header, _volumeRow, _detail },
        };

        Content = root;

        // 逐帧动画用的 UI 线程定时器；拿不到就退化成「直接切终态」，绝不留下中间值
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>把设备快照铺到界面上（UI 线程调用）。设备已由服务层排好序（连接优先、电量低优先）。</summary>
    internal void Apply(IReadOnlyList<BatteryDevice> devices, SystemVolume? volume)
    {
        var connected = devices.Where(d => d.Connected).ToList();
        var primary = connected.Count > 0 ? connected[0] : devices.Count > 0 ? devices[0] : null;

        UpdateVolumeRow(volume);

        if (primary is null)
        {
            _hasPrimary = false;
            _header.Height = 0;
            _header.Opacity = 0;
            _headline.Text = "未连接蓝牙设备";
            _percent.Text = string.Empty;
            _primaryFullName = string.Empty;
            _primaryModelName = string.Empty;
            _rows.Children.Clear();
            _hint.Text = "没有已连接的蓝牙设备 · 打开耳机 / 键鼠后显示电量";
            return;
        }

        _hasPrimary = true;
        _header.Height = double.NaN;
        _header.Opacity = 1;
        _icon.Foreground = _palette.BluetoothBlue;
        _primaryFullName = primary.Name;
        _primaryModelName = primary.ShortName;
        // 任务栏小胶囊只显示型号；展开后的明细显示品牌 + 型号
        _headline.Text = _expanded ? _primaryFullName : _primaryModelName;
        _percent.Text = primary.Percent is int value ? $"{value}%" : "—";
        _percent.Foreground = _palette.ForBattery(primary.Percent);

        _rows.Children.Clear();
        var shown = Math.Min(ExpandedRows, devices.Count);
        for (var i = 0; i < shown; i++)
        {
            _rows.Children.Add(new BatteryRow(devices[i], _palette, dense: true));
        }

        _hint.Text = devices.Count > ExpandedRows
            ? $"共 {devices.Count} 台 · 点岛体查看全部"
            : "点岛体查看全部 · 自动刷新";
    }

    /// <summary>只更新系统音量行（音量定时器每秒调用，不重建设备列表）。</summary>
    internal void ApplyVolume(SystemVolume? volume) => UpdateVolumeRow(volume);

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    /// <summary>插件停用 / 视图销毁时退订主题事件，避免旧视图被主题事件一直挂着。</summary>
    internal void Detach() => _themeSubscription.Dispose();

    private void UpdateVolumeRow(SystemVolume? volume)
    {
        if (volume is not { Available: true } value)
        {
            HideVolumeRow();
            return;
        }

        _volumeRow.Height = double.NaN;
        _volumeRow.Opacity = 1;
        _volumeIcon.Glyph = value.Muted ? "\uE74F" : "\uE767";   // Mute / Volume
        _volumeText.Text = value.Muted ? "静音" : $"{value.Percent}%";
        _volumeText.Foreground = value.Muted ? _palette.Faint : _palette.Muted;
        _volumeFill.Background = value.Muted ? _palette.Faint : _palette.Muted;

        var inner = VolumeBarWidth - 4;
        _volumeFill.Width = value.Muted ? 2 : Math.Max(2, inner * Math.Clamp(value.Percent, 0, 100) / 100d);
    }

    private void HideVolumeRow()
    {
        _volumeRow.Height = 0;
        _volumeRow.Opacity = 0;
    }

    private void StartMorph(double target, TimeSpan duration)
    {
        // 紧凑 → 展开时把标题换成完整名称（含品牌），收回去时换回型号
        _expanded = target >= 1;
        if (_hasPrimary)
        {
            _headline.Text = _expanded ? _primaryFullName : _primaryModelName;
        }

        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);   // 收尾时锁定终态，避免残留中间值
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与 XAML 那条曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    /// <summary>把 0~1 的形态进度铺到各元素上。progress 会因 BackEase 略微越过 0/1，记得夹住。</summary>
    private void ApplyMorph(double progress)
    {
        _progress = progress;

        // Height 不能为负（BackEase 在终点附近会把曲线推到目标值以下）
        _detail.Height = Math.Max(0, ExpandedDetailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        _icon.FontSize = Math.Max(1, CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress);
        _headline.FontSize = Math.Max(1, CompactTitleSize + (ExpandedTitleSize - CompactTitleSize) * progress);
        _percent.FontSize = Math.Max(1, CompactPercentSize + (ExpandedPercentSize - CompactPercentSize) * progress);
    }

    private void UpdateDetailClip()
    {
        _detailClip.Rect = new Windows.Foundation.Rect(0, 0,
            Math.Max(0, _detail.ActualWidth), Math.Max(0, _detail.ActualHeight));
    }
}
