using System.Globalization;
using System.Xml.Linq;
using E142Transfer.Models;

namespace E142Transfer;

/// <summary>
/// SEMI E142 TransferMap 段生成器。
/// 生成符合 XSD 结构的 XML：Overlay/TransferMap/T，并可选合并 BinCodeMap。
/// </summary>
public sealed class E142TransferMapWriter
{
    private readonly string _namespaceUri;
    private readonly XNamespace _ns;

    public E142TransferMapWriter(string? namespaceUri = null)
    {
        _namespaceUri = namespaceUri ?? E142Constants.DefaultNamespace;
        _ns = _namespaceUri;
    }

    /// <summary>
    /// 生成一个完整的 E142 文档，只含 TransferMap overlay。
    /// 用于从 wafer 侧导出"下游去向图"给 MES / OSAT。
    /// </summary>
    public XDocument Build(E142TransferMap transferMap, MapFrame targetFrame, E142Layout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(transferMap);
        ArgumentNullException.ThrowIfNull(targetFrame);

        var root = new XElement(_ns + "MapData");

        // Layouts（XSD 要求必填）
        var layouts = new XElement(_ns + "Layouts");
        var effectiveLayout = layout ?? new E142Layout { LayoutId = "LAYOUT", Columns = 0, Rows = 0 };
        var layoutNode = new XElement(_ns + "Layout",
            new XAttribute("LayoutId", effectiveLayout.LayoutId),
            new XAttribute("DefaultUnits", "mm"),
            new XElement(_ns + "Dimension",
                new XAttribute("X", effectiveLayout.Columns),
                new XAttribute("Y", effectiveLayout.Rows)));
        if (effectiveLayout.StepSizeX > 0 || effectiveLayout.StepSizeY > 0)
        {
            layoutNode.Add(new XElement(_ns + "StepSize",
                new XAttribute("X", Fmt(effectiveLayout.StepSizeX)),
                new XAttribute("Y", Fmt(effectiveLayout.StepSizeY))));
        }
        if (!string.IsNullOrWhiteSpace(effectiveLayout.ProductId))
            layoutNode.Add(new XElement(_ns + "ProductId", effectiveLayout.ProductId));
        layouts.Add(layoutNode);
        root.Add(layouts);

        // Substrates
        var substrates = new XElement(_ns + "Substrates");
        substrates.Add(new XElement(_ns + "Substrate",
            new XAttribute("SubstrateType", E142EnumParser.ToE142(targetFrame.SubstrateType)),
            new XAttribute("SubstrateId", targetFrame.SubstrateId),
            new XElement(_ns + "GoodDevices", "0")));
        root.Add(substrates);

        // SubstrateMaps → SubstrateMap → Overlay → TransferMap → T
        var maps = new XElement(_ns + "SubstrateMaps");
        var smNode = BuildSubstrateMapNode(targetFrame);
        smNode.Add(BuildOverlayNode(transferMap));
        maps.Add(smNode);
        root.Add(maps);

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    /// <summary>
    /// 把一个 TransferMap overlay 追加到已有 E142 文档的指定 SubstrateMap 上。
    /// 用于已有 wafer map 文件里补充 transfer 数据。
    /// </summary>
    public void AppendToDocument(XDocument doc, string targetSubstrateId, E142TransferMap transferMap)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(transferMap);

        var sm = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SubstrateMap"
                              && string.Equals(Attr(e, "SubstrateId")?.Trim(), targetSubstrateId.Trim(),
                                  StringComparison.Ordinal))
            ?? throw new E142ParseException($"未找到 SubstrateId={targetSubstrateId} 的 SubstrateMap 节点");

        sm.Add(BuildOverlayNode(transferMap));
    }

    /// <summary>构造 Overlay 节点（含 TransferMap）。</summary>
    public XElement BuildOverlayNode(E142TransferMap tm)
    {
        ArgumentNullException.ThrowIfNull(tm);

        var transferNode = new XElement(_ns + "TransferMap",
            new XAttribute("FromSubstrateType", E142EnumParser.ToE142(tm.FromSubstrateType)),
            new XAttribute("FromSubstrateId", tm.FromSubstrateId));

        if (!string.IsNullOrWhiteSpace(tm.FromLayoutSpecifier))
            transferNode.Add(new XAttribute("FromLayoutSpecifier", tm.FromLayoutSpecifier));
        if (!string.IsNullOrWhiteSpace(tm.FromSubstrateLayoutSpecifier))
            transferNode.Add(new XAttribute("FromSubstrateLayoutSpecifier", tm.FromSubstrateLayoutSpecifier));

        foreach (var e in tm.Entries)
        {
            var t = new XElement(_ns + "T");
            if (e.FX.HasValue) t.Add(new XAttribute("FX", e.FX.Value));
            if (e.FY.HasValue) t.Add(new XAttribute("FY", e.FY.Value));
            if (e.TX.HasValue) t.Add(new XAttribute("TX", e.TX.Value));
            if (e.TY.HasValue) t.Add(new XAttribute("TY", e.TY.Value));
            if (!string.IsNullOrWhiteSpace(e.FID)) t.Add(new XAttribute("FID", e.FID));
            if (!string.IsNullOrWhiteSpace(e.TID)) t.Add(new XAttribute("TID", e.TID));

            if (!t.HasAttributes)
                throw new E142WriteException("TransferMap 的 <T> 记录至少需要一个 FX/FY/TX/TY/FID/TID 属性");
            transferNode.Add(t);
        }

        return new XElement(_ns + "Overlay",
            new XAttribute("MapName", tm.MapName),
            new XAttribute("MapVersion", tm.MapVersion),
            transferNode);
    }

    private XElement BuildSubstrateMapNode(MapFrame frame) =>
        new(_ns + "SubstrateMap",
            new XAttribute("SubstrateType", E142EnumParser.ToE142(frame.SubstrateType)),
            new XAttribute("SubstrateId", frame.SubstrateId),
            new XAttribute("LayoutSpecifier", frame.LayoutSpecifier),
            new XAttribute("SubstrateSide", "TopSide"),
            new XAttribute("Orientation", (int)frame.Orientation),
            new XAttribute("OriginLocation", E142EnumParser.ToE142(frame.OriginLocation)),
            new XAttribute("AxisDirection", E142EnumParser.ToE142(frame.AxisDirection)));

    private static string? Attr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>保存为文件（格式化缩进）。</summary>
    public static void Save(XDocument doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        doc.Save(path, SaveOptions.None);
    }
}

/// <summary>E142 写入异常。</summary>
public sealed class E142WriteException : Exception
{
    public E142WriteException(string message) : base(message) { }
}
