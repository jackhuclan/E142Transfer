using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E142Transfer.Import;
using WaferMapStudio.Core.Visuals;
using WaferMapStudio.ViewModel.Services;

namespace WaferMapStudio.ViewModel;

/// <summary>
/// 晶圆图浏览器视图模型：负责「打开文件 → 解析 → 生成文档 → 多文档切换」的编排，
/// 以及全局错误日志。解析细节全部委托 E142Transfer 的导入门面，本层不做格式判断。
/// </summary>
public sealed partial class WaferMapBrowserViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialog;

    public WaferMapBrowserViewModel(IFileDialogService fileDialog)
    {
        _fileDialog = fileDialog;
    }

    /// <summary>已加载的文档列表（多文件并存，切换时无需重新解析）。</summary>
    public ObservableCollection<WaferMapDocumentViewModel> Documents { get; } = [];

    /// <summary>全局日志（打开/切换/解析错误/告警）。</summary>
    public ObservableCollection<LogEntryViewModel> Logs { get; } = [];

    [ObservableProperty]
    private WaferMapDocumentViewModel? _selectedDocument;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private DieVisual? _selectedDie;

    partial void OnSelectedDocumentChanged(WaferMapDocumentViewModel? value)
    {
        // 切换文档时清空点选，避免残留上一份文件的 die 引用
        SelectedDie = null;
        StatusText = value is null
            ? "就绪"
            : $"{value.FileName} · {value.FormatName} · {value.Grid} · 良率 {value.YieldText}";
        if (value is not null)
        {
            AddLog("INFO", $"切换到 {value.FileName}（{value.FormatName}，{value.DieCount} die，告警 {value.Issues.Count} 条）");
        }
    }

    /// <summary>弹出文件对话框并加载（工具栏「打开」按钮入口）。</summary>
    [RelayCommand]
    private void OpenFile()
    {
        var path = _fileDialog.PickWaferMapFile();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        LoadFile(path);
    }

    /// <summary>
    /// 加载指定文件。失败只记日志不抛异常——查看器不能因为一份坏文件崩溃，
    /// 也不该静默吞掉：错误必须出现在日志区。
    /// </summary>
    public void LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            AddLog("ERROR", $"文件不存在：{path}");
            StatusText = "加载失败";
            return;
        }

        try
        {
            var importer = new WaferMapImporter();
            var outcome = importer.Import(path);
            var document = WaferMapDocumentViewModel.Create(outcome);

            ReuseOrAdd(document);
            SelectedDocument = document;
            AddLog("INFO", $"已加载 {document.FileName}：{document.FormatName}，"
                + $"{document.Grid}，有效 die {document.DieCount}，良品 {document.GoodDieCount}");

            foreach (var issue in outcome.Map.Issues)
            {
                AddLog(issue.IsWarning ? "WARN" : "ERROR", $"{document.FileName}: [{issue.Code}] {issue.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or FormatException
            or E142Transfer.Import.Detection.UnsupportedWaferMapFormatException)
        {            AddLog("ERROR", $"解析失败：{Path.GetFileName(path)} · {ex.Message}");
            StatusText = "加载失败";
        }
    }

    private void AddLog(string level, string message) =>
        Logs.Add(new LogEntryViewModel(level, message));

    /// <summary>
    /// 同一文件再次打开时原位替换，而不是追加第二条：
    /// 「已加载」下拉里出现两个同名项，会让人以为显示内容和当前数据对不上。
    /// </summary>
    private void ReuseOrAdd(WaferMapDocumentViewModel document)
    {
        var index = IndexOfLoaded(document.SourcePath);
        if (index >= 0)
        {
            Documents[index] = document;
            return;
        }

        Documents.Add(document);
    }

    /// <summary>按完整路径查已加载项（Windows 路径不区分大小写）；路径缺失时一律视为新项。</summary>
    private int IndexOfLoaded(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return -1;
        }

        for (var i = 0; i < Documents.Count; i++)
        {
            if (string.Equals(Documents[i].SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
