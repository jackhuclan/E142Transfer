namespace WaferMapStudio.Core.Visuals;

/// <summary>图例条目：一段"码值 → 颜色 → 含义 → 数量"的展示数据。</summary>
public sealed record LegendEntry(
    string Key,
    string Label,
    DieColor Color,
    int Count);
