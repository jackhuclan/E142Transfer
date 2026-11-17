using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WaferMapStudio.App.ViewModels;

namespace WaferMapStudio.App;

/// <summary>
/// 应用入口：只做 Host 构建与 DI 装配，禁止业务逻辑。
/// 注册表见 AppServices（与测试共用同一份，避免「注册与实际构造不一致」）。
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWaferMapStudioApp();

        _host = builder.Build();
        _host.Start();

        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }
}
