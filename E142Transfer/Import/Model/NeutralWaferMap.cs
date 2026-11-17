using E142Transfer.Import.Abstractions;
using E142Transfer.Models;

namespace E142Transfer.Import.Model;

/// <summary>中性 bin 类别。屏蔽各格式对 pass/fail/null 的不同叫法。</summary>
public enum NeutralBinClass
{
    Unknown = 0,
    Pass,
    Fail,
    Null,
    Reference,
    Skip,
    Edge,
}

/// <summary>中性 bin 码定义：码值 → 语义。</summary>
public sealed class NeutralBinDefinition
{
    public required string Code { get; init; }
    public string Description { get; init; } = string.Empty;
    public NeutralBinClass Class { get; init; } = NeutralBinClass.Unknown;

    public bool IsPass => Class == NeutralBinClass.Pass;
    public bool IsNull => Class == NeutralBinClass.Null;

    public static NeutralBinClass FromQuality(BinQuality quality) => quality switch
    {
        BinQuality.Pass => NeutralBinClass.Pass,
        BinQuality.Fail => NeutralBinClass.Fail,
        BinQuality.Null => NeutralBinClass.Null,
        BinQuality.Reference => NeutralBinClass.Reference,
        BinQuality.Skip => NeutralBinClass.Skip,
        BinQuality.Edge => NeutralBinClass.Edge,
        _ => NeutralBinClass.Unknown,
    };
}

/// <summary>中性 die 记录。坐标一律为文件坐标，坐标变换交给 CoordinateNormalizer。</summary>
public readonly record struct NeutralDie(int X, int Y)
{
    public string BinCode { get; init; } = string.Empty;
    public NeutralBinClass Class { get; init; } = NeutralBinClass.Unknown;
    public bool IsDie => Class != NeutralBinClass.Null;
}

/// <summary>一条坐标转移记录（E142 TransferMap 的 &lt;T&gt; 中性化）。</summary>
public readonly record struct NeutralTransfer(
    int FX, int FY, int TX, int TY,
    string? Fid, string? Tid,
    SubstrateType FromSubstrateType, string FromSubstrateId,
    SubstrateType TargetSubstrateType, string TargetSubstrateId);

/// <summary>解析过程中发现的非致命问题。</summary>
public sealed record ImportIssue(string Code, string Message, bool IsWarning = true);

/// <summary>中性 Layout：把各格式对网格的描述收敛成同一组字段。</summary>
public sealed class NeutralLayout
{
    public int Columns { get; init; }
    public int Rows { get; init; }
    public double StepSizeX { get; init; }
    public double StepSizeY { get; init; }
    public OriginLocation OriginLocation { get; init; } = OriginLocation.LowerLeft;
    public AxisDirection AxisDirection { get; init; } = AxisDirection.UpRight;
    public WaferOrientation Orientation { get; init; } = WaferOrientation.Degree0;
    public bool IsValid => Columns > 0 && Rows > 0;
}

/// <summary>
/// 所有格式适配器共用的内部表示。对应 Artwork 的"neutral map structure"：
/// 先读成它，再从它写出任意目标格式，避免 N×M 个转换器。
/// </summary>
public sealed class NeutralWaferMap
{
    public WaferMapFormat Format { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string SubstrateId { get; set; } = string.Empty;
    public SubstrateType SubstrateType { get; set; } = SubstrateType.Wafer;
    public NeutralLayout Layout { get; set; } = new();

    private readonly Dictionary<string, NeutralBinDefinition> _binDefinitions =
        new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, NeutralBinDefinition> BinDefinitions => _binDefinitions;

    private readonly Dictionary<(int X, int Y), NeutralDie> _dies = [];
    public IReadOnlyDictionary<(int X, int Y), NeutralDie> Dies => _dies;

    private readonly List<NeutralTransfer> _transfers = [];
    public IReadOnlyList<NeutralTransfer> Transfers => _transfers;

    private readonly List<ImportIssue> _issues = [];
    public IReadOnlyList<ImportIssue> Issues => _issues;

    public void AddBin(NeutralBinDefinition def) => _binDefinitions[def.Code] = def;

    public void AddDie(NeutralDie die) => _dies[(die.X, die.Y)] = die;

    public void AddTransfer(NeutralTransfer transfer) => _transfers.Add(transfer);

    public void AddIssue(string code, string message, bool isWarning = true) =>
        _issues.Add(new ImportIssue(code, message, isWarning));

    public NeutralDie? GetDie(int x, int y) =>
        _dies.TryGetValue((x, y), out var die) ? die : null;

    public IEnumerable<NeutralDie> GoodDies =>
        _dies.Values.Where(d => d.IsDie && d.Class == NeutralBinClass.Pass);

    /// <summary>按 bin 类别统计，用于快速做 map 一致性校验。</summary>
    public IReadOnlyDictionary<NeutralBinClass, int> ClassCounts() =>
        _dies.Values.GroupBy(d => d.Class)
            .ToDictionary(g => g.Key, g => g.Count());
}
