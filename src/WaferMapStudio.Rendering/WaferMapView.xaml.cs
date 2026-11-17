using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WaferMapStudio.Core.Interaction;
using WaferMapStudio.Core.Visuals;

namespace WaferMapStudio.Rendering;

/// <summary>
/// 图例条目的渲染包装：Core 的 <see cref="LegendEntry"/> + 已转换的 Brush。
/// </summary>
public sealed class LegendItemView
{
    public LegendItemView(LegendEntry entry, Brush brush)
    {
        Label = entry.Label;
        Brush = brush;
        CountText = $"({entry.Count})";
    }

    public string Label { get; }
    public Brush Brush { get; }
    public string CountText { get; }
}

/// <summary>
/// 可复用晶圆图渲染控件。
///
/// 独立于 ViewModel：数据入口是 <see cref="Map"/>（Core 视觉模型）+
/// <see cref="Palette"/>（配色）；点选结果通过 <see cref="SelectedDie"/> 依赖属性
/// 双向暴露，宿主 XAML 可直接 <c>SelectedDie="{Binding SelectedDie, Mode=TwoWay}"</c>。
///
/// 交互：滚轮缩放（以鼠标位置为锚点，1.0 = 适应视口，上限见 <see cref="MaxZoom"/>）、
/// 放大后中键/右键拖动平移、左键点选 die。几何计算在
/// <see cref="ViewportZoom"/>（Core，可单测），本类只负责把结果写进变换。
/// </summary>
public partial class WaferMapView : UserControl
{
    private const string AccentHex = "#2563EB";

    /// <summary>缩放上限：再大也难看清单 die，且会把栅格撑到极限，8 倍足够逐格核对。</summary>
    public const double MaxZoom = 8.0;

    private readonly Brush _accentBrush;
    private readonly ViewportZoom _zoom = new(minScale: 1.0, maxScale: MaxZoom);

    private DieVisual? _selectedDie;

    /// <summary>平移拖动起点（控件坐标，屏幕像素）；null 表示未在拖动。</summary>
    private Point? _panAnchor;

    public WaferMapView()
    {
        InitializeComponent();
        _accentBrush = FrozenBrush(AccentHex);

        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
        PreviewMouseDown += OnPreviewMouseDown;
        MouseMove += OnMouseMove;
        PreviewMouseUp += OnPreviewMouseUp;
        LostMouseCapture += OnLostMouseCapture;
        MapViewbox.SizeChanged += OnViewboxSizeChanged;
    }

    /// <summary>单格边长（设备无关像素）。die 间留 1px 间隙便于区分相邻同色格。</summary>
    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(double), typeof(WaferMapView),
        new PropertyMetadata(16.0, OnRenderInputChanged));

    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(WaferMapVisual), typeof(WaferMapView),
        new PropertyMetadata(null, OnMapChanged));

    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette), typeof(BinColorPalette), typeof(WaferMapView),
        new PropertyMetadata(null, OnRenderInputChanged));

    public static readonly DependencyProperty ShowLegendProperty = DependencyProperty.Register(
        nameof(ShowLegend), typeof(bool), typeof(WaferMapView),
        new PropertyMetadata(true, OnShowLegendChanged));

    public static readonly DependencyProperty SelectedDieProperty = DependencyProperty.Register(
        nameof(SelectedDie), typeof(DieVisual), typeof(WaferMapView),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDieChanged));

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    /// <summary>要渲染的晶圆图（完整网格，含 Null 空位）。</summary>
    public WaferMapVisual? Map
    {
        get => (WaferMapVisual?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    /// <summary>配色；null 时用默认调色板。</summary>
    public BinColorPalette? Palette
    {
        get => (BinColorPalette?)GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public bool ShowLegend
    {
        get => (bool)GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
    }

    /// <summary>当前点选的 die；点击空白处清除。双向绑定到宿主视图模型。</summary>
    public DieVisual? SelectedDie
    {
        get => (DieVisual?)GetValue(SelectedDieProperty);
        set => SetValue(SelectedDieProperty, value);
    }

    /// <summary>当前缩放倍数（1.0 = 适应视口）。</summary>
    public double Zoom => _zoom.Scale;

    private bool HasMap => Map is { Dies.Count: > 0 };

    private static void OnMapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaferMapView)d;
        // 换一份数据就回到适应视口：上一份的缩放/平移对新的网格没有意义
        view.ResetZoom();
        view.Rebuild();
    }

    private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaferMapView)d).Rebuild();

    private static void OnShowLegendChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaferMapView)d).UpdateLegendVisibility();

    private static void OnSelectedDieChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaferMapView)d).HighlightSelected((DieVisual?)e.NewValue);

    private BinColorPalette EffectivePalette => Palette ?? BinColorPalette.FromMap(
        Map ?? new WaferMapVisual
        {
            Format = E142Transfer.Import.Abstractions.WaferMapFormat.Unknown,
            SourcePath = string.Empty,
            Columns = 1,
            Rows = 1,
            Dies = [],
            CategoryCounts = new Dictionary<DieCategory, int>(),
        });

    /// <summary>重建画布与图例。地图、配色、格距任一变化都会触发。</summary>
    private void Rebuild()
    {
        MapCanvas.Children.Clear();
        _selectedDie = null;

        var map = Map;
        if (map is null || map.Dies.Count == 0)
        {
            MapCanvas.Width = 0;
            MapCanvas.Height = 0;
            LegendHost.Visibility = Visibility.Collapsed;
            return;
        }

        var cell = Math.Max(CellSize, 2);
        MapCanvas.Width = map.Columns * cell;
        MapCanvas.Height = map.Rows * cell;
        MapCanvas.ClipToBounds = true;

        var palette = EffectivePalette;
        var nullBrush = FrozenBrush(BinColorPalette.CategoryColor(DieCategory.Null).ToHex());

        foreach (var die in map.Dies)
        {
            var rect = new Rectangle
            {
                Width = cell - 1,
                Height = cell - 1,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = die.Category == DieCategory.Null
                    ? nullBrush
                    : FrozenBrush(palette.ColorOf(die).ToHex()),
                StrokeThickness = 0,
                Tag = die,
                Cursor = Cursors.Hand,
                ToolTip = BuildTooltip(die),
            };

            Canvas.SetLeft(rect, die.Col * cell + 0.5);
            Canvas.SetTop(rect, die.Row * cell + 0.5);
            MapCanvas.Children.Add(rect);
        }

        MarkReferenceDie(map, cell);
        RebuildLegend(map, palette);
        UpdateLegendVisibility();

        if (SelectedDie is not null)
        {
            _selectedDie = SelectedDie;
            HighlightSelected(SelectedDie);
        }
    }

    private void MarkReferenceDie(WaferMapVisual map, double cell)
    {
        if (map.ReferenceDie is not { } reference)
        {
            return;
        }

        var rect = new Rectangle
        {
            Width = cell,
            Height = cell,
            Fill = Brushes.Transparent,
            Stroke = _accentBrush,
            StrokeThickness = 2,
            StrokeDashArray = [2, 2],
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, reference.Col * cell);
        Canvas.SetTop(rect, reference.Row * cell);
        MapCanvas.Children.Add(rect);
    }

    private void RebuildLegend(WaferMapVisual map, BinColorPalette palette)
    {
        var entries = palette.BuildLegend(map)
            .Select(e => new LegendItemView(e, FrozenBrush(e.Color.ToHex())))
            .ToList();
        LegendItems.ItemsSource = entries;
    }

    private void UpdateLegendVisibility() =>
        LegendHost.Visibility = ShowLegend && Map is not null && Map.Dies.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ========== 缩放与平移 ==========

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || !HasMap)
        {
            return;
        }

        // 注意：GetPosition(MapViewbox) 返回的是未变换的「内容坐标」，
        // 正是 ViewportZoom 需要的锚点（缩放后该点仍停在鼠标下）
        var anchor = e.GetPosition(MapViewbox);
        if (_zoom.ZoomByWheel(e.Delta, anchor.X, anchor.Y,
                MapViewbox.ActualWidth, MapViewbox.ActualHeight))
        {
            ApplyZoom();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_panAnchor is not null || !_zoom.IsZoomed || !HasMap)
        {
            return;
        }

        // 左键留给 die 点选，这里只接管中键/右键，避免改变既有选中交互
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right))
        {
            return;
        }

        _panAnchor = e.GetPosition(this);
        Mouse.OverrideCursor = Cursors.SizeAll;
        _ = CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panAnchor is not { } anchor)
        {
            return;
        }

        var current = e.GetPosition(this);
        // 增量本身是屏幕像素，直接交给 ViewportZoom 做边界钳制
        if (_zoom.PanBy(current.X - anchor.X, current.Y - anchor.Y,
                MapViewbox.ActualWidth, MapViewbox.ActualHeight))
        {
            ApplyZoom();
        }

        _panAnchor = current;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panAnchor is null)
        {
            return;
        }

        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right))
        {
            return;
        }

        EndPan();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) => EndPan();

    private void EndPan()
    {
        if (_panAnchor is null)
        {
            return;
        }

        _panAnchor = null;
        Mouse.OverrideCursor = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void OnViewboxSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口变大/变小后原来的平移量可能露出空白边，重新钳制一次
        if (_zoom.Clamp(MapViewbox.ActualWidth, MapViewbox.ActualHeight))
        {
            ApplyZoom();
        }
    }

    private void ApplyZoom()
    {
        ZoomScaleTransform.ScaleX = _zoom.Scale;
        ZoomScaleTransform.ScaleY = _zoom.Scale;
        ZoomPanTransform.X = _zoom.OffsetX;
        ZoomPanTransform.Y = _zoom.OffsetY;
    }

    private void ResetZoom()
    {
        if (_zoom.Reset())
        {
            ApplyZoom();
        }
    }

    // ========== 点选 ==========

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Shape { Tag: DieVisual die })
        {
            return;
        }

        // 同一格再次点击 = 取消选中；写入 DP 会触发 HighlightSelected
        SelectedDie = _selectedDie == die ? null : die;
    }

    private void HighlightSelected(DieVisual? die)
    {
        _selectedDie = die;

        foreach (var child in MapCanvas.Children.OfType<Rectangle>())
        {
            if (child.IsHitTestVisible && child.StrokeThickness > 0)
            {
                child.Stroke = null;
            }
        }

        if (die is null)
        {
            return;
        }

        var target = MapCanvas.Children
            .OfType<Rectangle>()
            .FirstOrDefault(r => ReferenceEquals(r.Tag, die));
        if (target is null)
        {
            return;
        }

        target.Stroke = _accentBrush;
        target.StrokeThickness = 2;
        Panel.SetZIndex(target, 10);
        target.BringIntoView();
    }

    private static string BuildTooltip(DieVisual die) =>
        die.Category == DieCategory.Null
            ? $"空位 (C{die.Col}, R{die.Row})"
            : $"C{die.Col}, R{die.Row}  |  Bin {die.BinCode}  |  {die.Category}"
              + (string.IsNullOrEmpty(die.Description) ? string.Empty : $"  |  {die.Description}");

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
