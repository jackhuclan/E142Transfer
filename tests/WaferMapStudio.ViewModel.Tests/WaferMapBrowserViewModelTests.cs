using WaferMapStudio.Core.Visuals;
using WaferMapStudio.ViewModel;
using WaferMapStudio.ViewModel.Services;
using Xunit;

namespace WaferMapStudio.ViewModel.Tests;

/// <summary>浏览器 VM：加载 CSV → 文档/日志；坏路径 → 只记日志不崩溃。</summary>
public sealed class WaferMapBrowserViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public WaferMapBrowserViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wms-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private sealed class StubDialog(string? result) : IFileDialogService
    {
        public string? PickWaferMapFile() => result;
    }

    private string WriteCsv(string name, params string[] dataLines)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllLines(path, ["x,y,bin,class", .. dataLines]);
        return path;
    }

    [Fact]
    public void LoadFile_CreatesDocumentWithParsedData()
    {
        var path = WriteCsv("demo.csv", "0,0,1,PASS", "1,0,7,FAIL", "2,0,1,PASS", "1,1,3,FAIL");
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));

        browser.LoadFile(path);

        var doc = Assert.Single(browser.Documents);
        Assert.Equal("CSV / XYBin", doc.FormatName);
        // 视觉模型是完整网格（含 Null 空位）：2×3 网格 + 1 个空位 = 6 行，其中 4 行为有效 die
        Assert.Equal(6, doc.DieRows.Count);
        Assert.Equal(4, doc.DieRows.Count(r => r.IsRealDie));
        Assert.Equal(2, doc.GoodDieCount);
        Assert.Equal(50.0, doc.Visual.Yield!.Value, precision: 6);
        // CSV 缺元数据的固定告警应出现在文档与全局日志
        Assert.Contains(doc.Issues, i => i.Level == "WARN");
        Assert.Equal(doc, browser.SelectedDocument);
        Assert.Contains(browser.Logs, l => l.Level == "INFO" && l.Message.Contains("已加载"));
    }

    [Fact]
    public void LoadFile_MissingPath_LogsErrorOnly()
    {
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));

        browser.LoadFile(Path.Combine(_tempDir, "no-such-file.csv"));

        Assert.Empty(browser.Documents);
        Assert.Null(browser.SelectedDocument);
        var error = Assert.Single(browser.Logs, l => l.Level == "ERROR");
        Assert.Contains("文件不存在", error.Message);
        Assert.Equal("加载失败", browser.StatusText);
    }

    [Fact]
    public void LoadFile_UnknownFormat_LogsParseFailure()
    {
        var path = Path.Combine(_tempDir, "junk.xyz");
        File.WriteAllText(path, "%%完全无法识别的内容%%");
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));

        browser.LoadFile(path);

        Assert.Empty(browser.Documents);
        Assert.Contains(browser.Logs, l => l.Level == "ERROR" && l.Message.Contains("解析失败"));
    }

    [Fact]
    public void OpenFile_WithPickedPath_LoadsDocument()
    {
        var path = WriteCsv("picked.csv", "0,0,1,PASS");
        var browser = new WaferMapBrowserViewModel(new StubDialog(path));

        browser.OpenFileCommand.Execute(null);

        Assert.Single(browser.Documents);
    }

    [Fact]
    public void OpenFile_CancelledByUser_DoesNothing()
    {
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));

        browser.OpenFileCommand.Execute(null);

        Assert.Empty(browser.Documents);
        Assert.Empty(browser.Logs);
    }

    [Fact]
    public void SwitchingDocuments_ClearsSelectedDie()
    {
        var path1 = WriteCsv("a.csv", "0,0,1,PASS");
        var path2 = WriteCsv("b.csv", "0,0,7,FAIL");
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));

        browser.LoadFile(path1);
        browser.LoadFile(path2);
        Assert.Equal(2, browser.Documents.Count);

        var first = browser.Documents[0];
        browser.SelectedDie = new DieVisual(0, 0, "1", DieCategory.Pass, null);
        browser.SelectedDocument = first;

        Assert.Same(first, browser.SelectedDocument);
        Assert.Null(browser.SelectedDie);
    }
}
