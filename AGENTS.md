# AGENTS.md · 仓库组织规范

> 本文件是**项目结构与文件归属的唯一规范**，面向在本仓库工作的开发者与 AI 代理。
> 新增项目、新增文件、调整解决方案分层前先读本文件；与 README（面向使用者）分工不同：
> README 讲"怎么构建与使用"，本文件讲"东西放在哪里、为什么"。

---

## 1. 顶层布局

```
E142Transfer\  （仓库即项目名：多格式晶圆图工具箱）
├─ E142Transfer.slnx         解决方案（唯一入口）
├─ .editorconfig             代码风格（解决方案级）
├─ Directory.Build.props     全解决方案共享元数据：版本单点、语言基线、Deterministic
├─ Directory.Packages.props  中央包管理（CPM）：所有 PackageReference 的版本在此
├─ nuget.config              包源配置（覆盖本机多源，消除 NU1507）
├─ .gitignore
├─ README.md                 使用说明（构建/运行/功能）
├─ AGENTS.md                 本文件：组织规范
├─ src/                      源码项目（可交付产物）
├─ tests/                    测试项目（不发布）
├─ tools/                    工具项目（现场/验收用，不随主程序发布）
├─ assets/                   运行时会被程序加载的文件（示例 map 等）
└─ docs/                     知识文档（点墨 → bin 编码 → E142 → 格式详解 → 工程实现）
```

**Solution Items = 解决方案级文件**，指"不属于任何单个项目、但整个解决方案共享"的文件。
它们必须留在仓库根目录（MSBuild 的 `Directory.Build.props` / `Directory.Packages.props`
只在目录树向上查找时才生效，挪走即失效），在 Visual Studio 中通过 `Solution Items` 文件夹集中呈现。

---

## 2. `src/` 的分层与归属规则

依赖方向严格单向：

```
WaferMapStudio.App ──► WaferMapStudio.Rendering ──► WaferMapStudio.Core ──► E142Transfer
         │                                                ▲
         └──► WaferMapStudio.ViewModel ───────────────────┘
```

| 项目 | 目标框架 | 职责 | 禁止 |
|---|---|---|---|
| `E142Transfer` | `net10.0` | 解析库：E142/G85/SINF/CSV Reader → 中性模型 `NeutralWaferMap`、TransferMap 解析/生成、坐标归一化 | 引用任何 UI/Windows 类型 |
| `WaferMapStudio.Core` | `net10.0` | 视觉契约（`DieVisual`/`WaferMapVisual`）、`NeutralWaferMap → 视觉模型` 映射器、BinCode→颜色调色板与图例、视口缩放/平移几何（`Interaction/ViewportZoom`）、按 die 步距等比的网格几何（`Visuals/GridMetrics`） | 引用任何 UI/Windows 类型 |
| `WaferMapStudio.ViewModel` | `net10.0` | 文档/浏览器视图模型、加载与切换编排、解析信息与错误日志 | 引用 `WindowsBase`/WPF（须可跨平台单测）；弹文件对话框（走 `IFileDialogService`） |
| `WaferMapStudio.Rendering` | `net10.0-windows` | 可复用 `WaferMapView` 控件：Canvas 绘制、Viewbox 自适应、按 pitch 等比格宽/格高、滚轮缩放/拖动平移、点选、图例浮层 | 引用 App 或 ViewModel；持有业务状态；自行推导网格几何 |
| `WaferMapStudio.App` | `net10.0-windows` (WinExe) | WPF 主程序、Host+DI 组装、主窗口布局、文件对话框实现 | 承载可复用的领域逻辑（应下沉到 Core/ViewModel） |

**归属判定顺序**（新代码放哪里）：
1. 与文件格式/解析相关（新增 Reader、字段映射）→ `E142Transfer` 的 `Import/`；
2. NeutralWaferMap → 视觉模型 / 颜色 / 图例的纯数据规则 → `WaferMapStudio.Core`；
3. 与 WPF 视觉对象相关、可被其他上位机复用 → `WaferMapStudio.Rendering`；
4. 只服务界面的状态编排 → `WaferMapStudio.ViewModel`（仅当必须依赖 WPF 类型时才允许留在 App）；
5. 文件对话框等宿主能力 → 接口在 `ViewModel/Services`，实现放 `App/Services`；
6. 只在验收/运维时运行 → `tools/`。

---

## 3. `tests/`、`tools/` 与 `assets/`

### tests/
- 自检程序 `E142Transfer.SelfTest`（运行即打印全部 OK，覆盖解析闭环与导入链路）；
- xunit 工程命名 `WaferMapStudio.<被测层>.Tests`，一个被测项目对应一个测试工程；
- 测试工程不得被 `src/` 引用；测试数据写到 `Path.GetTempPath()` 并在用例结束清理。

### tools/
- `E142Transfer.RealSampleCheck`：对真实晶圆图文件做端到端解析报告（现场验收用）；
- 工具可引用 `src/`，但 `src/` 不得引用 `tools/`。

### assets/
- 只放**运行时会被程序加载**的文件（当前为 `sample-maps/` 示例晶圆图）；
  文档、临时输出不入内；大型真实产线数据不入 Git。

---

## 4. Visual Studio 解决方案分层

解决方案必须与磁盘结构一致，分组名固定 `Solution Items` / `src` / `tests` / `tools`，
不允许项目平铺在根。新增项目必须同时改两处：磁盘目录 + `E142Transfer.slnx` 对应分组；
漏改 slnx 会导致 CI 不编译该项目。

---

## 5. 新增文件/项目的检查清单

- [ ] 位置符合 §2 的归属规则，命名符合 `WaferMapStudio.<层>[.<子域>]` 或解析库既有命名；
- [ ] 目标框架与所在层一致（解析库/Core/ViewModel 为 `net10.0`，渲染/App 为 `net10.0-windows`）；
- [ ] 依赖方向符合 §2 的单向图，没有反向引用；
- [ ] `E142Transfer.slnx` 已加入对应分组；
- [ ] 有对应测试（解析库/Core/ViewModel 强制）；
- [ ] `dotnet build E142Transfer.slnx -c Release` 0 警告 0 错误、
      `dotnet test E142Transfer.slnx` 全绿、`dotnet run --project tests/E142Transfer.SelfTest` 全 OK；
- [ ] 文档/报告与代码同批提交，版本号只改 `Directory.Build.props` 一处；
- [ ] 提交信息用中文 Conventional Commits，尾注版本号（如 `feat(app): … (0.2.0)`）。

---

## 6. 代码风格要点（与 .editorconfig 对应）

- file-scoped namespace；私有/静态字段 `_camelCase`；常量 PascalCase；
- `var` 仅在类型明显时使用；表达式体、switch 表达式优先；
- XML 文档注释用中文，说明"为什么"而非重复"是什么"；
- ViewModels 用 CommunityToolkit.Mvvm（`ObservableObject` + `[ObservableProperty]`）；
- DI 注册集中在 `App/AppServices.cs`（单一事实来源）。

## 7. 与既有文档的关系

| 文档 | 关系 |
|---|---|
| `README.md` | 使用者视角：构建、运行、功能 |
| `docs/README.md` 及 `docs/01–12` | 晶圆图领域知识库（点墨、bin 编码、E142/G85/SINF/KLARF、KGD 追溯），概念与格式规范；本文件管"放哪里" |
