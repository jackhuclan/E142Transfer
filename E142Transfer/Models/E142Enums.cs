using System.ComponentModel;

namespace E142Transfer.Models;

/// <summary>
/// SEMI E142 载具类型。对应 XSD 中的 SubstrateTypeEnum。
/// </summary>
public enum SubstrateType
{
    Unknown = 0,
    Wafer,
    Frame,
    Strip,
    Tray,
    [Description("Tape and Reel")] Tnr,
    Panel,
    [Description("Printed Circuit Board")] Pcb,
}

/// <summary>
/// SEMI E142 BinCodeMap 的 MapType 枚举。对应 XSD 中的 MapTypeEnum。
/// 注意 E142.1-0225 多出一个 Coordinate 值。
/// </summary>
public enum E142MapType
{
    /// <summary>逐行连续存储，未声明时的默认值。</summary>
    TwoDArray = 0,
    /// <summary>起点 XY + 长度 + 值列表。</summary>
    RowColumn,
    /// <summary>单个长字符串，按列宽切分。</summary>
    Array,
    /// <summary>每个 bin 自带 X/Y 属性（Coordinate 型）。</summary>
    Coordinate,
}

/// <summary>
/// SEMI E142 BinType 枚举。对应 XSD 中的 BinTypeEnum。
/// </summary>
public enum BinType
{
    Ascii = 0,
    Decimal,
    Hexadecimal,
    Integer2,
}

/// <summary>
/// Bin 质量属性。来自 BinDefinition/@BinQuality，各厂字符串略有差异。
/// </summary>
public enum BinQuality
{
    Unknown = 0,
    Pass,
    Fail,
    [Description("空位 / 未测")] Null,
    Reference,
    Skip,
    Edge,
    [Description("已标记未判定（G85 Marked Bin）")] Marked,
}

/// <summary>
/// 阵列原点位置。XSD 属性 OriginLocation，默认 LowerLeft。
/// </summary>
public enum OriginLocation
{
    LowerLeft = 0,
    LowerRight,
    UpperLeft,
    UpperRight,
    Center,
}

/// <summary>
/// 阵列轴向。XSD 属性 AxisDirection，默认 UpRight。
/// </summary>
public enum AxisDirection
{
    UpRight = 0,
    DownRight,
    UpLeft,
    DownLeft,
}

/// <summary>
/// 晶圆朝向角。XSD 属性 Orientation，默认 0。
/// </summary>
public enum WaferOrientation
{
    Degree0 = 0,
    Degree90 = 90,
    Degree180 = 180,
    Degree270 = 270,
}

/// <summary>
/// 字符串到枚举的容错转换：XSD 允许大小写混用（Ascii / ASCII），
/// 且不同版本对连字符、缩写的写法不一致，这里统一归一。
/// </summary>
public static class E142EnumParser
{
    public static SubstrateType ParseSubstrateType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SubstrateType.Wafer;
        var s = Normalize(raw);
        return s switch
        {
            "WAFER" => SubstrateType.Wafer,
            "FRAME" or "FILMFRAME" => SubstrateType.Frame,
            "STRIP" or "LEADFRAME" => SubstrateType.Strip,
            "TRAY" => SubstrateType.Tray,
            "TNR" or "TAPEANDREEL" or "TAPEREEL" => SubstrateType.Tnr,
            "PANEL" => SubstrateType.Panel,
            "PCB" => SubstrateType.Pcb,
            _ => SubstrateType.Unknown,
        };
    }

    public static string ToE142(SubstrateType type) => type switch
    {
        SubstrateType.Wafer => "Wafer",
        SubstrateType.Frame => "Frame",
        SubstrateType.Strip => "Strip",
        SubstrateType.Tray => "Tray",
        SubstrateType.Tnr => "TNR",
        SubstrateType.Panel => "Panel",
        SubstrateType.Pcb => "PCB",
        _ => "Wafer",
    };

    public static E142MapType ParseMapType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return E142MapType.TwoDArray; // 规范默认值
        var s = Normalize(raw);
        return s switch
        {
            "2DARRAY" or "ARRAY2D" => E142MapType.TwoDArray,
            "ROWCOLUMN" or "ROW/COLUMN" or "ROW COLUMN" => E142MapType.RowColumn,
            "ARRAY" => E142MapType.Array,
            "COORDINATE" => E142MapType.Coordinate,
            _ => E142MapType.TwoDArray,
        };
    }

    public static string ToE142(E142MapType type) => type switch
    {
        E142MapType.TwoDArray => "2DArray",
        E142MapType.RowColumn => "Row/Column",
        E142MapType.Array => "Array",
        E142MapType.Coordinate => "Coordinate",
        _ => "2DArray",
    };

    public static BinType ParseBinType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return BinType.Ascii;
        return Normalize(raw) switch
        {
            "ASCII" => BinType.Ascii,
            "DECIMAL" => BinType.Decimal,
            "HEXADECIMAL" or "HEX" => BinType.Hexadecimal,
            "INTEGER2" or "INTEGER" => BinType.Integer2,
            _ => BinType.Ascii,
        };
    }

    public static string ToE142(BinType type) => type switch
    {
        BinType.Ascii => "Ascii",
        BinType.Decimal => "Decimal",
        BinType.Hexadecimal => "HexaDecimal",
        BinType.Integer2 => "Integer2",
        _ => "Ascii",
    };

    public static OriginLocation ParseOriginLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return OriginLocation.LowerLeft; // 规范默认值
        return Normalize(raw) switch
        {
            "LOWERLEFT" => OriginLocation.LowerLeft,
            "LOWERRIGHT" => OriginLocation.LowerRight,
            "UPPERLEFT" => OriginLocation.UpperLeft,
            "UPPERRIGHT" => OriginLocation.UpperRight,
            "CENTER" => OriginLocation.Center,
            _ => OriginLocation.LowerLeft,
        };
    }

    public static string ToE142(OriginLocation value) => value switch
    {
        OriginLocation.LowerLeft => "LowerLeft",
        OriginLocation.LowerRight => "LowerRight",
        OriginLocation.UpperLeft => "UpperLeft",
        OriginLocation.UpperRight => "UpperRight",
        OriginLocation.Center => "Center",
        _ => "LowerLeft",
    };

    public static AxisDirection ParseAxisDirection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AxisDirection.UpRight; // 规范默认值
        return Normalize(raw) switch
        {
            "UPRIGHT" => AxisDirection.UpRight,
            "DOWNRIGHT" => AxisDirection.DownRight,
            "UPLEFT" => AxisDirection.UpLeft,
            "DOWNLEFT" => AxisDirection.DownLeft,
            _ => AxisDirection.UpRight,
        };
    }

    public static string ToE142(AxisDirection value) => value switch
    {
        AxisDirection.UpRight => "UpRight",
        AxisDirection.DownRight => "DownRight",
        AxisDirection.UpLeft => "UpLeft",
        AxisDirection.DownLeft => "DownLeft",
        _ => "UpRight",
    };

    public static WaferOrientation ParseOrientation(string? raw)
    {
        if (!int.TryParse(raw, out var d)) return WaferOrientation.Degree0;
        return d switch
        {
            90 => WaferOrientation.Degree90,
            180 => WaferOrientation.Degree180,
            270 => WaferOrientation.Degree270,
            _ => WaferOrientation.Degree0,
        };
    }

    public static BinQuality ParseBinQuality(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return BinQuality.Unknown;
        return Normalize(raw) switch
        {
            "PASS" or "GOOD" => BinQuality.Pass,
            "FAIL" or "BAD" => BinQuality.Fail,
            "NULL" or "NONE" => BinQuality.Null,
            "REFERENCE" or "REF" => BinQuality.Reference,
            "SKIP" or "SKIPDIE" => BinQuality.Skip,
            "EDGE" or "UGLYDIE" => BinQuality.Edge,
            _ => BinQuality.Unknown,
        };
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
