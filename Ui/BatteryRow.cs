using BluetoothBattery.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BluetoothBattery.Ui;

/// <summary>
/// 一台设备一行：分类图标 + 设备名 + 电量条 + 百分比（+ 未连接标签）。
/// 颜色全部用 <see cref="IslandPalette"/> 里的共享画刷，主题切换时自动跟着变。
/// </summary>
internal sealed class BatteryRow : Grid
{
    public BatteryRow(BatteryDevice device, IslandPalette palette, bool dense)
    {
        ColumnSpacing = dense ? 8 : 10;
        VerticalAlignment = VerticalAlignment.Center;
        Height = dense ? 26 : 34;

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = GlyphFor(device.Category),
            FontSize = dense ? 13 : 15,
            Foreground = device.Connected ? palette.Muted : palette.Faint,
            VerticalAlignment = VerticalAlignment.Center,
            Width = dense ? 18 : 20,
        };
        Children.Add(icon);

        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = dense ? 12.5 : 13.5,
            Foreground = palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = device.Connected ? 1 : 0.65,
        };
        SetColumn(name, 1);
        Children.Add(name);

        var cluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = dense ? 6 : 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!device.Connected)
        {
            cluster.Children.Add(new TextBlock
            {
                Text = "未连接",
                FontSize = dense ? 10 : 10.5,
                Foreground = palette.Faint,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // 2.4G 键鼠给一个小标签，和蓝牙设备区分开
        if (device.Is24G)
        {
            cluster.Children.Add(new TextBlock
            {
                Text = "2.4G",
                FontSize = dense ? 9.5 : 10,
                Foreground = palette.Faint,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        cluster.Children.Add(BuildBatteryBar(device.Percent, palette, dense));

        cluster.Children.Add(new TextBlock
        {
            Text = device.Percent is int percent ? $"{percent}%" : "—",
            FontSize = dense ? 12 : 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = palette.ForBattery(device.Percent),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = dense ? 34 : 38,
            TextAlignment = TextAlignment.Right,
        });

        SetColumn(cluster, 2);
        Children.Add(cluster);
    }

    /// <summary>画一个小电量条：外框 + 按百分比填充的内条（不依赖 ProgressBar 的模板）。</summary>
    internal static Grid BuildBatteryBar(int? percent, IslandPalette palette, bool dense)
    {
        var width = dense ? 38d : 52d;
        var height = dense ? 12d : 14d;
        var inset = 2d;

        var fillWidth = percent is int value
            ? Math.Max(0, (width - inset * 2) * Math.Clamp(value, 0, 100) / 100d)
            : 0;

        var fill = new Border
        {
            Width = fillWidth,
            Height = Math.Max(2, height - inset * 2),
            CornerRadius = new CornerRadius(1.5),
            Background = palette.ForBattery(percent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(inset, 0, inset, 0),
        };

        var track = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        track.Children.Add(fill);

        return new Grid
        {
            Width = width,
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = palette.Outline,
                },
                track,
            },
        };
    }

    /// <summary>分类图标只用样本里验证过的 Segoe 字形（耳机 E7F6 / 手柄 E7FC / 其它 E88E）。</summary>
    private static string GlyphFor(BatteryDeviceCategory category) => category switch
    {
        BatteryDeviceCategory.Headset or BatteryDeviceCategory.Speaker => "\uE7F6",
        BatteryDeviceCategory.Controller => "\uE7FC",
        _ => "\uE88E",
    };
}
