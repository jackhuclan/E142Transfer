# E142Transfer — SEMI E142 TransferMap 解析/生成骨架

面向 PL 检测上位机的 .NET 10 类库骨架，用于把"三映射 + die 判定结果"升级为可跨站追溯的 KGD 链路的一环。

## Schema 依据

严格按 SEMI E142.1 XSD 建模，核心结构：

```
MapData
 ├─ Layouts / Layout (Dimension, StepSize, ProductId)
 ├─ Substrates / Substrate
 └─ SubstrateMaps / SubstrateMap (SubstrateType, SubstrateId, LayoutSpecifier,
 │                                 Orientation, OriginLocation, AxisDirection)
     └─ Overlay (MapName, MapVersion)
         ├─ BinCodeMap (BinType, NullBin, MapType) → BinDefinitions / BinCode
         ├─ DeviceIdMap
         ├─ DeviceDataMap
         └─ TransferMap (FromSubstrateType, FromSubstrateId,
         │               FromLayoutSpecifier, FromSubstrateLayoutSpecifier)
             └─ T (FX, FY, TX, TY, FID, TID)   ← 源坐标→目标坐标
```

- `FX/FY` = 源 die 阵列坐标；`TX/TY` = 目标 die 阵列坐标
- `FID/TID` = 可选的字符串 die ID
- 目标载具由父 `SubstrateMap` 的 `SubstrateId` 决定；源载具由 `TransferMap/@FromSubstrateId` 决定
- 各属性在 XSD 中均为 `optional`，但本骨架要求 `<T>` 至少带一个属性，否则无意义

参考：SEMI E142.1-0225 / Critical Manufacturing 公开的 E142 XSD、Artwork 的 E142 说明。

## 文件清单

| 文件 | 职责 |
|---|---|
| `Models/E142Enums.cs` | 枚举 + 容错解析（大小写、版本差异） |
| `Models/TransferRef.cs` | 业务侧"下游去向"引用（含 StackLayer 支持 3D 堆叠） |
| `Models/DieResult.cs` | 单 die 判定结果：三映射 + bin + Transfers 列表 |
| `Models/DieIdentity.cs` | 载具类型 + ID + 坐标的复合身份 |
| `Models/E142Document.cs` | E142 文档视图模型 |
| `CoordinateNormalizer.cs` | 文件坐标 ↔ 逻辑坐标 ↔ 物理坐标 |
| `E142TransferMapParser.cs` | 解析（按 LocalName 匹配，兼容命名空间版本差异） |
| `E142TransferMapWriter.cs` | 生成完整文档 / 追加 overlay 到已有文档 |
| `DieTransferProjector.cs` | DieResult ↔ TransferMap 的桥接、正反查索引 |
| `E142Transfer.SelfTest` | 可运行自检，覆盖写入→解析→反查→坐标归一化 |

## 关键设计点

1. **命名空间无关解析**：所有 XML 读取按 `LocalName` 匹配，兼容 V1005 / V1016 / V0225 等版本 URI 差异。
2. **XSD 默认值补齐**：`OriginLocation=LowerLeft`、`AxisDirection=UpRight`、`Orientation=0`、`MapType=2DArray` 均按规范默认处理。
3. **3D 堆叠支持**：同一封装位可被多颗 die 指向，`TransferRef.StackLayer` 区分层号，反查索引会返回多条源 die。
4. **坐标归一化**：`CoordinateNormalizer` 处理 Origin + Axis + Orientation 三重变换，避免 map 旋转/偏移导致拾取错误。
5. **NullBin 跳过**：`IsDie=false` 的 die 不生成 transfer 记录。

## 接入步骤（上位机）

1. 在 die 判定流程里给每颗 KGD 挂 `TransferRef`（下游载具类型、ID、目标坐标、StackLayer）。
2. 用 `DieTransferProjector.BuildTransferMap(dies)` 生成 `E142TransferMap`。
3. 用 `E142TransferMapWriter.Build(...)` 导出完整 E142 文件给 MES，或用 `AppendToDocument(...)` 往已有 wafer map 里追加 overlay。
4. 失效反查时用 `DieTransferProjector.BuildReverseIndex(parsed)`，由成品位号查回晶圆坐标。

## 运行自检

```bash
dotnet run --project E142Transfer.SelfTest/E142Transfer.SelfTest.csproj
```

## 多格式导入适配器（Import 层）

按 Artwork 的"中性内部 map 结构"思路实现：**多格式 → 中性模型 → 三映射分析 / E142 写出**，分析代码永不接触文件格式。

### 目录结构

```
E142Transfer/Import/
├─ Abstractions/
│   └─ IWaferMapAdapter.cs      # WaferMapFormat / FormatProbe / IWaferMapReader / IWaferMapWriter
├─ Model/
│   └─ NeutralWaferMap.cs       # NeutralDie / NeutralLayout / NeutralBinDefinition / NeutralTransfer
├─ Detection/
│   └─ WaferMapFormatDetector.cs# 扩展名 + 文件头嗅探，置信度打分选 Reader
├─ Readers/
│   ├─ XyBinCsvReader.cs        # CSV/XYBin：x,y,bin（可带 class 第 4 列）
│   ├─ SinfMapReader.cs         # SINF：BCEQU / hex+3-Digit bin / 特殊码 / 1-based 修正
│   ├─ G85MapReader.cs          # SEMI G85-1101 XML（Amkor/Camtek）：Marked/数字原点/单位告警
│   └─ E142MapReader.cs         # E142：复用 Parser + 补 bin 阵列解析
├─ Writers/
│   └─ CsvXyBinWriter.cs        # 中性 map → x,y,bin CSV
├─ WaferMapImporter.cs          # 门面：Import() / Probe()
└─ NeutralMapProjector.cs       # NeutralWaferMap → DieResult（接既有链路）

E142Transfer.RealSampleCheck/  # 验证工具：读真实晶圆图，输出识别/映射/Bin 校验/Issues 报告
.workbuddy/skills/amkor-g85-ingest/  # 可复用技能：G85 接入五步流程 + 字段映射 + 预检脚本
```

### 核心接口

```csharp
public interface IWaferMapReader
{
    WaferMapFormat Format { get; }
    IReadOnlyList<string> SupportedExtensions { get; }
    double DetectConfidence(in FormatProbe probe);   // 0..1
    NeutralWaferMap Read(string path);
}
```

### 上位机调用方式

```csharp
var importer = new WaferMapImporter();          // 默认注册 E142 / CSV / SINF / G85
var outcome  = importer.Import(path);           // 自动识别 + 读取
var dies     = NeutralMapProjector.ToDieResults(outcome.Map);  // 挂 EL/PL/AOI 判定
foreach (var issue in outcome.Map.Issues) log.Warn(issue.Message);  // 解析告警不可丢
```

### 新增一种格式（如 KLARF）

1. 在 `WaferMapFormat` 加枚举值；
2. 新建一个 `IWaferMapReader` 实现，把该格式读成 `NeutralWaferMap`；
3. 在 `WaferMapFormatDetector.CreateDefault()` 里注册。

不需要改动分析代码，也不需要改动其他 Reader——这正是避免 N×M 转换器的关键。

### 自检覆盖

`ImportChecks.Run()` 的 55 项已验证：CSV 格式识别与负坐标平移、CSV 写回闭环、SINF 头字段与网格、SINF `BCEQU` 良品判定与 `FF/FD/FE/@@/__` 特殊码、SINF 1-based 参考坐标修正与 3-Digit 变体、E142 的 bin 阵列 + BinCode 表 + TransferMap、**G85 识别/Marked bin/数字原点/单位告警/参考 die/BinCount 交叉校验**、投影到 `DieResult`、未知格式明确报错。

真实样本验证工具：

```bash
dotnet run --project E142Transfer.RealSampleCheck -- "<晶圆图路径>"   # 可传多个文件
python .workbuddy/skills/amkor-g85-ingest/scripts/validate_g85.py <file.xml>  # 无 .NET 的轻量预检
```

## 知识文档

`docs/` 下有 12 篇按主题拆分的知识文档（点墨 → bin 编码 → E142 → TransferMap/KGD → 格式生态 → 工程实现 → SINF/KLARF/G85 详解），入口见 [docs/README.md](./docs/README.md)。

## 待办 / 可按厂内规范扩展

- `BinQuality` 当前只覆盖常见字符串，厂内自定义值可在 `E142EnumParser.ParseBinQuality` 增补。
- `MapType=Row/Column` 的 bin 阵列尚未解析（会写入 `ImportIssue` 提示）；2DArray / Array / Coordinate 已支持。
- SINF `BCEQU`、hex/3-Digit bin、`FF/FD/FE/@@/__` 特殊码与 1-based 参考坐标已支持；**行序与 flat/notch 方向仍需按厂内样本校准**（写入 `ImportIssue`，不自动猜测）。详见 [docs/10](./docs/10-SINF格式详解.md)。
- KLARF 是缺陷级数据，接它需给中性模型扩展缺陷集合（含 `XREL/YREL` 内部坐标）。详见 [docs/11](./docs/11-KLARF格式详解.md)。
- G85 已支持（含 `None`→`Marked`、数字原点、单位告警、BinCount 校验、与 SINF 转置提示）；接新 Amkor/G85 样本用 `amkor-g85-ingest` 技能按五步流程处理，详见 [docs/12](./docs/12-SEMI-G85-格式与Amkor样本接入.md)。
- GEM 接口传输（E142.2 的 S14F1 消息）不在本骨架范围，属设备通信层。
