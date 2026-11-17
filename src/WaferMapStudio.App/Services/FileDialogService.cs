using Microsoft.Win32;
using WaferMapStudio.ViewModel.Services;

namespace WaferMapStudio.App.Services;

/// <summary>
/// WPF <see cref="OpenFileDialog"/> 实现（ViewModel 层通过接口使用，
/// 本层是唯一允许依赖 WPF UI 类型的地方）。
/// </summary>
public sealed class FileDialogService : IFileDialogService
{
    private const string Filter =
        "晶圆图文件|*.xml;*.csv;*.txt;*.xybin;*.e142;*.klarf"
        + "|SEMI E142 / G85 XML|*.xml"
        + "|CSV / XYBin|*.csv;*.xybin"
        + "|全部文件|*.*";

    public string? PickWaferMapFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择晶圆图文件",
            Filter = Filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
