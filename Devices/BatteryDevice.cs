namespace BluetoothBattery.Devices;

/// <summary>设备分类（只用于挑图标，不影响数据）。</summary>
internal enum BatteryDeviceCategory
{
    Generic,
    Headset,
    Speaker,
    Mouse,
    Keyboard,
    Controller,
    Watch,
    Phone,
}

/// <summary>
/// 一台无线设备及其电量快照。
///
/// <para>
/// 两个数据来源共用这个模型：
/// <list type="bullet">
/// <item>蓝牙设备：Windows PnP 设备属性 <c>DEVPKEY_Bluetooth_Battery</c>（与「设置 → 蓝牙」同一份数据）；</item>
/// <item>2.4G 无线键鼠：接收器厂商协议（如罗技 HID++）或 HID Battery Strength 上报，见 <c>WirelessBatteryReader</c>。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class BatteryDevice
{
    /// <summary>稳定唯一标识（"bt:MAC" / "hid:…"），用于去重、低电量提醒计数和界面 diff。</summary>
    public required string Id { get; init; }

    /// <summary>设备名（蓝牙设备已去掉「Hands-Free AG」「Avrcp 传输」这类服务节点后缀）。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 去掉品牌词的型号名（如「HECATE G4 S PRO」→「G4 S PRO」），小岛（任务栏胶囊）用这个；
    /// 展开明细和聚光卡仍用 <see cref="Name"/> 显示完整名称。
    /// </summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>小岛用的短名称：有型号名就用型号名，否则退回完整名称。</summary>
    public string ShortName => string.IsNullOrWhiteSpace(ModelName) ? Name : ModelName;

    /// <summary>电量百分比 0~100；null 表示这台设备没有上报电量（或系统里没有这份数据）。</summary>
    public int? Percent { get; init; }

    /// <summary>当前是否连接。</summary>
    public bool Connected { get; init; }

    /// <summary>"Classic"（经典蓝牙）/ "BLE"（低功耗蓝牙）/ "2.4G"（USB 接收器无线键鼠）。</summary>
    public string Transport { get; init; } = "Classic";

    /// <summary>界面上显示的补充信息（蓝牙地址 / 接收器名称等），可为空。</summary>
    public string? Detail { get; init; }

    public BatteryDeviceCategory Category { get; init; } = BatteryDeviceCategory.Generic;

    /// <summary>是否 2.4G USB 接收器设备（界面上给个「2.4G」小标签）。</summary>
    public bool Is24G => string.Equals(Transport, "2.4G", StringComparison.OrdinalIgnoreCase);

    /// <summary>排序权重：已连接优先，其中电量低的优先，没电量的垫底。</summary>
    public (int ConnectedRank, int BatteryRank, string Name) SortKey =>
        (Connected ? 0 : 1, Percent is int p ? p : int.MaxValue, Name);

    public override string ToString() =>
        $"{Name} [{Transport}] {(Percent is int p ? p + "%" : "—")} {(Connected ? "connected" : "offline")}";
}
