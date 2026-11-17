# 03 BinCode：码值到语义的映射

[返回索引](./README.md)

## 一句话定义

**BinCode 是 E142 文件里的"码值 → 语义"映射条目**。晶圆图的数据阵列里每个 die 只存一个紧凑的码（一个字符或一个数字），码本身没有任何含义，必须查 BinCode 定义表才知道这颗 die 究竟是良品、哪一种失效、还是空位。

它是 bin 编码、数据阵列、E142 文件三者之间的"转换齿轮"。

## 核心本质

码值是**任意指定（arbitrarily assigned）**的，语义是**文件自定义**的。同一个字符 `X`，在这份文件里可能是"开路"，在另一份文件里可能是"短路"——所以解析器绝不能硬编码码值含义，**必须先读定义表**。

## BinCode 的三层结构

- **BinCode 值**：数据阵列里实际存的符号（ASCII 字符、3 位十进制、2 位十六进制等）。
- **类别（Bin Type / Category）**：属于哪一大类——最常见 Pass、Fail、Null，扩展的还有 Alignment（对准标记）、TestDevice（测试结构）、EdgeDie 等。
- **语义描述（Description / Name）**：人类可读的名字，如 "Open Fail"、"Leakage High"、"Continuity Pass"。良率分析和失效定位靠的就是这一层。

## 最少要有哪几类

E142 规定一份 map 至少定义三类码，否则下游无法工作：

| 类别 | 作用 | 缺失后果 |
|---|---|---|
| **NullBin** | 表示阵列里没有 die（圆形晶圆的方形阵列四角）或未测的 die | 良率分母算错、把空气当坏片 |
| **Pass** | 良品，die attach 会拾取 | 好片被漏拾 |
| **Fail** | 不良，跳过；可细分多个失效类型 | 坏片被封装进去 |

注意：`0 / P / X / 1` 只是示意——真实文件里 Pass 可能是 `1` 也可能是 `P`，完全取决于 test program 和 BinType 设定。

## 在整条链路中的位置

```
die 测完
  → test program 分配 bin 号（soft bin）
    → 收敛成 hard bin
      → 按 BinType 编码成码值写进阵列
        → BinCode 定义表声明码值对应什么
          → E142 文件交给下游
            → die sorter 读定义表，只拾取 Pass 码的 die
```

BinCode 定义表就是整条链路的**解码字典**——没有它，数据阵列只是一堆没有意义的字符。这也是为什么 [02](./02-Bin编码规则-hard-soft-bin.md) 中强调"Pass bin 必须显式配置"：软件并不知道哪个码是 Pass，全靠这张表告诉它。

## 对上位机的直接意义

EL / PL / AOI 三映射导入模块要做两件事：

1. 解析 Layout（把行列索引映射成晶圆坐标）；
2. 解析 BinCode 定义表（把每个码值翻译成判定类别）。

三张 mapping 的比对，就是三套 BinCode 语义在 die 坐标系下的对齐与交叉统计。把自己的判定类别（如"PL 暗点"、"AOI 崩边"）设计成可扩展的 BinCode 表，比硬编码颜色要干净得多。

本工程的实现见 [09 工程实现](./09-工程实现-E142Transfer适配器.md) 的 `NeutralBinDefinition`。
