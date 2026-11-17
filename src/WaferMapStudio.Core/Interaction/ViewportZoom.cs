namespace WaferMapStudio.Core.Interaction;

/// <summary>
/// 视口缩放/平移状态机：把「滚轮缩放 + 拖动平移」的几何计算从 WPF 控件里抽出来。
///
/// 放在 Core 而不是 Rendering，是因为这里只有纯几何：不引用任何 UI 类型，
/// 可以直接跨平台单测（锚点不漂移、上下限钳制这类规则靠界面手点很难验）。
/// 渲染层只负责把 <see cref="Scale"/>/<see cref="OffsetX"/>/<see cref="OffsetY"/>
/// 写进 ScaleTransform + TranslateTransform。
///
/// 坐标约定与 WPF RenderTransform 一致：<c>屏幕坐标 = 内容坐标 × Scale + Offset</c>，
/// 变换原点在视口左上角。<c>Scale = MinScale</c>（默认 1.0）表示「整图适应视口」，
/// 等价于未缩放时 Viewbox 的等比自适应，此时偏移恒为 0（画面居中）。
/// </summary>
public sealed class ViewportZoom
{
    /// <summary>每 120 单位滚轮增量（一格）对应的缩放倍数。</summary>
    public const double WheelStep = 1.15;

    /// <summary>浮点比较容差：低于该差值视为「没有变化」，避免重复刷新变换。</summary>
    private const double Epsilon = 1e-9;

    public ViewportZoom(double minScale = 1.0, double maxScale = 8.0)
    {
        if (!(minScale > 0) || double.IsInfinity(minScale))
        {
            throw new ArgumentOutOfRangeException(nameof(minScale), minScale, "最小缩放必须是有限正数。");
        }

        if (!(maxScale > minScale) || double.IsInfinity(maxScale))
        {
            throw new ArgumentOutOfRangeException(nameof(maxScale), maxScale, "最大缩放必须大于最小缩放。");
        }

        MinScale = minScale;
        MaxScale = maxScale;
        Scale = minScale;
    }

    /// <summary>缩放下限（1.0 = 适应视口，不允许缩得比整图更小，否则四周出现空白带）。</summary>
    public double MinScale { get; }

    /// <summary>缩放上限：防止长时间滚动把内容放大到内存与渲染都失控。</summary>
    public double MaxScale { get; }

    public double Scale { get; private set; }

    /// <summary>内容在视口坐标系中的平移量（屏幕像素，未放大时恒为 0）。</summary>
    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    /// <summary>是否已放大（决定能否平移）。</summary>
    public bool IsZoomed => Scale > MinScale + Epsilon;

    /// <summary>
    /// 按滚轮增量缩放：<paramref name="anchorX"/>/<paramref name="anchorY"/> 是鼠标在
    /// <b>内容坐标</b>中的位置，缩放后该点仍停在鼠标下方（以鼠标为中心放大/缩小）。
    /// </summary>
    /// <returns>缩放或偏移是否真的发生变化（已到上/下限时返回 false）。</returns>
    public bool ZoomByWheel(int wheelDelta, double anchorX, double anchorY,
        double viewportWidth, double viewportHeight)
    {
        if (wheelDelta == 0)
        {
            return false;
        }

        // 一格 = 120；支持高精度滚轮的小增量（部分触控板按 30/40 报送）
        var factor = Math.Pow(WheelStep, wheelDelta / 120.0);
        return ZoomTo(Scale * factor, anchorX, anchorY, viewportWidth, viewportHeight);
    }

    /// <summary>缩放到指定倍率，并让锚点保持不动。</summary>
    public bool ZoomTo(double targetScale, double anchorX, double anchorY,
        double viewportWidth, double viewportHeight)
    {
        if (double.IsNaN(targetScale) || double.IsInfinity(targetScale))
        {
            return false;
        }

        var clamped = Math.Clamp(targetScale, MinScale, MaxScale);
        if (Math.Abs(clamped - Scale) < Epsilon)
        {
            // 已在上/下限：仍尝试纠正偏移（内容可能因窗口尺寸变化需要重新钳制）
            return Clamp(viewportWidth, viewportHeight);
        }

        // 先记住锚点当前落在屏幕上的位置，缩放后再反解偏移量把它按回原位
        var screenX = anchorX * Scale + OffsetX;
        var screenY = anchorY * Scale + OffsetY;

        Scale = clamped;
        OffsetX = screenX - anchorX * Scale;
        OffsetY = screenY - anchorY * Scale;

        Clamp(viewportWidth, viewportHeight);
        return true;
    }

    /// <summary>平移（增量单位为屏幕像素）；未放大时无效，避免整图被拖出视口。</summary>
    public bool PanBy(double deltaX, double deltaY,
        double viewportWidth, double viewportHeight)
    {
        if (!IsZoomed || (Math.Abs(deltaX) < Epsilon && Math.Abs(deltaY) < Epsilon))
        {
            return false;
        }

        var x = ClampOffset(OffsetX + deltaX, viewportWidth);
        var y = ClampOffset(OffsetY + deltaY, viewportHeight);
        if (Math.Abs(x - OffsetX) < Epsilon && Math.Abs(y - OffsetY) < Epsilon)
        {
            return false;
        }

        OffsetX = x;
        OffsetY = y;
        return true;
    }

    /// <summary>回到「整图适应视口」。</summary>
    public bool Reset()
    {
        if (Math.Abs(Scale - MinScale) < Epsilon
            && Math.Abs(OffsetX) < Epsilon
            && Math.Abs(OffsetY) < Epsilon)
        {
            return false;
        }

        Scale = MinScale;
        OffsetX = 0;
        OffsetY = 0;
        return true;
    }

    /// <summary>
    /// 把偏移量钳制在「内容始终铺满视口」的范围内：放大后不允许拖出空白边，
    /// 未放大时强制归零（居中）。视口尺寸未知（布局尚未完成）时保持原状。
    /// </summary>
    public bool Clamp(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        var x = ClampOffset(OffsetX, viewportWidth);
        var y = ClampOffset(OffsetY, viewportHeight);
        if (Math.Abs(x - OffsetX) < Epsilon && Math.Abs(y - OffsetY) < Epsilon)
        {
            return false;
        }

        OffsetX = x;
        OffsetY = y;
        return true;
    }

    private double ClampOffset(double offset, double viewport)
    {
        if (viewport <= 0 || !IsZoomed)
        {
            return 0;
        }

        // 内容尺寸 = viewport × Scale：左/上边界最大为 0，右/下边界贴住视口
        return Math.Clamp(offset, viewport * (1 - Scale), 0);
    }
}
