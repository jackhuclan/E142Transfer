using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
/// </summary>
public partial class WaferMapView : UserControl
{
    private const string AccentHex = "#2563EB";

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

    private readonly Brush _accentBrush;
    private DieVisual? _selectedDie;

    public WaferMapView()
    {
        InitializeComponent();
        _accentBrush = FrozenBrush(AccentHex);
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

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

    private static void OnMapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WaferMapView)d).Rebuild();

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
