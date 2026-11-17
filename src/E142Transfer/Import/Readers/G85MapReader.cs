using System.Xml.Linq;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// SEMI G85-1101 XML 读取器（Amkor / Camtek OQA / Micron 老牌 probe 数据）。
///
/// 结构：&lt;Maps&gt;&lt;Map&gt; → &lt;Device Rows Columns BinType NullBin OriginLocation
/// Orientation DeviceSize*/StepSize*&gt; → &lt;Bin BinCode BinCount BinQuality&gt;* →
/// &lt;SupplierData ...&gt; → &lt;Data&gt;&lt;Row&gt;&lt;![CDATA[空格分隔 bin 码]]&gt;。
///
/// 三个必须避免的坑（都在本 Reader 里显式处理，记录到 Issues 而非静默猜测）：
///  1. BinQuality="None" 是 Camtek 的 "Marked Bin"，是有效 die，不是 Null。
///     绝不能用 E142EnumParser.ParseBinQuality（它把 None→Null，会抹掉 4463 颗 die）。
///  2. OriginLocation 是 G85 数字码（2=upper left，见 Micron TN0021），
///     不是 E142 的字符串枚举，必须单独映射。
///  3. DUTMS 决定 DeviceSize/StepSize 单位（样本为 10^-6*m=µm）；原样保留 + 告警，
///     不在读取阶段偷偷换算成 mm。
/// </summary>
public sealed class G85MapReader : IWaferMapReader
{
    public WaferMapFormat Format => WaferMapFormat.G85;

    public IReadOnlyList<string> SupportedExtensions => [".xml", ".g85", ".map"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var text = probe.HeadText;
        var hasRevision = text.Contains("FormatRevision", StringComparison.OrdinalIgnoreCase);
        var hasG85 = text.Contains("G85", StringComparison.OrdinalIgnoreCase);
        var hasDevice = text.Contains("<Device", StringComparison.OrdinalIgnoreCase);
        var hasNullBin = text.Contains("NullBin", StringComparison.OrdinalIgnoreCase);
        var hasData = text.Contains("<Data", StringComparison.OrdinalIgnoreCase);
        if (!hasRevision || !hasG85 || !hasDevice || !hasNullBin) return 0;

        var score = 0.92;
        if (hasData) score += 0.03;
        if (text.Contains("BinQuality", StringComparison.OrdinalIgnoreCase)) score += 0.03;
        if (text.Contains("SupplierData", StringComparison.OrdinalIgnoreCase)) score += 0.02;
        var ext = probe.Extension?.ToLowerInvariant();
        if (ext is ".g85") score += 0.05;
        return Math.Min(1.0, score);
    }

    public NeutralWaferMap Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var doc = XDocument.Load(path);
        var mapNode = Descendants(doc.Root!, "Map").FirstOrDefault()
            ?? throw new FormatException("G85 文件缺少 <Map> 节点");

        var revision = Attr(mapNode, "FormatRevision") ?? string.Empty;
        var device = Children(mapNode, "Device").FirstOrDefault()
            ?? throw new FormatException("G85 文件缺少 <Device> 节点");

        var map = new NeutralWaferMap
        {
            Format = Format,
            SourcePath = path,
            SubstrateId = Attr(mapNode, "WaferId") ?? string.Empty,
        };

        if (!revision.Contains("G85", StringComparison.OrdinalIgnoreCase))
            map.AddIssue("G85_REVISION", $"FormatRevision='{revision}' 非 G85，解析器按 G85 结构尝试读取", false);

        // ---------- 1. Device：网格、尺寸、原点、朝向 ----------
        var rows = ParseInt(Attr(device, "Rows"));
        var columns = ParseInt(Attr(device, "Columns"));
        var binTypeRaw = Attr(device, "BinType") ?? "Decimal";
        var nullBin = Attr(device, "NullBin") ?? string.Empty;
        var originRaw = Attr(device, "OriginLocation");
        var orientationRaw = Attr(device, "Orientation");
        var dutms = Attr(device, "DUTMS");

        map.DeviceId = Attr(device, "ProductId") ?? string.Empty;
        map.LotId = Attr(device, "LotId") ?? string.Empty;
        map.DieSizeX = ParseDouble(Attr(device, "DeviceSizeX"));
        map.DieSizeY = ParseDouble(Attr(device, "DeviceSizeY"));

        var (origin, originNote) = ParseOrigin(originRaw);
        map.Layout = new NeutralLayout
        {
            Columns = columns,
            Rows = rows,
            StepSizeX = ParseDouble(Attr(device, "StepSizeX")),
            StepSizeY = ParseDouble(Attr(device, "StepSizeY")),
            OriginLocation = origin,
            AxisDirection = AxisDirection.DownRight, // G85 行序自顶向下、列序自左向右
            Orientation = ParseOrientation(orientationRaw),
        };
        map.SetMeta("FrameId", Attr(device, "FrameId") ?? string.Empty);
        map.SetMeta("CreateDate", Attr(device, "CreateDate") ?? string.Empty);
        map.SetMeta("BinType", binTypeRaw);
        map.SetMeta("NullBin", nullBin);
        map.SetMeta("Revision", revision);

        if (rows <= 0 || columns <= 0)
            map.AddIssue("G85_NO_GRID", $"Rows={rows} / Columns={columns} 非法，无法建立 die 网格", false);
        if (originNote is not null)
            map.AddIssue("G85_ORIGIN", originNote);
        if (string.IsNullOrEmpty(nullBin))
            map.AddIssue("G85_NO_NULLBIN", "Device 缺少 NullBin，空位无法识别", false);

        // ---------- 2. Bin 定义表 ----------
        var declaredCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bin in Children(device, "Bin"))
        {
            var code = Attr(bin, "BinCode") ?? string.Empty;
            if (code.Length == 0) continue;
            var qualityRaw = Attr(bin, "BinQuality") ?? string.Empty;
            var cls = ParseQuality(qualityRaw);
            map.AddBin(new NeutralBinDefinition
            {
                Code = code,
                Description = Attr(bin, "BinDescription") ?? string.Empty,
                Class = cls,
            });
            if (int.TryParse(Attr(bin, "BinCount"), out var declared))
                declaredCounts[code] = declared;

            if (cls == NeutralBinClass.Marked)
                map.AddIssue("G85_BIN_MARKED",
                    $"Bin {code} 的 BinQuality='{qualityRaw}'（Marked Bin）：按有效但未判定 die 处理，已排除出 GoodDies；" +
                    "禁止当作 Null 丢弃，disposition 需产线确认");
            if (string.Equals(code, nullBin, StringComparison.Ordinal))
                map.AddIssue("G85_NULLBIN_DEFINED",
                    $"NullBin={nullBin} 同时出现在 <Bin> 定义中（G85 规范通常不定义 NullBin），已按 Null 处理");
        }
        if (map.BinDefinitions.Count == 0)
            map.AddIssue("G85_NO_BINS", "文件没有任何 <Bin> 定义，所有 die 将判为 Unknown", false);

        // ---------- 3. SupplierData：厂商扩展字段原样保留 ----------
        var supplier = Children(device, "SupplierData").FirstOrDefault();
        if (supplier is not null)
        {
            foreach (var attr in supplier.Attributes())
                map.SetMeta(attr.Name.LocalName, attr.Value);
            dutms ??= Attr(supplier, "DUTMS");
            var rx = ParseInt(Attr(supplier, "REFP1.X"));
            var ry = ParseInt(Attr(supplier, "REFP1.Y"));
            if (rx >= 0 && ry >= 0) map.ReferenceDie = (rx, ry);
            var slot = Attr(supplier, "SLOT");
            if (slot is not null) map.SetMeta("SLOT", slot);
        }

        if (dutms is not null)
        {
            map.SetMeta("DUTMS", dutms);
            var unit = DescribeUnit(dutms);
            map.AddIssue("G85_UNITS",
                $"DUTMS={dutms}（{unit}）：DeviceSize/StepSize 原样保留未换算；" +
                "与 mm 基准的工具（如 E142 Layout）互操作前须按此单位转换");
        }
        if (map.ReferenceDie is { } refDie)
            map.AddIssue("G85_REFERENCE",
                $"参考 die 位于 (X={refDie.X}, Y={refDie.Y})；上线前须用它校验 map 与物理 die 的对齐");
        var orgFnLoc = Attr(supplier, "ORGFNLOC");
        if (orgFnLoc is not null && orientationRaw is not null &&
            !string.Equals(orgFnLoc, NormalizeAngle(orientationRaw), StringComparison.Ordinal))
            map.AddIssue("G85_ORIENTATION_CONFLICT",
                $"Device/Orientation={orientationRaw} 与 SupplierData/ORGFNLOC={orgFnLoc} 不一致，投影前须确认 flat/notch 基准");

        // ---------- 4. Data/Row：bin 阵列 ----------
        var data = Children(device, "Data").FirstOrDefault();
        if (data is null)
        {
            map.AddIssue("G85_NO_DATA", "文件缺少 <Data> 节点，没有任何 die 数据", false);
            return map;
        }
        map.SetMeta("MapName", Attr(data, "MapName") ?? string.Empty);
        map.SetMeta("MapVersion", Attr(data, "Version") ?? string.Empty);

        var rowNodes = Children(data, "Row").ToArray();
        var actualCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rowMismatch = 0;
        var dataRow = 0;

        foreach (var rowNode in rowNodes)
        {
            var tokens = rowNode.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns > 0 && tokens.Length != columns) rowMismatch++;
            for (var col = 0; col < tokens.Length; col++)
            {
                var code = tokens[col];
                var isNull = string.Equals(code, nullBin, StringComparison.Ordinal);
                var cls = isNull
                    ? NeutralBinClass.Null
                    : map.BinDefinitions.TryGetValue(code, out var def) ? def.Class : NeutralBinClass.Unknown;

                if (!isNull && !map.BinDefinitions.ContainsKey(code))
                {
                    map.AddBin(new NeutralBinDefinition
                        { Code = code, Class = cls, Description = "未在 <Bin> 中定义" });
                    map.AddIssue("G85_UNDEFINED_BIN",
                        $"数据出现未定义的 bin 码 '{code}'，已按 Unknown 处理（不可拾取）");
                }

                map.AddDie(new NeutralDie(col, dataRow) { BinCode = code, Class = cls });
                if (!isNull)
                {
                    actualCounts.TryGetValue(code, out var n);
                    actualCounts[code] = n + 1;
                }
            }
            dataRow++;
        }

        // ---------- 5. 校验：行数 / 列数 / BinCount ----------
        if (rows > 0 && rowNodes.Length != rows)
            map.AddIssue("G85_ROW_COUNT",
                $"<Row> 数 {rowNodes.Length} 与 Device/Rows={rows} 不一致，已按实际行写入", false);
        if (rowMismatch > 0)
            map.AddIssue("G85_COL_COUNT",
                $"{rowMismatch} 行的 token 数与 Device/Columns={columns} 不一致，已按实际 token 写入", false);

        foreach (var (code, declared) in declaredCounts)
        {
            actualCounts.TryGetValue(code, out var actual);
            if (actual != declared)
                map.AddIssue("G85_BINCOUNT_MISMATCH",
                    $"Bin {code}：BinCount 声明 {declared}，实际统计 {actual}", false);
        }
        foreach (var code in map.BinDefinitions.Keys)
        {
            if (!declaredCounts.ContainsKey(code) && !string.Equals(code, nullBin, StringComparison.Ordinal))
                map.AddIssue("G85_BIN_NO_COUNT", $"Bin {code} 缺少 BinCount 声明，无法交叉校验");
        }
        if (!string.IsNullOrEmpty(nullBin))
        {
            var nullActual = map.Dies.Values.Count(d => d.Class == NeutralBinClass.Null);
            map.AddIssue("G85_NULL_COUNT",
                $"NullBin={nullBin} 空位 {nullActual} 个（G85 规范不为 NullBin 定义 BinCount，属正常）");
        }

        return map;
    }

    /// <summary>G85 BinQuality → 中性类别。None = Marked（关键：不可映射为 Null）。</summary>
    private static NeutralBinClass ParseQuality(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "PASS" or "GOOD" => NeutralBinClass.Pass,
        "FAIL" or "BAD" => NeutralBinClass.Fail,
        "NULL" => NeutralBinClass.Null,
        "REFERENCE" or "REF" => NeutralBinClass.Reference,
        "SKIP" or "SKIPDIE" => NeutralBinClass.Skip,
        "EDGE" or "UGLYDIE" => NeutralBinClass.Edge,
        "NONE" or "MARKED" or "" => NeutralBinClass.Marked,
        _ => NeutralBinClass.Unknown,
    };

    /// <summary>
    /// G85 OriginLocation 数字码 → E142 原点枚举。权威依据 Micron TN0021：2 = upper left。
    /// 其余码各家实现不一，按上/下 + 左/右的对称推断并附告警，绝不静默套用 E142 默认值。
    /// </summary>
    private static (OriginLocation Origin, string? Note) ParseOrigin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (OriginLocation.LowerLeft, "缺少 OriginLocation，按 G85 常见默认 LowerLeft 处理，须确认");
        var s = raw.Trim().ToUpperInvariant();
        if (s is "2" or "UPPERLEFT" or "UL")
            return (OriginLocation.UpperLeft, null);
        return s switch
        {
            "1" or "UPPERRIGHT" or "UR" => (OriginLocation.UpperRight, "OriginLocation=1 按 UpperRight 推断，须按厂内规范确认"),
            "3" or "LOWERLEFT" or "LL" => (OriginLocation.LowerLeft, "OriginLocation=3 按 LowerLeft 推断，须按厂内规范确认"),
            "4" or "LOWERRIGHT" or "LR" => (OriginLocation.LowerRight, "OriginLocation=4 按 LowerRight 推断，须按厂内规范确认"),
            _ => (OriginLocation.LowerLeft, $"未知 OriginLocation='{raw}'，按 LowerLeft 处理，须按厂内规范确认"),
        };
    }

    private static WaferOrientation ParseOrientation(string? raw) =>
        Enum.TryParse<WaferOrientation>($"Degree{NormalizeAngle(raw)}", out var o)
            ? o : WaferOrientation.Degree0;

    private static string NormalizeAngle(string? raw)
    {
        if (!int.TryParse(raw, out var d)) return "0";
        return (((d % 360) + 360) % 360) switch
        {
            90 => "90", 180 => "180", 270 => "270", _ => "0",
        };
    }

    private static string DescribeUnit(string dutms)
    {
        var s = dutms.ToLowerInvariant().Replace(" ", "");
        if (s.Contains("10^-6") || s.Contains("10-6") || s.Contains("micron") || s is "um" or "µm")
            return "微米 µm";
        if (s.Contains("10^-3") || s is "mm") return "毫米 mm";
        if (s.Contains("10^-9") || s is "nm") return "纳米 nm";
        return s;
    }

    private static int ParseInt(string? raw) =>
        int.TryParse(raw, out var v) ? v : -1;

    private static double ParseDouble(string? raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static IEnumerable<XElement> Descendants(XElement parent, string localName) =>
        parent.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Attr(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
}
