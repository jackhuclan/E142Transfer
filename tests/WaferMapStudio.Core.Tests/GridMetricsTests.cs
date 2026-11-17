using WaferMapStudio.Core.Visuals;
using Xunit;

namespace WaferMapStudio.Core.Tests;

/// <summary>
/// 网格几何：x/y 两轴必须共用同一套换算比例（像素/µm 相等）。
///
/// 历史缺陷：控件对两个方向都用同一个 cell 长度，遇到 3600×1580、800×1170
/// 这类非正方形 die 时，圆晶圆被画成椭圆。以下用例以真实样本的数值守住该回归。
/// </summary>
public sealed class GridMetricsTests
{
    private const double BaseCell = 16.0;

    [Fact]
    public void SquarePitch_ProducesSquareCells()
    {
        var grid = GridMetrics.Compute(columns: 40, rows: 30, stepSizeX: 1000, stepSizeY: 1000, BaseCell);

        Assert.Equal(BaseCell, grid.CellWidth, precision: 9);
        Assert.Equal(BaseCell, grid.CellHeight, precision: 9);
        Assert.Equal(40 * BaseCell, grid.Width, precision: 6);
        Assert.Equal(30 * BaseCell, grid.Height, precision: 6);
        Assert.True(grid.IsSquare);
    }

    [Fact]
    public void TallDie_TskSample_RestoresCircularFootprint()
    {
        // TSK Q4FR02-02B4.DAT：55 列 × 125 行，die 3600 × 1580 µm（物理幅面 198.0 × 197.5 mm）
        var grid = GridMetrics.Compute(columns: 55, rows: 125, stepSizeX: 3600, stepSizeY: 1580, BaseCell);

        Assert.Equal(BaseCell, grid.CellWidth, precision: 9);
        Assert.True(grid.CellHeight < grid.CellWidth, "高 1580 宽 3600 的 die，格子应更扁");

        // 画布宽高比应逼近物理幅面比 198.0 / 197.5 ≈ 1.003，而不是列行数比 55 / 125 = 0.44
        Assert.Equal(1.003, grid.Width / grid.Height, precision: 2);
    }

    [Fact]
    public void WideDie_KlarfSample_RestoresCircularFootprint()
    {
        // KLARF KC07043-14G1：238 列 × 159 行，DiePitch 800 × 1170 µm（物理幅面 190.4 × 186.0 mm）
        var grid = GridMetrics.Compute(columns: 238, rows: 159, stepSizeX: 800, stepSizeY: 1170, BaseCell);

        Assert.True(grid.CellHeight > grid.CellWidth);
        Assert.Equal(190.4 / 186.0, grid.Width / grid.Height, precision: 2);
    }

    [Theory]
    [InlineData(3600, 1580)]
    [InlineData(800, 1170)]
    [InlineData(1000, 1000)]
    [InlineData(1250, 250)]
    public void BothAxes_ShareSamePixelsPerMicron(double stepX, double stepY)
    {
        var grid = GridMetrics.Compute(columns: 60, rows: 45, stepX, stepY, BaseCell);

        Assert.Equal(
            grid.PixelsPerUnitX(stepX),
            grid.PixelsPerUnitY(stepY),
            precision: 9);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 800)]
    [InlineData(800, double.NaN)]
    [InlineData(double.PositiveInfinity, 800)]
    public void MissingOrInvalidPitch_FallsBackToSquareCells(double stepX, double stepY)
    {
        var grid = GridMetrics.Compute(columns: 10, rows: 10, stepX, stepY, BaseCell);

        Assert.Equal(BaseCell, grid.CellWidth, precision: 9);
        Assert.Equal(BaseCell, grid.CellHeight, precision: 9);
        Assert.True(grid.IsSquare);
    }

    [Fact]
    public void ExtremeAspect_IsClamped()
    {
        var flat = GridMetrics.Compute(columns: 4, rows: 4, stepSizeX: 100_000, stepSizeY: 1, BaseCell);
        var tall = GridMetrics.Compute(columns: 4, rows: 4, stepSizeX: 1, stepSizeY: 100_000, BaseCell);

        Assert.Equal(GridMetrics.MinAspect, flat.Aspect, precision: 9);
        Assert.Equal(GridMetrics.MaxAspect, tall.Aspect, precision: 9);
    }

    [Fact]
    public void DegenerateInputs_NeverProduceZeroSizedCanvas()
    {
        var grid = GridMetrics.Compute(columns: 0, rows: -5, stepSizeX: 800, stepSizeY: 1170, baseCellSize: 0);

        Assert.True(grid.Width > 0);
        Assert.True(grid.Height > 0);
        Assert.True(grid.CellWidth >= GridMetrics.MinCellSize);
    }

    [Fact]
    public void RenderedColumnAndRowCounts_MatchSourceGrid()
    {
        // 渲染层按 Col*CellWidth / Row*CellHeight 定位、画布按 Columns/Rows 计算，
        // 因此格子数在 x/y 上分别严格等于数据的 Columns / Rows，不会被补齐或裁掉
        const int columns = 55;
        const int rows = 125;
        var grid = GridMetrics.Compute(columns, rows, stepSizeX: 3600, stepSizeY: 1580, BaseCell);

        Assert.Equal(columns, (int)Math.Round(grid.Width / grid.CellWidth));
        Assert.Equal(rows, (int)Math.Round(grid.Height / grid.CellHeight));
    }
}
