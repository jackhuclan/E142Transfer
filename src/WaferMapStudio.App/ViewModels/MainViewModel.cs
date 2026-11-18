using CommunityToolkit.Mvvm.ComponentModel;
using WaferMapStudio.ViewModel;

namespace WaferMapStudio.App.ViewModels;

/// <summary>
/// 主窗口视图模型：持有各功能页视图模型，并提供顶部导航的功能切换。
/// 页内业务逻辑一律留在对应 VM 内，本类只负责页间协同的出口。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(WaferMapBrowserViewModel browser, InterpolationViewModel interpolation)
    {
        Browser = browser;
        Interpolation = interpolation;
    }

    /// <summary>晶圆图浏览器（打开 / 切换 / 解析信息的唯一编排者）。</summary>
    public WaferMapBrowserViewModel Browser { get; }

    /// <summary>插补算法演示页（参数输入 / 执行 / 结果预览）。</summary>
    public InterpolationViewModel Interpolation { get; }

    /// <summary>主界面「功能」下拉菜单选项：0 = 图谱解析，1 = 插补算法。</summary>
    public IReadOnlyList<string> FeatureOptions { get; } = ["图谱解析", "插补算法"];

    /// <summary>
    /// 当前功能选中项：0 = 图谱解析，1 = 插补算法。
    /// 顶栏下拉菜单与主区内容切换、工具栏显隐均依据该值。
    /// </summary>
    [ObservableProperty]
    private int activeFeatureIndex;

    public string Version =>
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
