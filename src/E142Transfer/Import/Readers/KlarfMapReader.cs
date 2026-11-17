using System.Globalization;
using System.Text;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// KLARF（KLA Result File，SEMI E5 风格 "KEY value;" 文本）读取器，
/// 面向 AOI / 缺陷复查站输出的"抽样 die 图 + 缺陷清单"文件。
///
/// 与 E142 / G85 / SINF 的根本差别：<b>KLARF 没有 die bin</b>。
/// 它的 die 图（SampleDieMap / SampleTestPlan）只声明"哪些格位有 die"，
/// 真正的信息全在 DefectList 的逐条缺陷坐标上。因此这里的 die 类别是由缺陷聚合推导的
/// （有缺陷 → Fail，无缺陷 → Pass），并显式报 KLARF_NO_DIE_BIN 提醒调用方：
/// 这不是官方 disposition，只能用于可视化与定位，不能当 yield / 出货判定。
///
/// 已处理的边界情况（均来自真实样本 KC07043-14G1 实测）：
///  1. 缺陷记录可能比 DefectRecordSpec 声明的字段多出若干列（样本多 2 列，恒为 "1 0"），
///     必须按字段名索引取值，不能按列位置硬编码。
///  2. 每个缺陷一个 TiffFileName + DefectList 块；标准 KLARF 也允许一块多条记录，
///     两种都要能读，一块多条时图片归属有歧义，要告警。
///  3. ClassLookup 声明条目数可少于最大类号（样本 0..128 缺 93..122），
///     缺陷类号落在空洞里时不能抛异常，回落到类号本身。
///  4. SampleDieMap / SampleTestPlan 可能只有 X Y 两列（缺 die 类别 / 缺陷数），
///     不能假定第 3 列存在。
///  5. 缺陷坐标必须与 XINDEX/YINDEX/XREL/YREL 交叉校验，三者不自洽说明写入方算错，
///     会直接影响 die 归属，必须告警而不是静默采信。
///  6. SummaryList 的 DEFDENSITY 口径各厂不一（样本反算差 1.25 倍），只做提示不当真值。
/// </summary>
public sealed class KlarfMapReader : IWaferMapReader
{
    public WaferMapFormat Format => WaferMapFormat.Klarf;

    public IReadOnlyList<string> SupportedExtensions => [".klarf", ".kdf"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var text = probe.HeadText;
        // 这三个关键字都在 KLARF 前 1KB 内；DefectRecordSpec 可能在文件末尾，不能当判据
        var hasVersion = text.Contains("FileVersion", StringComparison.Ordinal);
        var hasSampleType = text.Contains("SampleType", StringComparison.Ordinal);
        var hasStation = text.Contains("InspectionStationID", StringComparison.Ordinal);
        if (!hasVersion || !hasSampleType || !hasStation) return 0;

        var score = 0.90;
        if (text.Contains("SampleDieMap", StringComparison.Ordinal)) score += 0.04;
        if (text.Contains("ClassLookup", StringComparison.Ordinal)) score += 0.03;
        if (text.Contains("DiePitch", StringComparison.Ordinal)) score += 0.02;
        if (probe.Extension?.ToLowerInvariant() is ".klarf" or ".kdf") score += 0.05;
        return Math.Min(1.0, score);
    }

    public NeutralWaferMap Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var parsed = Parse(path);
        return Build(path, parsed, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
    }

    // ------------------------------------------------------------------ 单遍扫描

    private sealed class ParsedFile
    {
        public Dictionary<string, string> Header { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> Classes { get; } = [];
        public List<(int X, int Y)> DieCells { get; } = [];
        public Dictionary<(int X, int Y), int> TestPlanCounts { get; } = [];
        public List<RawDefect> Defects { get; } = [];
        public Dictionary<string, double> Summary { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int DeclaredDie { get; set; } = -1;
        public int DeclaredClass { get; set; } = -1;
        public int DeclaredTestPlan { get; set; } = -1;
        public int TestPlanRows { get; set; }
        public int SpecFieldCount { get; set; }
        public int DefectBlocks { get; set; }
        public int MultiRecordBlocks { get; set; }
        public int ExtraColumnRows { get; set; }
        public int DieRowsWithExtraColumn { get; set; }
    }

    private static ParsedFile Parse(string path)
    {
        var result = new ParsedFile();
        var specIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var summarySpec = new List<string>();

        var inClass = false;
        var inDieMap = false;
        var inTestPlan = false;
        var inDefects = false;
        var inSummary = false;
        var recordsInBlock = 0;
        var currentImage = string.Empty;

        foreach (var rawLine in File.ReadLines(path, Encoding.Latin1))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var statementEnds = line.EndsWith(';');
            var body = statementEnds ? line[..^1].TrimEnd() : line;
            if (body.Length == 0) continue;

            var space = body.IndexOf(' ');
            var key = space < 0 ? body : body[..space];
            var rest = space < 0 ? string.Empty : body[(space + 1)..].Trim();

            // ---------- 续行：按当前块类型分派 ----------
            if (inClass && int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var classId))
            {
                result.Classes[classId] = rest.Trim('"');
                if (result.DeclaredClass > 0 && result.Classes.Count >= result.DeclaredClass) inClass = false;
                continue;
            }

            if (inDieMap && TryPoint(body, out var dx, out var dy, out var dieExtra))
            {
                result.DieCells.Add((dx, dy));
                if (dieExtra) result.DieRowsWithExtraColumn++;
                if (result.DeclaredDie > 0 && result.DieCells.Count >= result.DeclaredDie) inDieMap = false;
                continue;
            }
            inDieMap = false;

            if (inTestPlan)
            {
                var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ty))
                {
                    result.TestPlanRows++;
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                        result.TestPlanCounts[(tx, ty)] = count;
                    if (result.DeclaredTestPlan > 0 && result.TestPlanRows >= result.DeclaredTestPlan) inTestPlan = false;
                    continue;
                }
                inTestPlan = false;
            }

            if (inDefects)
            {
                var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    result.Defects.Add(ReadDefect(parts, specIndex, currentImage));
                    recordsInBlock++;
                    if (result.SpecFieldCount > 0 && parts.Length > result.SpecFieldCount) result.ExtraColumnRows++;
                    if (statementEnds)
                    {
                        inDefects = false;
                        if (recordsInBlock > 1) result.MultiRecordBlocks++;
                    }
                    continue;
                }
                inDefects = false;
                if (recordsInBlock > 1) result.MultiRecordBlocks++;
            }

            if (inSummary && double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length && i < summarySpec.Count; i++)
                    result.Summary[summarySpec[i]] = ParseDouble(parts[i]);
                inSummary = false;
                continue;
            }
            inSummary = false;

            // 走到这里说明当前行不是任何块的续行：先复位块状态，再按关键字分派。
            // （类表/抽样图/测试计划可能声明条数多于实际行数，标志位必须由关键字复位，
            //   否则后续的数字行会被误当成本块的续行吞掉。）
            inClass = false;
            inTestPlan = false;
            inSummary = false;
            inDefects = false;
            inDieMap = false;

            // ---------- 关键字 ----------
            switch (key)
            {
                case "ClassLookup":
                    result.DeclaredClass = ParseInt(rest);
                    inClass = true;
                    break;

                case "SampleDieMap":
                    result.DeclaredDie = ParseInt(rest);
                    inDieMap = true;
                    break;

                case "SampleTestPlan":
                    result.DeclaredTestPlan = ParseInt(rest);
                    inTestPlan = true;
                    break;

                case "DefectRecordSpec":
                {
                    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var declared = parts.Length > 0 ? ParseInt(parts[0]) : -1;
                    specIndex.Clear();
                    for (var i = 1; i < parts.Length; i++) specIndex[parts[i]] = i - 1;
                    result.SpecFieldCount = specIndex.Count;
                    if (declared >= 0 && declared != specIndex.Count)
                        result.Header["DefectRecordSpecDeclared"] = declared.ToString(CultureInfo.InvariantCulture);
                    break;
                }

                case "TiffFileName":
                    currentImage = rest.Trim('"');
                    break;

                case "DefectList":
                    result.DefectBlocks++;
                    recordsInBlock = 0;
                    inDefects = true;
                    break;

                case "SummarySpec":
                {
                    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    summarySpec.Clear();
                    summarySpec.AddRange(parts.Skip(1)); // 跳过字段个数
                    break;
                }

                case "SummaryList":
                    inSummary = true;
                    break;

                default:
                    result.Header[key] = rest;
                    break;
            }
        }

        return result;
    }

    private static bool TryPoint(string body, out int x, out int y, out bool hasExtra)
    {
        x = y = 0;
        hasExtra = false;
        var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) return false;
        hasExtra = parts.Length > 2;
        return true;
    }

    private static RawDefect ReadDefect(string[] parts, Dictionary<string, int> index, string image)
    {
        double Get(string name) =>
            index.TryGetValue(name, out var i) && i < parts.Length ? ParseDouble(parts[i]) : 0;

        int GetInt(string name) => (int)Math.Round(Get(name));

        return new RawDefect
        {
            Id = GetInt("DEFECTID"),
            X = Get("X"),
            Y = Get("Y"),
            XRel = Get("XREL"),
            YRel = Get("YREL"),
            XIndex = GetInt("XINDEX"),
            YIndex = GetInt("YINDEX"),
            SizeX = Get("XSIZE"),
            SizeY = Get("YSIZE"),
            Area = Get("DEFECTAREA"),
            Size = Get("DSIZE"),
            ClassNumber = GetInt("CLASSNUMBER"),
            Test = GetInt("TEST"),
            Cluster = GetInt("CLUSTERNUMBER"),
            Image = image,
        };
    }

    // ------------------------------------------------------------------ 组装中性模型

    private NeutralWaferMap Build(string path, ParsedFile parsed, string directory)
    {
        var header = parsed.Header;
        var pitchX = ParsePair(header.GetValueOrDefault("DiePitch"), 0);
        var pitchY = ParsePair(header.GetValueOrDefault("DiePitch"), 1);
        var originX = ParsePair(header.GetValueOrDefault("DieOrigin"), 0);
        var originY = ParsePair(header.GetValueOrDefault("DieOrigin"), 1);

        var map = new NeutralWaferMap
        {
            Format = Format,
            SourcePath = path,
            SubstrateId = Unquote(header.GetValueOrDefault("WaferID")),
            LotId = Unquote(header.GetValueOrDefault("LotID")),
            DeviceId = Unquote(header.GetValueOrDefault("DeviceID")),
            DieSizeX = pitchX,
            DieSizeY = pitchY,
        };

        // ---------- 1. 头部元数据 ----------
        foreach (var key in new[]
                 {
                     "FileVersion", "FileTimestamp", "TiffSpec", "InspectionStationID", "SampleType",
                     "ResultTimestamp", "SetupID", "StepID", "SampleOrientationMarkType",
                     "OrientationMarkLocation", "DiePitch", "DieOrigin", "Slot", "SampleCenterLocation",
                     "SampleSize", "InspectionTest", "WaferStatus",
                 })
        {
            if (header.TryGetValue(key, out var value)) map.SetMeta(key, value);
        }

        var columns = parsed.DieCells.Count > 0 ? parsed.DieCells.Max(c => c.X) + 1 : 0;
        var rows = parsed.DieCells.Count > 0 ? parsed.DieCells.Max(c => c.Y) + 1 : 0;
        map.Layout = new NeutralLayout
        {
            Columns = columns,
            Rows = rows,
            StepSizeX = pitchX,
            StepSizeY = pitchY,
            // KLARF 的 XINDEX/YINDEX 自 DieOrigin 起 0 基递增，X 向右、Y 向上
            OriginLocation = OriginLocation.LowerLeft,
            AxisDirection = AxisDirection.UpRight,
            Orientation = WaferOrientation.Degree0,
        };

        if (pitchX <= 0 || pitchY <= 0)
            map.AddIssue("KLARF_NO_PITCH", "缺少或非法的 DiePitch：坐标无法换算为 die 索引", false);

        // ---------- 2. ClassLookup ----------
        if (parsed.DeclaredClass > 0 && parsed.Classes.Count != parsed.DeclaredClass)
            map.AddIssue("KLARF_CLASS_COUNT",
                $"ClassLookup 声明 {parsed.DeclaredClass} 条，实际解析 {parsed.Classes.Count} 条", false);
        if (parsed.Classes.Count > 0)
        {
            var maxClass = parsed.Classes.Keys.Max();
            var holes = Enumerable.Range(0, maxClass + 1).Where(i => !parsed.Classes.ContainsKey(i)).ToArray();
            if (holes.Length > 0)
                map.AddIssue("KLARF_CLASS_GAP",
                    $"ClassLookup 类号 0..{maxClass} 缺 {holes.Length} 个（{Compact(holes)}）：" +
                    "缺陷类号落在空洞里时只能显示类号，按类别的统计会缺名称");
        }

        // ---------- 3. die 图 ----------
        if (parsed.DieCells.Count == 0)
        {
            map.AddIssue("KLARF_NO_DIEMAP",
                "没有 SampleDieMap（或为空）：die 只能由缺陷反推，晶圆轮廓不可信", false);
        }
        else if (parsed.DeclaredDie >= 0 && parsed.DieCells.Count != parsed.DeclaredDie)
        {
            map.AddIssue("KLARF_DIEMAP_COUNT",
                $"SampleDieMap 声明 {parsed.DeclaredDie} 条，实际解析 {parsed.DieCells.Count} 条", false);
        }
        if (parsed.DieRowsWithExtraColumn > 0)
            map.AddIssue("KLARF_DIEMAP_EXTRA",
                $"SampleDieMap 有 {parsed.DieRowsWithExtraColumn} 行带第 3 列，已忽略（本格式未使用 die 类别列）");

        map.AddBin(new NeutralBinDefinition
        {
            Code = "0",
            Description = "无缺陷 die（KLARF 无 die bin，按缺陷存在性判定）",
            Class = NeutralBinClass.Pass,
        });
        map.AddBin(new NeutralBinDefinition
        {
            Code = "1",
            Description = "含缺陷 die（按缺陷存在性判定，非官方 disposition）",
            Class = NeutralBinClass.Fail,
        });

        var seen = new HashSet<(int X, int Y)>();
        foreach (var (x, y) in parsed.DieCells)
        {
            if (!seen.Add((x, y))) continue;
            map.AddDie(new NeutralDie(x, y) { BinCode = "0", Class = NeutralBinClass.Pass });
        }

        map.AddIssue("KLARF_NO_DIE_BIN",
            "KLARF 的 die 图只有格位坐标、没有 die bin：本读取器按「有缺陷 → Fail、无缺陷 → Pass」推导。" +
            "该结果只用于可视化与定位，不能作为最终 yield 或出货判定（真实 disposition 来自复查/复测）");

        // ---------- 4. 缺陷 ----------
        var byDie = parsed.Defects.GroupBy(d => (d.XIndex, d.YIndex)).ToArray();
        var orphan = 0;
        var multiDefectDies = 0;
        foreach (var group in byDie)
        {
            var (x, y) = group.Key;
            if (group.Count() > 1) multiDefectDies++;
            if (map.GetDie(x, y) is null) orphan++;
            map.AddDie(new NeutralDie(x, y) { BinCode = "1", Class = NeutralBinClass.Fail });
        }

        foreach (var defect in parsed.Defects)
        {
            map.AddDefect(new NeutralDefect
            {
                DefectId = defect.Id,
                DieX = defect.XIndex,
                DieY = defect.YIndex,
                X = defect.X,
                Y = defect.Y,
                XRel = defect.XRel,
                YRel = defect.YRel,
                SizeX = defect.SizeX,
                SizeY = defect.SizeY,
                Area = defect.Area,
                Size = defect.Size,
                ClassNumber = defect.ClassNumber,
                ClassName = parsed.Classes.GetValueOrDefault(defect.ClassNumber, string.Empty),
                TestNumber = defect.Test,
                ClusterNumber = defect.Cluster,
                ImageFile = defect.Image,
            });
        }

        if (parsed.Defects.Count > 0 && parsed.Defects.Select(d => d.ClassNumber).Distinct().Count() == 1)
        {
            var only = parsed.Defects[0].ClassNumber;
            map.AddIssue("KLARF_SINGLE_CLASS",
                $"全部 {parsed.Defects.Count} 条缺陷都是同一类（{only} " +
                $"{parsed.Classes.GetValueOrDefault(only, "未定义")}）：该文件未做缺陷分类，" +
                "按类别的帕累托/统计会失真");
        }

        var undefinedClass = parsed.Defects.Count(d => !parsed.Classes.ContainsKey(d.ClassNumber));
        if (undefinedClass > 0)
            map.AddIssue("KLARF_UNDEFINED_CLASS",
                $"{undefinedClass} 条缺陷的类号不在 ClassLookup 中，类别名缺失");

        if (orphan > 0)
            map.AddIssue("KLARF_ORPHAN_DEFECT",
                $"{orphan} 个缺陷落在 SampleDieMap 未声明的 die 上：die 归属可能错位（坐标基准不一致）", false);

        if (multiDefectDies > 0)
            map.AddIssue("KLARF_MULTI_DEFECT_DIE",
                $"{multiDefectDies} 颗 die 上有多个缺陷：NDEFECT({parsed.Defects.Count}) 与 " +
                $"NDEFDIE({byDie.Length}) 不等属正常，统计时勿混用");

        if (parsed.MultiRecordBlocks > 0)
            map.AddIssue("KLARF_MULTI_RECORD_BLOCK",
                $"{parsed.MultiRecordBlocks}/{parsed.DefectBlocks} 个 DefectList 块含多条缺陷：" +
                "该块的 TiffFileName 归属有歧义，图片只能按 DEFECTID 反查");

        if (parsed.ExtraColumnRows > 0)
            map.AddIssue("KLARF_EXTRA_COLUMNS",
                $"{parsed.ExtraColumnRows}/{parsed.Defects.Count} 条缺陷记录的列数多于 DefectRecordSpec " +
                $"声明的 {parsed.SpecFieldCount} 个字段：已按字段名取值，多余列忽略");

        // ---------- 5. 坐标自洽 ----------
        var badIndex = 0;
        var badRel = 0;
        foreach (var d in parsed.Defects)
        {
            if (pitchX <= 0 || pitchY <= 0) break;
            var ex = (int)Math.Floor((d.X - originX) / pitchX);
            var ey = (int)Math.Floor((d.Y - originY) / pitchY);
            if (ex != d.XIndex || ey != d.YIndex) badIndex++;

            var rx = d.X - (originX + d.XIndex * pitchX);
            var ry = d.Y - (originY + d.YIndex * pitchY);
            if (Math.Abs(rx - d.XRel) > 0.05 || Math.Abs(ry - d.YRel) > 0.05) badRel++;
        }
        if (badIndex > 0)
            map.AddIssue("KLARF_INDEX_MISMATCH",
                $"{badIndex}/{parsed.Defects.Count} 条缺陷的 X/Y 与 XINDEX/YINDEX 对不上" +
                $"（DieOrigin={originX}/{originY}, DiePitch={pitchX}/{pitchY}）：die 归属不可信", false);
        if (badRel > 0)
            map.AddIssue("KLARF_REL_MISMATCH",
                $"{badRel}/{parsed.Defects.Count} 条缺陷的 XREL/YREL 不等于 (X/Y − DieOrigin − 索引×pitch)：区内定位有偏差", false);

        // ---------- 6. 几何自洽（外接框 vs 面积） ----------
        var geomBad = new List<int>();
        foreach (var d in parsed.Defects)
        {
            if (d.SizeX <= 0 || d.SizeY <= 0 || d.Area <= 0) continue;
            var fill = d.Area / (d.SizeX * d.SizeY);
            if (fill > 1.05 || fill < 0.01) geomBad.Add(d.Id);
        }
        if (geomBad.Count > 0)
            map.AddIssue("KLARF_GEOMETRY_INCONSISTENT",
                $"{geomBad.Count} 条缺陷的 DEFECTAREA 与外接框 XSIZE×YSIZE 矛盾（填充率>105% 或 <1%）：" +
                $"{Compact(geomBad)} —— 面积与边界框不是同一分割口径，做面积阈值筛片前须确认");

        // ---------- 7. SummaryList 对账 ----------
        var ndef = parsed.Summary.GetValueOrDefault("NDEFECT", double.NaN);
        var ndefDie = parsed.Summary.GetValueOrDefault("NDEFDIE", double.NaN);
        var ndie = parsed.Summary.GetValueOrDefault("NDIE", double.NaN);
        var density = parsed.Summary.GetValueOrDefault("DEFDENSITY", double.NaN);
        var area = ParseDouble(header.GetValueOrDefault("AreaPerTest"));

        if (parsed.Summary.Count > 0)
            map.SetMeta("Summary", string.Join(", ", parsed.Summary.Select(kv => $"{kv.Key}={kv.Value:0.###}")));

        if (!double.IsNaN(ndef) && (int)ndef != parsed.Defects.Count)
            map.AddIssue("KLARF_SUMMARY_DEFECT",
                $"SummaryList NDEFECT={ndef:0} 与实际缺陷记录 {parsed.Defects.Count} 条不一致", false);
        if (!double.IsNaN(ndefDie) && (int)ndefDie != byDie.Length)
            map.AddIssue("KLARF_SUMMARY_DEFDIE",
                $"SummaryList NDEFDIE={ndefDie:0} 与实际有缺陷 die 数 {byDie.Length} 不一致", false);
        if (!double.IsNaN(ndie) && parsed.DieCells.Count > 0 && (int)ndie != parsed.DieCells.Count)
            map.AddIssue("KLARF_SUMMARY_NDIE",
                $"SummaryList NDIE={ndie:0} 与 SampleDieMap 的 {parsed.DieCells.Count} 颗 die 不一致", false);

        if (!double.IsNaN(density) && area > 0 && parsed.Defects.Count > 0)
        {
            var computed = parsed.Defects.Count / (area / 1e8); // 个/cm²
            map.SetMeta("DefectDensityDeclared", density.ToString("0.######", CultureInfo.InvariantCulture));
            map.SetMeta("DefectDensityComputed", computed.ToString("0.######", CultureInfo.InvariantCulture));
            if (Math.Abs(computed - density) / Math.Max(density, 1e-9) > 0.02)
                map.AddIssue("KLARF_DENSITY_MISMATCH",
                    $"SummaryList DEFDENSITY={density:0.######} 与 NDEFECT/AreaPerTest 反算 {computed:0.######} 不一致" +
                    $"（差 {computed / Math.Max(density, 1e-9):0.###} 倍）：密度口径未声明，勿直接引用");
        }

        // ---------- 8. 复核图 ----------
        var images = parsed.Defects.Select(d => d.Image).Where(n => n.Length > 0).Distinct().ToArray();
        map.SetMeta("ImageCount", images.Length.ToString(CultureInfo.InvariantCulture));
        var missing = images.Count(name => !File.Exists(Path.Combine(directory, name)));
        if (missing > 0)
            map.AddIssue("KLARF_IMAGE_MISSING",
                $"{missing}/{images.Length} 个复核图引用在本目录下找不到（图只是引用，缺失不影响坐标解析）");

        var tiffSpec = header.GetValueOrDefault("TiffSpec");
        if (!string.IsNullOrEmpty(tiffSpec) && images.Length > 0 &&
            images.All(n => n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
            map.AddIssue("KLARF_TIFFSPEC",
                $"TiffSpec='{tiffSpec}' 声明 TIFF，实际引用的是 JPG：按 ImageFile 原名打开，勿按扩展名硬编码解码器");

        // ---------- 9. 晶圆外形与朝向 ----------
        if (parsed.DieCells.Count > 0 && pitchX > 0 && pitchY > 0)
        {
            var radius = parsed.DieCells.Max(c =>
            {
                var x = Math.Max(Math.Abs(originX + c.X * pitchX), Math.Abs(originX + (c.X + 1) * pitchX));
                var y = Math.Max(Math.Abs(originY + c.Y * pitchY), Math.Abs(originY + (c.Y + 1) * pitchY));
                return Math.Sqrt(x * x + y * y);
            });
            map.SetMeta("InferredRadiusMm", (radius / 1000).ToString("0.##", CultureInfo.InvariantCulture));
            if (!header.ContainsKey("WaferDiameter"))
                map.AddIssue("KLARF_NO_DIAMETER",
                    $"文件未声明 WaferDiameter：按 die 外沿（取角点）反推半径约 {radius / 1000:0.##} mm，" +
                    "实际 wafer 半径会略小；晶圆轮廓只能由 die 分布拟合");
        }

        var markLocation = header.GetValueOrDefault("OrientationMarkLocation");
        if (!string.IsNullOrEmpty(markLocation))
            map.AddIssue("KLARF_ORIENTATION",
                $"OrientationMarkType={header.GetValueOrDefault("SampleOrientationMarkType")} / " +
                $"OrientationMarkLocation={markLocation}：与多数工具默认的 notch-up 相反，" +
                "渲染与跨机台对齐前必须先统一方位基准");

        if (parsed.DeclaredTestPlan > 0 && parsed.TestPlanCounts.Count == 0)
            map.AddIssue("KLARF_TESTPLAN_NO_COUNT",
                $"SampleTestPlan 声明 {parsed.DeclaredTestPlan} 行但每行只有 X Y" +
                "（标准 KLARF 为 X Y 缺陷数 测试号）：逐 die 缺陷数只能由 DefectList 聚合");

        map.SetMeta("DefectCount", parsed.Defects.Count.ToString(CultureInfo.InvariantCulture));
        map.SetMeta("DefectDieCount", byDie.Length.ToString(CultureInfo.InvariantCulture));
        map.SetMeta("ClassCount", parsed.Classes.Count.ToString(CultureInfo.InvariantCulture));

        return map;
    }

    // ------------------------------------------------------------------ 基础解析

    private static double ParsePair(string? raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? ParseDouble(parts[index]) : 0;
    }

    private static int ParseInt(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

    private static double ParseDouble(string? raw) =>
        double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Unquote(string? raw) => raw?.Trim().Trim('"') ?? string.Empty;

    private static string Compact(IReadOnlyList<int> values) =>
        values.Count <= 12
            ? string.Join(", ", values)
            : string.Join(", ", values.Take(12)) + $" …共 {values.Count} 条";

    private readonly record struct RawDefect
    {
        public int Id { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double XRel { get; init; }
        public double YRel { get; init; }
        public int XIndex { get; init; }
        public int YIndex { get; init; }
        public double SizeX { get; init; }
        public double SizeY { get; init; }
        public double Area { get; init; }
        public double Size { get; init; }
        public int ClassNumber { get; init; }
        public int Test { get; init; }
        public int Cluster { get; init; }
        public string Image { get; init; }
    }
}
