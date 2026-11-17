using System.Xml.Linq;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// SEMI E142 读取器：复用现有 E142TransferMapParser 解析 Layout / BinCode / TransferMap，
/// 并补上 2DArray / Array / Coordinate 三种 bin 阵列的读取（原工程只解析结构、未解析 bin 数据）。
/// 解析仍按 LocalName 匹配，兼容各厂命名空间版本差异。
/// </summary>
public sealed class E142MapReader : IWaferMapReader
{
    public WaferMapFormat Format => WaferMapFormat.E142;

    public IReadOnlyList<string> SupportedExtensions => [".xml", ".e142", ".x142", ".txt"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var text = probe.HeadText;
        var hasRoot = text.Contains("MapData", StringComparison.OrdinalIgnoreCase);
        var hasMarker = text.Contains("SubstrateMap", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("semi-org", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("E142", StringComparison.OrdinalIgnoreCase);
        if (!hasRoot || !hasMarker) return 0;

        var score = 0.95;
        var ext = probe.Extension?.ToLowerInvariant();
        if (ext is ".xml" or ".e142" or ".x142") score = Math.Min(1.0, score + 0.05);
        return score;
    }

    public NeutralWaferMap Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var doc = XDocument.Load(path);
        var parsed = new E142TransferMapParser().Parse(doc);

        var layout = parsed.Layouts.FirstOrDefault() ?? new E142Layout();
        var frame = parsed.Substrates.FirstOrDefault() ?? new MapFrame();

        var map = new NeutralWaferMap
        {
            Format = Format,
            SourcePath = path,
            LotId = parsed.LotId ?? string.Empty,
            SubstrateId = frame.SubstrateId,
            SubstrateType = frame.SubstrateType,
            Layout = new NeutralLayout
            {
                Columns = layout.Columns,
                Rows = layout.Rows,
                StepSizeX = layout.StepSizeX,
                StepSizeY = layout.StepSizeY,
                OriginLocation = frame.OriginLocation,
                AxisDirection = frame.AxisDirection,
                Orientation = frame.Orientation,
            },
        };

        foreach (var (_, table) in parsed.BinTables)
        foreach (var (code, def) in table)
            map.AddBin(new NeutralBinDefinition
            {
                Code = code,
                Description = def.Description,
                Class = NeutralBinDefinition.FromQuality(def.Quality),
            });

        foreach (var (targetFrame, tm) in parsed.TransferMaps)
        foreach (var t in tm.Entries)
        {
            if (!t.HasCoordinates) continue;
            map.AddTransfer(new NeutralTransfer(
                t.FX!.Value, t.FY!.Value, t.TX!.Value, t.TY!.Value,
                t.FID, t.TID,
                tm.FromSubstrateType, tm.FromSubstrateId,
                targetFrame.SubstrateType, targetFrame.SubstrateId));
        }

        ReadBinArrays(doc, map, layout);

        if (!map.Layout.IsValid)
            map.AddIssue("E142_NO_LAYOUT", "文件缺少有效 Layout/Dimension，无法建立 die 网格", isWarning: false);

        return map;
    }

    /// <summary>从各 Overlay/BinCodeMap 读取 bin 阵列，归一化到文件坐标 (col,row)。</summary>
    private static void ReadBinArrays(XDocument doc, NeutralWaferMap map, E142Layout layout)
    {
        var root = doc.Root;
        if (root is null) return;

        foreach (var bcm in Descendants(root, "BinCodeMap"))
        {
            var nullBin = Attr(bcm, "NullBin");
            var binType = E142EnumParser.ParseBinType(Attr(bcm, "BinType"));
            var mapType = E142EnumParser.ParseMapType(Attr(bcm, "MapType"));

            switch (mapType)
            {
                case E142MapType.TwoDArray:
                case E142MapType.Array:
                    ReadFlatArray(bcm, map, layout, nullBin, binType, mapType);
                    break;

                case E142MapType.Coordinate:
                    ReadCoordinateArray(bcm, map, nullBin);
                    break;

                case E142MapType.RowColumn:
                default:
                    map.AddIssue("E142_MAPTYPE_UNSUPPORTED",
                        $"MapType={E142EnumParser.ToE142(mapType)} 的 bin 阵列尚未解析，仅结构与 TransferMap 已读取");
                    break;
            }
        }
    }

    private static void ReadFlatArray(
        XElement bcm, NeutralWaferMap map, E142Layout layout,
        string? nullBin, BinType binType, E142MapType mapType)
    {
        var raw = string.Concat(Children(bcm, "BinCode").Select(e => e.Value));
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length == 0) return;

        if (mapType == E142MapType.Array && layout.Columns > 0)
            lines = Chunk(lines[0], layout.Columns);

        for (var row = 0; row < lines.Length; row++)
        {
            var tokens = binType == BinType.Ascii
                ? lines[row].Select(ch => ch.ToString()).ToArray()
                : lines[row].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            for (var col = 0; col < tokens.Length; col++)
            {
                var code = tokens[col];
                if (nullBin is not null && string.Equals(code, nullBin, StringComparison.OrdinalIgnoreCase))
                    continue;

                var cls = ResolveClass(map, code);
                EnsureBin(map, code, cls);
                map.AddDie(new NeutralDie(col, row) { BinCode = code, Class = cls });
            }
        }
    }

    private static void ReadCoordinateArray(XElement bcm, NeutralWaferMap map, string? nullBin)
    {
        foreach (var node in Descendants(bcm, "Bin"))
        {
            if (!int.TryParse(Attr(node, "X"), out var x) || !int.TryParse(Attr(node, "Y"), out var y))
                continue;
            var code = Attr(node, "BinCode") ?? string.Empty;
            if (nullBin is not null && string.Equals(code, nullBin, StringComparison.OrdinalIgnoreCase))
                continue;
            var cls = ResolveClass(map, code);
            EnsureBin(map, code, cls);
            map.AddDie(new NeutralDie(x, y) { BinCode = code, Class = cls });
        }
    }

    private static NeutralBinClass ResolveClass(NeutralWaferMap map, string code) =>
        map.BinDefinitions.TryGetValue(code, out var def) ? def.Class : NeutralBinClass.Unknown;

    private static void EnsureBin(NeutralWaferMap map, string code, NeutralBinClass cls)
    {
        if (!map.BinDefinitions.ContainsKey(code))
            map.AddBin(new NeutralBinDefinition { Code = code, Class = cls, Description = cls.ToString() });
    }

    private static string[] Chunk(string text, int width)
    {
        var result = new List<string>();
        for (var i = 0; i < text.Length; i += width)
            result.Add(text.Substring(i, Math.Min(width, text.Length - i)));
        return [.. result];
    }

    private static IEnumerable<XElement> Descendants(XElement parent, string localName) =>
        parent.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Attr(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
}
