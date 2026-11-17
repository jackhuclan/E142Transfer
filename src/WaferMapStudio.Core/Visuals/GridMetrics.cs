namespace WaferMapStudio.Core.Visuals;

/// <summary>
/// 网格几何：把「列数 / 行数 + 两轴 die 步距 + 基准格宽」换算成画布尺寸与单格宽高。
///
/// 关键约束——**两轴必须共用同一套换算比例**：
/// <code>
/// CellWidth  / StepSizeX == CellHeight / StepSizeY
/// </code>
/// 即 1 µm 在 X 与 Y 方向映射到相同像素数。这样：
/// <list type="bullet">
/// <item>die 保持真实长宽比（3600×1580 µm 的 die 不会被画成正方形）；</item>
/// <item>圆晶圆在屏幕上仍是正圆（列数×Xpitch 与行数×Ypitch 本就相等）；</item>
/// <item>行列数各自等于数据本身的 Columns / Rows，不会为了"看起来整齐"而插空位。</item>
/// </list>
///
/// 若上游未给步距（StepSizeX/Y ≤ 0 或非有限值，如 CSV、部分 G85），退化为正方形格子
/// （宽高比 1.0），与历史行为一致。
/// </summary>
public readonly record struct GridMetrics(
    double CellWidth,
    double CellHeight,
    double Width,
    double Height)
{
    /// <summary>单格最小边长；再小就无法点选，也无意义。</summary>
    public const double MinCellSize = 2.0;

    /// <summary>宽高比下限（约 8:1 的细长 die 已是极端工况，防畸形数据把画布撑爆）。</summary>
    public const double MinAspect = 1.0 / 8.0;

    /// <summary>宽高比上限（同上）。</summary>
    public const double MaxAspect = 8.0;

    /// <summary>Y/X 方向的格子高度比：等于两轴步距之比。</summary>
    public double Aspect => CellWidth > 0 ? CellHeight / CellWidth : 1.0;

    /// <summary>格子是否为正方形（上游未提供步距时的退化形态）。</summary>
    public bool IsSquare => Math.Abs(CellHeight - CellWidth) < 1e-9;

    /// <summary>
    /// 像素/微米比例。两轴应相等——这是"x/y 用同一套换算规则"的可断言形式。
    /// </summary>
    public double PixelsPerUnitX(double stepSizeX) => stepSizeX > 0 ? CellWidth / stepSizeX : 0;

    /// <inheritdoc cref="PixelsPerUnitX"/>
    public double PixelsPerUnitY(double stepSizeY) => stepSizeY > 0 ? CellHeight / stepSizeY : 0;

    public static GridMetrics Compute(
        int columns,
        int rows,
        double stepSizeX,
        double stepSizeY,
        double baseCellSize)
    {
        var cols = Math.Max(columns, 1);
        var rowCount = Math.Max(rows, 1);

        var cellWidth = double.IsFinite(baseCellSize) && baseCellSize > MinCellSize
            ? baseCellSize
            : MinCellSize;

        var aspect = AspectOf(stepSizeX, stepSizeY);
        var cellHeight = cellWidth * aspect;

        return new GridMetrics(cellWidth, cellHeight, cols * cellWidth, rowCount * cellHeight);
    }

    /// <summary>
    /// 由两轴步距求格子高度比。任一维缺失、非正、非有限 → 视为正方形（1.0）；
    /// 结果再钳制到 [<see cref="MinAspect"/>, <see cref="MaxAspect"/>]。
    /// </summary>
    public static double AspectOf(double stepSizeX, double stepSizeY)
    {
        if (!double.IsFinite(stepSizeX) || !double.IsFinite(stepSizeY) ||
            stepSizeX <= 0 || stepSizeY <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(stepSizeY / stepSizeX, MinAspect, MaxAspect);
    }
}
