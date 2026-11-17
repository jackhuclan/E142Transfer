using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;

namespace WaferMapStudio.Core.Visuals;

/// <summary>
/// 渲染就绪的晶圆图：完整网格（含 Null 空位以呈现晶圆外形）、
/// 归一化 0 基坐标、按类别统计与良率。由 <see cref="WaferMapVisualMapper"/> 从中性模型生成。
/// </summary>
public sealed class WaferMapVisual
{
    public required WaferMapFormat Format { get; init; }
    public required string SourcePath { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public string SubstrateId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string LotId { get; init; } = string.Empty;

    /// <summary>参考 die（归一化 0 基坐标）；上游未提供时为 null。</summary>
    public (int Col, int Row)? ReferenceDie { get; init; }

    /// <summary>完整网格：Columns × Rows，含 Null 空位；行优先（Row 外层、Col 内层）。</summary>
    public required IReadOnlyList<DieVisual> Dies { get; init; }

    /// <summary>按类别计数（不含 Null 空位则查 <see cref="DieCategory.Null"/> 项）。</summary>
    public required IReadOnlyDictionary<DieCategory, int> CategoryCounts { get; init; }

    /// <summary>有效 die 总数（不含 Null 空位）。</summary>
    public int DieCount => CategoryCounts.Where(kv => kv.Key != DieCategory.Null)
        .Sum(kv => kv.Value);

    /// <summary>良率 = Pass / 有效 die；无有效 die 时为 null（不可误显示为 0%）。</summary>
    public double? Yield =>
        CategoryCounts.TryGetValue(DieCategory.Pass, out var pass) && DieCount > 0
            ? pass * 100.0 / DieCount
            : null;
}
