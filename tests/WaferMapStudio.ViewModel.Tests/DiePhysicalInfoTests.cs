using E142Transfer.Import;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using WaferMapStudio.Core.Visuals;
using WaferMapStudio.ViewModel;
using WaferMapStudio.ViewModel.Services;
using Xunit;

namespace WaferMapStudio.ViewModel.Tests;

/// <summary>
/// 物理尺寸与选中 die 摘要：每个网格行要给出物理坐标与宽高（含单位），
/// 「选中 die」栏必须展示全部字段（此前直接绑 record ToString + 省略号截断）。
/// </summary>
public sealed class DiePhysicalInfoTests
{
    // TSK 真实样本参数：55 × 125，die 3600 × 1580 µm
    private const double StepX = 3600.0;
    private const double StepY = 1580.0;

    [Fact]
    public void DieRow_WithPitch_ShowsPhysicalPositionAndSize()
    {
        var row = new DieRowViewModel(new DieVisual(2, 3, "1", DieCategory.Pass, "Accept"), StepX, StepY);

        // die 中心：(2+0.5)×3600 = 9000 µm，(3+0.5)×1580 = 5530 µm
        Assert.Equal(9000, ParseNumber(row.PhysicalX), precision: 6);
        Assert.Equal(5530, ParseNumber(row.PhysicalY), precision: 6);
        Assert.Equal("3600 × 1580 µm", row.SizeText);
    }

    [Fact]
    public void DieRow_WithoutPitch_ShowsDashInsteadOfFakeZero()
    {
        var row = new DieRowViewModel(new DieVisual(0, 0, "1", DieCategory.Pass, null), 0, 0);

        Assert.Equal("—", row.PhysicalX);
        Assert.Equal("—", row.PhysicalY);
        Assert.Equal("—", row.SizeText);
    }

    [Fact]
    public void Document_ShowsDieSizeAndPhysicalExtent()
    {
        var doc = CreateDocument(columns: 55, rows: 125, stepX: StepX, stepY: StepY);

        Assert.Equal("3600 × 1580 µm", doc.DieSizeText);
        // 55×3600 = 198.0 mm，125×1580 = 197.5 mm
        Assert.Equal("198 × 197.5 mm", doc.PhysicalExtentText);
    }

    [Fact]
    public void Document_WithoutPitch_ShowsDash()
    {
        var doc = CreateDocument(columns: 2, rows: 2, stepX: 0, stepY: 0);

        Assert.Equal("—（上游未提供步距）", doc.DieSizeText);
        Assert.Equal("—", doc.PhysicalExtentText);
        Assert.All(doc.DieRows, r =>
        {
            Assert.Equal("—", r.PhysicalX);
            Assert.Equal("—", r.SizeText);
        });
    }

    [Fact]
    public void SelectedDieSummary_ShowsAllFieldsWithoutRecordToString()
    {
        var doc = CreateDocument(columns: 55, rows: 125, stepX: StepX, stepY: StepY);
        var browser = new WaferMapBrowserViewModel(new StubDialog(null)) { SelectedDocument = doc };

        browser.SelectedDie = new DieVisual(2, 3, "5", DieCategory.Fail, "Reject");

        var summary = browser.SelectedDieSummary;
        Assert.Contains("C2 · R3", summary);
        Assert.Contains("Bin 5", summary);
        Assert.Contains("Fail", summary);
        Assert.Contains("Reject", summary);
        Assert.Contains("3600 × 1580 µm", summary);
        Assert.DoesNotContain("DieVisual", summary); // 不能再是 record 的 ToString
    }

    [Fact]
    public void SelectedDieSummary_NoPitch_ShowsIdentityOnly()
    {
        var doc = CreateDocument(columns: 2, rows: 2, stepX: 0, stepY: 0);
        var browser = new WaferMapBrowserViewModel(new StubDialog(null)) { SelectedDocument = doc };

        browser.SelectedDie = new DieVisual(1, 1, "7", DieCategory.Fail, null);

        var summary = browser.SelectedDieSummary;
        Assert.Contains("C1 · R1", summary);
        Assert.DoesNotContain("µm", summary);
    }

    [Fact]
    public void SelectedDieSummary_SkipsDescriptionThatOnlyEchoesBinCode()
    {
        // TskDatReader 在无 XTR 时把说明合成成 "bin 21"，与 Bin 段完全重复
        var doc = CreateDocument(columns: 4, rows: 4, stepX: StepX, stepY: StepY);
        var browser = new WaferMapBrowserViewModel(new StubDialog(null)) { SelectedDocument = doc };

        browser.SelectedDie = new DieVisual(1, 1, "21", DieCategory.Marked, "bin 21");

        Assert.DoesNotContain("bin 21", browser.SelectedDieSummary);
        Assert.Contains("Bin 21 · Marked", browser.SelectedDieSummary);
    }

    [Fact]
    public void SelectedDieSummary_WhenNothingSelected_ShowsPlaceholder()
    {
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));

        Assert.Contains("未选中", browser.SelectedDieSummary);
    }

    [Fact]
    public void SwitchingDocument_RefreshesSummaryToPlaceholderThenNewPitch()
    {
        var browser = new WaferMapBrowserViewModel(new StubDialog(null));
        var tskLike = CreateDocument(columns: 55, rows: 125, stepX: StepX, stepY: StepY, name: "tsk.csv");
        var csvLike = CreateDocument(columns: 2, rows: 2, stepX: 0, stepY: 0, name: "csv.csv");

        browser.SelectedDocument = tskLike;
        browser.SelectedDie = new DieVisual(2, 3, "5", DieCategory.Fail, null);
        Assert.Contains("3600 × 1580 µm", browser.SelectedDieSummary);

        // 切到无步距的文档：点选被清空 → 占位文案；再点选 → 用新文档的口径
        browser.SelectedDocument = csvLike;
        Assert.Null(browser.SelectedDie);
        Assert.Contains("未选中", browser.SelectedDieSummary);

        browser.SelectedDie = new DieVisual(0, 0, "1", DieCategory.Pass, null);
        Assert.DoesNotContain("µm", browser.SelectedDieSummary);
    }

    // ------------------------------------------------------------------ helpers

    private static double ParseNumber(string text) =>
        double.Parse(text.Replace(" ", string.Empty), System.Globalization.NumberStyles.Number);

    private static WaferMapDocumentViewModel CreateDocument(
        int columns, int rows, double stepX, double stepY, string name = "demo.csv")
    {
        var grid = new List<DieVisual>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                grid.Add(new DieVisual(col, row, "1", DieCategory.Pass, "Accept"));
            }
        }

        var visual = new WaferMapVisual
        {
            Format = WaferMapFormat.CsvXyBin,
            SourcePath = Path.Combine(Path.GetTempPath(), name),
            Columns = columns,
            Rows = rows,
            StepSizeX = stepX,
            StepSizeY = stepY,
            Dies = grid,
            CategoryCounts = new Dictionary<DieCategory, int> { [DieCategory.Pass] = grid.Count },
        };

        var map = new NeutralWaferMap
        {
            Format = WaferMapFormat.CsvXyBin,
            SourcePath = visual.SourcePath,
        };
        var outcome = new ImportOutcome(WaferMapFormat.CsvXyBin, new StubReader(), map);

        return WaferMapDocumentViewModel.Create(
            outcome, visual, BinColorPalette.FromMap(visual));
    }

    private sealed class StubDialog(string? result) : IFileDialogService
    {
        public string? PickWaferMapFile() => result;
    }

    private sealed class StubReader : IWaferMapReader
    {
        public WaferMapFormat Format => WaferMapFormat.CsvXyBin;
        public IReadOnlyList<string> SupportedExtensions => [".csv"];
        public double DetectConfidence(in FormatProbe probe) => 0;
        public NeutralWaferMap Read(string path) => throw new NotSupportedException();
    }
}
