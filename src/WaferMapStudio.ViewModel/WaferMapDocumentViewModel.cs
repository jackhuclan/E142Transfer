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
    public DieRowViewModel(DieVisual die, double stepSizeX, double stepSizeY)
    {
        Col = die.Col;
        Row = die.Row;
        BinCode = die.BinCode.Length == 0 ? "—" : die.BinCode;
        Category = die.Category.ToString();
        Description = die.Description ?? string.Empty;
        IsGood = die.Category == DieCategory.Pass;
        IsRealDie = die.IsRealDie;

        // 物理信息：die 中心点相对网格原点（µm）+ 单格宽高（µm）。
        // 上游未提供步距（StepSize ≤ 0）时给 "—"，而不是捏造 0 误导对账。
        var hasPitch = stepSizeX > 0 && stepSizeY > 0;
        PhysicalX = hasPitch ? ((die.Col + 0.5) * stepSizeX).ToString("N0") : "—";
        PhysicalY = hasPitch ? ((die.Row + 0.5) * stepSizeY).ToString("N0") : "—";
        SizeText = hasPitch ? $"{FormatMicron(stepSizeX)} × {FormatMicron(stepSizeY)} µm" : "—";
    }

    public int Col { get; }
    public int Row { get; }
    public string BinCode { get; }
    public string Category { get; }
    public string Description { get; }

    /// <summary>die 中心点相对网格原点的物理 X（µm）；无步距时为 "—"。</summary>
    public string PhysicalX { get; }

    /// <summary>die 中心点相对网格原点的物理 Y（µm）；无步距时为 "—"。</summary>
    public string PhysicalY { get; }

    /// <summary>单格物理尺寸「宽 × 高 µm」；无步距时为 "—"。</summary>
    public string SizeText { get; }

    public bool IsGood { get; }
    public bool IsRealDie { get; }

    internal static string FormatMicron(double value) => value.ToString("0.###");
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
            DieRows.Add(new DieRowViewModel(die, visual.StepSizeX, visual.StepSizeY));
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

    /// <summary>原始文件路径：用于「已加载」下拉的去重判定与悬停提示。</summary>
    public string SourcePath => Visual.SourcePath;

    public string FormatName => FormatDisplayName(Outcome.Format);

    public string ReaderName => Outcome.Reader.GetType().Name;

    public ObservableCollection<DieRowViewModel> DieRows { get; } = [];

    /// <summary>解析告警（同一份文件在数据网格与日志区都会露出）。</summary>
    public ObservableCollection<LogEntryViewModel> Issues { get; } = [];

    public string SubstrateId => Visual.SubstrateId;
    public string DeviceId => Visual.DeviceId;
    public string LotId => Visual.LotId;
    public string Grid => $"{Visual.Columns} × {Visual.Rows}";

    /// <summary>单格物理尺寸「宽 × 高 µm」（即网格里每个格子的实际大小）；上游未提供步距时为 "—"。</summary>
    public string DieSizeText => HasPitch
        ? $"{DieRowViewModel.FormatMicron(Visual.StepSizeX)} × {DieRowViewModel.FormatMicron(Visual.StepSizeY)} µm"
        : "—（上游未提供步距）";

    /// <summary>
    /// 网格物理幅面 = 列数×X 步距 × 行数×Y 步距，换算为 mm 显示。
    /// 对圆晶圆，这两个数应近似相等（正圆的量化表达）。
    /// </summary>
    public string PhysicalExtentText => HasPitch
        ? $"{Visual.Columns * Visual.StepSizeX / 1000:0.##} × {Visual.Rows * Visual.StepSizeY / 1000:0.##} mm"
        : "—";

    private bool HasPitch => Visual.StepSizeX > 0 && Visual.StepSizeY > 0;

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
