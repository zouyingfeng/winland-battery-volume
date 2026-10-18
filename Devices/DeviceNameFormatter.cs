namespace BluetoothBattery.Devices;

/// <summary>
/// 把设备名里的品牌词去掉，只留型号 —— 小岛（任务栏里的胶囊）空间很小，
/// 显示「G4 S PRO」比「HECATE G4 S PRO」更有用；展开后的明细仍显示完整名称。
///
/// <para>
/// Windows 的 PnP 属性里没有可用的品牌字段（蓝牙设备节点的 Manufacturer 是
/// “Microsoft”，那是驱动厂商，不是硬件品牌），所以这里用**已知品牌词表**做前缀剥离：
/// 名字以品牌词开头就删掉它（可连续多个，如 “EDIFIER HECATE G4 S PRO”）。
/// 删空了就保留原名；品牌不在表里也保留原名 —— 宁可多显示，不会把型号删掉。
/// </para>
/// </summary>
internal static class DeviceNameFormatter
{
    /// <summary>
    /// 常见音频 / 外设品牌（含子品牌与中文名）。匹配时不区分大小写，只匹配名字开头的词。
    /// 想加新品牌：往这里加一项即可，不需要改别处。
    /// </summary>
    private static readonly string[] BrandWords =
    {
        // 本次实测设备所属品牌
        "HECATE", "EDIFIER", "漫步者",

        // 外设 / 音频常见品牌
        "LOGITECH", "罗技", "RAPOO", "雷柏", "SONY", "索尼", "JBL", "BOSE", "博士",
        "APPLE", "苹果", "BEATS", "SENNHEISER", "森海塞尔", "MARSHALL", "马歇尔",
        "HARMAN", "哈曼", "AKG", "AUDIO-TECHNICA", "铁三角", "STEELSERIES", "赛睿",
        "RAZER", "雷蛇", "HYPERX", "CORSAIR", "海盗船", "CREATIVE", "创新",
        "ANKER", "声阔", "SOUNDCORE", "BASEUS", "倍思", "UGREEN", "绿联",
        "XIAOMI", "小米", "REDMI", "红米", "HUAWEI", "华为", "HONOR", "荣耀",
        "OPPO", "VIVO", "ONEPLUS", "一加", "REALME", "MEIZU", "魅族",
        "LENOVO", "联想", "DELL", "戴尔", "ASUS", "华硕", "ACER", "宏碁",
        "MICROSOFT", "微软", "NINTENDO", "任天堂", "8BITDO", "八位堂",

        // 游戏外设品牌
        "HYPERX", "TURTLE", "TURTLEBEACH", "ASTRO", "ELGATO", "GENKI",
    };

    /// <summary>去掉名字开头的品牌词，返回型号；没匹配到或删空时返回原名字。</summary>
    internal static string ToModelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var original = name.Trim();
        var tokens = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;

        // 连续剥离开头的品牌词（“EDIFIER HECATE G4 S PRO” → “G4 S PRO”）
        while (start < tokens.Length && IsBrandWord(tokens[start]))
        {
            start++;
        }

        if (start == 0 || start == tokens.Length) return original;

        var model = string.Join(' ', tokens[start..]).Trim();
        return model.Length == 0 ? original : model;
    }

    private static bool IsBrandWord(string token)
    {
        var cleaned = token.Trim().TrimEnd(':', '-', '·', '|').Trim();
        if (cleaned.Length == 0) return false;

        foreach (var brand in BrandWords)
        {
            if (string.Equals(cleaned, brand, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
