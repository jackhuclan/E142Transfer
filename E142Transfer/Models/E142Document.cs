namespace E142Transfer.Models;

/// <summary>Layout 定义：阵列维度与坐标系。</summary>
public sealed class E142Layout
{
    public string LayoutId { get; set; } = "LAYOUT";
    public int Columns { get; set; }
    public int Rows { get; set; }
    public double StepSizeX { get; set; }
    public double StepSizeY { get; set; }
    public string? ProductId { get; set; }
}

/// <summary>一个 SubstrateMap 节点上的坐标系属性。</summary>
public sealed class MapFrame
{
    public SubstrateType SubstrateType { get; set; } = SubstrateType.Wafer;
    public string SubstrateId { get; set; } = string.Empty;
    public string LayoutSpecifier { get; set; } = string.Empty;
    public OriginLocation OriginLocation { get; set; } = OriginLocation.LowerLeft;
    public AxisDirection AxisDirection { get; set; } = AxisDirection.UpRight;
    public WaferOrientation Orientation { get; set; } = WaferOrientation.Degree0;
}

/// <summary>一个 Overlay 节点里的 TransferMap 定义。</summary>
public sealed class E142TransferMap
{
    /// <summary>Overlay/@MapName。</summary>
    public string MapName { get; set; } = "TransferMap";

    /// <summary>Overlay/@MapVersion。</summary>
    public string MapVersion { get; set; } = "1";

    /// <summary>TransferMap/@FromSubstrateType（源载具类型）。</summary>
    public SubstrateType FromSubstrateType { get; set; } = SubstrateType.Wafer;

    /// <summary>TransferMap/@FromSubstrateId（源载具 ID）。</summary>
    public string FromSubstrateId { get; set; } = string.Empty;

    /// <summary>TransferMap/@FromLayoutSpecifier，可选。</summary>
    public string? FromLayoutSpecifier { get; set; }

    /// <summary>TransferMap/@FromSubstrateLayoutSpecifier，可选。</summary>
    public string? FromSubstrateLayoutSpecifier { get; set; }

    /// <summary>所有 &lt;T&gt; 记录。</summary>
    public List<TransferEntry> Entries { get; } = [];

    public TransferEntry? FindByTarget(int tx, int ty) =>
        Entries.FirstOrDefault(e => e.TX == tx && e.TY == ty);

    public IEnumerable<TransferEntry> FindBySource(int fx, int fy) =>
        Entries.Where(e => e.FX == fx && e.FY == fy);
}

/// <summary>TransferMap 中的单条 &lt;T&gt; 记录，源坐标 → 目标坐标。</summary>
public sealed class TransferEntry
{
    /// <summary>FX：源 die 阵列 X。</summary>
    public int? FX { get; set; }

    /// <summary>FY：源 die 阵列 Y。</summary>
    public int? FY { get; set; }

    /// <summary>TX：目标 die 阵列 X。</summary>
    public int? TX { get; set; }

    /// <summary>TY：目标 die 阵列 Y。</summary>
    public int? TY { get; set; }

    /// <summary>FID：源 die 字符串 ID。</summary>
    public string? FID { get; set; }

    /// <summary>TID：目标 die 字符串 ID。</summary>
    public string? TID { get; set; }

    public bool HasCoordinates => FX.HasValue && FY.HasValue && TX.HasValue && TY.HasValue;

    public override string ToString() =>
        $"({FX},{FY}) → ({TX},{TY})" + (TID is null ? string.Empty : $" [{TID}]");
}

/// <summary>解析后的完整文档视图。</summary>
public sealed class E142DocumentModel
{
    public string NamespaceUri { get; set; } = E142Constants.DefaultNamespace;
    public string? LotId { get; set; }
    public List<E142Layout> Layouts { get; } = [];
    public List<MapFrame> Substrates { get; } = [];

    /// <summary>文档里所有 TransferMap（可能多个 substrate、多个 overlay）。</summary>
    public List<(MapFrame Frame, E142TransferMap TransferMap)> TransferMaps { get; } = [];

    /// <summary>文档里所有 BinCodeMap（供 BinCode 定义表查询）。</summary>
    public List<(MapFrame Frame, Dictionary<string, BinDefinition> Definitions)> BinTables { get; } = [];
}

public static class E142Constants
{
    /// <summary>E142.1 V1005 命名空间；各厂版本略有差异，解析时按 LocalName 匹配。</summary>
    public const string DefaultNamespace = "urn:semi-org:xsd.E142-1.V1005.SubstrateMap";

    public static readonly string[] KnownNamespaces =
    [
        DefaultNamespace,
        "urn:semi-org:xsd.E142-1.V1016.SubstrateMap",
        "urn:semi-org:xsd.E142-1.V0225.SubstrateMap",
    ];
}
