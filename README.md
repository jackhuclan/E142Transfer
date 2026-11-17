# E142Transfer — 多格式晶圆图工具箱（解析库 + WaferMapStudio 查看器）

面向 PL 检测上位机的 .NET 10 工具箱：**E142Transfer 解析库**把 E142 / G85 / SINF / CSV
等多格式晶圆图收敛为中性模型，**WaferMapStudio WPF 查看器**在其上提供加载、切换、
渲染、明细与日志的完整 UI。分层规范见 [AGENTS.md](./AGENTS.md)，领域知识库见
[docs/README.md](./docs/README.md)。

## 功能总览

**解析库（src/E142Transfer）**
- 多格式 Reader（自动识别 + 置信度打分）：SEMI E142（含 TransferMap 追溯段）、
  SEMI G85-1101（Amkor/Camtek OQA）、SINF（hex bin / BCEQU / 3-Digit / 1-based 修正）、
  TSK 探针台二进制 DAT（含配对 .XTR bin 表）、KLARF 缺陷清单（AOI，die 图无 bin 由缺陷聚合）、
  CSV/XYBin；
- 中性模型 `NeutralWaferMap`（die 阵列 + BinCode 定义表 + Transfer + 缺陷明细 `NeutralDefect` + 解析告警）；
- E142 TransferMap 解析/生成、坐标归一化、3D 堆叠反查；
- CSV 写出适配器与导入门面 `WaferMapImporter`。

**WaferMapStudio 查看器（src/WaferMapStudio.*）**
- 加载 / 多文档切换 / 自动识别（解析错误进日志区，不静默吞错）；
- 解析信息面板：格式、读取器、网格、有效 die / 良品 / 良率、类别分布、Substrate/Device；
- die 明细 DataGrid（虚拟化）与解析/运行日志区；
- 可复用 `WaferMapView` 控件：Canvas 逐格渲染 + Viewbox 自适应，BinCode → 颜色
  （同类别不同码 = 同色系不同深浅），参考 die 虚线高亮，点选双向绑定，颜色图例浮层。

## 构建 / 运行 / 测试

```bash
dotnet build E142Transfer.slnx -c Release          # 0 警告 0 错误
dotnet test E142Transfer.slnx                       # xunit（Core/ViewModel，15 项）
dotnet run --project tests/E142Transfer.SelfTest    # 解析库自检（99 项 OK）
dotnet run --project src/WaferMapStudio.App         # 启动查看器
dotnet run --project tools/E142Transfer.RealSampleCheck -- <wafer-map 文件路径>
```

依赖：.NET 10 SDK（Windows；WPF 项目）。示例数据：`assets/sample-maps/sample-xybin.csv`。

## 目录

```
src/E142Transfer/                 解析库（Reader/Writer → NeutralWaferMap）
src/WaferMapStudio.Core/          视觉契约 + 映射器 + BinCode 调色板
src/WaferMapStudio.ViewModel/     浏览器/文档 VM、IFileDialogService 接口
src/WaferMapStudio.Rendering/     WaferMapView 可复用控件
src/WaferMapStudio.App/           WPF 主程序、Host+DI、深色主题
tests/E142Transfer.SelfTest/      解析库自检程序（99 项）
tests/WaferMapStudio.*.Tests/     xunit 单测
tools/E142Transfer.RealSampleCheck/  真实样本端到端验收工具
assets/sample-maps/               示例晶圆图
docs/                             领域知识库（点墨 → bin → E142 → 格式详解 → 工程实现）
```

依赖方向（单向）：`App ─► Rendering ─► Core ─► E142Transfer`，`App ─► ViewModel ─► Core`。

## 解析库 Schema 依据（SEMI E142）

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
         └─ TransferMap (FromSubstrateType, FromSubstrateId, ...)
             └─ T (FX, FY, TX, TY, FID, TID)   ← 源坐标→目标坐标
```

- `FX/FY` = 源 die 阵列坐标；`TX/TY` = 目标 die 阵列坐标；`FID/TID` = 可选 die ID
- 目标载具由父 `SubstrateMap` 的 `SubstrateId` 决定；源载具由 `TransferMap/@FromSubstrateId` 决定

参考：SEMI E142.1-0225 / Critical Manufacturing 公开的 E142 XSD、Artwork 的 E142/SINF/G85 说明。

## 解析库要点

- **命名空间无关解析**：按 `LocalName` 匹配，兼容 V1005/V1016/V0225 各厂 URI；
- **XSD 默认值补齐**：OriginLocation、AxisDirection、Orientation、MapType 按规范默认处理；
- **3D 堆叠**：同一封装位可被多颗 die 指向，反查返回多条源 die；
- **G85 陷阱**：`BinQuality="None"`（Camtek Marked Bin）是有效但未判定的 die，
  绝不映射为 Null；`OriginLocation=2` = 左上（Micron TN0021）；
- **SINF 陷阱**：`BCEQU` 良品码缺省仅 `01`、`REFPX/REFPY` 为 1-based、
  未检测码 `@@`/`@@@` 两种方言均兼容；
- **坐标对齐防线**：map 与物理 die 的对齐靠 reference die + notch，解析告警
  （单位/方位/行序）一律进 `Issues`，不静默猜测。

详细机制与工程决策见 [docs/](./docs/README.md)（12 篇主题文档）。

## 版本

单点维护于 [Directory.Build.props](./Directory.Build.props)（当前 0.2.0）。
