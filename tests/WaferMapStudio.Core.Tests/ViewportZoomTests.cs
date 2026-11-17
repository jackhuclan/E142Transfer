using WaferMapStudio.Core.Interaction;
using Xunit;

namespace WaferMapStudio.Core.Tests;

/// <summary>
/// 视口缩放/平移几何：锚点不漂移、上下限钳制、未放大时不得平移。
/// 这些规则靠界面手点验证既慢又难覆盖边界，因此算法层单独测。
/// </summary>
public sealed class ViewportZoomTests
{
    private const double ViewportWidth = 800;
    private const double ViewportHeight = 600;

    private static double ScreenX(ViewportZoom zoom, double contentX) => contentX * zoom.Scale + zoom.OffsetX;
    private static double ScreenY(ViewportZoom zoom, double contentY) => contentY * zoom.Scale + zoom.OffsetY;

    [Fact]
    public void NewInstance_StartsAtFitWithoutOffset()
    {
        var zoom = new ViewportZoom();

        Assert.Equal(1.0, zoom.Scale);
        Assert.Equal(0.0, zoom.OffsetX);
        Assert.Equal(0.0, zoom.OffsetY);
        Assert.False(zoom.IsZoomed);
    }

    [Fact]
    public void WheelUp_ZoomsInByOneStep()
    {
        var zoom = new ViewportZoom();

        var changed = zoom.ZoomByWheel(120, 100, 100, ViewportWidth, ViewportHeight);

        Assert.True(changed);
        Assert.Equal(ViewportZoom.WheelStep, zoom.Scale, precision: 9);
        Assert.True(zoom.IsZoomed);
    }

    [Fact]
    public void WheelUp_KeepsPointUnderCursorFixed()
    {
        var zoom = new ViewportZoom();
        const double anchorX = 320;
        const double anchorY = 210;

        zoom.ZoomByWheel(120, anchorX, anchorY, ViewportWidth, ViewportHeight);

        // 锚点在屏幕上的落点前后一致 = 以鼠标为中心放大
        Assert.Equal(anchorX, ScreenX(zoom, anchorX), precision: 6);
        Assert.Equal(anchorY, ScreenY(zoom, anchorY), precision: 6);
    }

    [Fact]
    public void WheelDown_ZoomsBackOutToFitAndCenters()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomByWheel(120, 100, 100, ViewportWidth, ViewportHeight);

        var changed = zoom.ZoomByWheel(-120, 100, 100, ViewportWidth, ViewportHeight);

        Assert.True(changed);
        Assert.Equal(1.0, zoom.Scale, precision: 9);
        // 回到适应视口后必须归零，否则整图会偏出视野
        Assert.Equal(0.0, zoom.OffsetX, precision: 9);
        Assert.Equal(0.0, zoom.OffsetY, precision: 9);
        Assert.False(zoom.IsZoomed);
    }

    [Fact]
    public void WheelUp_StopsAtMaxScale()
    {
        var zoom = new ViewportZoom(maxScale: 4.0);

        for (var i = 0; i < 100; i++)
        {
            zoom.ZoomByWheel(120, 400, 300, ViewportWidth, ViewportHeight);
        }

        Assert.Equal(4.0, zoom.Scale, precision: 9);
        Assert.False(zoom.ZoomByWheel(120, 400, 300, ViewportWidth, ViewportHeight));
        Assert.Equal(4.0, zoom.Scale, precision: 9);
    }

    [Fact]
    public void WheelDown_StopsAtMinScaleAndNeverGoesBelowFit()
    {
        var zoom = new ViewportZoom();

        for (var i = 0; i < 100; i++)
        {
            zoom.ZoomByWheel(-120, 400, 300, ViewportWidth, ViewportHeight);
        }

        Assert.Equal(1.0, zoom.Scale, precision: 9);
        Assert.Equal(0.0, zoom.OffsetX, precision: 9);
        Assert.Equal(0.0, zoom.OffsetY, precision: 9);
    }

    [Fact]
    public void ZoomAtDifferentAnchor_KeepsThatAnchorFixed()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomTo(2.5, 120, 90, ViewportWidth, ViewportHeight);

        const double anchorX = 640;
        const double anchorY = 470;
        // 锚点坐标是「内容坐标」：要保证的是它在屏幕上的落点前后不变
        var beforeX = ScreenX(zoom, anchorX);
        var beforeY = ScreenY(zoom, anchorY);

        zoom.ZoomTo(5.0, anchorX, anchorY, ViewportWidth, ViewportHeight);

        Assert.Equal(5.0, zoom.Scale, precision: 9);
        Assert.Equal(beforeX, ScreenX(zoom, anchorX), precision: 6);
        Assert.Equal(beforeY, ScreenY(zoom, anchorY), precision: 6);
    }

    [Fact]
    public void ZoomOutToFit_FromZoomedState_ResetsOffset()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomTo(3.0, 700, 500, ViewportWidth, ViewportHeight);

        zoom.ZoomTo(1.0, 700, 500, ViewportWidth, ViewportHeight);

        Assert.Equal(1.0, zoom.Scale, precision: 9);
        Assert.Equal(0.0, zoom.OffsetX, precision: 9);
        Assert.Equal(0.0, zoom.OffsetY, precision: 9);
    }

    [Fact]
    public void PanAtFit_IsIgnored()
    {
        var zoom = new ViewportZoom();

        var changed = zoom.PanBy(120, -80, ViewportWidth, ViewportHeight);

        Assert.False(changed);
        Assert.Equal(0.0, zoom.OffsetX);
        Assert.Equal(0.0, zoom.OffsetY);
    }

    [Fact]
    public void PanWhenZoomed_MovesContent()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomTo(2.0, 400, 300, ViewportWidth, ViewportHeight);
        var startX = zoom.OffsetX;
        var startY = zoom.OffsetY;

        var changed = zoom.PanBy(40, 25, ViewportWidth, ViewportHeight);

        Assert.True(changed);
        Assert.Equal(startX + 40, zoom.OffsetX, precision: 9);
        Assert.Equal(startY + 25, zoom.OffsetY, precision: 9);
    }

    [Fact]
    public void Pan_IsClampedSoContentAlwaysCoversViewport()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomTo(2.0, 400, 300, ViewportWidth, ViewportHeight);

        zoom.PanBy(100_000, 100_000, ViewportWidth, ViewportHeight);
        Assert.Equal(0.0, zoom.OffsetX, precision: 9);
        Assert.Equal(0.0, zoom.OffsetY, precision: 9);

        zoom.PanBy(-100_000, -100_000, ViewportWidth, ViewportHeight);
        Assert.Equal(ViewportWidth * (1 - 2.0), zoom.OffsetX, precision: 9);
        Assert.Equal(ViewportHeight * (1 - 2.0), zoom.OffsetY, precision: 9);
    }

    [Fact]
    public void Reset_ReturnsToFitOnce()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomTo(3.0, 100, 100, ViewportWidth, ViewportHeight);

        Assert.True(zoom.Reset());
        Assert.Equal(1.0, zoom.Scale, precision: 9);
        Assert.Equal(0.0, zoom.OffsetX);
        Assert.Equal(0.0, zoom.OffsetY);
        // 已在适应状态：再重置不做无谓的界面刷新
        Assert.False(zoom.Reset());
    }

    [Fact]
    public void Clamp_WithUnknownViewport_KeepsState()
    {
        var zoom = new ViewportZoom();
        zoom.ZoomTo(2.0, 400, 300, ViewportWidth, ViewportHeight);
        var offsetX = zoom.OffsetX;

        Assert.False(zoom.Clamp(0, 0));
        Assert.Equal(offsetX, zoom.OffsetX, precision: 9);
    }

    [Fact]
    public void ZoomByWheel_WithNonStepDelta_ScalesProportionally()
    {
        var zoom = new ViewportZoom();

        zoom.ZoomByWheel(240, 400, 300, ViewportWidth, ViewportHeight);

        Assert.Equal(Math.Pow(ViewportZoom.WheelStep, 2), zoom.Scale, precision: 9);
    }

    [Fact]
    public void ZoomByWheel_WithZeroDelta_DoesNothing()
    {
        var zoom = new ViewportZoom();

        Assert.False(zoom.ZoomByWheel(0, 10, 10, ViewportWidth, ViewportHeight));
        Assert.Equal(1.0, zoom.Scale);
    }

    [Theory]
    [InlineData(0.0, 8.0)]
    [InlineData(-1.0, 8.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(2.0, 1.0)]
    public void Constructor_RejectsInvalidRanges(double minScale, double maxScale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportZoom(minScale, maxScale));
    }
}
