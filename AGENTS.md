# AGENTS.md

UotanStudio：.NET 10 的 Avalonia 桌面固件刷写工具。解决方案为 `UotanStudio.slnx`（新版 slnx 格式，旧工具不支持）。

## 构建 / 测试 / 格式化

- 构建：`dotnet build UotanStudio.slnx`（`TreatWarningsAsErrors=true`，必须零警告）
- 测试：`dotnet test UotanStudio.Firmware.Tests`（唯一测试工程，xUnit）
- 格式化：`dotnet format UotanStudio.slnx --no-restore`（.atomcode hook 每次编辑后自动执行）
- 发布：`dotnet publish UotanStudio.Desktop -c Release -r <RID> --self-contained`（AOT 兼容）

## 架构

- `UotanStudio`：Avalonia UI（Areas/ 功能分区 + ViewModels/Views/Services）
- `UotanStudio.Comm`：USB/ADB/EDL/Fastboot/Hdlc 通信协议（LibUsbDotNet）
- `UotanStudio.Firmware`：boot image/cpio/AVB/payload 解析与压缩——安全敏感，输入不可信
- `UotanStudio.Desktop`：程序入口；`UotanStudio.Firmware.Tests`：测试

## 关键约定（详见 .atomcode/skills/project-conventions/）

- 无 DI 容器：服务用 `Lazy<T>` 单例暴露（`Instance` 静态属性），勿引入 DI 框架
- MVVM：CommunityToolkit.Mvvm source generators（`[ObservableProperty]` / `[RelayCommand]`），消息走 `WeakReferenceMessenger`
- 编译绑定已开启：XAML 必须写 `x:DataType`，绑定错误在编译期暴露
- 包版本集中在 `Directory.Packages.props`（Central Package Management），.csproj 不写版本号
- public 成员需中英双语 `<summary>`；命名与格式由 .editorconfig 强制（多为 `:error`）

## 维护规则

项目结构、构建测试命令、架构边界、开发约定或本文件记录的其他事实发生变化时，必须在同一次改动中同步更新本文件。
