using System.Globalization;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// TSK（Tokyo Seimitsu）探针台逐 die 二进制记录读取器（.DAT，样本定长 69158 字节）。
///
/// 文件布局（大端；偏移已用 Q4FR02-02B4 / 03A7 两颗真实晶圆逐字节核对）：
/// <code>
/// 0x0000  头部 230 字节：签名 "OP"、产品号、die 尺寸、flat 角度、行列数、
///         晶圆/批次 ID、4 个时间戳、总/良/不良计数
/// 0x00E6  特殊记录 1 条（X=0x0040 Y=0x0100 bin=0），语义未定，跳过
/// 0x00EC  网格：Rows×Columns 条 × 6 字节，按行优先、行内按实际列号升序
/// 0xA20E  9 条全零填充 + 第二统计头（Total/Pass/Fail/Fail）+ 稀疏次级表
/// </code>
///
/// die 记录 6 字节：<c>[标志][v][几何码][w][aux][bin]</c>
///  - v、w 不是绝对坐标，而是"到参考 die 的距离幅值"：v=|col+51|、w=|row+79|
///    （参考 die 的原始坐标恒为 (-51,-79)，与 EG/Amkor .MAP 头里的 refdie 一致）。
///  - 列侧别在几何码 bit3（0x08）：置位表示在参考列右侧，未置位表示在左侧；
///    行号直接由记录序号 / Columns 得到，无需符号位（实测 w 与 |79-行号| 全等）。
///  - 标志字节按位解释：0x10=空位、0x80=FAIL、0x40(或0x04)=PASS、
///    其余(0x00)=非良非明确不良（EDGE/PROTECT/WAFER_ID/MIRROR 等 None 类）。
///  - bin 语义（GOOD/BAD/None）不在 DAT 里，只在配对的 .XTR 里；本读取器
///    以标志字节分类为准，再把同目录同名 .XTR 的 bin 表作为命名与交叉校验来源。
/// </summary>
public sealed class TskDatReader : IWaferMapReader
{
    // ---------- 头部偏移 ----------
    private const int SignatureOffset = 0x00;
    private const int ProductIdOffset = 0x14;
    private const int ProductIdLength = 16;
    private const int DieSizeXOffset = 0x28;
    private const int DieSizeYOffset = 0x2C;
    private const int FlatAngleOffset = 0x30;
    private const int ColumnsOffset = 0x34;
    private const int RowsOffset = 0x36;
    private const int WaferIdOffset = 0x3C;
    private const int WaferIdLength = 11;
    private const int LotIdOffset = 0x52;
    private const int LotIdLength = 8;
    private const int TimeLength = 10;
    /// <summary>4 个时间戳（起测/结束/装卸等），等距 12 字节排列，语义未逐一定名。</summary>
    private static readonly int[] TimeOffsets = [0x94, 0xA0, 0xAC, 0xB8];
    private const int TotalCountOffset = 0xD2;
    private const int PassCountOffset = 0xD4;
    private const int FailCountOffset = 0xD6;

    // ---------- 网格 ----------
    private const int GridOffset = 0xEC;
    private const int RecordSize = 6;

    // ---------- 坐标编码 ----------
    /// <summary>参考 die 的原始列幅值：v=|col+51|，故 1-based 列 = v+(Columns-51) 或 (Columns-51)-v。</summary>
    private const int ReferenceRawColumn = 51;
    /// <summary>参考 die 的原始行幅值：w=|row+79|，用于校验行编码是否仍符合该约定。</summary>
    private const int ReferenceRawRow = 79;
    /// <summary>参考 die 的 0-based 行号。</summary>
    private const int ReferenceRow0Based = 79;

    /// <summary>头部 die 尺寸以 0.01 µm 为单位（360000 → 3600 µm）。</summary>
    private const double DieSizeScale = 100.0;

    // ---------- 标志位 ----------
    private const byte FlagFail = 0x80;
    private const byte FlagPass = 0x40;
    private const byte FlagPickable = 0x04;
    private const byte FlagEmpty = 0x10;
    private const byte FlagColumnRight = 0x08;

    public WaferMapFormat Format => WaferMapFormat.TskDat;

    public IReadOnlyList<string> SupportedExtensions => [".dat"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var head = probe.Head.Span;
        if (head.Length < GridOffset + RecordSize) return 0;
        if (!HasSignature(head)) return 0;

        var rows = ReadUInt16(head, RowsOffset);
        var columns = ReadUInt16(head, ColumnsOffset);
        if (rows is <= 0 or > 1000 || columns is <= 0 or > 1000) return 0;

        // 签名 "OP" 只是弱特征（多种探针台输出都可能以 OP 开头），
        // 真正把本格式与其他二进制区分开的是"网格尺寸 + 计数自洽 + 行距编码"三重结构特征。
        var score = 0.50;
        var total = ReadUInt16(head, TotalCountOffset);
        var pass = ReadUInt16(head, PassCountOffset);
        var fail = ReadUInt16(head, FailCountOffset);
        if (total > 0 && total == pass + fail && total <= rows * columns) score += 0.15;
        if (IsPrintableAscii(head.Slice(ProductIdOffset, ProductIdLength))) score += 0.05;
        if (RowDistanceConsistent(head, rows, columns)) score += 0.20;
        if (string.Equals(probe.Extension, ".dat", StringComparison.OrdinalIgnoreCase)) score += 0.10;
        return Math.Min(1.0, score);
    }

    public NeutralWaferMap Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = File.ReadAllBytes(path);
        if (data.Length < GridOffset)
            throw new FormatException($"TSK DAT 长度 {data.Length} 不足头部 {GridOffset} 字节，不是本格式");

        var rows = ReadUInt16(data, RowsOffset);
        var columns = ReadUInt16(data, ColumnsOffset);
        if (rows <= 0 || columns <= 0)
            throw new FormatException($"TSK DAT 网格非法：Rows={rows}, Columns={columns}");

        var map = new NeutralWaferMap
        {
            Format = Format,
            SourcePath = path,
            SubstrateType = SubstrateType.Wafer,
            SubstrateId = ReadAscii(data, WaferIdOffset, WaferIdLength),
            LotId = ReadAscii(data, LotIdOffset, LotIdLength),
            DeviceId = ReadAscii(data, ProductIdOffset, ProductIdLength),
        };

        map.DieSizeX = ReadUInt32(data, DieSizeXOffset) / DieSizeScale;
        map.DieSizeY = ReadUInt32(data, DieSizeYOffset) / DieSizeScale;

        var flatAngle = ReadUInt16(data, FlatAngleOffset);
        var total = ReadUInt16(data, TotalCountOffset);
        var headerPass = ReadUInt16(data, PassCountOffset);
        var headerFail = ReadUInt16(data, FailCountOffset);

        // 1-based 列 = v + offset（参考列右侧）或 offset - v（左侧）。
        var offset = columns - ReferenceRawColumn;
        if (offset < 1)
        {
            map.AddIssue("DAT_COLUMN_OFFSET",
                $"Columns={columns} 小于参考列幅值 {ReferenceRawColumn}，列换算偏移退化为 4，须按厂内约定复核");
            offset = 4;
        }

        map.ReferenceDie = (offset - 1, ReferenceRow0Based);
        map.Layout = new NeutralLayout
        {
            Columns = columns,
            Rows = rows,
            StepSizeX = map.DieSizeX,
            StepSizeY = map.DieSizeY,
            OriginLocation = OriginLocation.UpperLeft,
            AxisDirection = AxisDirection.DownRight,
            Orientation = flatAngle == 180 ? WaferOrientation.Degree180 : WaferOrientation.Degree0,
        };

        map.SetMeta("Product", map.DeviceId);
        map.SetMeta("WaferId", map.SubstrateId);
        map.SetMeta("LotId", map.LotId);
        map.SetMeta("FlatAngle", flatAngle.ToString(CultureInfo.InvariantCulture));
        map.SetMeta("DieSizeRawX", ReadUInt32(data, DieSizeXOffset).ToString(CultureInfo.InvariantCulture));
        map.SetMeta("DieSizeRawY", ReadUInt32(data, DieSizeYOffset).ToString(CultureInfo.InvariantCulture));
        map.SetMeta("TotalDieCount", total.ToString(CultureInfo.InvariantCulture));
        map.SetMeta("PassDieCount", headerPass.ToString(CultureInfo.InvariantCulture));
        map.SetMeta("FailDieCount", headerFail.ToString(CultureInfo.InvariantCulture));

        var stamps = new List<string>(TimeOffsets.Length);
        for (var i = 0; i < TimeOffsets.Length; i++)
        {
            var stamp = ReadAscii(data, TimeOffsets[i], TimeLength);
            map.SetMeta($"Time{i + 1}", stamp);
            if (stamp.Length == TimeLength) stamps.Add(stamp);
        }
        // 时间戳语义（起测/结束/装卸）未定名，取其中最晚的一个代表本次测试时刻，
        // 仅用于在首测/复测两份 XTR 之间做配对裁决。
        var testTime = stamps.Count == 0 ? string.Empty : stamps.Max(StringComparer.Ordinal) ?? string.Empty;
        map.SetMeta("TestTime", testTime);
        map.SetMeta("FlatOrNotch", flatAngle == 180 ? "notch" : "未知");

        // ---------- 网格解码 ----------
        var cells = rows * columns;
        var gridEnd = GridOffset + cells * RecordSize;
        var available = Math.Max(0, (data.Length - GridOffset) / RecordSize);
        if (available < cells)
            map.AddIssue("DAT_GRID_TRUNCATED",
                $"文件长度 {data.Length} 仅够 {available} 条记录，网格需 {cells} 条（{rows}×{columns}），尾部 die 缺失");

        var count = Math.Min(cells, available);
        var occupied = new HashSet<(int Col, int Row)>();
        var binCounts = new Dictionary<(int Bin, NeutralBinClass Class), int>();
        var rowDistanceMismatch = 0;
        var columnOutOfRange = 0;
        var collisions = 0;

        for (var index = 0; index < count; index++)
        {
            var o = GridOffset + index * RecordSize;
            var row = index / columns;
            var flag = data[o];
            var v = data[o + 1];
            var geometry = data[o + 2];
            var w = data[o + 3];
            var bin = data[o + 5];

            // 行距校验：w 恒等于 |79-行号|，一旦不符说明行编码约定变了，必须报出来。
            if (Math.Abs(ReferenceRawRow - row) != w) rowDistanceMismatch++;

            var cls = Classify(flag);
            // 空位的 bin 字节也是 0，与良品同码；空位本身不携带 bin 语义，
            // 因此不赋 bin 码，否则投影时会按 bin 0 的定义被误标成 Pass。
            var code = cls == NeutralBinClass.Null ? string.Empty : bin.ToString(CultureInfo.InvariantCulture);
            if (cls != NeutralBinClass.Null)
            {
                binCounts.TryGetValue((bin, cls), out var seen);
                binCounts[(bin, cls)] = seen + 1;
            }

            var col1 = (geometry & FlagColumnRight) != 0 ? v + offset : offset - v;
            if (col1 < 1 || col1 > columns)
            {
                columnOutOfRange++;
                continue;
            }

            var cell = (Col: col1 - 1, Row: row);
            if (!occupied.Add(cell))
            {
                collisions++;
                continue;
            }

            map.AddDie(new NeutralDie(cell.Col, cell.Row) { BinCode = code, Class = cls });
        }

        // ---------- 配对 XTR：bin 语义与命名 ----------
        var xtr = XtrBinTable.FindBest(path, map.SubstrateId, testTime, binCounts, headerPass, headerFail);
        if (xtr is null)
        {
            map.AddIssue("DAT_NO_XTR",
                "未找到与晶圆 ID 匹配的 .XTR：bin 码只有数字，语义按状态位推断（PASS/FAIL/None 类）；" +
                "bin 名称与 GOOD/BAD 声明须以同批 XTR 为准");
        }
        else
        {
            map.SetMeta("XtrSource", xtr.Table.SourcePath);
            map.AddIssue("DAT_XTR_PAIRED",
                $"已配对 {Path.GetFileName(xtr.Table.SourcePath)}：bin 名称取自该 XTR，" +
                "其 GOOD/BAD/None 声明仅用于交叉校验，分类仍以 DAT 状态位为准");
        }

        AddBinDefinitions(map, binCounts, xtr?.Table);

        // ---------- 计数与结构校验 ----------
        var countedPass = map.Dies.Values.Count(d => d.Class == NeutralBinClass.Pass);
        var countedFail = map.Dies.Values.Count(d => d.Class == NeutralBinClass.Fail);
        var countedMarked = map.Dies.Values.Count(d => d.Class == NeutralBinClass.Marked);
        var countedNull = map.Dies.Values.Count(d => d.Class == NeutralBinClass.Null);

        if (total > 0 && total != countedPass + countedFail + countedMarked)
            map.AddIssue("DAT_TOTAL_MISMATCH",
                $"头部总 die 数 {total} 与实际有 die 格位 {countedPass + countedFail + countedMarked} 不一致" +
                $"（Pass={countedPass}, Fail={countedFail}, None 类={countedMarked}）");

        if (headerPass > 0 && headerPass != countedPass)
            map.AddIssue("DAT_PASS_MISMATCH",
                $"头部良品数 {headerPass} 与按状态位统计的 Pass {countedPass} 不一致，须核对状态位含义");

        if (headerFail > 0 && headerFail != countedFail)
            map.AddIssue("DAT_FAIL_COUNT_SEMANTICS",
                $"头部不良数 {headerFail} ≠ 明确不良 {countedFail}：该计数口径为 BAD + None 类" +
                $"（{countedFail}+{countedMarked}={countedFail + countedMarked}），做良率统计时不可直接当作 FAIL 数");

        if (rowDistanceMismatch > 0)
            map.AddIssue("DAT_ROW_ENCODING",
                $"{rowDistanceMismatch} 条记录的 w 值与 |79-行号| 不符，行编码约定可能变化，坐标需重新标定");

        if (columnOutOfRange > 0)
            map.AddIssue("DAT_COL_OUT_OF_RANGE",
                $"{columnOutOfRange} 条记录换算出的列号超出 1..{columns}，已丢弃；" +
                "通常是参考列幅值或列侧别位与厂内约定不一致");

        if (collisions > 0)
            map.AddIssue("DAT_CELL_COLLISION",
                $"{collisions} 条记录落到已占用的格位，已丢弃；说明列换算存在系统性偏移");

        if (data.Length > gridEnd && data.AsSpan(gridEnd).IndexOfAnyExcept((byte)0) >= 0)
            map.AddIssue("DAT_TRAILING_SECTION",
                $"网格在 0x{gridEnd:X} 结束，其后 {data.Length - gridEnd} 字节为第二统计头 + 稀疏次级表，" +
                "本读取器未解码（不影响 die 网格）");

        map.AddIssue("DAT_ROW_DIRECTION",
            "行 0 按记录顺序取为顶端（与 EG/Amkor .MAP 一致，已用 02B4/03A7 双样本对账）；" +
            "notch 方位由 FlatAngle 与产线约定决定，上机前须用已知方位的晶圆复核");

        map.AddIssue("DAT_UNDECODED_BYTES",
            "每条记录的 aux 字节（0~63）与几何码低 2 位在 02B4/03A7 两颗晶圆间逐格相同，" +
            "只随 (行,列) 变化、不携带 die 判定信息，语义未定，已忽略");

        map.AddIssue("DAT_DIE_SIZE_UNITS",
            $"die 尺寸按头部 0.01 µm 单位换算为 {map.DieSizeX:0.##}×{map.DieSizeY:0.##} µm；" +
            "DAT 无 die 间距字段，StepSize 直接取 die 尺寸，实际间距须以 XTR 的 dieSpacingCX/CY 复核");

        return map;
    }

    /// <summary>状态位 → 中性类别。空位优先，其次 FAIL，再次 PASS。</summary>
    private static NeutralBinClass Classify(byte flag)
    {
        if ((flag & FlagEmpty) != 0) return NeutralBinClass.Null;
        if ((flag & FlagFail) != 0) return NeutralBinClass.Fail;
        if ((flag & FlagPass) != 0) return NeutralBinClass.Pass;
        if ((flag & FlagPickable) != 0) return NeutralBinClass.Pass;
        // 0x00：已测但既非良品也非明确不良（EDGE_DIE / PROTECT / WAFER_ID_ZONE / MIRROR_DIE 等
        // XTR 里声明为 None 的 bin）。它是有效 die，按 Marked 处理，禁止进 GoodDies。
        return NeutralBinClass.Marked;
    }

    /// <summary>
    /// 建立 bin 定义：类别取 DAT 状态位统计出的主导类别；名称与 XTR 声明冲突时以状态位为准并告警。
    /// </summary>
    private static void AddBinDefinitions(
        NeutralWaferMap map,
        IReadOnlyDictionary<(int Bin, NeutralBinClass Class), int> binCounts,
        XtrBinTable? xtr)
    {
        foreach (var group in binCounts.GroupBy(kv => kv.Key.Bin).OrderBy(g => g.Key))
        {
            var dominant = group.OrderByDescending(kv => kv.Value).First().Key.Class;
            var code = group.Key.ToString(CultureInfo.InvariantCulture);

            var description = $"bin {code}";
            var cls = dominant;

            if (xtr is not null && xtr.TryGet(group.Key, out var name, out var xtrClass))
            {
                description = $"{name} (bin {code})";
                if (xtrClass != NeutralBinClass.Unknown && xtrClass != dominant)
                {
                    map.AddIssue("DAT_BIN_CLASS_CONFLICT",
                        $"bin {code}（{name}）：XTR 声明为 {xtrClass}，DAT 状态位判定为 {dominant}，" +
                        "已按状态位处理，须确认该 bin 的判定口径");
                }
            }

            map.AddBin(new NeutralBinDefinition { Code = code, Description = description, Class = cls });
        }
    }

    private static bool HasSignature(ReadOnlySpan<byte> head) =>
        head[SignatureOffset] == (byte)'O' && head[SignatureOffset + 1] == (byte)'P';

    /// <summary>结构性判别：前若干行的首条记录是否满足 w=|79-行号|。</summary>
    private static bool RowDistanceConsistent(ReadOnlySpan<byte> head, int rows, int columns)
    {
        var probes = Math.Min(4, rows);
        if (probes <= 0 || columns <= 0) return false;
        for (var row = 0; row < probes; row++)
        {
            var offset = GridOffset + row * columns * RecordSize;
            if (offset + RecordSize > head.Length) return false;
            if (Math.Abs(ReferenceRawRow - row) != head[offset + 3]) return false;
        }
        return true;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> span)
    {
        var hasContent = false;
        foreach (var b in span)
        {
            if (b == 0) break;
            if (b is < 0x20 or > 0x7E) return false;
            if (b != (byte)' ') hasContent = true;
        }
        return hasContent;
    }

    private static int ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        offset + 2 <= data.Length ? (data[offset] << 8) | data[offset + 1] : 0;

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        offset + 4 <= data.Length
            ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
              | ((uint)data[offset + 2] << 8) | data[offset + 3]
            : 0;

    private static string ReadAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset >= data.Length) return string.Empty;
        var slice = data.Slice(offset, Math.Min(length, data.Length - offset));
        var chars = new char[slice.Length];
        for (var i = 0; i < slice.Length; i++)
            chars[i] = slice[i] is >= 0x20 and <= 0x7E ? (char)slice[i] : ' ';
        return new string(chars).Trim();
    }
}
