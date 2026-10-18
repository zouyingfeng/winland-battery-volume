using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BluetoothBattery.Devices;

/// <summary>
/// 从 Windows PnP 设备属性里读蓝牙设备电量 —— 和「设置 → 蓝牙」显示的是同一份数据：
/// 设备通过 HFP / HID 等协议主动上报，Windows 缓存在设备节点属性里。
///
/// <para>
/// 属性集 <c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5}</c>（DEVPKEY_Bluetooth_*）的
/// pid 2 是电量百分比（实测 <c>DEVPROP_TYPE_BYTE</c>，0~100）。电量通常挂在设备的
/// **服务节点**上（经典蓝牙耳机是 <c>BTHENUM\{{0000111E-…}}</c> 的 Hands-Free AG 节点，
/// BLE 设备是 <c>BTHLEDEVICE\…</c>），而设备名在 <c>BTHENUM\DEV_&lt;地址&gt;</c> 这个
/// 主节点上，所以这里按「设备实例 ID 里的 12 位蓝牙地址」把两者对上，再读电量。
/// </para>
///
/// <para>
/// 为什么不用 WinRT 的 GATT：设备未连接时 <c>BluetoothLEDevice.FromIdAsync</c> 直接返回
/// null；而且只有支持标准 0x180F Battery Service 的设备才读得到。PnP 属性这条路更短更稳，
/// 覆盖经典蓝牙的 HFP 电量上报，也不需要任何第三方依赖。任意线程可调用（只读、无状态）。
/// </para>
/// </summary>
internal static class BluetoothBatteryReader
{
    /// <summary><c>DEVPKEY_Bluetooth_*</c> 属性集。</summary>
    private static readonly Guid BluetoothPropertySet = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

    /// <summary>pid 2 = 电量百分比（0~100）。</summary>
    private const uint BatteryPercentPid = 2;

    /// <summary><c>DEVPKEY_Device_FriendlyName</c>（取自 <c>{A45C254E-DF1C-4EFD-8020-67D146A850E0}</c> pid 14）。</summary>
    private static readonly Guid DevicePropertySet = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint FriendlyNamePid = 14;

    // CfgMgr32 常量（实测验证过）
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const int CrBufferSmall = 0x1A;
    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;
    private const uint DnDeviceDisconnected = 0x10000000;

    /// <summary>12 位十六进制蓝牙地址（先剥掉 ID 里的 GUID，避免把 GUID 尾段误当地址）。</summary>
    private static readonly Regex AddressPattern =
        new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.Compiled);

    private static readonly Regex GuidPattern =
        new(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);

    /// <summary>本地化服务节点后缀：没有主设备节点可读名字时用它把名字擦干净。</summary>
    private static readonly Regex ServiceSuffixPattern = new(
        @"\s*(?:Hands-Free\s+AG|Hands-Free|Audio\s+Gateway|Avrcp\s*(?:传输|Transport)?|A2DP\s*(?:Sink|Source)?|HFP(?:\s*AG)?|HID\s*(?:Device)?|Audio\s*Sink|音频接收器|免提)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>名字 → 分类的粗关键字表（只为选图标，不参与电量判断）。</summary>
    private static readonly (string Keyword, BatteryDeviceCategory Category)[] CategoryHints =
    {
        ("headset", BatteryDeviceCategory.Headset),
        ("headphone", BatteryDeviceCategory.Headset),
        ("earbud", BatteryDeviceCategory.Headset),
        ("earphone", BatteryDeviceCategory.Headset),
        ("buds", BatteryDeviceCategory.Headset),
        ("airpods", BatteryDeviceCategory.Headset),
        ("耳机", BatteryDeviceCategory.Headset),
        ("耳麦", BatteryDeviceCategory.Headset),
        ("speaker", BatteryDeviceCategory.Speaker),
        ("soundbar", BatteryDeviceCategory.Speaker),
        ("音箱", BatteryDeviceCategory.Speaker),
        ("扬声器", BatteryDeviceCategory.Speaker),
        ("mouse", BatteryDeviceCategory.Mouse),
        ("鼠标", BatteryDeviceCategory.Mouse),
        ("keyboard", BatteryDeviceCategory.Keyboard),
        ("键盘", BatteryDeviceCategory.Keyboard),
        ("controller", BatteryDeviceCategory.Controller),
        ("gamepad", BatteryDeviceCategory.Controller),
        ("手柄", BatteryDeviceCategory.Controller),
        ("watch", BatteryDeviceCategory.Watch),
        ("手表", BatteryDeviceCategory.Watch),
        ("phone", BatteryDeviceCategory.Phone),
        ("手机", BatteryDeviceCategory.Phone),
    };

    /// <summary>服务节点里出现这些经典蓝牙音频服务 GUID，就可以判定「这是一副耳机」。</summary>
    private static readonly string[] HeadsetServiceHints =
    {
        "0000111E", // Handsfree（Hands-Free AG 在这里上报电量）
        "0000111F", // Handsfree Audio Gateway
        "00001108", // Headset
        "0000110B", // Audio Sink（A2DP）
        "0000110C", // A/V Remote Control
        "0000110E", // A/V Remote Control Target
    };

    /// <summary>
    /// 扫一遍 PnP 设备树，返回所有能识别出蓝牙地址的**在场**设备（已连接优先、电量可用者在前）。
    /// 只读、无状态；任何一步失败都只跳过该项，不会抛异常给调用方。
    /// </summary>
    internal static List<BatteryDevice> Read()
    {
        try
        {
            return ReadCore();
        }
        catch
        {
            // 设备被拔出 / PnP 服务忙等场景：宁可这一拍没有数据，也不把异常抛给界面
            return new List<BatteryDevice>();
        }
    }

    private static List<BatteryDevice> ReadCore()
    {
        var ids = ListPresentDeviceInstanceIds();
        var groups = new Dictionary<string, DeviceNodeGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids)
        {
            if (!id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;

            var address = ExtractBluetoothAddress(id);
            if (address is null) continue;

            if (!TryLocatePresent(id, out var devnode)) continue;

            if (!groups.TryGetValue(address, out var group))
            {
                group = new DeviceNodeGroup(address);
                groups.Add(address, group);
            }

            group.Nodes.Add(new DeviceNode(id, devnode));
            if (IsPrimaryDeviceNode(id)) group.HasPrimaryNode = true;
            if (id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)) group.IsLowEnergy = true;
            if (IsStartedAndHealthy(devnode)) group.Connected = true;

            // 电量：同一台设备可能挂在多个服务节点上，读到一个即可
            if (group.Percent is null
                && TryReadProperty(devnode, BluetoothPropertySet, BatteryPercentPid, out var type, out var bytes)
                && TryConvertPercent(bytes, type, out var percent))
            {
                group.Percent = percent;
            }
        }

        var result = new List<BatteryDevice>(groups.Count);
        foreach (var group in groups.Values)
        {
            // 000000000000 是「本地串口」这类服务节点造出来的假地址，不是真实蓝牙设备
            if (IsPlaceholderAddress(group.Address)) continue;

            // 没有电量、也没有主设备节点的条目基本是服务节点（如蓝牙串口），不当作设备列出
            if (group.Percent is null && !group.HasPrimaryNode) continue;

            var name = ResolveName(group);
            result.Add(new BatteryDevice
            {
                Id = $"bt:{group.Address}",
                Detail = FormatAddress(group.Address),
                Name = name,
                ModelName = DeviceNameFormatter.ToModelName(name),
                Percent = group.Percent,
                Connected = group.Connected,
                Transport = group.IsLowEnergy ? "BLE" : "Classic",
                Category = GuessCategory(name, group),
            });
        }

        result.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
        return result;
    }

    /// <summary>选一个像样的设备名：优先主设备节点（<c>BTHENUM\DEV_…</c>）的友好名。</summary>
    private static string ResolveName(DeviceNodeGroup group)
    {
        // 1) 主设备节点（BTHENUM\DEV_<MAC> / BTHLE\DEV_<MAC>）的友好名：这是 Windows 设置里显示的名字
        foreach (var node in group.Nodes.Where(n => IsPrimaryDeviceNode(n.InstanceId)))
        {
            var candidate = ReadFriendlyName(node.DevNode);
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        }

        // 2) 任意节点的友好名：服务节点的名字带后缀，擦掉即可
        foreach (var node in group.Nodes)
        {
            var candidate = ReadFriendlyName(node.DevNode);
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            candidate = CleanServiceSuffix(candidate.Trim());
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }

        // 3) 实在没有名字：用地址尾号，至少还能在界面上区分
        return $"蓝牙设备 {group.Address[^4..]}";
    }

    private static string CleanServiceSuffix(string name)
    {
        var cleaned = ServiceSuffixPattern.Replace(name, string.Empty).Trim();
        return cleaned.Length == 0 ? name : cleaned;
    }

    private static BatteryDeviceCategory GuessCategory(string name, DeviceNodeGroup group)
    {
        foreach (var (keyword, category) in CategoryHints)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return category;
        }

        // 名字里没有线索时，看设备树里有没有经典蓝牙音频服务节点（耳机/音箱都会挂这些服务）
        foreach (var node in group.Nodes)
        {
            foreach (var hint in HeadsetServiceHints)
            {
                if (node.InstanceId.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return BatteryDeviceCategory.Headset;
                }
            }
        }

        return BatteryDeviceCategory.Generic;
    }

    /// <summary>全 0 / 全 F 的「地址」是服务节点伪造的，不是真实蓝牙设备。</summary>
    private static bool IsPlaceholderAddress(string address)
    {
        if (address.Length != 12) return true;

        var allZero = true;
        var allF = true;
        foreach (var c in address)
        {
            if (c != '0') allZero = false;
            if (c != 'F') allF = false;
        }

        return allZero || allF;
    }

    // ---- 设备实例 ID 解析 ----

    /// <summary>
    /// 从设备实例 ID 里抠出 12 位蓝牙地址：先把 <c>{{GUID}}</c> 段剥掉（GUID 尾段天然是
    /// 12 位十六进制，会误判），再找一段独立的 12 位十六进制。
    /// 经典蓝牙节点 <c>BTHENUM\DEV_80BF21CA057B\…</c>、服务节点
    /// <c>BTHENUM\{{0000111E-…}}_VID&amp;…\8&amp;…&amp;80BF21CA057B_C00000000</c> 都能命中。
    /// </summary>
    internal static string? ExtractBluetoothAddress(string? deviceInstanceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId)) return null;

        var sanitized = GuidPattern.Replace(deviceInstanceId, " ");
        var match = AddressPattern.Match(sanitized);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>把 12 位地址格式化成 <c>52:59:20:00:80:AF</c>，给界面显示用。</summary>
    internal static string FormatAddress(string address) =>
        address.Length == 12
            ? $"{address[..2]}:{address[2..4]}:{address[4..6]}:{address[6..8]}:{address[8..10]}:{address[10..]}"
            : address;

    /// <summary>主设备节点 = 实例 ID 里带 <c>\DEV_&lt;地址&gt;</c> 的那种（BTHENUM / BTHLE 都适用）。</summary>
    private static bool IsPrimaryDeviceNode(string instanceId) =>
        instanceId.Contains(@"\DEV_", StringComparison.OrdinalIgnoreCase);

    private static List<string> ListPresentDeviceInstanceIds()
    {
        var result = new List<string>(512);

        var code = NativeMethods.CM_Get_Device_ID_List_SizeW(out var length, null, CmGetIdListFilterPresent);
        if (code != 0 || length == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
        try
        {
            if (NativeMethods.CM_Get_Device_ID_ListW(null, buffer, length, CmGetIdListFilterPresent) != 0) return result;

            // 这是「一串字符串 + 双 null 结尾」，必须按已知长度读：
            // PtrToStringUni(ptr) 会在第一个 '\0' 处截断，只能拿到第一个设备。
            var text = Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
            foreach (var id in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(id);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static bool TryLocatePresent(string instanceId, out uint devnode) =>
        NativeMethods.CM_Locate_DevNodeW(out devnode, instanceId, 0) == 0;

    /// <summary>present + started + 没有 problem 码 + 未被标记为断开，才算「真的连着」。</summary>
    private static bool IsStartedAndHealthy(uint devnode)
    {
        if (NativeMethods.CM_Get_DevNode_Status(out var status, out var problem, devnode, 0) != 0) return false;

        return (status & DnStarted) != 0
               && (status & DnHasProblem) == 0
               && (status & DnDeviceDisconnected) == 0
               && problem == 0;
    }

    private static string? ReadFriendlyName(uint devnode)
    {
        if (!TryReadProperty(devnode, DevicePropertySet, FriendlyNamePid, out var type, out var bytes)) return null;

        // DEVPROP_TYPE_STRING = 0x12（UTF-16，含结尾 '\0'）
        if (type != 0x12 || bytes.Length < 2) return null;

        var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0', '\uFEFF').Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool TryReadProperty(uint devnode, Guid propertySet, uint pid, out uint type, out byte[] bytes)
    {
        type = 0;
        bytes = Array.Empty<byte>();
        var key = new NativeMethods.DevPropKey { FormatId = propertySet, Pid = pid };
        uint size = 0;

        // 先用 NULL 缓冲探大小：系统正常返回 CR_BUFFER_SMALL 并在 size 里填所需字节数，不是失败
        var code = NativeMethods.CM_Get_DevNode_PropertyW(devnode, ref key, out type, IntPtr.Zero, ref size, 0);
        if ((code != 0 && code != CrBufferSmall) || size == 0 || size > 4096) return false;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.CM_Get_DevNode_PropertyW(devnode, ref key, out type, buffer, ref size, 0) != 0) return false;

            bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, (int)size);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>电量属性统一成 0~100；越界/无法解析一律当「读不到」（不把脏值糊到界面上）。</summary>
    private static bool TryConvertPercent(byte[] bytes, uint type, out int percent)
    {
        percent = 0;
        if (bytes.Length == 0) return false;

        var value = type switch
        {
            0x02 when bytes.Length >= 1 => (int)(sbyte)bytes[0],      // DEVPROP_TYPE_SBYTE
            0x03 when bytes.Length >= 1 => bytes[0],                  // DEVPROP_TYPE_BYTE（实测是它）
            0x04 when bytes.Length >= 2 => BitConverter.ToInt16(bytes, 0),
            0x05 when bytes.Length >= 2 => BitConverter.ToUInt16(bytes, 0),
            0x06 when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
            0x07 when bytes.Length >= 4 => (int)BitConverter.ToUInt32(bytes, 0),
            0x12 => int.TryParse(Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim(), out var parsed) ? parsed : -1,
            _ => -1,
        };

        if (value is < 0 or > 100) return false;

        percent = value;
        return true;
    }

    // ---- 内部小数据类 ----

    private sealed class DeviceNode
    {
        public DeviceNode(string instanceId, uint devnode)
        {
            InstanceId = instanceId;
            DevNode = devnode;
        }

        public string InstanceId { get; }
        public uint DevNode { get; }
    }

    private sealed class DeviceNodeGroup
    {
        public DeviceNodeGroup(string address) => Address = address;

        public string Address { get; }
        public List<DeviceNode> Nodes { get; } = new();
        public int? Percent { get; set; }
        public bool Connected { get; set; }
        public bool IsLowEnergy { get; set; }

        /// <summary>是否存在主设备节点（<c>BTHENUM\DEV_…</c>）—— 只有真实设备才有。</summary>
        public bool HasPrimaryNode { get; set; }
    }

    /// <summary>CfgMgr32 导入。都只读，不会持有任何句柄。</summary>
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DevPropKey
        {
            public Guid FormatId;
            public uint Pid;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_ID_ListW(string? filter, IntPtr buffer, uint bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Locate_DevNodeW(out uint devnode, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devnode, uint flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_DevNode_PropertyW(uint devnode, ref DevPropKey propertyKey,
            out uint propertyType, IntPtr propertyBuffer, ref uint propertyBufferSize, uint flags);
    }
}
