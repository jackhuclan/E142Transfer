namespace WaferMapStudio.Core.Visuals;

/// <summary>
/// 单个 die 的视觉数据：网格坐标 + bin 码 + 类别。
/// 坐标一律是归一化后的 0 基阵列坐标（列/行），像素换算归 Rendering。
/// </summary>
public sealed record DieVisual(
    int Col,
    int Row,
    string BinCode,
    DieCategory Category,
    string? Description)
{
    public bool IsRealDie => Category != DieCategory.Null;
}
