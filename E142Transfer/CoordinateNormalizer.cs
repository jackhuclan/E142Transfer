using E142Transfer.Models;

namespace E142Transfer;

/// <summary>
/// 把 E142 的"文件坐标"归一到统一的"逻辑坐标"（行从上到下、列从左到右）。
///
/// E142 里两个 die 坐标含义由 SubstrateMap 上的两个属性决定：
///  - OriginLocation：行列的计数起点（LowerLeft 为默认）
///  - AxisDirection ：X/Y 递增方向（UpRight 为默认）
/// 另外 Orientation 表示整个载体相对基准（notch）的旋转。
///
/// .warn 这一步没做对，transfer map 的坐标会整体转 90° 或偏移一行，
/// 而且文件本身不会报错——这是 KGD 追溯里最常见的坑。
/// </summary>
public static class CoordinateNormalizer
{
    /// <summary>归一后的逻辑坐标：Row 自上而下 0..rows-1，Col 自左而右 0..cols-1。</summary>
    public readonly record struct LogicalPoint(int Col, int Row);

    /// <summary>
    /// 文件坐标 (x, y) → 逻辑坐标 (col, row)。
    /// </summary>
    public static LogicalPoint ToLogical(
        int x,
        int y,
        int columns,
        int rows,
        OriginLocation origin,
        AxisDirection axis)
    {
        var (colDir, rowDir) = GetDirectionVectors(axis);
        var (originCol, originRow) = GetOriginCorner(origin, columns, rows);

        var col = originCol + x * colDir;
        var row = originRow + y * rowDir;

        // Center 原点时 x/y 以中心为 0，需直接映射
        if (origin == OriginLocation.Center)
        {
            col = columns / 2 + x * colDir;
            row = rows / 2 + y * rowDir;
        }

        return new LogicalPoint(col, row);
    }

    /// <summary>
    /// 逻辑坐标 → 文件坐标（ToLogical 的逆运算，用于生成文件）。
    /// </summary>
    public static (int X, int Y) ToFile(
        int col,
        int row,
        int columns,
        int rows,
        OriginLocation origin,
        AxisDirection axis)
    {
        var (colDir, rowDir) = GetDirectionVectors(axis);
        var (originCol, originRow) = GetOriginCorner(origin, columns, rows);

        if (origin == OriginLocation.Center)
            return ((col - columns / 2) * colDir, (row - rows / 2) * rowDir);

        return ((col - originCol) * colDir, (row - originRow) * rowDir);
    }

    /// <summary>把逻辑坐标转成物理坐标（mm），以晶圆中心为原点。</summary>
    public static (double PxMm, double PyMm) ToPhysical(
        LogicalPoint point,
        E142Layout layout,
        WaferOrientation orientation)
    {
        var centerCol = (layout.Columns - 1) / 2.0;
        var centerRow = (layout.Rows - 1) / 2.0;
        var dx = (point.Col - centerCol) * layout.StepSizeX;
        var dy = (centerRow - point.Row) * layout.StepSizeY; // 逻辑 row 向下，物理 Y 向上

        return orientation switch
        {
            WaferOrientation.Degree0 => (dx, dy),
            WaferOrientation.Degree90 => (dy, -dx),
            WaferOrientation.Degree180 => (-dx, -dy),
            WaferOrientation.Degree270 => (-dy, dx),
            _ => (dx, dy),
        };
    }

    /// <summary>根据 Origin + Axis 推断 X/Y 的递增方向（+1 / -1）。</summary>
    private static (int ColDir, int RowDir) GetDirectionVectors(AxisDirection axis) => axis switch
    {
        AxisDirection.UpRight => (1, -1),
        AxisDirection.DownRight => (1, 1),
        AxisDirection.UpLeft => (-1, -1),
        AxisDirection.DownLeft => (-1, 1),
        _ => (1, -1),
    };

    private static (int OriginCol, int OriginRow) GetOriginCorner(
        OriginLocation origin, int columns, int rows) => origin switch
    {
        OriginLocation.LowerLeft => (0, rows - 1),
        OriginLocation.LowerRight => (columns - 1, rows - 1),
        OriginLocation.UpperLeft => (0, 0),
        OriginLocation.UpperRight => (columns - 1, 0),
        OriginLocation.Center => (columns / 2, rows / 2),
        _ => (0, rows - 1),
    };
}
