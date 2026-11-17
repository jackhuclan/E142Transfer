# 02 Bin 编码规则（hard bin / soft bin）

[返回索引](./README.md)

## 什么是 bin

**bin（分档）**就是 wafer map 里每个 die 携带的那一个分类码——它既是测试结论，也是 die attach 设备决定"拾取还是跳过"的唯一依据。

理解 bin 编码，核心是三层：**hard bin、soft bin，以及两者之间的收敛关系**。

## hard bin 与 soft bin

- **hard bin**：最粗的物理分类，直接对应 die 的"命运"——pass、fail、retest 或 dummy，数量通常只有个位数。
- **soft bin**：测试程序给每个 die 打的具体失败标签，可以有几十到几百类，是良率分析和失效定位的原始数据。
- **映射关系**：多个 soft bin 收敛进一个 hard bin，这张映射表是 test program 的核心产物之一。良率看 hard bin 的分布，失效分析和良率提升（YPD）靠 soft bin 的细分计数。

举例：同一 hard bin（Fail）下可挂 open / short / leakage / Vt 偏移 / 时序失败等几十个 soft bin；哪个 soft bin 占比异常，就定位到哪一道工序。

## 编号约定

业界有强烈的惯例，但**没有全球统一强制标准**——最终以各家 test program 的定义为准。常见约定是 bin 1 = pass，但不能想当然（见下）。

## 四条必须知道的编码规则

### 1. Pass bin 必须显式声明

行业惯例是 bin 1 = pass，但很多 program 用多个 pass bin（比如 bin 1 全通过、bin 2 降级可用），还有的 pass bin 根本不是 1。良率计算工具里都要配置 `passBins` 列表。

配置错了会直接导致良率算错、坏片被当良品拾取——而且 wafer map 本身不会报警。

### 2. Soft bin → hard bin 是收敛映射，不是一一对应

见上一节。这是 YPD 能定位到具体失效机理的前提。

### 3. Bin limit 带 guard band，不是裸阈值

分档阈值要留出余量，覆盖测试机测量误差以及温度、电压的波动。否则临界良品会飘到低档、边缘芯片误判。这也是 speed binning 能成立的前提：同一颗 die 在不同条件下可能落进不同 bin，guard band 保证分级稳定。

### 4. Edge exclusion：边缘 die 不进良率分母

晶圆外圈一圈 die 受机械应力和光刻场边缘影响，结果不可靠，通常按毫米指定排除宽度，分子分母都不算。这与点墨中的 Dummy die 是同一回事。

## 数据怎么流转：SEMI E142

当点墨从"喷墨"变成"数据"，需要标准来定义 map 怎么存、怎么传。**SEMI E142**（2005 年批准）定义了 substrate map 的格式，覆盖 wafer、film frame、strip、tray 多种载体，把 bin 码以 overlay 形式附加；还支持 transfer map——把 strip/tray 上每颗 die 追溯回原始晶圆坐标，用于失效回溯。

### 一个隐蔽的坑：map 坐标必须和物理 die 对齐

map 是针测时写的，die 是切割、贴膜后才拾取，中间靠 notch 定位 + 参考 die 校准。如果 map 被旋转 90° 或偏移一个 die，拾取机会拿坏片、丢良品，而 **map 文件本身看不出任何异常**。详见 [04 SEMI E142 文件结构](./04-SEMI-E142-文件结构.md) 的 reference die 一节。

## 与三映射的对应

EL / PL / AOI 三映射就是三张"光学域的 wafer map"：每个 die 的判定结果（缺陷/发光异常/外观不良）就是一种 bin，三张 mapping 的叠加与一致性比对，等价于电性测试里 multi-bin 的交叉分析。区别只在判定源——一个是 ATE 的电性结论，一个是相机 + 算法的光学结论。

这套规则（hard/soft 分层、passBins 配置、guard band、edge exclusion、坐标配准）几乎可以原样迁移到上位机的数据结构里。
