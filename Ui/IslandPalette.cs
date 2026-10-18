using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace BluetoothBattery.Ui;

/// <summary>
/// 岛内配色。所有视图共用一套「共享画刷」：岛体主题一变只改这些画刷的 Color，
/// 用它们的元素当场跟着变 —— 不用重建视图，也不会留下「深色岛上的黑字」。
///
/// <para>
/// 注意 <see cref="IIslandTheme.IsLight"/> 说的是**岛体**明暗，不是系统主题：
/// Fluent 风格跟随系统，Apple 风格恒为深色。不要自己去读注册表。
/// </para>
/// </summary>
internal sealed class IslandPalette
{
    private readonly IIslandTheme _theme;

    public IslandPalette(IIslandTheme theme)
    {
        _theme = theme;
        Refresh();
    }

    /// <summary>主文字（设备名、标题）。</summary>
    public SolidColorBrush Text { get; } = new();

    /// <summary>次级文字（提示、状态）。</summary>
    public SolidColorBrush Muted { get; } = new();

    /// <summary>最淡的文字（地址、脚注）。</summary>
    public SolidColorBrush Faint { get; } = new();

    /// <summary>电量条外框 / 分隔线。</summary>
    public SolidColorBrush Outline { get; } = new();

    /// <summary>聚光卡里的分隔线。</summary>
    public SolidColorBrush Divider { get; } = new();

    /// <summary>聚光卡里小卡片的半透明底。</summary>
    public SolidColorBrush CardFill { get; } = new();

    /// <summary>电量充足（≥60%）。</summary>
    public SolidColorBrush BatteryHigh { get; } = new();

    /// <summary>电量偏低（20% ~ 59%）。</summary>
    public SolidColorBrush BatteryMid { get; } = new();

    /// <summary>电量告急（&lt;20%）。</summary>
    public SolidColorBrush BatteryLow { get; } = new();

    /// <summary>蓝牙品牌蓝（深色/浅色岛上都够亮）。</summary>
    public SolidColorBrush BluetoothBlue { get; } = new();

    public bool IsLight => _theme.IsLight;

    /// <summary>订阅主题变化。返回的 IDisposable 记得在视图销毁/插件停用时释放。</summary>
    public IDisposable Attach()
    {
        _theme.Changed += Refresh;
        return new ActionDisposable(() => _theme.Changed -= Refresh);
    }

    /// <summary>把当前电量映射到画刷。</summary>
    public SolidColorBrush ForBattery(int? percent) => percent switch
    {
        null => Faint,
        >= 50 => BatteryHigh,
        >= 20 => BatteryMid,
        _ => BatteryLow,
    };

    /// <summary>中性色：岛体深色时是白色系，浅色时是黑色系。</summary>
    public Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private void Refresh()
    {
        Text.Color = Neutral(255);
        Muted.Color = Neutral(205);
        Faint.Color = Neutral(145);
        Outline.Color = Neutral(115);
        Divider.Color = Neutral(28);
        CardFill.Color = Neutral(14);

        BatteryHigh.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 26, 150, 60)
            : Windows.UI.Color.FromArgb(255, 64, 209, 88);
        BatteryMid.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 190, 105, 0)
            : Windows.UI.Color.FromArgb(255, 255, 179, 64);
        BatteryLow.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 200, 40, 40)
            : Windows.UI.Color.FromArgb(255, 255, 99, 71);

        BluetoothBlue.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0, 103, 192)
            : Windows.UI.Color.FromArgb(255, 76, 194, 255);
    }
}
