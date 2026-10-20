# UotanStudio.IconVerification 项目指令

在本目录内改图标检查或渲染宿主前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目是手动运行的 Avalonia Headless 图标验证宿主，不在 `UotanStudio.slnx` 内；`dotnet build/test --solution UotanStudio.slnx` 不会构建或运行它。
- 它引用宿主 `UotanStudio` 以复用 `AppIcon`/`AppIconKind` 和图标控件；不要在这里改产品行为、图标资源或主题。它也不是测试工程，没有 xUnit 断言宿主。
- csproj 刻意关闭 AOT/裁剪分析与 `IsTrimmable`/`IsAotCompatible`，因为该项目只用反射枚举公共图标控件；不要把这组设置复制到生产项目。

## 输出与检查

- 输出目录固定为仓库根的 `artifacts/icon-verification/`；程序会先删除该目录再重建，删除前有路径后缀校验。不要把这个目录改到仓库外，也不要绕过校验。
- 静态图标从 `UotanStudio/**/*.axaml` 的 TDesign 命名空间读取；动态图标枚举 `AppIconKind`。发现缺失的 TDesign 类型会写入错误并影响退出码。
- 自动检查覆盖：非空形状、边界/裁切、前景色占比、16/24/48/96 px 缩放，以及 24 px 图标间距/外边距。语义形状与视觉质量仍需查看 `sheets/` 下的联系表，不能只看退出码。
- 输出包括 `icons/`、`sheets/`、`report.txt` 与 `summary.txt`；有失败时进程返回 1。`artifacts/` 已被 `.gitignore` 忽略，不要把生成图提交进仓库。

## 运行

```text
dotnet run --project UotanStudio.IconVerification/UotanStudio.IconVerification.csproj -c Debug
```

- 只支持仓库内运行：程序需要向上找到 `UotanStudio.slnx`，找不到直接报错。
- 该宿主只证明 headless 渲染与几何检查结果，不证明真实窗口的 DPI、字体回退或系统主题下的视觉效果。