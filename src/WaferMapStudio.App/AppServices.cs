using Microsoft.Extensions.DependencyInjection;
using WaferMapStudio.App.Services;
using WaferMapStudio.App.ViewModels;
using WaferMapStudio.ViewModel;
using WaferMapStudio.ViewModel.Services;

namespace WaferMapStudio.App;

/// <summary>
/// 应用服务注册（单一事实来源）。测试用同一份注册表构造容器，
/// 保证「DI 注册的服务与界面实际拿到的对象」一致。
/// </summary>
public static class AppServices
{
    /// <summary>注册全部应用服务（顺序无关，依赖由容器解析）。</summary>
    public static IServiceCollection AddWaferMapStudioApp(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 宿主能力：文件对话框（接口在 ViewModel 层，实现在 App 层）
        services.AddSingleton<IFileDialogService, FileDialogService>();

        services.AddSingleton<WaferMapBrowserViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
