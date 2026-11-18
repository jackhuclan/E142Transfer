using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WaferMapStudio.App.ViewModels;
using WaferMapStudio.ViewModel;

namespace WaferMapStudio.App.Views;

/// <summary>
/// 插补算法演示页。代码后台只做一件事：把 VM 里的轨迹点按包围盒缩放画到 Canvas 上，
/// 包括起点/终点标记；其余交互全部走绑定。
/// </summary>
public partial class InterpolationView : UserControl
{
    private InterpolationViewModel? _viewModel;

    public InterpolationView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => DrawTrajectory();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as InterpolationViewModel;
        if (_viewModel != null)
        {
            _viewModel.TrajectoryUpdated += OnTrajectoryUpdated;
            DrawTrajectory();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.TrajectoryUpdated -= OnTrajectoryUpdated;
            _viewModel = null;
        }
    }

    private void OnTrajectoryUpdated(object? sender, EventArgs e) => DrawTrajectory();

    private void DrawTrajectory()
    {
        PreviewCanvas.Children.Clear();
        var points = _viewModel?.Trajectory;
        if (points is null || points.Count < 2) return;

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double spanX = Math.Max(maxX - minX, 1e-6);
        double spanY = Math.Max(maxY - minY, 1e-6);

        // 留出 36px 边距并保持等比缩放，避免轨迹被拉伸变形
        double margin = 36;
        double scaleX = (PreviewCanvas.ActualWidth - margin * 2) / spanX;
        double scaleY = (PreviewCanvas.ActualHeight - margin * 2) / spanY;
        double scale = Math.Min(scaleX, scaleY);
        if (scale <= 0 || !double.IsFinite(scale)) return;

        double offsetX = margin + (PreviewCanvas.ActualWidth - margin * 2 - spanX * scale) / 2 - minX * scale;
        double offsetY = margin + (PreviewCanvas.ActualHeight - margin * 2 - spanY * scale) / 2 + maxY * scale;

        var polyline = new Polyline
        {
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        foreach (var p in points)
            polyline.Points.Add(new Point(offsetX + p.X * scale, offsetY - p.Y * scale));
        PreviewCanvas.Children.Add(polyline);

        var start = points[0];
        var end = points[^1];
        AddMarker(offsetX + start.X * scale, offsetY - start.Y * scale,
            (Brush)FindResource("OkBrush"), "起点");
        AddMarker(offsetX + end.X * scale, offsetY - end.Y * scale,
            (Brush)FindResource("DangerBrush"), "终点");
    }

    private void AddMarker(double x, double y, Brush color, string label)
    {
        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = color,
            Stroke = (Brush)FindResource("SurfaceBrush"),
            StrokeThickness = 2
        };
        Canvas.SetLeft(dot, x - 5);
        Canvas.SetTop(dot, y - 5);
        PreviewCanvas.Children.Add(dot);

        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = color,
            FontFamily = (FontFamily)FindResource("UiFont")
        };
        Canvas.SetLeft(text, x + 8);
        Canvas.SetTop(text, y - 8);
        PreviewCanvas.Children.Add(text);
    }
}
