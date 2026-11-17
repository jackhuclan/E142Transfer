# 09 工程实现：E142Transfer 适配器

[返回索引](./README.md)

## 工程概况

- 路径：本仓库根（分层见 `AGENTS.md`；解析库位于 `src/E142Transfer/`）
- 技术栈：.NET 10 解析类库 `E142Transfer` + 自检程序 `tests/E142Transfer.SelfTest`
  + WaferMapStudio WPF 查看器（`src/WaferMapStudio.*`）+ `E142Transfer.slnx`
- 目标：为 PL 检测上位机提供"多格式导入 → 中性模型 → 三映射判定 → E142 跨站追溯"的完整链路

## 两阶段交付

### 阶段一：E142 TransferMap 解析/生成

按 SEMI E142.1 XSD 建模，核心结构 `TransferMap` + `<T FX FY TX TY FID TID>` + `FromSubstrateType/FromSubstrateId`。

关键设计：

- **命名空间无关解析**：所有 XML 读取按 `LocalName` 匹配，兼容 V1005 / V1016 / V0225 版本 URI 差异；
- **XSD 默认值补齐**：OriginLocation=LowerLeft、AxisDirection=UpRight、Orientation=0、MapType=2DArray；
- **3D 堆叠支持**：同一封装位可被多颗 die 指向，`TransferRef.StackLayer` 区分层号；
- **坐标归一化**：`CoordinateNormalizer` 处理 Origin + Axis + Orientation 三重变换；
- **NullBin 跳过**：`IsDie=false` 的 die 不生成 transfer 记录。

### 阶段二：多格式导入适配器（Import 层）

按 Artwork 的中性结构思路实现：**多格式 Reader → NeutralWaferMap → 分析 / 写出**。

```
src/E142Transfer/Import/
├─ Abstractions/
│   └─ IWaferMapAdapter.cs      # WaferMapFormat / FormatProbe / Reader/Writer 接口
├─ Model/
│   └─ NeutralWaferMap.cs       # NeutralDie / NeutralLayout / NeutralBinDefinition
│                               # / NeutralTransfer / ImportIssue
├─ Detection/
│   └─ WaferMapFormatDetector.cs# 扩展名 + 文件头嗅探，置信度打分
├─ Readers/
│   ├─ XyBinCsvReader.cs        # CSV/XYBin：x,y,bin（可带 class 第4列）
│   ├─ SinfMapReader.cs         # SINF：BCEQU / hex+3-Digit bin / 特殊码 / 1-based 修正
│   └─ E142MapReader.cs         # E142：复用 Parser + bin 阵列解析
├─ Writers/
│   └─ CsvXyBinWriter.cs        # 中性 map → x,y,bin CSV
├─ WaferMapImporter.cs          # 门面：Import() / Probe()
└─ NeutralMapProjector.cs       # NeutralWaferMap → DieResult
```

## 核心接口

```csharp
public interface IWaferMapReader
{
    WaferMapFormat Format { get; }
    IReadOnlyList<string> SupportedExtensions { get; }
    double DetectConfidence(in FormatProbe probe);   // 0..1，0 表示不匹配
    NeutralWaferMap Read(string path);
}
```

## 上位机调用方式

```csharp
var importer = new WaferMapImporter();          // 默认注册 E142 / CSV / SINF
var outcome  = importer.Import(path);           // 自动识别 + 读取
var dies     = NeutralMapProjector.ToDieResults(outcome.Map);  // 挂 EL/PL/AOI 判定

foreach (var issue in outcome.Map.Issues)
    log.Warn(issue.Message);                     // 解析告警不可丢
```

生成追溯链：

```csharp
var projector = new DieTransferProjector(waferFrame);
var tm = projector.BuildTransferMap(dies);
var doc = new E142TransferMapWriter().Build(tm, targetFrame, layout);
E142TransferMapWriter.Save(doc, outputPath);
```

## 工程处理要点

1. **内容优先于扩展名**：现场常见 `.txt` 装 E142、`.csv` 装 SINF，识别器按内容打分，扩展名只做加分项；识别不出抛 `UnsupportedWaferMapFormatException`，不静默猜。
2. **解析告警进模型**：坐标缺 pitch/origin、负坐标平移、SINF 行序未确认等记为 `ImportIssue`，既不抛异常也不悄悄吞掉。
3. **E142 bin 阵列解析**：补齐了原工程只解析结构不解析 bin 数据的缺口，支持 2DArray / Array / Coordinate；Row/Column 留告警。
4. **Transfer 带目标载具**：`NeutralTransfer` 同时记录源载具与目标载具类型/ID，投影到 `DieResult.Transfers` 时不会丢失 strip ID。

## 验证

```bash
dotnet run --project E142Transfer.SelfTest/E142Transfer.SelfTest.csproj
```

自检覆盖：

- **阶段一（7 项）**：写入→解析闭环逐条校验、3D 堆叠反查、坐标归一化往返、BinCode 定义解析、追加 overlay。
- **阶段二（48 项）**：CSV 自动识别与负坐标平移、CSV 写回再导入闭环、SINF 头字段与网格、SINF `BCEQU` 良品判定与 `FF/FD/FE/@@/__` 特殊码、SINF 1-based 参考坐标修正与 3-Digit 变体、E142 bin 阵列 + BinCode 表 + TransferMap、投影到 `DieResult.Transfers`、未知格式明确报错，以及 G85 识别/Marked bin/数字原点/单位告警/参考 die/BinCount 交叉校验（20 项，见 docs/12）。

全部通过（合计 55 项）。

## 新增一种格式（如 KLARF）

1. 在 `WaferMapFormat` 加枚举值；
2. 新建 `IWaferMapReader` 实现，把该格式读成 `NeutralWaferMap`；
3. 在 `WaferMapFormatDetector.CreateDefault()` 里注册。

不需要改动分析代码，也不需要改动其他 Reader——这正是避免 N×M 转换器的关键。

注意：KLARF 是"缺陷级"数据，含 die 内部坐标（XREL/YREL）和缺陷图像指针。接它时中性模型需要扩一个缺陷位置字段，建议作为 `NeutralDie` 的扩展集合而非污染现有 `BinCode` 语义。格式细节见 [11 KLARF 格式详解](./11-KLARF格式详解.md)。

## 未覆盖范围

- `MapType=Row/Column` 的 bin 阵列解析（写入 `ImportIssue` 提示）；
- SINF 已支持 `BCEQU`、hex/3-Digit bin、特殊码与 1-based 参考坐标修正（见 [10 SINF 格式详解](./10-SINF格式详解.md)），但**行序与 flat/notch 方向仍需按厂内样本校准**；
- GEM 接口传输（E142.2 的 S14F1 消息）属设备通信层，不在本工程范围。
