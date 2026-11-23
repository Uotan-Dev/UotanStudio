# UotanStudio.Scripting 项目指令

在本目录内新增或修改脚本执行能力前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`；测试写法见 `UotanStudio.Tests/AGENTS.md`。

## 边界

- 本库是叶子库，只依赖 BCL 与 Jint，不得引用任何业务项目；设备、镜像与 UI 的真实操作由宿主通过接口注入，本库只做校验、编排与沙箱。
- 脚本执行只允许白名单 API：不得通过 Jint interop 暴露任意 CLR 类型、文件系统、网络、反射或模块加载能力。
- 所有脚本输入（分区名、路径、单位、UI 描述、日志文本）必须在 C# 侧校验或限额后再交给宿主；返回值统一走 JSON 字符串桥，不把活 CLR 对象交回脚本。
- 每次执行使用全新 Engine；不可在信任域之间复用引擎或全局快照。
- 限制由 `ScriptLimits` 与 Jint `UntrustedCodeLimits` 共同表达：超时、语句数、内存、递归、源码长度、AST 节点、日志与 UI 元素数量必须有界。
- 约束是协作式的，不能替代操作系统级隔离；恶意输入仍需在宿主进程边界外评估。

## 契约

- `ScriptRunner.RunAsync` 是唯一执行入口，返回 `ScriptExecutionResult`，脚本异常转成用户可读错误，不向宿主抛出 Jint 内部异常。
- `ScriptApiContext` 提供宿主能力注入点；默认实现是模拟设备与空 UI，仅用于测试和脚本预览。
- 公共类型与成员必须有中英双语 XML 注释；`TreatWarningsAsErrors` 会让 `CS1591` 失败。

## 验证

```text
dotnet build UotanStudio.Scripting/UotanStudio.Scripting.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Scripting --minimum-expected-tests 1
```
