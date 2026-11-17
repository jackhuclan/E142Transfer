using System.Globalization;
using System.Xml.Linq;
using E142Transfer.Models;

namespace E142Transfer;

/// <summary>
/// SEMI E142 Substrate Map 解析器（聚焦 Layout / Substrate / TransferMap / BinCodeMap）。
///
/// 设计要点：
/// 1. 全部用 LocalName 匹配，不依赖固定命名空间版本（各厂 E142.1 版本 URI 不同）。
/// 2. 严格按 XSD 默认值补齐缺省属性（OriginLocation=LowerLeft, AxisDirection=UpRight 等）。
/// 3. 解析阶段只做结构映射，不做坐标变换——变换交给 CoordinateNormalizer。
/// </summary>
public sealed class E142TransferMapParser
{
    /// <summary>从文件解析。</summary>
    public E142DocumentModel ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(XDocument.Load(path));
    }

    /// <summary>从流解析。</summary>
    public E142DocumentModel Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Parse(XDocument.Load(stream));
    }

    /// <summary>从 XML 字符串解析。</summary>
    public E142DocumentModel Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        return Parse(XDocument.Parse(xml));
    }

    /// <summary>从 XDocument 解析。</summary>
    public E142DocumentModel Parse(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var root = doc.Root ?? throw new E142ParseException("XML 根节点缺失");

        var model = new E142DocumentModel
        {
            NamespaceUri = root.Name.NamespaceName,
        };

        // ---- Layouts ----
        var layoutsNode = FindChild(root, "Layouts");
        if (layoutsNode is not null)
        {
            foreach (var layoutNode in Children(layoutsNode, "Layout"))
            {
                var dim = FindChild(layoutNode, "Dimension");
                var step = FindChild(layoutNode, "StepSize");
                model.Layouts.Add(new E142Layout
                {
                    LayoutId = Attr(layoutNode, "LayoutId") ?? "LAYOUT",
                    Columns = IntAttr(dim, "X") ?? 0,
                    Rows = IntAttr(dim, "Y") ?? 0,
                    StepSizeX = DoubleAttr(step, "X") ?? 0,
                    StepSizeY = DoubleAttr(step, "Y") ?? 0,
                    ProductId = FindChild(layoutNode, "ProductId")?.Value.Trim(),
                });
            }
        }

        // ---- Substrates ----
        var substratesNode = FindChild(root, "Substrates");
        if (substratesNode is not null)
        {
            foreach (var s in Children(substratesNode, "Substrate"))
            {
                model.Substrates.Add(new MapFrame
                {
                    SubstrateType = E142EnumParser.ParseSubstrateType(Attr(s, "SubstrateType")),
                    SubstrateId = Attr(s, "SubstrateId")?.Trim() ?? string.Empty,
                });
            }
        }

        model.LotId = FindChild(root, "LotId")?.Value.Trim();

        // ---- SubstrateMaps ----
        var mapsNode = FindChild(root, "SubstrateMaps");
        if (mapsNode is null) return model;

        foreach (var sm in Children(mapsNode, "SubstrateMap"))
        {
            var frame = new MapFrame
            {
                SubstrateType = E142EnumParser.ParseSubstrateType(Attr(sm, "SubstrateType")),
                SubstrateId = Attr(sm, "SubstrateId")?.Trim() ?? string.Empty,
                LayoutSpecifier = Attr(sm, "LayoutSpecifier")?.Trim() ?? string.Empty,
                OriginLocation = E142EnumParser.ParseOriginLocation(Attr(sm, "OriginLocation")),
                AxisDirection = E142EnumParser.ParseAxisDirection(Attr(sm, "AxisDirection")),
                Orientation = E142EnumParser.ParseOrientation(Attr(sm, "Orientation")),
            };

            foreach (var overlay in Children(sm, "Overlay"))
            {
                var mapName = Attr(overlay, "MapName") ?? string.Empty;
                var mapVersion = Attr(overlay, "MapVersion") ?? string.Empty;

                // TransferMap：XSD 中 Overlay/TransferMap maxOccurs=unbounded
                foreach (var tmNode in Children(overlay, "TransferMap"))
                {
                    var fromTypeRaw = Attr(tmNode, "FromSubstrateType");
                    var fromIdRaw = Attr(tmNode, "FromSubstrateId");
                    if (string.IsNullOrWhiteSpace(fromTypeRaw) || string.IsNullOrWhiteSpace(fromIdRaw))
                        throw new E142ParseException(
                            $"TransferMap (MapName={mapName}) 缺少必填属性 FromSubstrateType / FromSubstrateId");

                    var tm = new E142TransferMap
                    {
                        MapName = mapName,
                        MapVersion = mapVersion,
                        FromSubstrateType = E142EnumParser.ParseSubstrateType(fromTypeRaw),
                        FromSubstrateId = fromIdRaw.Trim(),
                        FromLayoutSpecifier = Attr(tmNode, "FromLayoutSpecifier")?.Trim(),
                        FromSubstrateLayoutSpecifier = Attr(tmNode, "FromSubstrateLayoutSpecifier")?.Trim(),
                    };

                    foreach (var t in Children(tmNode, "T"))
                    {
                        tm.Entries.Add(new TransferEntry
                        {
                            FX = IntAttr(t, "FX"),
                            FY = IntAttr(t, "FY"),
                            TX = IntAttr(t, "TX"),
                            TY = IntAttr(t, "TY"),
                            FID = Attr(t, "FID")?.Trim(),
                            TID = Attr(t, "TID")?.Trim(),
                        });
                    }

                    model.TransferMaps.Add((frame, tm));
                }

                // BinCodeMap：用于把 bin 码翻译成语义
                var bcmNode = FindChild(overlay, "BinCodeMap");
                if (bcmNode is not null)
                {
                    var table = new Dictionary<string, BinDefinition>(StringComparer.Ordinal);
                    var binDefsNode = FindChild(bcmNode, "BinDefinitions");
                    if (binDefsNode is not null)
                    {
                        foreach (var d in Children(binDefsNode, "BinDefinition"))
                        {
                            var code = Attr(d, "BinCode")?.Trim();
                            if (string.IsNullOrEmpty(code)) continue;
                            table[code] = new BinDefinition
                            {
                                Code = code,
                                Description = Attr(d, "BinDescription") ?? string.Empty,
                                Quality = E142EnumParser.ParseBinQuality(Attr(d, "BinQuality")),
                                Count = IntAttr(d, "BinCount"),
                                Pick = BoolAttr(d, "Pick"),
                            };
                        }
                    }
                    model.BinTables.Add((frame, table));
                }
            }
        }

        return model;
    }

    // ---------- XML 辅助：按 LocalName 匹配，忽略命名空间版本差异 ----------

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static XElement? FindChild(XElement parent, string localName) =>
        Children(parent, localName).FirstOrDefault();

    private static string? Attr(XElement? element, string localName)
    {
        if (element is null) return null;
        return element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
    }

    private static int? IntAttr(XElement? element, string localName)
    {
        var s = Attr(element, localName);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? DoubleAttr(XElement? element, string localName)
    {
        var s = Attr(element, localName);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? BoolAttr(XElement? element, string localName)
    {
        var s = Attr(element, localName)?.Trim().ToUpperInvariant();
        return s switch
        {
            "TRUE" or "1" => true,
            "FALSE" or "0" => false,
            _ => null,
        };
    }
}

/// <summary>E142 解析异常。</summary>
public sealed class E142ParseException : Exception
{
    public E142ParseException(string message) : base(message) { }
    public E142ParseException(string message, Exception inner) : base(message, inner) { }
}
