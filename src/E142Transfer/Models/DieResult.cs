namespace E142Transfer.Models;

/// <summary>
/// 单个 die 的完整判定结果：三映射 + bin + 下游去向。
/// 这是你上位机里"晶圆内分析"与"跨站追溯"共用的核心记录。
/// </summary>
public sealed class DieResult
{
    /// <summary>阵列坐标（已归一到文件声明的坐标系）。</summary>
    public int X { get; set; }

    /// <summary>阵列坐标（已归一到文件声明的坐标系）。</summary>
    public int Y { get; set; }

    /// <summary>E142 的原始 bin 码值（解码后的字符串，如 "P" / "001" / "X"）。</summary>
    public string BinCode { get; set; } = string.Empty;

    /// <summary>BinCode 定义表翻译后的语义。</summary>
    public BinDefinition? Bin { get; set; }

    /// <summary>是否在 Layout 的有效 die 区内（false = NullBin 空位）。</summary>
    public bool IsDie { get; set; } = true;

    /// <summary>EL 映射判定。</summary>
    public string? ElClass { get; set; }

    /// <summary>PL 映射判定。</summary>
    public string? PlClass { get; set; }

    /// <summary>AOI 映射判定。</summary>
    public string? AoiClass { get; set; }

    /// <summary>
    /// 该 die 的下游去向。一颗 die 可能进多个封装位（chiplet 复制）或多个载具，
    /// 因此是列表而非单值。
    /// </summary>
    public List<TransferRef> Transfers { get; } = [];

    public DieIdentity ToIdentity(SubstrateType sourceType, string sourceId) =>
        new(sourceType, sourceId, X, Y);
}

/// <summary>
/// BinCode 定义表中的一行：码值 → 语义。
/// 对应 XSD 的 BinDefinitionType。
/// </summary>
public sealed class BinDefinition
{
    /// <summary>码值字符串，如 "P" / "001" / "FF"。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>人类可读描述，如 "FIRST_FAIL" / "漏电超标"。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>质量类别。</summary>
    public BinQuality Quality { get; set; } = BinQuality.Unknown;

    /// <summary>该码在阵列中出现的次数（E142 可选属性）。</summary>
    public int? Count { get; set; }

    /// <summary>是否被 die attach 拾取（E142 可选属性 Pick）。</summary>
    public bool? Pick { get; set; }

    public bool IsPass => Quality == BinQuality.Pass || Pick == true;
    public bool IsNull => Quality == BinQuality.Null;

    public override string ToString() => $"{Code} → {Description} ({Quality})";
}
