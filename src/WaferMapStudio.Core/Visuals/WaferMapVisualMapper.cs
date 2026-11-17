using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;

namespace WaferMapStudio.Core.Visuals;

/// <summary>
/// 中性模型 → 视觉模型的映射器。
///
/// 归一化策略：上游 Reader 理论上都已输出 0 基坐标（CSV 已平移、SINF 已减 1），
/// 但现场文件方言多，这里再做一次防御性归一化——以 die 实际坐标的最小值为基准平移，
/// 网格尺寸取 Layout 声明与实际覆盖范围的较大者，保证"图永远完整、永不越界"。
/// </summary>
public static class WaferMapVisualMapper
{
    public static WaferMapVisual Map(NeutralWaferMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var dies = map.Dies.Values.ToList();
        var layout = map.Layout;

        // 空图也要给出合法网格，避免渲染层除零/负尺寸
        if (dies.Count == 0 || !layout.IsValid)
        {
            return new WaferMapVisual
            {
                Format = map.Format,
                SourcePath = map.SourcePath,
                Columns = Math.Max(layout.Columns, 1),
                Rows = Math.Max(layout.Rows, 1),
                SubstrateId = map.SubstrateId,
                DeviceId = map.DeviceId,
                LotId = map.LotId,
                Dies = [],
                CategoryCounts = new Dictionary<DieCategory, int>(),
            };
        }

        var minX = dies.Min(d => d.X);
        var minY = dies.Min(d => d.Y);
        var columns = Math.Max(layout.Columns, dies.Max(d => d.X) - minX + 1);
        var rows = Math.Max(layout.Rows, dies.Max(d => d.Y) - minY + 1);

        // 完整网格：先全部填 Null 空位，再用实际 die 覆盖（行优先，与渲染层 Canvas 布局一致）
        var grid = new DieVisual[rows * columns];
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                grid[row * columns + col] = new DieVisual(
                    col, row, string.Empty, DieCategory.Null, null);
            }
        }

        foreach (var die in dies)
        {
            var col = die.X - minX;
            var row = die.Y - minY;
            var definition = map.BinDefinitions.GetValueOrDefault(die.BinCode);
            grid[row * columns + col] = new DieVisual(
                col,
                row,
                die.BinCode,
                CategoryOf(die),
                string.IsNullOrEmpty(definition?.Description)
                    ? die.Class.ToString()
                    : definition.Description);
        }

        var counts = grid
            .Where(d => d.Category != DieCategory.Null)
            .GroupBy(d => d.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        return new WaferMapVisual
        {
            Format = map.Format,
            SourcePath = map.SourcePath,
            Columns = columns,
            Rows = rows,
            SubstrateId = map.SubstrateId,
            DeviceId = map.DeviceId,
            LotId = map.LotId,
            ReferenceDie = map.ReferenceDie is { } reference
                ? (reference.X - minX, reference.Y - minY)
                : null,
            Dies = grid,
            CategoryCounts = counts,
        };
    }

    private static DieCategory CategoryOf(NeutralDie die) => die.Class switch
    {
        NeutralBinClass.Pass => DieCategory.Pass,
        NeutralBinClass.Fail => DieCategory.Fail,
        NeutralBinClass.Marked => DieCategory.Marked,
        NeutralBinClass.Reference => DieCategory.Reference,
        NeutralBinClass.Skip => DieCategory.Skip,
        NeutralBinClass.Edge => DieCategory.Edge,
        NeutralBinClass.Unknown => DieCategory.Unknown,
        _ => DieCategory.Null,
    };
}
