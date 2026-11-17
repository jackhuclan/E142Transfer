---
name: amkor-g85-ingest
description: 接入 SEMI G85-1101 XML 晶圆图（Amkor / Camtek OQA / Micron probe 数据）到 E142Transfer 项目，并按同一流程处理任何新的 Amkor/G85 样本。当用户提供 .xml/.g85/.map 晶圆图并说明来自 Amkor、Camtek、G85、OQA，或要求"按上次的 G85 流程解析/转换/校验/接入"时，应使用本技能。执行格式识别、字段映射、bin 语义归一、坐标/单位校验、自检验证，并产出报告。
agent_created: true
---

# Amkor / SEMI G85 晶圆图接入

## 概述

把一份 SEMI G85-1101 XML 晶圆图接入 E142Transfer 的中性模型（`NeutralWaferMap`），使它能与 E142/SINF/CSV 一起参与三映射分析和 E142 追溯。本技能固化了从真实 Amkor/Camtek 样本（WaferId `4EEEB00000-HW3UREG`）总结出的字段映射、转换规则和校验清单，保证每次接入结果一致、可复现。

**核心原则**：解析器只读、不猜、不丢。任何不确定信息写入 `map.Issues`，绝不静默套用默认值或换算单位。

## 何时使用

- 用户提供新的 G85 / Amkor / Camtek 晶圆图，要求解析、转换、校验或接入项目。
- 需要复核一份 G85 文件的 bin 分布、行列、原点、单位是否正确。
- 需要判断一份新样本是否属于 G85，或与既有 G85 结构有无差异。

## 接入工作流（每次接新样本都按此顺序执行）

### 第 1 步：识别与结构确认
1. 用项目自带的验证工具做格式预检与全量读取：
   ```bash
   dotnet run --project E142Transfer.RealSampleCheck -- "<样本绝对路径>"
   ```
   支持一次传多个文件批量出报告。工具会输出识别格式/置信度、网格、字段映射、bin 分布、全部 `Issues`。
2. 确认识别为 `G85` 且置信度 ≥ 0.9。若识别成别的格式，先查 `references/g85-field-mapping.md` 的识别特征，必要时扩展 `G85MapReader.DetectConfidence`。
3. 打开样本头部，核对 `<Device>` / `<Bin>` / `<SupplierData>` / `<Data>` 四段是否存在、字段有无新增或改名。

### 第 2 步：字段映射与转换
按 `references/g85-field-mapping.md` 的映射表逐字段确认。重点核对（易错项）：
- `BinQuality="None"` = Camtek **Marked Bin**，映射为 `NeutralBinClass.Marked`，**绝不能当 Null**（真实样本里是 4463 颗，误判会抹掉大半张图）。
- `OriginLocation` 是数字码（`2` = upper left，见 Micron TN0021），不是 E142 的字符串枚举。
- `NullBin` 在 `<Bin>` 中通常无定义条目；null 位保留为 `NeutralBinClass.Null` die 以维持完整网格。
- `DUTMS` 决定尺寸单位（样本 `10^-6*m` = µm）；读取阶段原样保留 + `G85_UNITS` 告警，**不换算**。

若新样本出现映射表之外的字段或取值：先记录到 `Issues`，再决定是否扩展 Reader；不要硬编码丢弃。

### 第 3 步：校验（必须全部核对）
逐条检查工具输出的 `Issues`，并对照 `references/g85-field-mapping.md` 的"校验清单"：
- `G85_ROW_COUNT` / `G85_COL_COUNT`：`<Row>` 数 = Rows、每行 token 数 = Columns。
- `G85_BINCOUNT_MISMATCH`：每个 `<Bin BinCount>` = 阵列实际计数（真实样本四项应完全吻合）。
- `G85_BIN_MARKED` / `G85_UNITS` / `G85_REFERENCE`：属于预期告警，确认已在交付说明中转述给产线。
- 若与配套 SINF 文件一起接入：两者是**转置关系**（G85 66×92 ↔ SINF 92×66，参考点 X/Y 互换），叠加比对前必须按 `scripts/validate_g85.py` 做一致性核对，否则全盘错位。

### 第 4 步：集成与回归
1. 若是新格式变体（非标准结构），在 `E142Transfer/Import/Readers/G85MapReader.cs` 扩展解析或置信度；新增格式特征同步更新 `references/g85-field-mapping.md`。
2. 在 `E142Transfer.SelfTest/ImportChecks.cs` 增加该样本的解析用例（含 G85 专属断言：Marked、Null 网格、BinCount、单位、参考 die）。
3. 运行全量自检，必须全部通过：
   ```bash
   dotnet run --project E142Transfer.SelfTest/E142Transfer.SelfTest.csproj
   ```
4. 更新 `docs/12-SEMI-G85格式详解.md` 与 `docs/README.md`（如引入新变体）。

### 第 5 步：提交
按项目惯例提交并推送 `origin/main`，用只读 `git ls-remote origin` 核实远程 HEAD 与本地一致；更新工作日志。

## 异常处理原则

| 情况 | 处理 |
|---|---|
| 格式识别不出 | 抛 `UnsupportedWaferMapFormatException`，不静默猜测 |
| 字段缺失 / 非法（Rows、NullBin 等） | 写 `Issues`（`isWarning:false`），尽力继续解析 |
| BinCount 与实际不符 | 写 `G85_BINCOUNT_MISMATCH`，不修改数据 |
| `BinQuality=None` | 归一为 Marked，排除出 `GoodDies`，保留为有效 die |
| 单位不确定 | 原样保留 + `G85_UNITS` 告警，不换算 |
| 原点/方位冲突 | 写告警，投影前交产线用参考 die 确认 |
| 未知 bin 码 | 建 `Unknown` 定义 + `G85_UNDEFINED_BIN` 告警，不可拾取 |

## 快速参考

- 字段映射全表、bin 语义、校验清单：`references/g85-field-mapping.md`
- 无 .NET 环境下的轻量预检脚本：`scripts/validate_g85.py`
- 项目内验证工具：`E142Transfer.RealSampleCheck`（见第 1 步）
- 设计背景与完整概念：`docs/12-SEMI-G85格式详解.md`、`docs/07-Wafer-Map常见文件格式.md`
