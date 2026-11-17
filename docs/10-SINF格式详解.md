# 10 SINF 格式详解

[返回索引](./README.md)

## 结论先行

**SINF（Simplified Integrator Nested Format）** 是 KLA Tencor 的 **INF 结果文件的简化子集**，纯文本，由「头部 + 逐行 RowData」两部分组成。它是老牌通用格式，遗留系统覆盖面极广。

但正因为「简化」且缺少强制约束，SINF 是现场**方言最多**的 wafer map 格式之一。Artwork 的原话是：他们一开始处理 SINF 就收到大量**不遵守规定语法**的文件，不得不新增解析例程，甚至弹窗让用户补填本该写在文件里的信息。

在格式生态中的位置见 [07 Wafer Map 常见文件格式](./07-Wafer-Map常见文件格式.md)；SINF 属于**结果级**数据（每颗 die 一个 bin 码），不是缺陷级。

## 整体结构

```
DEVICE:DEMO                 ← 头部：KEY:VALUE，一行一个字段
LOT:LOT-9
WAFER:07
FNLOC:180
ROWCT:2
COLCT:3
BCEQU:01
REFPX:2
REFPY:1
DUTMS:mm
XDIES:2.945
YDIES:2.945

RowData:__ 01 01           ← 数据段：每行以 RowData: 开头
RowData:FF 0B 01
```

头部与数据段之间没有固定的分隔标记，解析器靠「是否含 `RowData:` 前缀」判断进入数据段。

## 头部字段

| 字段 | 示例 | 含义 | 可选 |
|---|---|---|---|
| `DEVICE` | `DEMO` | 产品/器件标识，由文件生成方指定 | 否 |
| `LOT` | `LOT-9` | 批次号 | 否 |
| `WAFER` | `07` | 晶圆号 | 否 |
| `FNLOC` | `180` | 晶圆 flat/notch 方位：`0`=TOP、`90`=RIGHT、`180`=BOTTOM、`270`=LEFT | 否 |
| `ROWCT` | `62` | 行数 | 否 |
| `COLCT` | `63` | 列数 | 否 |
| `BCEQU` | `01` | **良品 bin 码列表**，逗号或空格分隔 | 否（缺失有默认） |
| `REFPX` | `12` | 参考 die 的 X 坐标（**1-based**） | 是 |
| `REFPY` | `31` | 参考 die 的 Y 坐标（**1-based**） | 是 |
| `DUTMS` | `mm` | die 尺寸单位（`mm` 或 `mil`） | 否 |
| `DIECT` | `3405` | 文件中 die 的总数 | 是 |
| `XDIES` | `2.945` | X 方向 die pitch | 否 |
| `YDIES` | `2.945` | Y 方向 die pitch | 否 |
| `COMMENT` | `sample` | 自由文本注释 | 是 |

**`BCEQU` 缺失或为空时，默认只有 `01` 被视为 Pass。** 这和 [02 Bin 编码规则](./02-Bin编码规则-hard-soft-bin.md) 强调的「Pass bin 必须显式配置」是同一个坑，只不过 SINF 把它写进了格式规范。解析器不能硬编码「1 = 良品」。

## RowData 的 bin 码语义

数据段每行的 bin 码是**十六进制字符串**，不是直觉上的十进制编号：

| 码值 | 含义 |
|---|---|
| `00`–`0A` | 良品。每种 die 类型有唯一标识，从 `00`、`01`、`02`… 递增；**`0A` 保留给晶圆中心的 die** |
| `0B`–`F0` | 不良品 |
| `__` | 无 die（两个下划线），矩阵占位符 |
| `@@` | 未检测（uninspected） |
| `FD`–`FE` | 边缘 die（可选字段，并非所有文件都有） |
| `FF` | 参考 die（通常是外观上与普通 die 不同的那颗） |

真实数据行长这样：

```
RowData:__ __ __ __ __ __ __ __ __ __ 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01
RowData:__ __ __ __ __ __ __ __ 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01
```

注意首行的 `__` 占位：圆形晶圆落在方形阵列里，四角和边缘没有 die 的位置必须显式占位，这和 E142 的 NullBin 是同一回事（见 [05 MapType 三种编码](./05-MapType-三种bin阵列编码.md)）。

## 参考 die 的两种定位方式

SINF 有两种标注参考 die 的方法：

1. **数据段里用 `FF` 码**标出那颗 die；
2. **头部用 `REFPX:` / `REFPY:`** 给出坐标。

### 最危险的坑：坐标是 1-based

Artwork 特别注明：**`REFPX` / `REFPY` 的 X、Y 数组索引从 `(1,1)` 开始**，而大多数其他 map 格式（含 E142）从 `(0,0)` 开始。

> 忽视这一点会导致 X 和 Y 方向各偏移一颗 die 的对齐错误。

这是整份规范里最容易踩的陷阱，因为 map 文件本身不会报任何错——和 [06 TransferMap 与 KGD 追溯链路](./06-TransferMap与KGD追溯链路.md) 里讲的「map 旋转 90° 也不会报错」属于同一类问题。升级后的 `SinfMapReader` 会把 `REFPX/REFPY` 减 1 再写入中性模型，并生成一条告警记录供人工确认。

## 方言与变体

### 3 Digit SINF

看着像 SINF，但 bin ID 是三位（`000`），空位是三个下划线（`___`）。头部看起来与标准 SINF 相同：

```
RowData:___ ___ ___ 000 000 000 000 000 000 000 000 000 000 000
```

**没有正式规范**，Artwork 只能假设值域是 `000`–`255`。2019 年才有 foundry / OSAT 给出非正式指引，而且各家互相矛盾：

| 来源 | bin 码约定 |
|---|---|
**真实样本补充**（Amkor / Camtek 链路）：3 Digit SINF 里的未检测码会写成 `@@@`（三位），而不是标准 SINF 的 `@@`。解析器若只认 `@@`，会把整行判为非法并跳过，导致整张 map 变空。本工程的 `SinfMapReader` 已同时支持两者。

| Tower Semiconductor（Foundry） | `001`–`003` = PASS；`004`–`253` = FAIL；`253`–`254` = UGLY |
| Amkor（OSAT）单 site | `000` = PASS；`001`–`100` = FAIL |
| Amkor（OSAT）多 site（>2 die） | `001`–`200` = PASS；`001`–`100` = FAIL |

结论：**3 Digit SINF 必须拿实际样本校准**，不能按通用规则解析。

### 其他变体

- **1 Digit No Padding**：只有单个数字 bin ID，空位用空格而非 `__`，用 `*` 表示部分/边缘 die，且 die 之间没有空格分隔。
- **1 Digit TSMC**：单数字 bin ID，源自台积电，头部不含 die 尺寸/单位和参考 die 位置。
- **SINF XML**：把 SINF 数据编码进 XML，好处是数据库可以显式定义 Null/Skip/Test die 的 ID。Artwork 也不确定这是不是「官方」格式。

## 工程落地：本工程的 SinfMapReader

当前升级后的 `SinfMapReader` 支持范围：

| 能力 | 状态 |
|---|---|
| `KEY:VALUE` 头部解析（含全部字段） | 已支持 |
| `BCEQU` 良品列表（逗号/空格分隔，缺省 `01`） | 已支持 |
| `RowData:` 前缀 + 无前缀两种数据行 | 已支持 |
| 2 位 hex bin 码 + 3 Digit（`000`/`___`）变体 | 已支持 |
| `__`/`___` 无 die、`@@`/`@@@` 未检测、`FD/FE` 边缘 die、`FF` 参考 die | 已支持 |
| `REFPX/REFPY` 1-based → 0-based 修正 | 已支持，并记告警 |
| `FNLOC` flat 方位 | 已记录到 Issue，不参与坐标变换 |
| 行序上下翻转、厂内方言符号 | **需按样本校准**，不自动猜测 |

设计原则与 [08 Artwork 工具与中性模型架构](./08-Artwork转换工具与中性模型架构.md) 一致：**不确定的信息不静默吞掉，写成 `ImportIssue` 交给上层判断**。

真实接入厂内 SINF 时的检查清单：

1. 确认 `BCEQU` 是否存在、多个 Pass bin 如何分隔；
2. 确认 bin 码是 2 位还是 3 位，是否用 hex；
3. 确认参考 die 用 `FF` 还是 `REFPX/REFPY`，后者是否已按 1-based 修正；
4. 确认 RowData 第一行对应晶圆顶部还是底部（行序）；
5. 确认 `FNLOC` 与坐标系原点、旋转方向的关系。

## 资料来源

- Artwork Conversion Software — [SINF 格式页](https://artwork.com/package/wmapconvert/map_formats/SINF/index.htm)（头部字段表、hex 码语义、1-based 坐标注释、3 Digit 变体与 2019 更新）；
- Artwork — Supported Wafer Map Formats 总页（1 Digit / SINF XML 变体）。
