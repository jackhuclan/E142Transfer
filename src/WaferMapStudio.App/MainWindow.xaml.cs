using System.Windows;
using WaferMapStudio.App.ViewModels;

namespace WaferMapStudio.App;

/// <summary>主窗口：布局与绑定全部在 XAML；代码后台只做 InitializeComponent。</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
