# UotanStudio 固件 IDE 设计文档

> 版本：v0.1（规划草案）
> 范围：工作区（Workspace）页面的产品化升级方向
> 读者：开发团队、产品评审

---

## 1. 现状分析

### 1.1 当前工作区已具备的能力

| 能力 | 现状载体 | 说明 |
|------|----------|------|
| 可停靠 IDE 框架 | `WorkspaceDockFactory` (Dock.Avalonia) | 已有 Documents / ProjectExplorer / Properties / Output / AdbLog 五个面板，支持拖拽分离/重停靠 |
| 多文档编辑 | `Document.Document1/2` | 当前仅为占位文本（`build.prop`/`init.rc`），无真实文件打开/保存 |
| 可视化流程编排 | `NodeDockFactory` + NodifyM.Avalonia | 已有节点画布、连接线、缩放、小地图、工具箱、属性面板、执行日志的骨架 |
| 固件解析底座 | `FirmwareService` + `UotanStudio.Firmware` | 已支持 AVB/boot/压缩/cpio/设备树/分区/DZ-KDZ/LP/NTPi/NB0/OPS/Oppo/PAC/payload/sparse 等格式 |
| 设备通信底座 | `DeviceMonitorService` + `UotanStudio.Comm` | 已有 ADB/Fastboot/EDL/RKDL 协议与 USB 传输层 |
| 日志服务 | `LogService` / `UsbLogBridge` | 已具备统一日志与 USB 传输跟踪桥 |

### 1.2 关键差距（Gap）

1. **面板是空壳**：四个工具面板（ProjectExplorer/Properties/Output/AdbLog）当前返回占位字符串，没有真实 ViewModel/View 绑定。
2. **"固件工程"概念缺失**：没有项目/工程文件模型，Document 无法感知打开的是何种固件部件。
3. **解析能力未接入 UI**：`UotanStudio.Firmware` 能力强大但只被 `FirmwareService` 以库方式使用，没有在编辑器内可视化（如分区树、payload 列表）。
4. **节点编辑器与真实操作脱节**：节点仅 5 个类别骨架，未映射到真实烧录/解析流程，未连到设备底座。
5. **无构建/打包/验证工作流**：缺乏"构建→签名→验证→烧录"的完整闭环 UI。

---

## 2. 定位与目标用户

**UotanStudio 是一个面向系统移植、刷机与固件逆向的跨平台固件 IDE**，而非通用文本编辑器。

目标用户：
- **ROM 制作/移植开发者**：需要拆包、改包、重打包、签名。
- **刷机/救援用户**：需要识别设备模式、选择固件、写入分区。
- **固件研究/操作系统爱好者**：需要查看分区结构、boot 镜像头、payload 清单。
- **自动化抽卡/流程化操作者**：需要把多步操作编排为可复用流程（节点编辑器目标）。

核心价值一句话：**"把一台砖头设备和一份固件，变成可视、可改、可验、可烧、可复用的工程。"**

---

## 3. 功能矩阵（含用途与实现项）

按模块分组，标注优先级 D（本期必做）/ N（近期）/ L（远期）。

### 3.1 工程管理（Project System）— 核心地基

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 固件工程文件 | 把一组固件部件（payload/分区/镜像/脚本）组织为一个可打开的项目 | 定义 `FirmwareProject` 模型（.uotproj JSON），含部件清单、设备目标、模式 | D |
| 最近工程/工程扫描 | 快速打开历史工程，折叠压缩包 | ProjectExplorer 面板左树，历史记录服务 | D |
| 部件树 | 树形展示固件内部结构（分区→镜像，payload→bundle→tar） | 基于 FirmwareService 的解析结果构建树节点 | D |
| 解包/分包动作 | 从固件包提取部件到工程目录，或反向 | FirmwareService 对接，带进度 | N |

### 3.2 多文档编辑器（Editor）— 打开即所见

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 文本/脚本编辑 | 编辑 `build.prop`、`init.rc`、`.uotproj`、设备树 source | Avalonia `TextEditor`（绑文件），语法高亮，无依赖尽量少 | D |
| 十六进制查看 | 查看/编辑 boot 镜像、分区裸数据 | HexView 控件，只读起步，按块增量加载大文件 | N |
| 结构化 JSON/XML 查看 | 只读查看 payload/配置/元数据 | 树/网格视图渲染解析结果 | N |
| 编辑-撤销/保存 | 文件脏标记、保存到工程目录 | 编辑器核心，含编码/换行处理 | D |
| 镜像头查看 | 查看 boot/vendor_boot 等镜像头的版本/大小/页 | 接入 `UotanStudio.Firmware.BootImage` | N |

### 3.3 固件分析与可视化（Analyzer）— 差异化亮点

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 分区结构视图 | 显示分区表（稀疏/逻辑分区块映射） | 树/表控件绑定解析结果 | N |
| 固件差异对比 | 对比原包与改包后的分区差异 | 哈希比对 + 差异列表 | L |
| 元数据面板导航 | 选中镜像显示品牌/型号/版本/算法 | 右侧 Properties 面板动态填充 | D |

### 3.4 流程编排（Node Editor）— 复用与自动化

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 真实节点库 | 将烧录/解析/替换/签名/打包映射为节点 | 扩展 `NodeCategory`，每个操作一个节点 VM + 参数 UI | N |
| 节点-设备联动 | 节点执行时调用真实设备会话 | 注入 `IStudioRuntime`（封装 FirmwareService/DeviceMonitorService） | N |
| 流程保存/加载 | 把画布存为可复用 .uotflow | 序列化 Nodes/Connections | L |
| 执行引擎 | 按连线顺序执行，含条件与失败暂停 | 解释执行器 + 日志面板输出 | L |
| 撤销/重做/对齐 | 编辑体验完善 | NodifyM 能力补充 | L |

### 3.5 设备与烧录集成（Device/Flash）— 闭环关键

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 模式感知设备栏 | 显示当前设备（ADB/Fastboot/EDL/RKDL）+ 厂商/型号 | 复用现有 DeviceViewModel | D |
| 一键刷写当前固件 | 把当前工程部件刷到当前设备 | 对接 Firhose/Fastboot/flash 会话 | N |
| 刷写进度/日志 | 实时展示分区写入进度 | 复用 Output/AdbLog 面板 | N |

### 3.6 构建与验证（Build/Verify）— 专业闭环

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 校验和验证（AVB/哈希） | 验证镜像签名/完整性 | 暴露 `AvbVerificationService`/HashService | N |
| 打包输出 | 修改后重新打包为可刷固件 | FirmwareService 打包链路 | L |
| 构建日志解析 | 显示错误定位到文件行 | Output 面板 + 诊断模型 | L |

### 3.7 输出/日志面板强化

| 功能 | 用途 | 实现项 | 优先级 |
|------|------|--------|--------|
| 分通道日志 | 构建/烧录/设备三类日志分区隔离 | Output 面板 Tab | D |
| USB 传输跟踪 | 底层传输细节可视化 | 接入现有 `UsbLogBridge` | N |

---

## 4. 目标工程结构

在现有工程基础上，将"工作区"承载的 IDE 能力抽象为一套清晰目录：

```
UotanStudio/
├── Areas/                        # 按业务域组织的模块（新增，替代散落的 Pages/ViewModels）
│   ├── ProjectExplorer/          # 工程/部件树
│   │   ├── ProjectExplorerView.axaml
│   │   ├── ProjectExplorerViewModel.cs
│   │   └── Models/              # FirmwareProject, ProjectNode
│   ├── Editor/                   # 多文档编辑器
│   │   ├── EditorView.axaml     # 宿主（文本/hex/结构 三种子视图切换）
│   │   ├── EditorViewModel.cs
│   │   └── Views/TextDocumentView.axaml, HexDocumentView.axaml
│   ├── Analyzer/                # 固件分析（分区/镜像头）
│   │   ├── AnalyzerView.axaml
│   │   └── AnalyzerViewModel.cs
│   ├── NodeEditor/               # 现有节点编辑器迁入并增强
│   │   ├── NodeEditorView.axaml (现 Pages/NodeEditorView)
│   │   ├── ViewModels/ (现 ViewModels/NodeViewModels/)
│   │   └── Nodes/               # 具体节点实现
│   ├── Device/                    # 设备与烧录（复用现有 Device*，迁展示）
│   └── Output/                    # 输出/日志
├── Core/                         # 与 UI 无关的领域逻辑（新增）
│   ├── Projects/FirmwareProjectService.cs
│   ├── Runtime/IStudioRuntime.cs # 统一访问固件/设备/日志的运行时门面
│   ├── Build/BuildService.cs
│   └── Verification/VerificationService.cs
├── Services/                     # 既有全局服务维持现状
├── Pages/DockControl            # 停靠工厂保留，仅改面板内容注入
└── Data/Catalog/VendorIdTable   # 既有数据表保持
```

**不变更的**：`UotanStudio.Firmware`（纯库）、`UotanStudio.Comm`（协议栈）、`Services/`（全局服务）——它们已分层合理，仅向其追加能力，不做结构震荡。

**关键抽象：`IStudioRuntime`**
让"固件解析、设备会话、日志、进度"在 IDE 各面板和节点编辑器之间共享，避免每一个面板各自直连 Services，是后续所有功能打通的前提。

---

## 5. 分期实施路径

### 阶段 D0 — 把空壳面板变成真实面板（地基）
- **目标**：四个工具面板从占位串变为真实 ViewModel+View；Document 面板能打开真实文件。
- 关键产物：
  - `ProjectExplorerViewModel`：读当前工程目录，展示文件/部件树。
  - `EditorViewModel`：打开文件到新 Document，脏标记+保存。
  - `OutputViewModel`：聚合 LogService，分区 Tab。
  - 停靠工厂的 `ContextLocator` 从返回字符串改为返回真实面板 VM。
- **验收**：打开一个解包目录，左树可点开文件，中部文档可编辑保存，底部日志实时滚动。

### 阶段 D1 — 接入固件解析做可视化分析
- **目标**：把 `UotanStudio.Firmware` 能力上到 UI。
- 关键产物：
  - `FirmwareProjectService`：扫描一个包 → 解析 → 输出部件树。
  - `AnalyzerView`：分区结构树、boot 镜像头只读查看。
  - 右键解包到工程目录。
- **验收**：拖入一个 payload/OPS/KDZ 包，能查看内部结构并解包。

### 阶段 N1 — 设备与烧录闭环
- **目标**：让 IDE 能对真实设备执行。
- 关键产物：`IStudioRuntime` 落地；Device 面板嵌入工作区；烧录动作带进度与日志。
- **验收**：Fastboot/EDL 设备可见，选择工程某分区可一键刷写。

### 阶段 N2 — 节点编辑器真实化 + 流程保存
- **目标**：节点映射真实操作，可保存/加载流程。
- 关键产物：真实节点库接入 `IStudioRuntime`；`.uotflow` 持久化。
- **验收**：编排"解包→替换→重打包→刷写"四节点流程并执行。

### 阶段 L1 — 构建/验证/差异（长期）
- 打包输出、AVB 验证、固件差异对比、十六进制编辑。

---

## 6. 待评审决策点

1. **工程文件格式**：`.uotproj` 采用纯 JSON（简单、可 diff、可版本管理）是否可以接受？还是需要 schema 演进（JSON + 版本字段）。
2. **节点编辑器**：是否有必要在 N1 之前就接设备，还是先做纯固件流程（不碰设备）以降低范围。
3. **目录重构风险**：本次把 `Pages/NodeEditorView` 等迁入 `Areas/` 会带来大面积文件移动，是否接受一次性的结构调整（会牺牲历史 diff 追踪）。
4. **第三方文本编辑器**：是否允许引入 Avalonia 生态文本编辑组件，还是坚持自研轻量编辑器（少依赖）。
5. **本期范围**：建议 D0+D1 作为首个可交付里程碑，需确认资源投入与排期预期是否与之匹配。