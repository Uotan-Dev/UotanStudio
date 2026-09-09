# CLI 与 MCP 暂时移除（2026-09-10）

完整实现保存在本地分支 `codex/archive-cli-mcp`，提交 `9c55695`。该提交与当前分支移除前的 `3079789` 文件树完全一致，包含前一轮事件／入口复用优化、CLI、MCP、相关 UI 和测试。

## 当前范围

- 移除固件 CLI、工作流 CLI、普通命令行选项解析及帮助／版本／文件关联注册命令。
- 移除 MCP 客户端、宿主、固件工具、格式别名解析、服务器配置模型和启动逻辑。
- 移除设置页 MCP 区域、MCP 工具箱分类／节点、中英文本及 Avalonia MCP 诊断接入。
- 移除直接使用的 MCP SDK、依赖注入包和诊断包引用及集中版本。
- 保留图形程序启动和文件关联传入文件的打开／导入，保留固件库、镜像操作、可视化工作流及通用终端。
- 设置文件通过 `JsonExtensionData` 原样保留暂不可用功能字段；复制设置时隔离扩展字段字典。保留数据不意味着执行旧 MCP 配置。

旧工作流中的 MCP 节点按既有未知节点规则跳过，相关连接也无法重建。因此含 MCP 节点的工程应在保存分支中使用，避免在当前版本覆盖保存而丢失这些节点。

## 验证

- 完整 Debug 解决方案构建通过，0 错误；5 条既有测试分析器警告均位于未改动的 `ContainerStructureTests.cs`。
- 独立输出目录运行全部 Services 测试及 `RepackMetadataTests`、`BootRepackMetadataTests`、`DtboRepackTests`：167 通过，0 失败，0 跳过。
- 元数据测试改用桌面 `PackingService.BuildImage`，保留格式分派、Boot／vendor_boot 各版本、DTBO、配置编辑、输出保护及固件内容回归覆盖。
- 新增源生成 JSON 回归，验证旧配置经读取、复制和序列化后保留，复制后修改扩展字段不影响原对象。
- 实际启动新构建的 Desktop，以鼠标打开设置页与节点页，通过 Windows UI Automation 读取控件确认没有 MCP 入口，普通设置及节点分类存在；已检查设置页截图。
- 构建日志、测试 XML、界面控件快照和截图位于本地忽略目录 `artifacts/cli-mcp-removal/`。
- 本次未执行完整固件／协议测试集、NativeAOT 发布或真实设备刷写验证，不能据此宣称所有功能已完成端到端验证。

## 使用保存版本

在工作区没有未保存改动时，可切换到 `codex/archive-cli-mcp` 使用完整版本。也可建立独立工作区，避免切换当前开发目录：

```powershell
git worktree add '..\UotanStudio-cli-mcp' codex/archive-cli-mcp
```

保存分支是完整项目快照；后续恢复时应审查当前分支的移除提交并按需要撤销，避免整树覆盖后续其他改动。此前入口重构报告中的 CLI／MCP 验证属于保存版本的历史记录。
