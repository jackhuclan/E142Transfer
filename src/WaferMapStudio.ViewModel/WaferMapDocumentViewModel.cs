using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using E142Transfer.Import;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using WaferMapStudio.Core.Visuals;

namespace WaferMapStudio.ViewModel;

/// <summary>DataGrid 行：单个 die 的解析结果（含 Null 空位行，便于核对完整网格）。</summary>
public sealed class DieRowViewModel
{
    public DieRowViewModel(DieVisual die)
    {
        Col = die.Col;
        Row = die.Row;
        BinCode = die.BinCode.Length == 0 ? "—" : die.BinCode;
        Category = die.Category.ToString();
        Description = die.Description ?? string.Empty;
        IsGood = die.Category == DieCategory.Pass;
        IsRealDie = die.IsRealDie;
    }

    public int Col { get; }
    public int Row { get; }
    public string BinCode { get; }
    public string Category { get; }
    public string Description { get; }
    public bool IsGood { get; }
    public bool IsRealDie { get; }
}

/// <summary>解析告警/错误的一条日志。</summary>
public sealed class LogEntryViewModel
{
    public LogEntryViewModel(string level, string message)
    {
        Time = DateTime.Now.ToString("HH:mm:ss");
        Level = level;
        Message = message;
    }

    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
}

/// <summary>
/// 一份已加载的晶圆图文档：格式识别结果、解析元信息、视觉模型、
/// die 明细行与解析告警。加载与切换编排见 <see cref="WaferMapBrowserViewModel"/>。
/// </summary>
public sealed partial class WaferMapDocumentViewModel : ObservableObject
{
    private WaferMapDocumentViewModel(
        ImportOutcome outcome,
        WaferMapVisual visual,
        BinColorPalette palette)
    {
        Outcome = outcome;
        Visual = visual;
        Palette = palette;
        FileName = Path.GetFileName(visual.SourcePath);

        foreach (var die in visual.Dies)
        {
            DieRows.Add(new DieRowViewModel(die));
        }

        foreach (var issue in outcome.Map.Issues)
        {
            Issues.Add(new LogEntryViewModel(issue.IsWarning ? "WARN" : "ERROR", issue.Message));
        }
    }

    /// <summary>原始导入结果（含 Reader / 中性模型，供深挖或导出）。</summary>
    public ImportOutcome Outcome { get; }

    public WaferMapVisual Visual { get; }

    /// <summary>本文件的专属配色（不同 BinCode → 不同颜色）。</summary>
    public BinColorPalette Palette { get; }

    public string FileName { get; }

    public string FormatName => FormatDisplayName(Outcome.Format);

    public string ReaderName => Outcome.Reader.GetType().Name;

    public ObservableCollection<DieRowViewModel> DieRows { get; } = [];

    /// <summary>解析告警（同一份文件在数据网格与日志区都会露出）。</summary>
    public ObservableCollection<LogEntryViewModel> Issues { get; } = [];

    public string SubstrateId => Visual.SubstrateId;
    public string DeviceId => Visual.DeviceId;
    public string LotId => Visual.LotId;
    public string Grid => $"{Visual.Columns} × {Visual.Rows}";
    public int DieCount => Visual.DieCount;
    public int GoodDieCount => Visual.CategoryCounts.GetValueOrDefault(DieCategory.Pass);

    public string YieldText => Visual.Yield is { } yield
        ? $"{yield:F2}%"
        : "—";

    /// <summary>按类别统计的一行摘要，如 "Pass 198 · Fail 51 · Marked 4463"。</summary>
    public string CategorySummary => string.Join(" · ", Visual.CategoryCounts
        .OrderBy(kv => kv.Key)
        .Select(kv => $"{kv.Key} {kv.Value}"));

    public static WaferMapDocumentViewModel Create(ImportOutcome outcome)
    {
        var visual = WaferMapVisualMapper.Map(outcome.Map);
        return new WaferMapDocumentViewModel(outcome, visual, BinColorPalette.FromMap(visual));
    }

    /// <summary>测试与定制入口：显式指定视觉模型与配色。</summary>
    public static WaferMapDocumentViewModel Create(
        ImportOutcome outcome, WaferMapVisual visual, BinColorPalette palette) =>
        new(outcome, visual, palette);

    internal static string FormatDisplayName(WaferMapFormat format) => format switch
    {
        WaferMapFormat.E142 => "SEMI E142",
        WaferMapFormat.G85 => "SEMI G85",
        WaferMapFormat.Sinf => "SINF",
        WaferMapFormat.CsvXyBin => "CSV / XYBin",
        WaferMapFormat.Klarf => "KLARF",
        WaferMapFormat.Tel => "TEL",
        WaferMapFormat.Tff => "TFF",
        WaferMapFormat.Stf => "STIF",
        WaferMapFormat.Unknown => "未知",
        _ => format.ToString(),
    };
}
