using CommunityToolkit.Mvvm.ComponentModel;
using WaferMapStudio.ViewModel;

namespace WaferMapStudio.App.ViewModels;

/// <summary>
/// 主窗口视图模型：持有浏览器视图模型。业务逻辑一律留在浏览器 VM 内，
/// 本类只负责宿主级信息（版本号）与未来页间协同的出口。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(WaferMapBrowserViewModel browser) => Browser = browser;

    /// <summary>晶圆图浏览器（打开 / 切换 / 解析信息的唯一编排者）。</summary>
    public WaferMapBrowserViewModel Browser { get; }

    public string Version =>
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
