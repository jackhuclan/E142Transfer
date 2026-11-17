# 08 Artwork 转换工具与中性模型架构

[返回索引](./README.md)

## 这家公司是做什么的

**Artwork Conversion Software, Inc.**（美国加州 Manhattan Beach）专门做晶圆图文件的转换、查看、编辑，以及给设备厂商用的底层库。

它的存在本身就说明一个行业现实：没有统一 wafer map 格式，所以必须有专门做格式翻译的工具。其官网描述："the industry has not adopted a few standard map file formats - instead a large number of similar (but different) formats are in use that force the test and assembly engineers to convert between formats to move from wafer to finished product."

前面 [07](./07-Wafer-Map常见文件格式.md) 引用的 24 种格式清单，正是这家公司产品的支持列表。

## 四条产品线

| 产品 | 定位 | 典型用法 |
|---|---|---|
| **WMEdit** | 图形界面的查看 + 编辑 + 转换 | 打开一张 map，检查 die 分布、改几个 bin、另存成别家设备要的格式；可设置 bin 码颜色与外观 |
| **WMBatch** | 命令行批量转换 | 一条命令把整 lot 25 片晶圆图转成设备支持的格式，适合封测厂日常批处理 |
| **WMVU** | 只读查看器 | 基于 WMEdit 但去掉编辑功能，基础许可证包含全部格式 |
| **WMAPI** | 给设备 OEM 的库 | C++ 和 .NET API，集成进探针台/测试机自己的软件，显示、更新、输出 map |

## 商业授权（官网标价）

| 产品 | 价格 |
|---|---|
| WMEdit / WMBatch（4 / 6 / 8 种输入输出格式） | $1,995 / $2,495 / $2,895 |
| WMEdit / WMBatch（不限格式） | 询价 |
| WMVU（全格式只读） | $995 |
| 年维护合同 | $550（WMVU $400） |
| 浮动公司许可证 | 加 $400 |

许可证默认绑定单机。官网提供下载入口，可先评估。

## 核心架构决策：中性内部 map 结构

这是最值得借鉴的部分。

Artwork 早期做 **1:1 格式转换器**，格式一多就维护不动了——N 种输入 × M 种输出意味着 N×M 个转换器。于是改成：

```
所有输入格式 → 读入中性内部 map 结构 → 写出目标格式
```

这样新加一种格式只需要写一个读适配器和一个写适配器，不用改其他所有组合。他们甚至提到，为处理"某些格式有特殊规则或元素"，不得不持续细化这个内部结构，最后才做到加新格式很直接。

四个产品共享同两个底层库，所以库的改动会传播到 WMEdit 和 WMBatch，也通过 WMAPI 提供给 OEM。

## 对上位机的借鉴

EL / PL / AOI 三映射导入模块应沿这个架构做：

- **分析代码永远只见中性模型**，不直接接触文件格式；
- 每种格式一个 Reader，收敛到 `NeutralWaferMap`；
- 需要导出时，从同一中性模型接不同 Writer；
- 新增格式不动分析代码。

本工程已按此实现，见 [09 工程实现](./09-工程实现-E142Transfer适配器.md)。
