using BluetoothBattery.Devices;
using Microsoft.UI.Xaml;
using WinIsland.Core;

namespace BluetoothBattery;

/// <summary>
/// 插件入口：后台定时读蓝牙设备电量和系统音量，把快照交给岛视图；
/// 点击岛体打开聚光卡看全部设备 / 音量；设置页可调刷新间隔、低电量提醒、是否显示音量。
///
/// 一个插件包有且只有一个 IIslandPlugin 实现，必须是 public sealed 且有公共无参构造函数。
/// </summary>
public sealed class BluetoothBatteryPlugin : IslandPluginBase
{
    private const int DefaultPriority = 60;
    private const int DefaultRefreshSeconds = 5;
    private const int DefaultLowThreshold = 20;

    /// <summary>刷新串行化：并发请求排队而不是丢弃（见 sdk-api §15.2）。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>已经提醒过低电量的设备（按蓝牙地址）；电量回升或断开后移出，下次可以再提醒。</summary>
    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);

    private BluetoothBatteryView? _view;
    private BluetoothBatterySpotlightView? _spotlightView;
    private IslandLiveContent? _liveContent;
    private bool _stopRequested;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private IReadOnlyList<BatteryDevice> _allDevices = Array.Empty<BatteryDevice>();
    private IReadOnlyList<BatteryDevice> _visibleDevices = Array.Empty<BatteryDevice>();
    private DateTimeOffset? _lastUpdated;
    private SystemVolume? _volume;
    private bool _volumeBusy;

    /// <summary>快照 / 设置变化（UI 线程触发）。设置页用它回填「当前检测到的设备」。</summary>
    internal event Action? StateChanged;

    internal string DisplayName => Manifest.Name;

    internal IReadOnlyList<BatteryDevice> Devices => _visibleDevices;

    internal DateTimeOffset? LastUpdated => _lastUpdated;

    /// <summary>最近一次读到的系统音量（设置页显示用）。</summary>
    internal SystemVolume? Volume => _volume;

    /// <summary>是否在界面上显示系统音量。</summary>
    internal bool ShowVolume => Settings.Get("showVolume", true);

    internal int RefreshSeconds =>
        Math.Clamp(Settings.Get("refreshSeconds", DefaultRefreshSeconds), 3, 120);

    protected override async Task OnInitializeAsync()
    {
        _stopRequested = false;
        _lastRefreshUtc = DateTime.MinValue;
        _allDevices = Array.Empty<BatteryDevice>();
        _visibleDevices = Array.Empty<BatteryDevice>();
        _lastUpdated = null;
        _volume = null;
        _lowNotified.Clear();

        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        _view = new BluetoothBatteryView(Manifest, Theme);
        _spotlightView = new BluetoothBatterySpotlightView(Manifest, Theme, () => RefreshNowAsync("聚光卡手动刷新"));
        _liveContent = BuildLiveContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE702",
            () => new BluetoothBatterySettingsPage(this), order: 120));

        // 设置项实时生效；设置页里的按钮点下去之前也会先提交输入框，这里只需响应变化
        Context.Register(Context.OnSettingsChanged("enabled", ApplyEnabledSetting));
        Context.Register(Context.OnSettingsChanged("refreshSeconds", OnRefreshIntervalChanged));
        Context.Register(Context.OnSettingsChanged("notifyLowBattery", OnNotifySettingChanged));
        Context.Register(Context.OnSettingsChanged("lowThreshold", OnNotifySettingChanged));
        Context.Register(Context.OnSettingsChanged("showVolume", OnShowVolumeChanged));

        UpdateContentVisibility();

        // 1 秒一拍：按设置间隔决定是否刷新设备电量；系统音量每秒刷新一次（变化要跟手）
        Context.CreateTimer(TimeSpan.FromSeconds(1), repeat: true, OnWatchdogTick);
        Context.CreateTimer(TimeSpan.FromSeconds(1), repeat: true, OnVolumeTick);

        // 先给岛一个空状态（避免加载时空白），再后台读第一份真实数据
        _view.Apply(_visibleDevices, null);
        await RefreshVolumeAsync();
        await RefreshNowAsync("插件启动");
    }

    protected override Task OnShutdownAsync()
    {
        _stopRequested = true;

        // 退订主题事件，避免旧视图被宿主主题广播一直挂着
        _view?.Detach();
        _spotlightView?.Detach();

        SetContent(null);
        return Task.CompletedTask;
    }

    // ---- 刷新 ----

    /// <summary>读一次蓝牙电量并刷新界面。内部串行化：并发的刷新请求排队，不会互相丢弃。</summary>
    internal async Task RefreshNowAsync(string reason)
    {
        if (_stopRequested) return;

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopRequested) return;

            var devices = await Task.Run(BluetoothBatteryReader.Read).ConfigureAwait(false);
            _lastRefreshUtc = DateTime.UtcNow;
            Context.RunOnUI(() => ApplySnapshot(devices, reason));
        }
        catch (Exception ex)
        {
            // 后台线程的异常必须自己兜住：抛进宿主未处理异常会累计停用计数
            Log.Error($"读取蓝牙电量失败（{reason}，已忽略）", ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void OnWatchdogTick()
    {
        if (_stopRequested) return;
        if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < RefreshSeconds) return;

        // 定时刷新是后台任务；内部已兜住异常，不 await 也不会让异常冒到宿主
        _ = RefreshNowAsync("定时刷新");
    }

    // ---- 系统音量 ----

    private void OnVolumeTick()
    {
        if (_stopRequested || _volumeBusy) return;
        _volumeBusy = true;
        _ = RefreshVolumeAsync();
    }

    /// <summary>读一次系统主音量并刷新界面。读取在后台线程，COM 不碰 UI 线程。</summary>
    private async Task RefreshVolumeAsync()
    {
        try
        {
            if (_stopRequested) return;

            var volume = await Task.Run(SystemVolumeReader.Read).ConfigureAwait(false);
            Context.RunOnUI(() =>
            {
                if (_stopRequested) return;

                _volume = volume;
                ApplyVolumeToViews();
                UpdateContentVisibility();
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统音量失败（已忽略）：{ex.Message}");
        }
        finally
        {
            _volumeBusy = false;
        }
    }

    private void ApplyVolumeToViews()
    {
        var volume = ShowVolume ? _volume : null;
        _view?.ApplyVolume(volume);
        _spotlightView?.ApplyVolume(volume);
    }

    /// <summary>设置页「立即刷新」用：马上重读一次系统音量。</summary>
    internal Task RefreshVolumeNowAsync() => RefreshVolumeAsync();

    private void OnShowVolumeChanged()
    {
        ApplyVolumeToViews();
        UpdateContentVisibility();
        RaiseStateChanged();
    }

    private bool HasVolumeContent => ShowVolume && _volume is { Available: true };

    private void ApplySnapshot(IReadOnlyList<BatteryDevice> devices, string reason)
    {
        if (_stopRequested) return;

        _allDevices = devices;
        _visibleDevices = FilterVisible(devices);
        _lastUpdated = DateTimeOffset.Now;

        _view?.Apply(_visibleDevices, ShowVolume ? _volume : null);
        _spotlightView?.Apply(_visibleDevices, ShowVolume ? _volume : null, _lastUpdated.Value, RefreshSeconds);
        UpdateContentVisibility();

        MaybeNotifyLowBattery(devices);

        Log.Debug($"刷新完成（{reason}）：共 {devices.Count} 台，显示 {_visibleDevices.Count} 台");
        RaiseStateChanged();
    }

    /// <summary>
    /// 只显示当前真正连接的设备。蓝牙设备断开后 Windows 会移除它的服务节点（电量也不缓存），
    /// 所以列表里不会留下「上次电量」的幽灵条目。
    /// </summary>
    private IReadOnlyList<BatteryDevice> FilterVisible(IReadOnlyList<BatteryDevice> devices) =>
        devices.Where(d => d.Connected).ToList();

    // ---- 低电量提醒 ----

    private void MaybeNotifyLowBattery(IReadOnlyList<BatteryDevice> devices)
    {
        if (!Settings.Get("notifyLowBattery", true))
        {
            _lowNotified.Clear();
            return;
        }

        var threshold = Math.Clamp(Settings.Get("lowThreshold", DefaultLowThreshold), 5, 50);
        var newlyLow = new List<BatteryDevice>();

        foreach (var device in devices)
        {
            if (!device.Connected || device.Percent is not int percent) continue;

            if (percent <= threshold)
            {
                if (_lowNotified.Add(device.Id)) newlyLow.Add(device);
            }
            else if (percent > threshold + 5)
            {
                // 电量回到阈值以上（留 5% 回差）才重新计数，避免在阈值附近反复弹
                _lowNotified.Remove(device.Id);
            }
        }

        // 断开 / 消失的设备也移出，下次重连时电量仍低可以再提醒一次
        var present = devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lowNotified.RemoveWhere(id => !present.Contains(id));

        if (newlyLow.Count == 0) return;

        var text = string.Join("、", newlyLow.Select(d => $"{d.Name} 剩余 {d.Percent}%"));
        Log.Info($"低电量提醒：{text}");
        Context.Island.ShowMessage(new IslandMessage
        {
            Title = "设备电量低",
            Text = text,
            Glyph = Manifest.IconGlyph ?? "\uE702",
            AccentColor = Windows.UI.Color.FromArgb(255, 255, 99, 71),
            Duration = TimeSpan.FromSeconds(6),
        });
    }

    /// <summary>设置页的「发一条测试提醒」：不管当前电量如何，弹一条和真实提醒同款的示例。</summary>
    internal void PreviewLowBatteryMessage()
    {
        var device = _visibleDevices.FirstOrDefault(d => d.Percent is not null) ?? _visibleDevices.FirstOrDefault();
        var text = device is null
            ? "示例：蓝牙耳机 剩余 15%"
            : $"{device.Name} 剩余 {device.Percent ?? 15}%（测试提醒）";

        Context.Island.ShowMessage(new IslandMessage
        {
            Title = "设备电量低",
            Text = text,
            Glyph = Manifest.IconGlyph ?? "\uE702",
            AccentColor = Windows.UI.Color.FromArgb(255, 255, 99, 71),
            Duration = TimeSpan.FromSeconds(6),
        });
    }

    // ---- 常驻内容 / 设置 ----

    private IslandLiveContent BuildLiveContent() => new()
    {
        Priority = DefaultPriority,
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Windows.UI.Color.FromArgb(255, 0, 122, 255),
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(250, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 160),
        OnTap = OpenSpotlight,
    };

    private void ApplyEnabledSetting()
    {
        UpdateContentVisibility();
        RaiseStateChanged();
    }

    /// <summary>
    /// 没有设备、也没有可显示的系统音量时，不占用岛（宿主「空闲时隐藏」才能真正闲下来）。
    /// 有设备电量或音量时才注册常驻内容。插件本身继续后台读取，设置页也一直可用。
    /// </summary>
    private void UpdateContentVisibility()
    {
        var show = Settings.Get("enabled", true) && (_visibleDevices.Count > 0 || HasVolumeContent);
        SetContent(show ? _liveContent : null);
    }

    private void OnRefreshIntervalChanged()
    {
        ApplyVolumeToViews();
        _spotlightView?.Apply(_visibleDevices, ShowVolume ? _volume : null,
            _lastUpdated ?? DateTimeOffset.Now, RefreshSeconds);
        RaiseStateChanged();
    }

    private void OnNotifySettingChanged()
    {
        if (!Settings.Get("notifyLowBattery", true)) _lowNotified.Clear();
    }

    private void OpenSpotlight()
    {
        if (_spotlightView is null) return;

        _spotlightView.Apply(_visibleDevices, ShowVolume ? _volume : null,
            _lastUpdated ?? DateTimeOffset.Now, RefreshSeconds);
        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlightView,
            Size = new Windows.Foundation.Size(720, 540),
            OnClosed = () => _spotlightView?.OnHostClosed(),
        });

        // 点开时顺手刷新一次，用户看到的是最新数据（内部已串行化，重复点也安全）
        _ = RefreshNowAsync("打开聚光卡");
        _ = RefreshVolumeAsync();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // 设置页已经卸掉之类的情况不能影响插件主流程
            Log.Warn($"设置页状态同步失败（已忽略）：{ex.Message}");
        }
    }

    // ---- 设置页使用的快捷读写 ----

    internal bool GetBool(string key, bool fallback) => Settings.Get(key, fallback);

    internal void SetBool(string key, bool value) => Settings.Set(key, value);

    internal int GetInt(string key, int fallback) => Settings.Get(key, fallback);

    internal void SetInt(string key, int value) => Settings.Set(key, value);
}
